using System.Runtime.InteropServices;

namespace Krupno.Core.Imaging;

/// <summary>
/// The parts of libwebp (BSD-3-Clause, the Imazen.WebP.NativeRuntime.win-x64 package) that animations need: the animation
/// decoder and encoder, the demuxer for the color profile and the muxer to write it. Only the leading fields of libwebp's
/// structures that are read or set here are declared; each structure gets more memory than libwebp's own, which its init
/// functions fill in. The ABI versions passed to the *Internal functions only have to match in their major byte.
/// </summary>
internal static unsafe partial class WebPNative
{
    private const string Webp = "libwebp";
    private const string Demux = "libwebpdemux";
    private const string Mux = "libwebpmux";

    public const int EncoderAbi = 0x0210;
    public const int DemuxAbi = 0x0107;
    public const int MuxAbi = 0x0109;
    public const int MaxDimension = 16383;

    /// <summary>MODE_BGRA: the decoder's canvas in our pixel order, not premultiplied.</summary>
    public const int ModeBgra = 3;

    /// <summary>Room for WebPPicture, WebPConfig and the iterators: more than the largest of them.</summary>
    public const int StructSize = 512;

    [StructLayout(LayoutKind.Sequential)]
    public struct WebPData
    {
        public byte* Bytes;
        public nuint Size;
    }

    /// <summary>WebPAnimDecoderOptions: color_mode, use_threads, padding[7].</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AnimDecoderOptions
    {
        public int ColorMode;
        public int UseThreads;
        public fixed uint Padding[7];
    }

    /// <summary>WebPAnimInfo.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AnimInfo
    {
        public uint CanvasWidth;
        public uint CanvasHeight;
        public uint LoopCount;
        public uint BackgroundColor;
        public uint FrameCount;
        public fixed uint Padding[4];
    }

    /// <summary>WebPAnimEncoderOptions: anim_params (bgcolor, loop_count), minimize_size, kmin, kmax, allow_mixed, verbose, padding[4].</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AnimEncoderOptions
    {
        public uint BackgroundColor;
        public int LoopCount;
        public int MinimizeSize;
        public int KMin;
        public int KMax;
        public int AllowMixed;
        public int Verbose;
        public fixed uint Padding[4];
    }

    /// <summary>WebPChunkIterator: chunk_num, num_chunks, chunk, padding[6], private_.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ChunkIterator
    {
        public int ChunkNumber;
        public int ChunkCount;
        public WebPData Chunk;
        public fixed uint Padding[6];
        public void* Private;
    }

    // WebPPicture: use_argb at 0, colorspace at 4, width at 8, height at 12.
    public const int PictureUseArgb = 0;
    public const int PictureWidth = 8;
    public const int PictureHeight = 12;

    // WebPConfig: lossless at 0, quality at 4, method at 8.
    public const int ConfigLossless = 0;
    public const int ConfigQuality = 4;
    public const int ConfigMethod = 8;

    [LibraryImport(Webp)]
    public static partial int WebPGetDecoderVersion();

    [LibraryImport(Webp)]
    public static partial void WebPFree(void* pointer);

    [LibraryImport(Webp)]
    public static partial int WebPConfigInitInternal(void* config, int preset, float quality, int version);

    [LibraryImport(Webp)]
    public static partial int WebPValidateConfig(void* config);

    [LibraryImport(Webp)]
    public static partial int WebPPictureInitInternal(void* picture, int version);

    [LibraryImport(Webp)]
    public static partial int WebPPictureImportBGRA(void* picture, byte* bgra, int stride);

    [LibraryImport(Webp)]
    public static partial void WebPPictureFree(void* picture);

    [LibraryImport(Demux)]
    public static partial int WebPAnimDecoderOptionsInitInternal(AnimDecoderOptions* options, int version);

    [LibraryImport(Demux)]
    public static partial void* WebPAnimDecoderNewInternal(WebPData* data, AnimDecoderOptions* options, int version);

    [LibraryImport(Demux)]
    public static partial int WebPAnimDecoderGetInfo(void* decoder, AnimInfo* info);

    [LibraryImport(Demux)]
    public static partial int WebPAnimDecoderHasMoreFrames(void* decoder);

    [LibraryImport(Demux)]
    public static partial int WebPAnimDecoderGetNext(void* decoder, byte** buffer, int* timestamp);

    [LibraryImport(Demux)]
    public static partial void* WebPAnimDecoderGetDemuxer(void* decoder);

    [LibraryImport(Demux)]
    public static partial void WebPAnimDecoderDelete(void* decoder);

    [LibraryImport(Demux)]
    public static partial int WebPDemuxGetChunk(void* demuxer, byte* fourcc, int chunkNumber, ChunkIterator* iterator);

    [LibraryImport(Demux)]
    public static partial void WebPDemuxReleaseChunkIterator(ChunkIterator* iterator);

    [LibraryImport(Mux)]
    public static partial int WebPAnimEncoderOptionsInitInternal(AnimEncoderOptions* options, int version);

    [LibraryImport(Mux)]
    public static partial void* WebPAnimEncoderNewInternal(int width, int height, AnimEncoderOptions* options, int version);

    [LibraryImport(Mux)]
    public static partial int WebPAnimEncoderAdd(void* encoder, void* picture, int timestamp, void* config);

    [LibraryImport(Mux)]
    public static partial int WebPAnimEncoderAssemble(void* encoder, WebPData* data);

    [LibraryImport(Mux)]
    public static partial byte* WebPAnimEncoderGetError(void* encoder);

    [LibraryImport(Mux)]
    public static partial void WebPAnimEncoderDelete(void* encoder);

    [LibraryImport(Mux)]
    public static partial void* WebPMuxCreateInternal(WebPData* data, int copyData, int version);

    [LibraryImport(Mux)]
    public static partial int WebPMuxSetChunk(void* mux, byte* fourcc, WebPData* chunk, int copyData);

    [LibraryImport(Mux)]
    public static partial int WebPMuxAssemble(void* mux, WebPData* data);

    [LibraryImport(Mux)]
    public static partial void WebPMuxDelete(void* mux);
}
