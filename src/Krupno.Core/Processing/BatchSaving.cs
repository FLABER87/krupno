using Krupno.Core.Imaging;

namespace Krupno.Core.Processing;

/// <summary>
/// In a batch, a result is written while the next file is processed, so the GPU doesn't sit idle while the encoder works:
/// compressing a 7680×4800 PNG can take seconds, most of a frame for a fast model. Meanwhile one more result stays in memory
/// (and the encoder's copy of it), so a large result is written before the next file starts instead. One save runs at a time,
/// so the files are finished in order. The last file of a batch has nothing to overlap with and is written right away.
/// </summary>
public static class BatchSaving
{
    /// <summary>The largest result written in the background: 512 MB, 134 megapixels (16384×8192, for example).</summary>
    public const long BackgroundLimit = 512L << 20;

    public static bool CanSaveInBackground(ImageBuffer result) => result.Pixels.LongLength <= BackgroundLimit;
}
