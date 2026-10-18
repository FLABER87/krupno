using System.Runtime.InteropServices;
using Krupno.Core.Properties;
using static Krupno.Core.Imaging.WebPNative;

namespace Krupno.Core.Imaging;

/// <summary>
/// WebP through libwebp: animations frame by frame, and a still WebP when Windows has no codec for it (the Microsoft Store
/// extension, missing on Windows 10 LTSC and Server). The decoder composites each frame onto the canvas itself.
/// </summary>
internal static unsafe class WebPAnimation
{
    private static ReadOnlySpan<byte> Iccp => "ICCP"u8;

    /// <summary>The animation flag of an extended (VP8X) WebP header, without reading the rest of the file.</summary>
    public static bool HasAnimationFlag(ReadOnlySpan<byte> header) =>
        header.Length >= 21 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)
        && header[12..16].SequenceEqual("VP8X"u8) && (header[20] & 0x02) != 0;

    /// <summary>A WebP with the animation flag and more than one frame.</summary>
    public static bool IsAnimated(string path)
    {
        Span<byte> header = stackalloc byte[21];
        using (var stream = File.OpenRead(path))
        {
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !HasAnimationFlag(header))
                return false;
        }
        using var reader = Open(path);
        return reader.FrameCount > 1;
    }

    public static AnimationReader Open(string path) => new Reader(path, File.ReadAllBytes(path));

    /// <summary>
    /// Whether every frame's image is stored as VP8L (lossless) rather than VP8 (lossy), from the RIFF chunks: the top-level
    /// one of a still image, or the ones inside each ANMF frame.
    /// </summary>
    internal static bool AllFramesLossless(ReadOnlySpan<byte> file)
    {
        int lossless = 0, lossy = 0;
        Scan(file.Length >= 12 ? file[12..] : [], ref lossless, ref lossy, frames: true);
        return lossless > 0 && lossy == 0;

        static void Scan(ReadOnlySpan<byte> chunks, ref int lossless, ref int lossy, bool frames)
        {
            while (chunks.Length >= 8)
            {
                var fourcc = chunks[..4];
                long size = BitConverter.ToUInt32(chunks[4..8]);
                var payload = chunks[8..(int)Math.Min(chunks.Length, 8 + size)];
                if (fourcc.SequenceEqual("VP8L"u8))
                    lossless++;
                else if (fourcc.SequenceEqual("VP8 "u8))
                    lossy++;
                else if (frames && fourcc.SequenceEqual("ANMF"u8) && payload.Length > 16)
                    Scan(payload[16..], ref lossless, ref lossy, frames: false);
                // Chunks are padded to an even size.
                long next = 8 + size + (size & 1);
                if (next >= chunks.Length)
                    break;
                chunks = chunks[(int)next..];
            }
        }
    }

    /// <summary>The first frame of any WebP, for a still image Windows can't decode.</summary>
    public static ImageBuffer LoadFirstFrame(string path)
    {
        using var reader = Open(path);
        return reader.ReadFrames().First().Image;
    }

    private sealed class Reader : AnimationReader
    {
        private readonly string _path;
        private byte* _data;
        private nuint _size;
        private void* _decoder;
        private readonly AnimInfo _info;
        private readonly byte[]? _icc;
        private readonly bool _lossless;

        public Reader(string path, byte[] file)
        {
            _path = path;
            // The decoder reads the data as long as it lives, so it gets memory that doesn't move.
            _size = (nuint)file.Length;
            _data = (byte*)NativeMemory.Alloc(Math.Max(_size, 1));
            file.CopyTo(new Span<byte>(_data, file.Length));
            try
            {
                _decoder = NewDecoder();
                AnimInfo info;
                if (WebPAnimDecoderGetInfo(_decoder, &info) == 0)
                    throw new NotSupportedException(string.Format(Strings.Error_CannotOpen, Path.GetFileName(path)));
                _info = info;
                _icc = ReadIcc();
                _lossless = AllFramesLossless(file);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public override AnimationFormat Format => AnimationFormat.WebP;
        public override int Width => (int)_info.CanvasWidth;
        public override int Height => (int)_info.CanvasHeight;
        public override int FrameCount => (int)_info.FrameCount;
        public override int? LoopCount => (int)_info.LoopCount;
        public override byte[]? IccProfile => _icc;
        public override bool IsLossless => _lossless;

        public override IEnumerable<AnimationFrame> ReadFrames(CancellationToken cancellationToken = default)
        {
            Restart();
            int previous = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var frame = Next(ref previous);
                if (frame is null)
                    yield break;
                yield return frame;
            }
        }

        /// <summary>A fresh decoder, so the frames can be read again from the start (an iterator can't touch the pointers).</summary>
        private void Restart()
        {
            WebPAnimDecoderDelete(_decoder);
            // Not left pointing at the freed decoder if a new one can't be made: Dispose would free it again.
            _decoder = null;
            _decoder = NewDecoder();
        }

        private AnimationFrame? Next(ref int previous)
        {
            if (WebPAnimDecoderHasMoreFrames(_decoder) == 0)
                return null;
            byte* canvas;
            int timestamp;
            if (WebPAnimDecoderGetNext(_decoder, &canvas, &timestamp) == 0)
                throw new NotSupportedException(string.Format(Strings.Error_CannotOpen, Path.GetFileName(_path)));
            var image = new ImageBuffer(Width, Height);
            new ReadOnlySpan<byte>(canvas, image.Pixels.Length).CopyTo(image.Pixels);
            // Timestamps are when each frame ends.
            var frame = new AnimationFrame(image, timestamp - previous);
            previous = timestamp;
            return frame;
        }

        private void* NewDecoder()
        {
            AnimDecoderOptions options;
            if (WebPAnimDecoderOptionsInitInternal(&options, DemuxAbi) == 0)
                throw new InvalidOperationException("libwebp's demuxer is not the version Krupno was built for.");
            options.ColorMode = ModeBgra;
            options.UseThreads = 1;
            var data = new WebPData { Bytes = _data, Size = _size };
            void* decoder = WebPAnimDecoderNewInternal(&data, &options, DemuxAbi);
            if (decoder == null)
                throw new NotSupportedException(string.Format(Strings.Error_CannotOpen, Path.GetFileName(_path)));
            return decoder;
        }

        private byte[]? ReadIcc()
        {
            void* demuxer = WebPAnimDecoderGetDemuxer(_decoder);
            ChunkIterator iterator;
            fixed (byte* fourcc = Iccp)
            {
                if (demuxer == null || WebPDemuxGetChunk(demuxer, fourcc, 1, &iterator) == 0)
                    return null;
            }
            try
            {
                return new ReadOnlySpan<byte>(iterator.Chunk.Bytes, (int)iterator.Chunk.Size).ToArray();
            }
            finally
            {
                WebPDemuxReleaseChunkIterator(&iterator);
            }
        }

        public override void Dispose()
        {
            if (_decoder != null)
                WebPAnimDecoderDelete(_decoder);
            _decoder = null;
            if (_data != null)
                NativeMemory.Free(_data);
            _data = null;
            base.Dispose();
        }
    }
}

/// <summary>
/// An animated WebP through libwebp's animation encoder, which stores each frame as the part that changed. Lossy frames
/// below quality 100, lossless at 100; the alpha channel is always lossless.
/// </summary>
internal sealed unsafe class WebPAnimationWriter : AnimationWriter
{
    private readonly string _path;
    private readonly int _width;
    private readonly int _height;
    private readonly AnimationSaveOptions _options;
    private void* _encoder;
    private byte* _config;
    private int _timestamp;

    public WebPAnimationWriter(string path, int width, int height, AnimationSaveOptions options)
    {
        _path = path;
        _width = width;
        _height = height;
        _options = options;
        AnimEncoderOptions encoderOptions;
        if (WebPNative.WebPAnimEncoderOptionsInitInternal(&encoderOptions, WebPNative.MuxAbi) == 0)
            throw new InvalidOperationException("libwebp's muxer is not the version Krupno was built for.");
        encoderOptions.LoopCount = options.LoopCount ?? 0;
        // Transparent, as the decoder starts the canvas (browsers ignore the background color anyway).
        encoderOptions.BackgroundColor = 0;
        _encoder = WebPNative.WebPAnimEncoderNewInternal(width, height, &encoderOptions, WebPNative.MuxAbi);
        if (_encoder == null)
            throw new OutOfMemoryException();

        _config = (byte*)NativeMemory.AllocZeroed(WebPNative.StructSize);
        try
        {
            int quality = Math.Clamp(options.Quality ?? AnimationSaveOptions.DefaultQuality, 1, 100);
            if (WebPNative.WebPConfigInitInternal(_config, 0, quality, WebPNative.EncoderAbi) == 0)
                throw new InvalidOperationException("libwebp's encoder is not the version Krupno was built for.");
            // Quality 100 is lossless, where "quality" is the effort (75, libwebp's default).
            *(int*)(_config + WebPNative.ConfigLossless) = quality == 100 ? 1 : 0;
            *(float*)(_config + WebPNative.ConfigQuality) = quality == 100 ? 75 : quality;
            *(int*)(_config + WebPNative.ConfigMethod) = 4;
            if (WebPNative.WebPValidateConfig(_config) == 0)
                throw new InvalidOperationException("libwebp rejected the encoder settings.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public override void Add(ImageBuffer frame, int duration)
    {
        if (frame.Width != _width || frame.Height != _height)
            throw new ArgumentException("Every frame must be the size of the animation.", nameof(frame));
        byte* picture = (byte*)NativeMemory.AllocZeroed(WebPNative.StructSize);
        try
        {
            if (WebPNative.WebPPictureInitInternal(picture, WebPNative.EncoderAbi) == 0)
                throw new InvalidOperationException("libwebp's encoder is not the version Krupno was built for.");
            *(int*)(picture + WebPNative.PictureUseArgb) = 1;
            *(int*)(picture + WebPNative.PictureWidth) = _width;
            *(int*)(picture + WebPNative.PictureHeight) = _height;
            fixed (byte* pixels = frame.Pixels)
            {
                if (WebPNative.WebPPictureImportBGRA(picture, pixels, frame.Stride) == 0)
                    throw new OutOfMemoryException();
            }
            if (WebPNative.WebPAnimEncoderAdd(_encoder, picture, _timestamp, _config) == 0)
                throw new InvalidOperationException(string.Format(Strings.Error_WebPWrite, Error()));
        }
        finally
        {
            WebPNative.WebPPictureFree(picture);
            NativeMemory.Free(picture);
        }
        _timestamp += Math.Max(0, duration);
    }

    public override void Finish()
    {
        // A last call without a picture says when the last frame ends.
        if (WebPNative.WebPAnimEncoderAdd(_encoder, null, _timestamp, null) == 0)
            throw new InvalidOperationException(string.Format(Strings.Error_WebPWrite, Error()));
        WebPNative.WebPData data = default;
        if (WebPNative.WebPAnimEncoderAssemble(_encoder, &data) == 0)
            throw new InvalidOperationException(string.Format(Strings.Error_WebPWrite, Error()));
        // The encoder's own copy of the frames goes before more copies of the file are made.
        WebPNative.WebPAnimEncoderDelete(_encoder);
        _encoder = null;
        try
        {
            if (_options.IccProfile is { Length: > 0 } icc)
                data = WithIcc(data, icc);
            using var file = File.Create(_path);
            using var bytes = new UnmanagedMemoryStream(data.Bytes, (long)data.Size);
            bytes.CopyTo(file);
        }
        finally
        {
            WebPNative.WebPFree(data.Bytes);
        }
    }

    /// <summary>The assembled file with the source's color profile; the original data is freed.</summary>
    private static WebPNative.WebPData WithIcc(WebPNative.WebPData data, byte[] icc)
    {
        // The muxer reads the file in place (no copy), so the original is freed only once the muxer is gone.
        void* mux = WebPNative.WebPMuxCreateInternal(&data, 0, WebPNative.MuxAbi);
        if (mux == null)
            return data;
        WebPNative.WebPData withIcc = default;
        bool done;
        fixed (byte* profile = icc)
        fixed (byte* fourcc = "ICCP"u8)
        {
            var chunk = new WebPNative.WebPData { Bytes = profile, Size = (nuint)icc.Length };
            // WEBP_MUX_OK is 1.
            done = WebPNative.WebPMuxSetChunk(mux, fourcc, &chunk, 1) == 1 && WebPNative.WebPMuxAssemble(mux, &withIcc) == 1;
        }
        WebPNative.WebPMuxDelete(mux);
        if (!done)
            return data;
        WebPNative.WebPFree(data.Bytes);
        return withIcc;
    }

    private string Error() => (_encoder == null ? null : Marshal.PtrToStringUTF8((nint)WebPNative.WebPAnimEncoderGetError(_encoder)))
                              ?? "unknown error";

    public override void Dispose()
    {
        if (_encoder != null)
            WebPNative.WebPAnimEncoderDelete(_encoder);
        _encoder = null;
        if (_config != null)
            NativeMemory.Free(_config);
        _config = null;
        base.Dispose();
    }
}
