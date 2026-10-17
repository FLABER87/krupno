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

    /// <summary>A length for one side of the result instead of <see cref="Scale"/>: the scale is worked out for each image,
    /// so every image of a batch comes out with that side exactly as long.</summary>
    public ScaleTarget? Target { get; init; }

    /// <summary>The window (network input) side in source pixels; 0 means no tiling. Determines VRAM usage.</summary>
    public int TileSize { get; init; } = 384;

    /// <summary>Context around each tile in source pixels; removes seams where tiles meet.
    /// For RealESRGAN_x4plus: 16 px leaves up to 30 levels at a seam, 32 px up to 9 on 0.01% of pixels, 64 px is indistinguishable from an untiled run.</summary>
    public int TilePadding { get; init; } = 32;

    /// <summary>How much of the network's result goes into the output, above 0 and up to 1. The rest is a plain Lanczos resize of
    /// the source: below 1 it tones down invented textures and oversharpening.</summary>
    public double Strength { get; init; } = 1;

    /// <summary>The final scale for an image of this size: from <see cref="Target"/>, <see cref="Scale"/> or the model's own.</summary>
    public double ScaleFor(int width, int height, int modelScale) =>
        Target?.ScaleFor(width, height) ?? (Scale > 0 ? Scale : modelScale);
}

/// <summary>A finished tile as fractions of the current pass's image sides (0..1), for highlighting in the preview.</summary>
public readonly record struct TileRegion(double Left, double Top, double Width, double Height);

/// <param name="Region">The tile that was just processed; null for a message or the start of a pass.</param>
/// <param name="IsWarning">The message reports something the user should know about (the tile was reduced, memory ran out),
/// not just the next stage of the work.</param>
public readonly record struct UpscaleProgress(int Pass, int PassCount, int Tile, int TileCount, string? Message = null,
    TileRegion? Region = null, bool IsWarning = false)
{
    public double Fraction => TileCount == 0 ? 0 : (Pass - 1 + (double)Tile / TileCount) / PassCount;
}

public sealed class Upscaler(UpscaleModel model)
{
    private const int MinimumTileSize = 128;
    private const int ShrinkGrid = 64;
    private const int FineShrinkLimit = 512;

    /// <summary>The tile chosen after running out of memory has already been reported: once per batch, not for every frame and pass.</summary>
    private bool _tileLimitReported;

    public Task<ImageBuffer> UpscaleAsync(ImageBuffer input, UpscaleOptions? options = null,
        IProgress<UpscaleProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Upscale(input, options, progress, cancellationToken), cancellationToken);

    /// <summary>The size of the result for an image of the given size.</summary>
    public (long Width, long Height) OutputSize(int width, int height, UpscaleOptions? options = null) =>
        OutputSize(width, height, (options ?? UpscaleOptions.Default).ScaleFor(width, height, model.Info.Scale));

    /// <summary>The size of an image of the given size scaled by <paramref name="scale"/>, rounded the way processing rounds it.</summary>
    public static (long Width, long Height) OutputSize(int width, int height, double scale) =>
        (Math.Max(1, (long)Math.Round(width * scale)), Math.Max(1, (long)Math.Round(height * scale)));

    public ImageBuffer Upscale(ImageBuffer input, UpscaleOptions? options = null,
        IProgress<UpscaleProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= UpscaleOptions.Default;
        if (!(options.Strength > 0 && options.Strength <= 1))
            throw new ArgumentOutOfRangeException(nameof(options), options.Strength, "The strength must be above 0 and at most 1.");
        int modelScale = model.Info.Scale;
        double scale = options.ScaleFor(input.Width, input.Height, modelScale);

        var (targetWidth, targetHeight) = OutputSize(input.Width, input.Height, options);
        if (!ImageBuffer.CanAllocate(targetWidth, targetHeight))
            throw new InvalidOperationException($"The result {targetWidth}×{targetHeight} is too large to process in memory.");

        // The network doesn't see alpha: it is resized separately, which is cheap. The colors under fully transparent pixels
        // are filled from the visible ones first, otherwise the (usually black) color stored there bleeds into the edges.
        byte[]? alpha = input.HasTransparency() ? input.ExtractAlpha() : null;
        int passes = PassCount(scale, modelScale);
        var source = alpha is null ? input : input.BleedTransparentColors();
        var current = source;
        for (int pass = 1; pass <= passes; pass++)
        {
            if (pass == passes)
                current = FitLastPass(current, alpha, input, targetWidth, targetHeight, pass, passes, progress, cancellationToken);
            current = RunPass(current, options, pass, passes, progress, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (current.Width != targetWidth || current.Height != targetHeight)
        {
            progress?.Report(new UpscaleProgress(passes, passes, 1, 1, $"Resizing to {targetWidth}×{targetHeight}"));
            if (alpha is null)
            {
                current = Resampler.Resize(current, (int)targetWidth, (int)targetHeight, cancellationToken);
            }
            else
            {
                // Colors are filtered weighted by alpha, so a nearly transparent pixel doesn't pull its color into its neighbors.
                // The alpha comes out of the same filter and is kept: every color then matches its alpha.
                current.SetAlpha(Resampler.ResizePlane(alpha, input.Width, input.Height, current.Width, current.Height, cancellationToken));
                current = Resampler.ResizePremultiplied(current, (int)targetWidth, (int)targetHeight, cancellationToken);
            }
        }
        else if (alpha is not null)
        {
            current.SetAlpha(Resampler.ResizePlane(alpha, input.Width, input.Height, current.Width, current.Height, cancellationToken));
        }

        if (options.Strength < 1)
        {
            // The plain resize starts from the pixels the network saw, with the colors under transparency filled in the same way.
            // The alpha computed above stays.
            progress?.Report(new UpscaleProgress(passes, passes, 1, 1,
                $"Blending with a plain resize, strength {(int)Math.Round(options.Strength * 100)}%"));
            Resampler.BlendResized(source, current, (float)options.Strength, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return current;
    }

    internal static int PassCount(double scale, int modelScale)
    {
        if (modelScale <= 1)
            return 1;

        // No cap on the number of passes: the size checks stop an absurd scale, and a cap would quietly replace the network with Lanczos.
        int passes = 1;
        for (double reached = modelScale; reached < scale - 1e-6; reached *= modelScale)
            passes++;
        return passes;
    }

    /// <summary>
    /// Before the last pass: if its full result doesn't fit in memory (×8 with a ×4 model makes a ×16 intermediate, 2.4 GB for
    /// a 1920×1200 photo; ×2 of an 8000×6000 photo makes a ×4 one), the image is first reduced so that the last pass lands just
    /// above the target. The network then sees a reduced image, which is not the same as reducing its result, so this is only a fallback.
    /// </summary>
    /// <param name="alpha">The source's alpha, null for an opaque image.</param>
    private ImageBuffer FitLastPass(ImageBuffer source, byte[]? alpha, ImageBuffer input, long targetWidth, long targetHeight,
        int pass, int passCount, IProgress<UpscaleProgress>? progress, CancellationToken cancellationToken)
    {
        int scale = model.Info.Scale;
        if (ImageBuffer.CanAllocate((long)source.Width * scale, (long)source.Height * scale))
            return source;

        // Rounding up lands the pass at or above the target, so the final resize only reduces. Right at the memory limit that
        // may not fit (40000×3355 at ×2 with a ×4 model), then rounding down does: it stays within the target, which fits.
        int width = (int)Math.Min(source.Width, (targetWidth + scale - 1) / scale);
        int height = (int)Math.Min(source.Height, (targetHeight + scale - 1) / scale);
        if (!ImageBuffer.CanAllocate((long)width * scale, (long)height * scale))
        {
            width = (int)Math.Max(1, targetWidth / scale);
            height = (int)Math.Max(1, targetHeight / scale);
        }
        progress?.Report(new UpscaleProgress(pass, passCount, 0, 1,
            $"Pass {pass} does not fit in memory at full size, reducing its input to {width}×{height}", IsWarning: true));
        if (alpha is null)
            return Resampler.Resize(source, width, height, cancellationToken);

        // With transparency the reduction weights colors by alpha as well, and the colors it leaves under fully transparent
        // pixels are filled again. The first pass gets the source with its own alpha; a later one gets the network's result,
        // which is opaque, so it is given the source's alpha at its size first.
        if (pass > 1)
            source.SetAlpha(Resampler.ResizePlane(alpha, input.Width, input.Height, source.Width, source.Height, cancellationToken));
        return Resampler.ResizePremultiplied(source, width, height, cancellationToken).BleedTransparentColors();
    }

    private ImageBuffer RunPass(ImageBuffer source, UpscaleOptions options, int pass, int passCount,
        IProgress<UpscaleProgress>? progress, CancellationToken cancellationToken)
    {
        int scale = model.Info.Scale;
        if (!ImageBuffer.CanAllocate((long)source.Width * scale, (long)source.Height * scale))
            throw new InvalidOperationException("The intermediate result is too large to process in memory.");

        int tileSize = StartTile(source, options.TileSize, pass, passCount, progress);
        while (true)
        {
            var device = model.Device;
            var (width, height) = model.ChooseWindow(source.Width, source.Height, tileSize);
            try
            {
                using var runner = new TileRunner(model, width, height);
                if (model.Device != device)
                {
                    // Creating the session fell back to the CPU after the GPU had already reduced the tile: the CPU has no
                    // such limit, so start over from the requested tile.
                    int requested = StartTile(source, options.TileSize, pass, passCount, progress);
                    if (model.ChooseWindow(source.Width, source.Height, requested) != (width, height))
                    {
                        tileSize = requested;
                        continue;
                    }
                }
                return RunTiles(runner, source, options.TilePadding, pass, passCount, progress, cancellationToken);
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
                    $"Window {width}×{height} did not fit on the device, reducing the tile to {tileSize} px", IsWarning: true));
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
            progress?.Report(new UpscaleProgress(pass, passCount, 0, 1, $"Tile {limit} px (chosen after running out of memory)",
                IsWarning: true));
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

    private ImageBuffer RunTiles(TileRunner runner, ImageBuffer source, int padding,
        int pass, int passCount, IProgress<UpscaleProgress>? progress, CancellationToken cancellationToken)
    {
        int scale = model.Info.Scale;
        // Each axis keeps its own context: a thin banner's short window must not cut the context along its long side.
        int paddingX = Math.Clamp(padding, 0, runner.Width / 4);
        int paddingY = Math.Clamp(padding, 0, runner.Height / 4);
        var columns = TilePlanner.PlanAxis(source.Width, runner.Width, paddingX);
        var rows = TilePlanner.PlanAxis(source.Height, runner.Height, paddingY);
        var result = new ImageBuffer(source.Width * scale, source.Height * scale);

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
