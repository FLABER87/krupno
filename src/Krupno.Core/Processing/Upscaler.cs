using Microsoft.ML.OnnxRuntime;
using Krupno.Core.Imaging;
using Krupno.Core.Inference;
using Krupno.Core.Properties;

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

    /// <summary>Test-time augmentation: every pass runs on the image in all 8 rotations and mirrorings, and the results, turned
    /// back, are averaged. Eight times slower; evens out noise and the network's preference for some directions.</summary>
    public bool TestTimeAugmentation { get; init; }

    /// <summary>The final scale for an image of this size: from <see cref="Target"/>, <see cref="Scale"/> or the model's own.</summary>
    public double ScaleFor(int width, int height, int modelScale) =>
        Target?.ScaleFor(width, height) ?? (Scale > 0 ? Scale : modelScale);
}

/// <summary>A finished tile as fractions of the current pass's image sides (0..1), for highlighting in the preview.</summary>
public readonly record struct TileRegion(double Left, double Top, double Width, double Height);

/// <param name="Region">The tile that was just processed; null for a message or the start of a pass or round.</param>
/// <param name="Round">With test-time augmentation, which of the <paramref name="RoundCount"/> orientations of the pass is running;
/// each round goes through all the tiles again.</param>
/// <param name="IsWarning">The message reports something the user should know about (the tile was reduced, memory ran out),
/// not just the next stage of the work.</param>
/// <param name="Denoising">The pass removes noise before the image is upscaled; it is the first of the <paramref name="PassCount"/>.</param>
public readonly record struct UpscaleProgress(int Pass, int PassCount, int Tile, int TileCount, string? Message = null,
    TileRegion? Region = null, bool IsWarning = false, int Round = 1, int RoundCount = 1, bool Denoising = false)
{
    public double Fraction => TileCount == 0 ? 0 : (Pass - 1 + (Round - 1 + (double)Tile / TileCount) / RoundCount) / PassCount;
}

/// <param name="denoiser">
/// A ×1 model that removes noise from the image before it is upscaled (<see cref="ModelCatalog.DenoiseModelName"/>), with the
/// same tiling as the upscaler; null to upscale the image as it is. At a scale of 1 or less only the denoiser runs.
/// </param>
public sealed class Upscaler(UpscaleModel model, UpscaleModel? denoiser = null)
{
    private const int MinimumTileSize = 128;
    private const int ShrinkGrid = 64;
    private const int FineShrinkLimit = 512;
    /// <summary>The rounds of test-time augmentation: the 8 orientations.</summary>
    private const int RoundCount = 8;
    /// <summary>
    /// The sessions the model keeps with test-time augmentation: a window and its transpose in each of two passes. With only two,
    /// a second pass would push out the first one's and every image would compile them again (about 10 s each for DAT2).
    /// </summary>
    private const int AugmentedSessionCapacity = 4;

    /// <summary>The models whose tile chosen after running out of memory has already been reported: once per batch, not for every
    /// frame and pass.</summary>
    private readonly HashSet<UpscaleModel> _tileLimitReported = [];

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
        if (denoiser is { Info.Scale: not 1 })
            throw new NotSupportedException(string.Format(Strings.Model_DenoiserScale, denoiser.Info.Name, denoiser.Info.Scale));
        int modelScale = model.Info.Scale;
        double scale = options.ScaleFor(input.Width, input.Height, modelScale);

        var (targetWidth, targetHeight) = OutputSize(input.Width, input.Height, options);
        if (!ImageBuffer.CanAllocate(targetWidth, targetHeight))
            throw new InvalidOperationException(string.Format(Strings.Error_ResultTooLarge, targetWidth, targetHeight));
        int capacity = options.TestTimeAugmentation ? AugmentedSessionCapacity : UpscaleModel.DefaultSessionCapacity;
        model.SetSessionCapacity(capacity);
        denoiser?.SetSessionCapacity(capacity);

        // The network doesn't see alpha: it is resized separately, which is cheap. The colors under fully transparent pixels
        // are filled from the visible ones first, otherwise the (usually black) color stored there bleeds into the edges.
        byte[]? alpha = input.HasTransparency() ? input.ExtractAlpha() : null;
        // With noise removal and no enlargement only the denoiser runs: the upscaler would invent detail that the reduction then loses.
        int passes = denoiser is not null && scale <= 1 ? 0 : PassCount(scale, modelScale);
        // The noise removal is a pass of its own, the first one, so the progress covers it.
        int passCount = passes + (denoiser is null ? 0 : 1);
        var source = alpha is null ? input : input.BleedTransparentColors();
        var current = source;
        int pass = 0;
        if (denoiser is not null)
        {
            current = RunPass(denoiser, current, options, ++pass, passCount,
                progress is null ? null : new DenoisingProgress(progress), cancellationToken);
            // The plain resize of Strength starts from the pixels the upscaler sees, so the noise doesn't come back with it.
            // Without an upscaling pass it starts from the source: Strength then tones down the noise removal itself.
            if (passes > 0)
                source = current;
        }
        for (int upscalePass = 1; upscalePass <= passes; upscalePass++)
        {
            pass++;
            if (upscalePass == passes)
                current = FitLastPass(current, alpha, input, targetWidth, targetHeight, pass, passCount, progress, cancellationToken);
            current = RunPass(model, current, options, pass, passCount, progress, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (current.Width != targetWidth || current.Height != targetHeight)
        {
            progress?.Report(new UpscaleProgress(passCount, passCount, 1, 1, string.Format(Strings.Progress_Resizing, targetWidth, targetHeight)));
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
            // The plain resize starts from the pixels the upscaler saw (the denoised ones with noise removal), with the colors
            // under transparency filled in the same way. The alpha computed above stays.
            progress?.Report(new UpscaleProgress(passCount, passCount, 1, 1,
                string.Format(Strings.Progress_Blending, (int)Math.Round(options.Strength * 100))));
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
            string.Format(Strings.Progress_PassReduced, pass, width, height), IsWarning: true));
        if (alpha is null)
            return Resampler.Resize(source, width, height, cancellationToken);

        // With transparency the reduction weights colors by alpha as well, and the colors it leaves under fully transparent
        // pixels are filled again. The first pass gets the source with its own alpha; a later one, the noise removal's included,
        // gets a network's result, which is opaque, so it is given the source's alpha at its size first.
        if (pass > 1)
            source.SetAlpha(Resampler.ResizePlane(alpha, input.Width, input.Height, source.Width, source.Height, cancellationToken));
        return Resampler.ResizePremultiplied(source, width, height, cancellationToken).BleedTransparentColors();
    }

    /// <param name="net">The upscaler, or the denoiser for the noise removal pass.</param>
    private ImageBuffer RunPass(UpscaleModel net, ImageBuffer source, UpscaleOptions options, int pass, int passCount,
        IProgress<UpscaleProgress>? progress, CancellationToken cancellationToken)
    {
        if (!options.TestTimeAugmentation)
            return RunOrientation(net, source, options, pass, passCount, progress, cancellationToken);

        // A quarter turn transposes the intermediate, and the size check isn't symmetric (WIC's row limit): check the transposed
        // one as well before the first four rounds, rather than fail on the fifth.
        int scale = net.Info.Scale;
        long width = (long)source.Width * scale, height = (long)source.Height * scale;
        if (!ImageBuffer.CanAllocate(width, height) || !ImageBuffer.CanAllocate(height, width))
            throw new InvalidOperationException(Strings.Error_IntermediateTooLarge);

        // The EXIF orientations 1–8 are exactly the 8 rotations and mirrorings. Each round's result is added up turned back; the
        // network output is opaque, so only the colors are summed (8 × 255 fits in a ushort). A result is read through the
        // orientation's pixel mapping instead of being turned back as a copy, so a round holds its result (4 bytes an output
        // pixel) and the sum (6), not another 4 for the copy: for ×16 of a 1600×1000 photo that is 4.1 GB rather than 5.7 GB.
        var sum = new ushort[width * height * 3];
        for (int orientation = 1; orientation <= RoundCount; orientation++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddRound(net, source, orientation, options, pass, passCount, progress, sum, cancellationToken);
        }

        var average = new ImageBuffer((int)width, (int)height);
        Average(sum, RoundCount, average.Pixels);
        return average;
    }

    /// <summary>
    /// One round of test-time augmentation: the image in <paramref name="orientation"/>, upscaled, added to <paramref name="sum"/>.
    /// A method of its own, so that neither the turned image nor the result outlives the round, even before the JIT optimizes.
    /// </summary>
    private void AddRound(UpscaleModel net, ImageBuffer source, int orientation, UpscaleOptions options, int pass, int passCount,
        IProgress<UpscaleProgress>? progress, ushort[] sum, CancellationToken cancellationToken)
    {
        var turned = source.ApplyExifOrientation(orientation);
        var roundProgress = progress is null ? null : new RoundProgress(progress, orientation);
        var result = RunOrientation(net, turned, options, pass, passCount, roundProgress, cancellationToken);
        AddColors(result, InverseOrientation(orientation), sum);
    }

    /// <summary>
    /// Adds the colors of <paramref name="turned"/>, turned by EXIF orientation <paramref name="orientation"/>, to <paramref name="sum"/>
    /// (three per pixel of the turned-back image): what adding up <c>turned.ApplyExifOrientation(orientation)</c> would, without the copy.
    /// </summary>
    internal static void AddColors(ImageBuffer turned, int orientation, Span<ushort> sum)
    {
        int tw = turned.Width, th = turned.Height;
        int width = orientation >= 5 ? th : tw, height = orientation >= 5 ? tw : th;
        var px = turned.Pixels;
        for (int y = 0, s = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++, s += 3)
            {
                var (sx, sy) = ImageBuffer.SourceOf(orientation, x, y, tw, th);
                int i = (sy * tw + sx) * ImageBuffer.BytesPerPixel;
                sum[s] += px[i];
                sum[s + 1] += px[i + 1];
                sum[s + 2] += px[i + 2];
            }
        }
    }

    /// <summary>Writes the colors of <paramref name="sum"/> divided by <paramref name="count"/>, rounded to the nearest level
    /// (halves up), into BGRA pixels, opaque like the network's results.</summary>
    internal static void Average(ReadOnlySpan<ushort> sum, int count, Span<byte> bgra)
    {
        for (int i = 0, s = 0; i < bgra.Length; i += ImageBuffer.BytesPerPixel, s += 3)
        {
            bgra[i] = (byte)((sum[s] + count / 2) / count);
            bgra[i + 1] = (byte)((sum[s + 1] + count / 2) / count);
            bgra[i + 2] = (byte)((sum[s + 2] + count / 2) / count);
            bgra[i + 3] = 255;
        }
    }

    /// <summary>The orientation that turns an image in EXIF orientation <paramref name="orientation"/> back: the two quarter
    /// turns undo each other, and every other one undoes itself.</summary>
    internal static int InverseOrientation(int orientation) => orientation switch { 6 => 8, 8 => 6, _ => orientation };

    /// <summary>
    /// A tile of the turned image as a part of the image as it is (both as fractions of the sides): the inverse of the
    /// pixel mapping in <see cref="ImageBuffer.ApplyExifOrientation"/>.
    /// </summary>
    internal static TileRegion Unturn(TileRegion r, int orientation) => orientation switch
    {
        2 => r with { Left = 1 - r.Left - r.Width },
        3 => r with { Left = 1 - r.Left - r.Width, Top = 1 - r.Top - r.Height },
        4 => r with { Top = 1 - r.Top - r.Height },
        5 => new TileRegion(r.Top, r.Left, r.Height, r.Width),
        6 => new TileRegion(r.Top, 1 - r.Left - r.Width, r.Height, r.Width),
        7 => new TileRegion(1 - r.Top - r.Height, 1 - r.Left - r.Width, r.Height, r.Width),
        8 => new TileRegion(1 - r.Top - r.Height, r.Left, r.Height, r.Width),
        _ => r,
    };

    /// <summary>Marks the reports of one round and shows its tiles where they are on the image as it is.</summary>
    private sealed class RoundProgress(IProgress<UpscaleProgress> inner, int orientation) : IProgress<UpscaleProgress>
    {
        public void Report(UpscaleProgress value) => inner.Report(value with
        {
            Round = orientation,
            RoundCount = RoundCount,
            Region = value.Region is { } region ? Unturn(region, orientation) : null,
        });
    }

    /// <summary>Marks the reports of the noise removal pass.</summary>
    private sealed class DenoisingProgress(IProgress<UpscaleProgress> inner) : IProgress<UpscaleProgress>
    {
        public void Report(UpscaleProgress value) => inner.Report(value with { Denoising = true });
    }

    private ImageBuffer RunOrientation(UpscaleModel net, ImageBuffer source, UpscaleOptions options, int pass, int passCount,
        IProgress<UpscaleProgress>? progress, CancellationToken cancellationToken)
    {
        int scale = net.Info.Scale;
        if (!ImageBuffer.CanAllocate((long)source.Width * scale, (long)source.Height * scale))
            throw new InvalidOperationException(Strings.Error_IntermediateTooLarge);

        // The other model of the run keeps its sessions between images, and they hold video memory too.
        var other = net == model ? denoiser : model;
        bool OthersHoldMemory(int width, int height) => net.HoldsOtherSessions(width, height) || other is not null && other != net && other.HasSessions;

        int tileSize = StartTile(net, source, options.TileSize, pass, passCount, progress);
        bool retriedAlone = false;
        while (true)
        {
            var device = net.Device;
            var (width, height) = net.ChooseWindow(source.Width, source.Height, tileSize);
            try
            {
                using var runner = CreateRunner(net, width, height, cancellationToken);
                if (net.Device != device)
                {
                    // Creating the session fell back to the CPU after the GPU had already reduced the tile: the CPU has no
                    // such limit, so start over from the requested tile.
                    int requested = StartTile(net, source, options.TileSize, pass, passCount, progress);
                    if (net.ChooseWindow(source.Width, source.Height, requested) != (width, height))
                    {
                        tileSize = requested;
                        continue;
                    }
                }
                return RunTiles(net, runner, source, options.TilePadding, pass, passCount, progress, cancellationToken);
            }
            catch (OnnxRuntimeException ex) when (!cancellationToken.IsCancellationRequested && MayBeOutOfMemory(device, ex) &&
                                                  (!retriedAlone && OthersHoldMemory(width, height) ||
                                                   SmallerTile(net, source, width, height) is not null))
            {
                if (!retriedAlone && OthersHoldMemory(width, height))
                {
                    // The sessions of other windows (the second pass's, with test-time augmentation the transposed one's, the
                    // other model's) hold video memory too: without them this window may well fit, so it gets one more try
                    // before it shrinks, also when it can't shrink any further.
                    retriedAlone = true;
                    net.ReleaseSessions();
                    if (other != net)
                        other?.ReleaseSessions();
                    continue;
                }
                // Usually this means out of VRAM or the D3D12 resource size limit: shrink the window and try again.
                int smaller = SmallerTile(net, source, width, height)!.Value;
                tileSize = smaller;
                net.LimitTile(tileSize);
                net.ReleaseSessions();
                _tileLimitReported.Add(net);
                progress?.Report(new UpscaleProgress(pass, passCount, 0, 1,
                    string.Format(Strings.Progress_TileReduced, width, height, tileSize), IsWarning: true));
            }
        }
    }

    /// <summary>
    /// Whether a failed run or session may mean the window didn't fit. DirectML's errors can't be told apart by type, so any of
    /// them shrinks the tile (see <see cref="UpscaleModel"/>). With a vendor's provider every attempt is an engine build of seconds
    /// to minutes, so only an error that reads as out of memory does; any other one says what is wrong at once.
    /// </summary>
    private static bool MayBeOutOfMemory(ExecutionDevice device, OnnxRuntimeException ex) =>
        device.Kind != DeviceKind.Accelerator || UpscaleModel.LooksLikeOutOfMemory(ex.Message);

    /// <summary>
    /// The runner and its session for a window. Building a session can take long (DirectML compiles the graph, ~10 s for DAT2;
    /// an accelerator's engine, minutes) and ONNX Runtime can't stop it, so a canceled run doesn't wait for it: the build goes on
    /// in the background, and what it builds is kept (the model's session, an accelerator's compiled model on disk).
    /// </summary>
    private static TileRunner CreateRunner(UpscaleModel net, int width, int height, CancellationToken cancellationToken)
    {
        if (net.Device.Kind == DeviceKind.Cpu || !cancellationToken.CanBeCanceled)
            return new TileRunner(net, width, height, cancellationToken);

        var creating = Task.Run(() => new TileRunner(net, width, height, cancellationToken));
        try
        {
            creating.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Nobody waits for it any more: the runner is let go, and a failure (the model disposed meanwhile) is observed.
            _ = creating.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                    t.Result.Dispose();
                else
                    _ = t.Exception;
            }, TaskScheduler.Default);
            throw;
        }
        catch (AggregateException)
        {
            // Rethrown below as the exception the session threw, which the out-of-memory handling looks for.
        }
        return creating.GetAwaiter().GetResult();
    }

    /// <summary>The requested tile, but no larger than the one chosen on previous frames and passes after running out of memory.</summary>
    private int StartTile(UpscaleModel net, ImageBuffer source, int requested, int pass, int passCount, IProgress<UpscaleProgress>? progress)
    {
        // The limit is a tile side, not a window: the second pass runs on a larger image and the window never extends past the image,
        // so ChooseWindow determines the window for it again.
        if (net.TileLimit is not int limit || (requested > 0 && requested <= limit))
            return requested;

        // On a small image the window is within the limit anyway, so there is nothing to report.
        if (!_tileLimitReported.Contains(net) &&
            net.ChooseWindow(source.Width, source.Height, limit) != net.ChooseWindow(source.Width, source.Height, requested))
        {
            _tileLimitReported.Add(net);
            progress?.Report(new UpscaleProgress(pass, passCount, 0, 1, string.Format(Strings.Progress_TileFromLimit, limit),
                IsWarning: true));
        }
        return limit;
    }

    /// <summary>The tile for the next attempt after running out of memory; null if it can't be shrunk any further.</summary>
    private static int? SmallerTile(UpscaleModel net, ImageBuffer source, int width, int height)
    {
        int side = Math.Max(width, height);
        if (net.Info.FixedWidth is not null || side <= MinimumTileSize)
            return null;

        // Up to 512 px, step down a 64 px grid (a multiple of the model's): 512 → 448 → 384 → 320 → 256. Halving skips past
        // a size that fits: DAT2 fp32 on 10 GB dropped from 384 straight to 192, and a frame took 71 s versus 61 s when falling back to 320.
        // A finer step isn't needed: DirectML silently spills a window just below the one that didn't fit (352 for the same DAT2) to shared memory.
        // A large window is halved, but not below 512: every failed attempt costs a graph compilation (~10 s for DAT2).
        int grid = TilePlanner.AlignUp(ShrinkGrid, net.Info.SizeMultiple);
        int tile = side > FineShrinkLimit
            ? Math.Max(FineShrinkLimit, TilePlanner.AlignDown(side / 2, grid))
            : Math.Max(MinimumTileSize, TilePlanner.AlignDown(side - 1, grid));

        // The model's minimum may keep the window from shrinking; then retrying is pointless.
        var (nextWidth, nextHeight) = net.ChooseWindow(source.Width, source.Height, tile);
        return Math.Max(nextWidth, nextHeight) < side ? tile : null;
    }

    private static ImageBuffer RunTiles(UpscaleModel net, TileRunner runner, ImageBuffer source, int padding,
        int pass, int passCount, IProgress<UpscaleProgress>? progress, CancellationToken cancellationToken)
    {
        int scale = net.Info.Scale;
        // Each axis keeps its own context: a thin banner's short window must not cut the context along its long side.
        int paddingX = Math.Clamp(padding, 0, runner.Width / 4);
        int paddingY = Math.Clamp(padding, 0, runner.Height / 4);
        var columns = TilePlanner.PlanAxis(source.Width, runner.Width, paddingX);
        var rows = TilePlanner.PlanAxis(source.Height, runner.Height, paddingY);
        var result = new ImageBuffer(source.Width * scale, source.Height * scale);

        using var runOptions = new RunOptions();
        using var registration = cancellationToken.Register(() => runOptions.Terminate = true);

        var tiles = rows.SelectMany(row => columns.Select(column => (Column: column, Row: row))).ToArray();
        progress?.Report(new UpscaleProgress(pass, passCount, 0, tiles.Length));

        void Report(int index)
        {
            var (column, row) = tiles[index];
            var region = new TileRegion(
                (double)column.CoreStart / source.Width, (double)row.CoreStart / source.Height,
                (double)column.CoreLength / source.Width, (double)row.CoreLength / source.Height);
            progress?.Report(new UpscaleProgress(pass, passCount, index + 1, tiles.Length, Region: region));
        }

        runner.Fill(0, source, tiles[0].Column.WindowStart, tiles[0].Row.WindowStart);
        for (int i = 0; i < tiles.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int slot = i % TileRunner.SlotCount, other = (i + 1) % TileRunner.SlotCount;
            int next = i + 1, previous = i - 1;

            // While the session runs tile i, another thread fills the input of tile i + 1 and copies tile i - 1 into the result.
            // Done in turn, that work leaves the GPU idle between tiles, and with a fast model at ×4 it isn't small next to the run.
            var background = next < tiles.Length || previous >= 0
                ? Task.Run(() =>
                {
                    if (next < tiles.Length)
                        runner.Fill(other, source, tiles[next].Column.WindowStart, tiles[next].Row.WindowStart);
                    if (previous >= 0)
                        runner.CopyCore(other, result, tiles[previous].Column, tiles[previous].Row);
                })
                : Task.CompletedTask;
            try
            {
                runner.Run(slot, runOptions);
            }
            catch (Exception ex)
            {
                // The caller disposes the runner after an error, so the background work must finish first.
                try { background.Wait(); } catch (AggregateException) { }
                if (ex is OnnxRuntimeException && cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                throw;
            }
            background.GetAwaiter().GetResult();
            if (previous >= 0)
                Report(previous);
        }
        runner.CopyCore((tiles.Length - 1) % TileRunner.SlotCount, result, tiles[^1].Column, tiles[^1].Row);
        Report(tiles.Length - 1);
        return result;
    }
}
