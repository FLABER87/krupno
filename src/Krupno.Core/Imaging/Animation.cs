using System.Windows.Media.Imaging;
using Krupno.Core.Properties;

namespace Krupno.Core.Imaging;

/// <summary>The container of an animation. An upscaled animation keeps it: GIF stays GIF, WebP stays WebP.</summary>
public enum AnimationFormat
{
    Gif,
    WebP,
}

/// <summary>A frame as it is shown: the whole canvas, drawn over the frames before it, and how long it stays.</summary>
/// <param name="Duration">In milliseconds; a GIF keeps its own hundredths of a second, so 0 stays 0 (browsers show that as 100 ms).</param>
public sealed record AnimationFrame(ImageBuffer Image, int Duration);

/// <summary>An animated GIF or WebP open for reading, one frame at a time.</summary>
public abstract class AnimationReader : IDisposable
{
    public abstract AnimationFormat Format { get; }
    public abstract int Width { get; }
    public abstract int Height { get; }
    public abstract int FrameCount { get; }

    /// <summary>How many times it plays: 0 is forever; null for a GIF without a loop count, which plays once.</summary>
    public abstract int? LoopCount { get; }

    /// <summary>The ICC profile the frames are in (WebP only), carried over to the result.</summary>
    public virtual byte[]? IccProfile => null;

    /// <summary>Every frame is stored losslessly (WebP's VP8L), so the result can be too.</summary>
    public virtual bool IsLossless => false;

    /// <summary>The frames in order. Each one is a new buffer the caller may keep.</summary>
    public abstract IEnumerable<AnimationFrame> ReadFrames(CancellationToken cancellationToken = default);

    public virtual void Dispose() => GC.SuppressFinalize(this);
}

/// <summary>Writes an animation frame by frame; nothing is in the file until <see cref="Finish"/>.</summary>
public abstract class AnimationWriter : IDisposable
{
    /// <param name="frame">The whole canvas, the size the writer was created with. The writer may keep it until the next one.</param>
    public abstract void Add(ImageBuffer frame, int duration);

    public abstract void Finish();

    public virtual void Dispose() => GC.SuppressFinalize(this);
}

public sealed record AnimationSaveOptions
{
    /// <summary>
    /// The quality of a WebP animation's frames, 1–100, where 100 is lossless; null keeps the source's kind of compression:
    /// lossless for a lossless WebP, otherwise <see cref="DefaultQuality"/>.
    /// </summary>
    public int? Quality { get; init; }

    public const int DefaultQuality = 95;

    /// <summary>The loop count of the source (see <see cref="AnimationReader.LoopCount"/>).</summary>
    public int? LoopCount { get; init; } = 0;

    public byte[]? IccProfile { get; init; }
}

/// <summary>Animated GIF and WebP: each frame is upscaled as an image of its own, and the result is an animation again.</summary>
public static class Animation
{
    /// <summary>The largest side a WebP can have; GIF stores sides in 16 bits.</summary>
    private static int MaxSide(AnimationFormat format) => format == AnimationFormat.WebP ? WebPNative.MaxDimension : ushort.MaxValue;

    public static string GetExtension(AnimationFormat format) => format == AnimationFormat.Gif ? ".gif" : ".webp";

    /// <summary>
    /// The format of an animation with more than one frame; null for a still image, another format, or a file that can't be
    /// read (opening it reports that). Reads the GIF's frame headers or the WebP's container, not the pixels.
    /// </summary>
    public static AnimationFormat? Detect(string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (extension == ".gif")
            {
                using var stream = File.OpenRead(path);
                var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                return decoder.Frames.Count > 1 ? AnimationFormat.Gif : null;
            }
            if (extension == ".webp")
                return WebPAnimation.IsAnimated(path) ? AnimationFormat.WebP : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException
                                       or ArgumentException or System.Runtime.InteropServices.COMException or OverflowException
                                       or DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return null;
    }

    public static AnimationReader Open(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".gif" => GifAnimation.Open(path),
        ".webp" => WebPAnimation.Open(path),
        _ => throw new NotSupportedException(string.Format(Strings.Error_CannotOpen, Path.GetFileName(path))),
    };

    /// <summary>Throws a clear error if the format can't hold frames of this size, before the work rather than after it.</summary>
    public static void EnsureCanSave(AnimationFormat format, long width, long height)
    {
        int max = MaxSide(format);
        if (width > max || height > max)
        {
            throw new InvalidOperationException(
                string.Format(Strings.Error_AnimationSizeLimit, format == AnimationFormat.Gif ? "GIF" : "WebP", max, width, height));
        }
    }

    public static AnimationWriter Create(string path, AnimationFormat format, int width, int height, AnimationSaveOptions options)
    {
        EnsureCanSave(format, width, height);
        return format == AnimationFormat.Gif
            ? new GifAnimationWriter(path, width, height, options.LoopCount)
            : new WebPAnimationWriter(path, width, height, options);
    }
}
