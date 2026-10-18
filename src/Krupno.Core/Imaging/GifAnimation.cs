using System.Windows.Media;
using System.Windows.Media.Imaging;
using Krupno.Core.Properties;

namespace Krupno.Core.Imaging;

/// <summary>
/// Animated GIF. WIC decodes each frame as stored (often only the part that changed), and the frames are drawn onto the canvas
/// the way browsers do: transparent pixels let the canvas show through, and after a frame its disposal clears its area or
/// puts back what was there before it.
/// </summary>
internal static class GifAnimation
{
    public static AnimationReader Open(string path) => new Reader(path);

    private static T? Query<T>(BitmapMetadata? metadata, string query)
    {
        try
        {
            return metadata?.GetQuery(query) is T value ? value : default;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.COMException)
        {
            return default;
        }
    }

    private static int Number(BitmapMetadata? metadata, string query) => Query<object>(metadata, query) switch
    {
        ushort value => value,
        byte value => value,
        short value => value,
        int value => value,
        _ => 0,
    };

    private sealed class Reader : AnimationReader
    {
        private readonly string _path;
        private readonly FileStream _stream;
        private readonly GifBitmapDecoder _decoder;
        private readonly int _width;
        private readonly int _height;
        private readonly int? _loopCount;

        public Reader(string path)
        {
            _path = path;
            _stream = File.OpenRead(path);
            try
            {
                _decoder = new GifBitmapDecoder(_stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
                if (_decoder.Frames.Count == 0)
                    throw new NotSupportedException(string.Format(Strings.Error_CannotOpen, Path.GetFileName(path)));
                BitmapMetadata? container = null;
                try
                {
                    container = _decoder.Metadata;
                }
                catch (NotSupportedException)
                {
                }
                _width = Number(container, "/logscrdesc/Width");
                _height = Number(container, "/logscrdesc/Height");
                // A logical screen smaller than the frames (or missing) grows to hold them.
                foreach (var frame in _decoder.Frames)
                {
                    var metadata = frame.Metadata as BitmapMetadata;
                    _width = Math.Max(_width, Number(metadata, "/imgdesc/Left") + frame.PixelWidth);
                    _height = Math.Max(_height, Number(metadata, "/imgdesc/Top") + frame.PixelHeight);
                }
                // NETSCAPE2.0: the data block is [3, 1, loop count low, high].
                if (Query<byte[]>(container, "/appext/Application") is { } application
                    && System.Text.Encoding.ASCII.GetString(application) is "NETSCAPE2.0" or "ANIMEXTS1.0"
                    && Query<byte[]>(container, "/appext/Data") is { Length: >= 4 } data && data[0] >= 3 && data[1] == 1)
                {
                    _loopCount = data[2] | data[3] << 8;
                }
            }
            catch (Exception ex) when (ex is FileFormatException or ArgumentException or System.Runtime.InteropServices.COMException
                                           or OverflowException)
            {
                _stream.Dispose();
                throw new NotSupportedException(string.Format(Strings.Error_CannotOpen, Path.GetFileName(path)), ex);
            }
            catch
            {
                _stream.Dispose();
                throw;
            }
        }

        public override AnimationFormat Format => AnimationFormat.Gif;
        public override int Width => _width;
        public override int Height => _height;
        public override int FrameCount => _decoder.Frames.Count;
        public override int? LoopCount => _loopCount;

        public override IEnumerable<AnimationFrame> ReadFrames(CancellationToken cancellationToken = default)
        {
            var canvas = new byte[_width * _height * 4];
            byte[]? saved = null;
            (int Left, int Top, int Width, int Height, int Disposal)? previous = null;
            foreach (var frame in _decoder.Frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // What the previous frame leaves behind: 2 clears its area, 3 puts back the canvas from before it.
                if (previous is { Disposal: 2 } cleared)
                    Clear(canvas, cleared.Left, cleared.Top, cleared.Width, cleared.Height);
                else if (previous is { Disposal: 3 } && saved is not null)
                    saved.CopyTo(canvas, 0);

                var metadata = frame.Metadata as BitmapMetadata;
                int left = Number(metadata, "/imgdesc/Left"), top = Number(metadata, "/imgdesc/Top");
                int disposal = Number(metadata, "/grctlext/Disposal");
                int delay = Number(metadata, "/grctlext/Delay");
                if (disposal == 3)
                    saved = (byte[])canvas.Clone();
                Draw(canvas, frame, metadata, left, top);
                previous = (left, top, frame.PixelWidth, frame.PixelHeight, disposal);
                yield return new AnimationFrame(new ImageBuffer(_width, _height, (byte[])canvas.Clone()), delay * 10);
            }
        }

        private void Clear(byte[] canvas, int left, int top, int width, int height)
        {
            int right = Math.Min(_width, left + width), bottom = Math.Min(_height, top + height);
            for (int y = Math.Max(0, top); y < bottom; y++)
            {
                int start = (y * _width + Math.Max(0, left)) * 4;
                Array.Clear(canvas, start, Math.Max(0, right - Math.Max(0, left)) * 4);
            }
        }

        /// <summary>The frame's pixels over the canvas; its transparent color leaves the canvas as it is.</summary>
        private void Draw(byte[] canvas, BitmapFrame frame, BitmapMetadata? metadata, int left, int top)
        {
            int width = frame.PixelWidth, height = frame.PixelHeight;
            var pixels = new byte[width * height * 4];
            try
            {
                if (frame.Format == PixelFormats.Indexed8 && frame.Palette is { } palette)
                {
                    // The palette as stored, with the transparent index (if any) left out by hand: WIC may or may not give it
                    // alpha 0.
                    var indices = new byte[width * height];
                    frame.CopyPixels(indices, width, 0);
                    bool hasTransparency = Query<object>(metadata, "/grctlext/TransparencyFlag") is true;
                    int transparentIndex = hasTransparency ? Number(metadata, "/grctlext/TransparentColorIndex") : -1;
                    var colors = palette.Colors;
                    for (int i = 0; i < indices.Length; i++)
                    {
                        int index = indices[i];
                        if (index == transparentIndex || index >= colors.Count)
                            continue;
                        var color = colors[index];
                        pixels[i * 4] = color.B;
                        pixels[i * 4 + 1] = color.G;
                        pixels[i * 4 + 2] = color.R;
                        pixels[i * 4 + 3] = color.A;
                    }
                }
                else
                {
                    new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, width * 4, 0);
                }
            }
            catch (Exception ex) when (ex is FileFormatException or ArgumentException or NotSupportedException
                                           or System.Runtime.InteropServices.COMException or OverflowException)
            {
                throw new NotSupportedException(string.Format(Strings.Error_CannotOpen, Path.GetFileName(_path)), ex);
            }

            for (int y = 0; y < height; y++)
            {
                int cy = top + y;
                if (cy < 0 || cy >= _height)
                    continue;
                for (int x = 0; x < width; x++)
                {
                    int cx = left + x;
                    int s = (y * width + x) * 4;
                    if (cx < 0 || cx >= _width || pixels[s + 3] == 0)
                        continue;
                    Buffer.BlockCopy(pixels, s, canvas, (cy * _width + cx) * 4, 4);
                }
            }
        }

        public override void Dispose()
        {
            _stream.Dispose();
            base.Dispose();
        }
    }
}

/// <summary>
/// Writes an animated GIF. Each frame gets a palette of its own (see <see cref="ColorQuantizer"/>), and only the area that
/// changed since the previous frame is stored. Pixels with alpha below 128 are transparent. A frame identical to the one
/// before it only makes that one last longer.
/// </summary>
internal sealed class GifAnimationWriter : AnimationWriter
{
    private readonly FileStream _file;
    private readonly int _width;
    private readonly int _height;
    // The frame waiting to be written: how it is disposed of depends on the frame after it.
    private ImageBuffer? _pending;
    private int _pendingDuration;
    private ImageBuffer? _previous;
    private int _previousDisposal;

    public GifAnimationWriter(string path, int width, int height, int? loopCount)
    {
        _width = width;
        _height = height;
        _file = File.Create(path);
        try
        {
            _file.Write("GIF89a"u8);
            WriteUInt16(width);
            WriteUInt16(height);
            // No global color table (each frame has its own), 8 bits of color resolution.
            _file.Write([0x70, 0, 0]);
            if (loopCount is { } loops)
            {
                _file.Write([0x21, 0xFF, 0x0B]);
                _file.Write("NETSCAPE2.0"u8);
                _file.Write([3, 1, (byte)loops, (byte)(loops >> 8), 0]);
            }
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public override void Add(ImageBuffer frame, int duration)
    {
        if (frame.Width != _width || frame.Height != _height)
            throw new ArgumentException("Every frame must be the size of the animation.", nameof(frame));
        if (_pending is not null && _pending.Pixels.AsSpan().SequenceEqual(frame.Pixels))
        {
            _pendingDuration += duration;
            return;
        }
        if (_pending is not null)
            WriteFrame(_pending, _pendingDuration, IsTransparent(frame));
        _pending = frame;
        _pendingDuration = duration;
    }

    public override void Finish()
    {
        if (_pending is not null)
            WriteFrame(_pending, _pendingDuration, nextTransparent: false);
        _pending = null;
        _file.WriteByte(0x3B);
        _file.Flush();
    }

    private static bool IsTransparent(ImageBuffer frame)
    {
        var pixels = frame.Pixels;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 128)
                return true;
        }
        return false;
    }

    /// <param name="nextTransparent">The next frame has transparent pixels: this one is cleared after it is shown (disposal 2),
    /// and so has to cover the whole canvas, or the next one would show older frames through its holes.</param>
    private void WriteFrame(ImageBuffer frame, int duration, bool nextTransparent)
    {
        bool transparent = IsTransparent(frame);
        var (left, top, width, height) = _previous is null || transparent || nextTransparent || _previousDisposal == 2
            ? (0, 0, _width, _height)
            : ChangedArea(_previous, frame);
        int disposal = nextTransparent ? 2 : 1;

        var region = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            Buffer.BlockCopy(frame.Pixels, ((top + y) * _width + left) * 4, region, y * width * 4, width * 4);
        var (palette, indices, transparentIndex) = ColorQuantizer.Quantize(region, width, height, transparent);

        int colors = palette.Length / 3 + (transparentIndex >= 0 ? 1 : 0);
        int bits = 1;
        while (1 << bits < colors)
            bits++;

        // Graphic control extension: disposal, transparency, delay in hundredths of a second.
        int delay = Math.Clamp((int)Math.Round(duration / 10.0), 0, ushort.MaxValue);
        _file.Write([0x21, 0xF9, 4, (byte)(disposal << 2 | (transparentIndex >= 0 ? 1 : 0)), (byte)delay, (byte)(delay >> 8),
            (byte)Math.Max(0, transparentIndex), 0]);
        // Image descriptor with a local color table of 2^bits entries.
        _file.WriteByte(0x2C);
        WriteUInt16(left);
        WriteUInt16(top);
        WriteUInt16(width);
        WriteUInt16(height);
        _file.WriteByte((byte)(0x80 | (bits - 1)));
        var table = new byte[3 << bits];
        for (int c = 0; c < palette.Length / 3; c++)
        {
            table[c * 3] = palette[c * 3 + 2];
            table[c * 3 + 1] = palette[c * 3 + 1];
            table[c * 3 + 2] = palette[c * 3];
        }
        _file.Write(table);
        GifLzw.Write(_file, indices, Math.Max(2, bits));

        _previous = frame;
        _previousDisposal = disposal;
    }

    /// <summary>The smallest rectangle around the pixels that differ (at least one pixel, for a frame that only waits).</summary>
    private (int Left, int Top, int Width, int Height) ChangedArea(ImageBuffer before, ImageBuffer after)
    {
        var a = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(before.Pixels.AsSpan());
        var b = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(after.Pixels.AsSpan());
        int minX = _width, minY = _height, maxX = -1, maxY = -1;
        for (int y = 0; y < _height; y++)
        {
            var rowA = a.Slice(y * _width, _width);
            var rowB = b.Slice(y * _width, _width);
            if (rowA.SequenceEqual(rowB))
                continue;
            int first = 0, last = _width - 1;
            while (rowA[first] == rowB[first])
                first++;
            while (rowA[last] == rowB[last])
                last--;
            minX = Math.Min(minX, first);
            maxX = Math.Max(maxX, last);
            minY = Math.Min(minY, y);
            maxY = y;
        }
        return maxY < 0 ? (0, 0, 1, 1) : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private void WriteUInt16(int value)
    {
        _file.WriteByte((byte)value);
        _file.WriteByte((byte)(value >> 8));
    }

    public override void Dispose()
    {
        _file.Dispose();
        base.Dispose();
    }
}

/// <summary>GIF's variable-length LZW, in data sub-blocks of up to 255 bytes.</summary>
internal static class GifLzw
{
    private const int MaxCode = 4095;

    public static void Write(Stream output, byte[] indices, int minCodeSize)
    {
        output.WriteByte((byte)minCodeSize);
        var writer = new BitWriter(output);
        int clear = 1 << minCodeSize;
        int codeSize = minCodeSize + 1;
        int last = clear + 1;
        // The string tables: a code followed by a pixel value → the code of that string.
        var table = new Dictionary<int, int>(MaxCode);
        writer.Write(clear, codeSize);
        int current = -1;
        foreach (byte value in indices)
        {
            if (current < 0)
            {
                current = value;
                continue;
            }
            if (table.TryGetValue(current << 8 | value, out int code))
            {
                current = code;
                continue;
            }
            writer.Write(current, codeSize);
            table[current << 8 | value] = ++last;
            if (last >= 1 << codeSize)
                codeSize++;
            if (last == MaxCode)
            {
                writer.Write(clear, codeSize);
                table.Clear();
                codeSize = minCodeSize + 1;
                last = clear + 1;
            }
            current = value;
        }
        if (current >= 0)
            writer.Write(current, codeSize);
        // A clear code before the end, so the decoder's code size is the initial one for it whatever it was before.
        writer.Write(clear, codeSize);
        writer.Write(clear + 1, minCodeSize + 1);
        writer.Flush();
        output.WriteByte(0);
    }

    private sealed class BitWriter(Stream output)
    {
        private readonly byte[] _block = new byte[255];
        private int _count;
        private int _bits;
        private int _bitCount;

        public void Write(int code, int size)
        {
            _bits |= code << _bitCount;
            _bitCount += size;
            while (_bitCount >= 8)
            {
                Put((byte)_bits);
                _bits >>= 8;
                _bitCount -= 8;
            }
        }

        public void Flush()
        {
            if (_bitCount > 0)
                Put((byte)_bits);
            _bits = 0;
            _bitCount = 0;
            if (_count > 0)
                WriteBlock();
        }

        private void Put(byte value)
        {
            _block[_count++] = value;
            if (_count == _block.Length)
                WriteBlock();
        }

        private void WriteBlock()
        {
            output.WriteByte((byte)_count);
            output.Write(_block, 0, _count);
            _count = 0;
        }
    }
}
