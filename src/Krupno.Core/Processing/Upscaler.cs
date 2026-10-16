using Microsoft.ML.OnnxRuntime;
using Krupno.Core.Imaging;
using Krupno.Core.Inference;

namespace Krupno.Core.Processing;

public sealed record UpscaleOptions
{
    public static UpscaleOptions Default { get; } = new();

    /// <summary>The final scale; 0 means the model's native scale. A fractional or larger scale is reached with
    /// several network passes and an exact final resize with a Lanczos filter.</summary>
    public double Scale { get; init; }

    /// <summary>The window (network input) side in source pixels; 0 means no tiling. Determines VRAM usage.</summary>
    public int TileSize { get; init; } = 384;

    /// <summary>Context around each tile in source pixels; removes seams where tiles meet.
    /// For RealESRGAN_x4plus: 16 px leaves up to 30 levels at a seam, 32 px up to 9 on 0.01% of pixels, 64 px is indistinguishable from an untiled run.</summary>
    public int TilePadding { get; init; } = 32;
}

/// <summary>A finished tile as fractions of the current pass's image sides (0..1), for highlighting in the preview.</summary>
public readonly record struct TileRegion(double Left, double Top, double Width, double Height);

/// <param name="Region">The tile that was just processed; null for a message or the start of a pass.</param>
public readonly record struct UpscaleProgress(int Pass, int PassCount, int Tile, int TileCount, string? Message = null,
    TileRegion? Region = null)
{
    public double Fraction => TileCount == 0 ? 0 : (Pass - 1 + (double)Tile / TileCount) / PassCount;
}

public sealed class Upscaler(UpscaleModel model)
{
    private const int MinimumTileSize = 128;
    private const int ShrinkGrid = 64;
    private const int FineShrinkLimit = 512;
    private const int MaxPasses = 3;

    /// <summary>The tile chosen after running out of memory has already been reported: once per batch, not for every frame and pass.</summary>
    private bool _tileLimitReported;

    public Task<ImageBuffer> UpscaleAsync(ImageBuffer input, UpscaleOptions? options = null,
        IProgress<UpscaleProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Upscale(input, options, progress, cancellationToken), cancellationToken);

    public ImageBuffer Upscale(ImageBuffer input, UpscaleOptions? options = null,
        IProgress<UpscaleProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= UpscaleOptions.Default;
        int modelScale = model.Info.Scale;
        double scale = options.Scale > 0 ? options.Scale : modelScale;

        long targetWidth = Math.Max(1, (long)Math.Round(input.Width * scale));
        long targetHeight = Math.Max(1, (long)Math.Round(input.Height * scale));
        if (!ImageBuffer.CanAllocate(targetWidth, targetHeight))
            throw new InvalidOperationException($"The result {targetWidth}×{targetHeight} is too large to process in memory.");

        int passes = PassCount(scale, modelScale);
        var current = input;
        for (int pass = 1; pass <= passes; pass++)
            current = RunPass(current, options, pass, passes, progress, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (current.Width != targetWidth || current.Height != targetHeight)
        {
            progress?.Report(new UpscaleProgress(passes, passes, 1, 1, $"Resizing to {targetWidth}×{targetHeight}"));
            current = Resampler.Resize(current, (int)targetWidth, (int)targetHeight);
        }

        // The network doesn't see alpha: resize it separately, which is cheap and avoids halos.
        if (input.HasTransparency())
            current.SetAlpha(Resampler.ResizePlane(input.ExtractAlpha(), input.Width, input.Height, current.Width, current.Height));

        return current;
    }

    private static int PassCount(double scale, int modelScale)
    {
        if (modelScale <= 1)
            return 1;

        int passes = 1;
        for (double reached = modelScale; reached < scale - 1e-6 && passes < MaxPasses; reached *= modelScale)
            passes++;
        return passes;
    }

    private ImageBuffer RunPass(ImageBuffer source, UpscaleOptions options, int pass, int passCount,
        IProgress<UpscaleProgress>? progress, CancellationToken cancellationToken)
    {
        int tileSize = StartTile(source, options.TileSize, pass, passCount, progress);
        while (true)
        {
            var (width, height) = model.ChooseWindow(source.Width, source.Height, tileSize);
            try
            {
                return RunTiles(source, width, height, options.TilePadding, pass, passCount, progress, cancellationToken);
            }
            catch (OnnxRuntimeException) when (!cancellationToken.IsCancellationRequested &&
                                               SmallerTile(source, width, height) is int smaller)
            {
                // Usually this means out of VRAM or the D3D12 resource size limit: shrink the window and try again.
                tileSize = smaller;
                model.LimitTile(tileSize);
                model.ReleaseSessions();
                _tileLimitReported = true;
                progress?.Report(new UpscaleProgress(pass, passCount, 0, 1,
                    $"Window {width}×{height} did not fit on the device, reducing the tile to {tileSize} px"));
            }
        }
    }

    /// <summary>The requested tile, but no larger than the one chosen on previous frames and passes after running out of memory.</summary>
    private int StartTile(ImageBuffer source, int requested, int pass, int passCount, IProgress<UpscaleProgress>? progress)
    {
        // The limit is a tile side, not a window: the second pass runs on a larger image and the window never extends past the image,
        // so ChooseWindow determines the window for it again.
        if (model.TileLimit is not int limit || (requested > 0 && requested <= limit))
            return requested;

        // On a small image the window is within the limit anyway, so there is nothing to report.
        if (!_tileLimitReported &&
            model.ChooseWindow(source.Width, source.Height, limit) != model.ChooseWindow(source.Width, source.Height, requested))
        {
            _tileLimitReported = true;
            progress?.Report(new UpscaleProgress(pass, passCount, 0, 1, $"Tile {limit} px (chosen after running out of memory)"));
        }
        return limit;
    }

    /// <summary>The tile for the next attempt after running out of memory; null if it can't be shrunk any further.</summary>
    private int? SmallerTile(ImageBuffer source, int width, int height)
    {
        int side = Math.Max(width, height);
        if (model.Info.FixedWidth is not null || side <= MinimumTileSize)
            return null;

        // Up to 512 px, step down a 64 px grid (a multiple of the model's): 512 → 448 → 384 → 320 → 256. Halving skips past
        // a size that fits: DAT2 fp32 on 10 GB dropped from 384 straight to 192, and a frame took 71 s versus 61 s when falling back to 320.
        // A finer step isn't needed: DirectML silently spills a window just below the one that didn't fit (352 for the same DAT2) to shared memory.
        // A large window is halved, but not below 512: every failed attempt costs a graph compilation (~10 s for DAT2).
        int grid = TilePlanner.AlignUp(ShrinkGrid, model.Info.SizeMultiple);
        int tile = side > FineShrinkLimit
            ? Math.Max(FineShrinkLimit, TilePlanner.AlignDown(side / 2, grid))
            : Math.Max(MinimumTileSize, TilePlanner.AlignDown(side - 1, grid));

        // The model's minimum may keep the window from shrinking; then retrying is pointless.
        var (nextWidth, nextHeight) = model.ChooseWindow(source.Width, source.Height, tile);
        return Math.Max(nextWidth, nextHeight) < side ? tile : null;
    }

    private ImageBuffer RunTiles(ImageBuffer source, int windowWidth, int windowHeight, int padding,
        int pass, int passCount, IProgress<UpscaleProgress>? progress, CancellationToken cancellationToken)
    {
        int scale = model.Info.Scale;
        if (!ImageBuffer.CanAllocate((long)source.Width * scale, (long)source.Height * scale))
            throw new InvalidOperationException("The intermediate result is too large to process in memory.");

        padding = Math.Clamp(padding, 0, Math.Min(windowWidth, windowHeight) / 4);
        var columns = TilePlanner.PlanAxis(source.Width, windowWidth, padding);
        var rows = TilePlanner.PlanAxis(source.Height, windowHeight, padding);
        var result = new ImageBuffer(source.Width * scale, source.Height * scale);

        using var runner = new TileRunner(model, windowWidth, windowHeight);
        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(() => runOptions.Terminate = true);

        int total = columns.Length * rows.Length, done = 0;
        progress?.Report(new UpscaleProgress(pass, passCount, 0, total));

        foreach (var row in rows)
        {
            foreach (var column in columns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                runner.Fill(source, column.WindowStart, row.WindowStart);
                try
                {
                    runner.Run(runOptions);
                }
                catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                runner.CopyCore(result, column, row);
                var region = new TileRegion(
                    (double)column.CoreStart / source.Width, (double)row.CoreStart / source.Height,
                    (double)column.CoreLength / source.Width, (double)row.CoreLength / source.Height);
                progress?.Report(new UpscaleProgress(pass, passCount, ++done, total, Region: region));
            }
        }
        return result;
    }
}
