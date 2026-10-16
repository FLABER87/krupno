using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Krupno.App.Controls;

/// <summary>
/// "Before/after" comparison: a split view with a divider or two images side by side, zoom relative to the result's
/// actual pixels, highlighting of finished tiles during processing.
/// </summary>
public partial class CompareView : UserControl
{
    public static readonly DependencyProperty BeforeSourceProperty = DependencyProperty.Register(
        nameof(BeforeSource), typeof(BitmapSource), typeof(CompareView), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty AfterSourceProperty = DependencyProperty.Register(
        nameof(AfterSource), typeof(BitmapSource), typeof(CompareView), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty TilesProperty = DependencyProperty.Register(
        nameof(Tiles), typeof(TileOverlay), typeof(CompareView), new PropertyMetadata(null, (d, _) => ((CompareView)d).UpdateTiles()));

    private const double ZoomStep = 1.25;
    private const double DividerGrip = 14;
    private const double SideGap = 8;

    private readonly ScaleTransform _beforeScale = new();
    private readonly TranslateTransform _beforeShift = new();
    private readonly ScaleTransform _afterScale = new();
    private readonly TranslateTransform _afterShift = new();

    private bool _sideBySide;
    private double _split = 0.5;
    private double _zoom = 1;   // relative to "fit to window"
    private Vector _offset;
    private bool _draggingDivider;
    private Point? _panOrigin;

    public CompareView()
    {
        InitializeComponent();
        BeforeBox.RenderTransform = new TransformGroup { Children = { _beforeScale, _beforeShift } };
        AfterBox.RenderTransform = new TransformGroup { Children = { _afterScale, _afterShift } };
    }

    public BitmapSource? BeforeSource
    {
        get => (BitmapSource?)GetValue(BeforeSourceProperty);
        set => SetValue(BeforeSourceProperty, value);
    }

    public BitmapSource? AfterSource
    {
        get => (BitmapSource?)GetValue(AfterSourceProperty);
        set => SetValue(AfterSourceProperty, value);
    }

    public TileOverlay? Tiles
    {
        get => (TileOverlay?)GetValue(TilesProperty);
        set => SetValue(TilesProperty, value);
    }

    private bool HasBefore => BeforeSource is not null;
    private bool HasAfter => AfterSource is not null;
    private bool IsSplit => HasAfter && !_sideBySide;
    private bool IsZoomed => _zoom > 1.0001;

    // ── Public actions (toolbar buttons and hotkeys) ────────────────

    public void ZoomIn() => ZoomAroundCenter(_zoom * ZoomStep);

    public void ZoomOut() => ZoomAroundCenter(_zoom / ZoomStep);

    public void FitToWindow()
    {
        _zoom = 1;
        _offset = default;
        ApplyTransforms();
    }

    /// <summary>One pixel of the result (or of the original while there is no result) = one screen pixel.</summary>
    public void ActualSize() => ZoomAroundCenter(ActualSizeZoom());

    // ── Sources ─────────────────────────────────────────────────────

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (CompareView)d;
        var bitmap = (BitmapSource?)e.NewValue;
        if (e.Property == BeforeSourceProperty)
        {
            var old = (BitmapSource?)e.OldValue;
            view.BeforeImage.Source = bitmap;
            SetPixelSize(view.BeforeContent, bitmap);
            // A new image starts at "fit to window"; for the same frame (for example, after processing) the zoom is kept.
            if (bitmap is null || old is null || old.PixelWidth != bitmap.PixelWidth || old.PixelHeight != bitmap.PixelHeight)
            {
                view._zoom = 1;
                view._offset = default;
            }
        }
        else
        {
            view.AfterImage.Source = bitmap;
            SetPixelSize(view.AfterImage, bitmap);
        }
        view.UpdateLayoutMode();
        view.UpdateTiles();
    }

    private static void SetPixelSize(FrameworkElement element, BitmapSource? bitmap)
    {
        element.Width = bitmap?.PixelWidth ?? double.NaN;
        element.Height = bitmap?.PixelHeight ?? double.NaN;
    }

    // ── Layout ──────────────────────────────────────────────────────

    private void UpdateLayoutMode()
    {
        bool side = HasAfter && _sideBySide;
        LeftColumn.Width = new GridLength(1, GridUnitType.Star);
        GapColumn.Width = new GridLength(side ? SideGap : 0);
        RightColumn.Width = side ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        Grid.SetColumnSpan(BeforeLayer, side ? 1 : 3);
        Grid.SetColumn(AfterLayer, side ? 2 : 0);
        Grid.SetColumnSpan(AfterLayer, side ? 1 : 3);
        Grid.SetColumnSpan(Divider, 3);
        Grid.SetColumnSpan(BeforeChip, side ? 1 : 3);
        Grid.SetColumn(AfterChip, side ? 2 : 0);
        Grid.SetColumnSpan(AfterChip, side ? 1 : 3);

        AfterLayer.Visibility = HasAfter ? Visibility.Visible : Visibility.Collapsed;
        Divider.Visibility = IsSplit ? Visibility.Visible : Visibility.Collapsed;
        Toolbar.Visibility = HasBefore ? Visibility.Visible : Visibility.Collapsed;
        ModeButtons.IsEnabled = HasAfter;
        ModeButtons.Opacity = HasAfter ? 1 : 0.4;
        ModeButtons.ToolTip = HasAfter ? null : "Comparison becomes available after processing";

        BeforeChipText.Text = BeforeSource is { } before ? $"Original · {before.PixelWidth} × {before.PixelHeight}" : "";
        AfterChipText.Text = AfterSource is { } after ? $"Result · {after.PixelWidth} × {after.PixelHeight}" : "";
        BeforeChip.Visibility = HasBefore ? Visibility.Visible : Visibility.Collapsed;
        AfterChip.Visibility = HasAfter ? Visibility.Visible : Visibility.Collapsed;

        // Column sizes take effect on the next layout pass, so recompute everything that depends on them then.
        Dispatcher.BeginInvoke(ApplyTransforms, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => ApplyTransforms();

    private void OnBoxSizeChanged(object sender, SizeChangedEventArgs e) => ApplyTransforms();

    // ── Zoom and pan ────────────────────────────────────────────────

    /// <summary>How many screen pixels one image pixel takes at "fit to window".</summary>
    private double FitPixelScale(Viewbox box, BitmapSource? bitmap)
    {
        if (bitmap is null || bitmap.PixelWidth == 0 || box.ActualWidth <= 0)
            return 1;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return box.ActualWidth / bitmap.PixelWidth * dpi;
    }

    private (Viewbox Box, BitmapSource? Bitmap) Reference => HasAfter ? (AfterBox, AfterSource) : (BeforeBox, BeforeSource);

    private double ActualSizeZoom()
    {
        var (box, bitmap) = Reference;
        return 1 / FitPixelScale(box, bitmap);
    }

    private (double Min, double Max) ZoomLimits()
    {
        double actual = ActualSizeZoom();
        return (Math.Min(1, actual), Math.Max(32, actual * 8));
    }

    private void ZoomAroundCenter(double zoom) =>
        ZoomAt(zoom, BeforeLayer, new Point(BeforeLayer.ActualWidth / 2, BeforeLayer.ActualHeight / 2));

    /// <summary>Anchored zoom: the layer's <paramref name="anchor"/> point stays under the cursor.</summary>
    private void ZoomAt(double zoom, FrameworkElement layer, Point anchor)
    {
        if (!HasBefore)
            return;
        var (min, max) = ZoomLimits();
        zoom = Math.Clamp(zoom, min, max);
        if (Math.Abs(zoom - 1) < 0.01)
            zoom = 1;

        var box = layer == AfterLayer ? AfterBox : BeforeBox;
        // The point in Viewbox coordinates before the transform: t' = t + (s − s')·x.
        var local = layer.TranslatePoint(anchor, box);
        _offset += new Vector(local.X, local.Y) * (_zoom - zoom);
        _zoom = zoom;
        ApplyTransforms();
    }

    private void ApplyTransforms()
    {
        ClampOffset();
        foreach (var (scale, shift) in new[] { (_beforeScale, _beforeShift), (_afterScale, _afterShift) })
        {
            scale.ScaleX = scale.ScaleY = _zoom;
            shift.X = _offset.X;
            shift.Y = _offset.Y;
        }

        // At high zoom show true pixels, otherwise smooth.
        RenderOptions.SetBitmapScalingMode(BeforeImage,
            FitPixelScale(BeforeBox, BeforeSource) * _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        RenderOptions.SetBitmapScalingMode(AfterImage,
            FitPixelScale(AfterBox, AfterSource) * _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        var (box, bitmap) = Reference;
        ZoomText.Text = (FitPixelScale(box, bitmap) * _zoom * 100).ToString("0", CultureInfo.CurrentCulture) + "%";

        UpdateSplit();
        UpdateChips();
        UpdateTileOutline();
    }

    /// <summary>Keeps the image from being dragged past the edge: a large one fills the layer, a small one stays centered.</summary>
    private void ClampOffset()
    {
        double layerWidth = BeforeLayer.ActualWidth, layerHeight = BeforeLayer.ActualHeight;
        double boxWidth = BeforeBox.ActualWidth, boxHeight = BeforeBox.ActualHeight;
        if (layerWidth <= 0 || boxWidth <= 0)
            return;

        _offset = new Vector(Clamp(_offset.X, layerWidth, boxWidth), Clamp(_offset.Y, layerHeight, boxHeight));

        double Clamp(double shift, double layer, double box)
        {
            double origin = (layer - box) / 2;
            double scaled = box * _zoom;
            if (scaled <= layer)
                return -(scaled - box) / 2;
            return Math.Clamp(shift, layer - origin - scaled, -origin);
        }
    }

    // ── Split, chips, tiles ─────────────────────────────────────────

    private double DividerX => _split * BeforeLayer.ActualWidth;

    private void UpdateSplit()
    {
        if (!IsSplit)
        {
            AfterLayer.Clip = null;
            return;
        }
        double x = DividerX;
        AfterLayer.Clip = new RectangleGeometry(new Rect(x, 0, Math.Max(0, AfterLayer.ActualWidth - x), AfterLayer.ActualHeight));
        Divider.Margin = new Thickness(x - Divider.Width / 2, 0, 0, 0);
    }

    /// <summary>The size chips stay at the visible corners of the image.</summary>
    private void UpdateChips()
    {
        if (!HasBefore || Stage.ActualWidth <= 0)
            return;

        var beforeRect = VisibleRect(BeforeBox, BeforeLayer);
        BeforeChip.Margin = new Thickness(beforeRect.Left + 10, beforeRect.Top + 10, 0, 0);
        if (HasAfter)
        {
            // In split view "Result" is pinned to the image's right edge with a right margin, so the chip width isn't needed.
            var afterRect = VisibleRect(AfterBox, AfterLayer);
            AfterChip.HorizontalAlignment = _sideBySide ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            AfterChip.Margin = _sideBySide
                ? new Thickness(afterRect.Left + 10, afterRect.Top + 10, 0, 0)
                : new Thickness(0, afterRect.Top + 10, Math.Max(0, AfterLayer.ActualWidth - afterRect.Right) + 10, 0);
        }
    }

    /// <summary>The visible part of the image in its layer's coordinates.</summary>
    private static Rect VisibleRect(Viewbox box, FrameworkElement layer)
    {
        if (box.ActualWidth <= 0)
            return new Rect(0, 0, layer.ActualWidth, layer.ActualHeight);
        var bounds = box.TransformToVisual(layer).TransformBounds(new Rect(box.RenderSize));
        bounds.Intersect(new Rect(0, 0, layer.ActualWidth, layer.ActualHeight));
        return bounds.IsEmpty ? new Rect(0, 0, layer.ActualWidth, layer.ActualHeight) : bounds;
    }

    private void UpdateTiles()
    {
        if (Tiles is not { } tiles || BeforeSource is not { } bitmap)
        {
            TileShade.Visibility = Visibility.Collapsed;
            TileOutline.Visibility = Visibility.Collapsed;
            return;
        }

        double width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        // Even-odd fill: finished tiles are "holes" in the shading.
        var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
        shade.Children.Add(new RectangleGeometry(new Rect(0, 0, width, height)));
        foreach (var tile in tiles.Done)
            shade.Children.Add(new RectangleGeometry(Scale(tile, width, height)));
        shade.Freeze();
        TileShade.Data = shade;
        TileShade.Visibility = Visibility.Visible;

        if (tiles.Current is { } current)
        {
            TileOutline.Data = new RectangleGeometry(Scale(current, width, height));
            TileOutline.Visibility = Visibility.Visible;
            UpdateTileOutline();
        }
        else
        {
            TileOutline.Visibility = Visibility.Collapsed;
        }

        static Rect Scale(Rect r, double w, double h) => new(r.X * w, r.Y * h, r.Width * w, r.Height * h);
    }

    private void UpdateTileOutline()
    {
        // The last tile's outline is about 2 screen pixels at any zoom.
        double pixelScale = FitPixelScale(BeforeBox, BeforeSource) * _zoom;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        TileOutline.StrokeThickness = pixelScale > 0 ? 2 * dpi / pixelScale : 2;
    }

    // ── Mouse ───────────────────────────────────────────────────────

    private bool IsNearDivider(Point stagePoint) => IsSplit && Math.Abs(stagePoint.X - DividerX) <= DividerGrip;

    private FrameworkElement LayerAt(Point stagePoint) =>
        _sideBySide && HasAfter && stagePoint.X > BeforeLayer.ActualWidth + SideGap / 2 ? AfterLayer : BeforeLayer;

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (!HasBefore || e.OriginalSource is DependencyObject source && IsInToolbar(source))
            return;

        var point = e.GetPosition(Stage);
        if (e.ClickCount == 2)
        {
            // Double click: "fit to window" ↔ 1:1 at the clicked point. For a small image 1:1 is below "fit" (zoom < 1),
            // so any departure from "fit" counts, not just IsZoomed.
            var layer = LayerAt(point);
            if (Math.Abs(_zoom - 1) > 0.01)
                FitToWindow();
            else
                ZoomAt(ActualSizeZoom(), layer, e.GetPosition(layer));
            return;
        }

        if (IsSplit && (IsNearDivider(point) || !IsZoomed))
        {
            _draggingDivider = true;
            MoveDivider(point.X);
        }
        else if (IsZoomed)
        {
            _panOrigin = e.GetPosition(Root);
            Cursor = Cursors.ScrollAll;
        }
        Root.CaptureMouse();
    }

    private void OnPanDown(object sender, MouseButtonEventArgs e) => StartPan(e);

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
            StartPan(e);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
            OnButtonUp(sender, e);
    }

    private void StartPan(MouseButtonEventArgs e)
    {
        if (!IsZoomed)
            return;
        _panOrigin = e.GetPosition(Root);
        Cursor = Cursors.ScrollAll;
        Root.CaptureMouse();
    }

    private void OnButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggingDivider = false;
        _panOrigin = null;
        Root.ReleaseMouseCapture();
        UpdateCursor(e.GetPosition(Stage));
    }

    /// <summary>
    /// Capture can be lost without a button-up (Alt+Tab, a message box): end the drag, otherwise the divider or the image
    /// would keep following the mouse with no button pressed.
    /// </summary>
    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (e.OriginalSource != Root)
            return;
        _draggingDivider = false;
        _panOrigin = null;
        Cursor = null;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(Stage);
        if (_draggingDivider)
        {
            MoveDivider(point.X);
        }
        else if (_panOrigin is { } origin)
        {
            var current = e.GetPosition(Root);
            _offset += current - origin;
            _panOrigin = current;
            ApplyTransforms();
        }
        else
        {
            UpdateCursor(point);
        }
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_draggingDivider && _panOrigin is null)
            Cursor = null;
    }

    private void UpdateCursor(Point stagePoint)
    {
        if (!HasBefore)
            Cursor = null;
        else if (IsNearDivider(stagePoint))
            Cursor = Cursors.SizeWE;
        else
            Cursor = IsZoomed ? Cursors.Hand : null;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!HasBefore)
            return;
        var layer = LayerAt(e.GetPosition(Stage));
        ZoomAt(_zoom * Math.Pow(ZoomStep, e.Delta / 120.0), layer, e.GetPosition(layer));
        e.Handled = true;
    }

    private void MoveDivider(double x)
    {
        double width = BeforeLayer.ActualWidth;
        _split = width > 0 ? Math.Clamp(x / width, 0, 1) : 0.5;
        UpdateSplit();
        UpdateChips();
    }

    private bool IsInToolbar(DependencyObject source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == Toolbar)
                return true;
            if (node is not Visual)
                break;
        }
        return false;
    }

    // ── Toolbar ─────────────────────────────────────────────────────

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomIn();

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomOut();

    private void OnFitClick(object sender, RoutedEventArgs e) => FitToWindow();

    private void OnActualSizeClick(object sender, RoutedEventArgs e) => ActualSize();

    private void OnModeChecked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
            return;
        _sideBySide = SideModeButton.IsChecked == true;
        UpdateLayoutMode();
    }
}
