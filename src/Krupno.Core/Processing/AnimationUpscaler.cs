using Krupno.Core.Imaging;

namespace Krupno.Core.Processing;

/// <summary>The progress of one frame of an animation.</summary>
/// <param name="Frame">1-based.</param>
public readonly record struct FrameProgress(int Frame, int FrameCount, UpscaleProgress Upscale)
{
    public double Fraction => FrameCount == 0 ? 0 : (Frame - 1 + Upscale.Fraction) / FrameCount;
}

/// <param name="FirstSource">The first frame as shown, for the preview.</param>
/// <param name="FirstResult">Its upscaled version.</param>
/// <param name="Upscaled">How many frames went through the network: a frame identical to the one before it is reused.</param>
public sealed record AnimationResult(AnimationFormat Format, int Frames, int Upscaled, ImageBuffer FirstSource, ImageBuffer FirstResult);

/// <summary>
/// Upscales an animated GIF or WebP frame by frame into an animation of the same format: each frame is the whole canvas as it
/// is shown, upscaled like an image of its own with the same options, and keeps its duration and the loop count.
/// </summary>
public static class AnimationUpscaler
{
    /// <summary>Runs on the calling thread from start to end (WIC's GIF decoder must stay on the thread that opened it).</summary>
    public static AnimationResult Upscale(Upscaler upscaler, string input, string output, UpscaleOptions options,
        AnimationSaveOptions? save = null, IProgress<FrameProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var reader = Animation.Open(input);
        var (width, height) = upscaler.OutputSize(reader.Width, reader.Height, options);
        Animation.EnsureCanSave(reader.Format, width, height);

        string directory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(directory);
        // As for images (ImageCodec.Save): a name of our own next to the result, moved over it once the file is complete.
        string tempPath = Path.Combine(directory, $"krupno-{Guid.NewGuid():N}.tmp");
        try
        {
            AnimationResult result;
            using (var writer = Animation.Create(tempPath, reader.Format, (int)width, (int)height,
                       (save ?? new AnimationSaveOptions()) with { LoopCount = reader.LoopCount, IccProfile = reader.IccProfile }))
            {
                result = Run(upscaler, reader, writer, options, progress, cancellationToken);
            }
            File.Move(tempPath, output, overwrite: true);
            return result;
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static AnimationResult Run(Upscaler upscaler, AnimationReader reader, AnimationWriter writer, UpscaleOptions options,
        IProgress<FrameProgress>? progress, CancellationToken cancellationToken)
    {
        int count = reader.FrameCount, index = 0, upscaled = 0;
        ImageBuffer? previousSource = null, previousResult = null, firstSource = null, firstResult = null;
        foreach (var frame in reader.ReadFrames(cancellationToken))
        {
            index++;
            ImageBuffer result;
            if (previousSource is not null && previousSource.Pixels.AsSpan().SequenceEqual(frame.Image.Pixels))
            {
                result = previousResult!;
            }
            else
            {
                int frameIndex = index;
                var frameProgress = progress is null
                    ? null
                    : new Progress(p => progress.Report(new FrameProgress(frameIndex, count, p)));
                result = upscaler.Upscale(frame.Image, options, frameProgress, cancellationToken);
                upscaled++;
            }
            writer.Add(result, frame.Duration);
            firstSource ??= frame.Image;
            firstResult ??= result;
            previousSource = frame.Image;
            previousResult = result;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (firstSource is null || firstResult is null)
            throw new NotSupportedException("The animation has no frames.");
        writer.Finish();
        return new AnimationResult(reader.Format, index, upscaled, firstSource, firstResult);
    }

    /// <summary>Reports synchronously, on the upscaling thread, like the image's own progress.</summary>
    private sealed class Progress(Action<UpscaleProgress> report) : IProgress<UpscaleProgress>
    {
        public void Report(UpscaleProgress value) => report(value);
    }
}
