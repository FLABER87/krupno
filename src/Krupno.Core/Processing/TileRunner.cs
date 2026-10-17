using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Krupno.Core.Imaging;
using Krupno.Core.Inference;

namespace Krupno.Core.Processing;

/// <summary>
/// Runs fixed-size windows: fills the input tensor, runs the session
/// and copies the tile core into the result. Buffers are reused between tiles.
/// </summary>
internal sealed class TileRunner : IDisposable
{
    private const float Inv255 = 1f / 255f;

    private readonly ModelInfo _info;
    private readonly InferenceSession _session;
    private readonly int _width;
    private readonly int _height;
    private readonly float[] _input;
    private readonly Float16[]? _inputHalf;
    private readonly OrtValue[] _inputValues;
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    /// <summary>A row of an fp16 output converted to float, for each of the three planes.</summary>
    private float[]? _rowScratch;
    private IDisposableReadOnlyCollection<OrtValue>? _outputs;

    public int Width => _width;
    public int Height => _height;

    public TileRunner(UpscaleModel model, int width, int height)
    {
        _info = model.Info;
        _width = width;
        _height = height;
        _session = model.GetSession(width, height);
        _input = new float[3 * width * height];

        long[] shape = [1, 3, height, width];
        if (_info.InputIsHalf)
        {
            _inputHalf = new Float16[_input.Length];
            _inputValues = [OrtValue.CreateTensorValueFromMemory(_inputHalf, shape)];
        }
        else
        {
            _inputValues = [OrtValue.CreateTensorValueFromMemory(_input, shape)];
        }
        _inputNames = [_info.InputName];
        _outputNames = [_info.OutputName];
    }

    /// <summary>Copies an image window into the input tensor (NCHW, RGB, 0..1), mirroring beyond the edges.</summary>
    public void Fill(ImageBuffer source, int windowX, int windowY)
    {
        int plane = _width * _height;
        var px = source.Pixels;

        Span<int> columns = _width <= 4096 ? stackalloc int[_width] : new int[_width];
        for (int x = 0; x < _width; x++)
            columns[x] = TilePlanner.Mirror(windowX + x, source.Width) * ImageBuffer.BytesPerPixel;

        for (int y = 0; y < _height; y++)
        {
            int row = TilePlanner.Mirror(windowY + y, source.Height) * source.Stride;
            int o = y * _width;
            for (int x = 0; x < _width; x++, o++)
            {
                int i = row + columns[x];
                _input[o] = px[i + 2] * Inv255;
                _input[plane + o] = px[i + 1] * Inv255;
                _input[2 * plane + o] = px[i] * Inv255;
            }
        }

        if (_inputHalf is not null)
        {
            var half = MemoryMarshal.Cast<Float16, Half>(_inputHalf.AsSpan());
            for (int i = 0; i < _input.Length; i++)
                half[i] = (Half)_input[i];
        }
    }

    public void Run(RunOptions runOptions)
    {
        _outputs?.Dispose();
        _outputs = null;
        _outputs = _session.Run(runOptions, _inputNames, _inputValues, _outputNames);

        long[] shape = _outputs[0].GetTensorTypeAndShape().Shape;
        int scale = _info.Scale;
        if (shape.Length != 4 || shape[1] != 3 || shape[2] != (long)_height * scale || shape[3] != (long)_width * scale)
        {
            throw new InvalidOperationException(
                $"Unexpected output shape [{string.Join(", ", shape)}], expected [1, 3, {_height * scale}, {_width * scale}].");
        }
    }

    /// <summary>
    /// Copies the tile core from the network output into the final image, a row at a time with SIMD (see <see cref="Pack"/>).
    /// With a model that costs nothing to run, a 1920×1200 → 7680×4800 frame took 0.28 s a pixel at a time and 0.16 s this way.
    /// </summary>
    public void CopyCore(ImageBuffer destination, TileSpan column, TileSpan row)
    {
        var output = _outputs?[0] ?? throw new InvalidOperationException("Run must be called first.");
        int scale = _info.Scale;
        int outWidth = _width * scale;
        int plane = outWidth * _height * scale;
        bool half = _info.OutputIsHalf;
        var floats = half ? default : output.GetTensorDataAsSpan<float>();
        var halves = half ? MemoryMarshal.Cast<Float16, Half>(output.GetTensorDataAsSpan<Float16>()) : default;

        int srcX = (column.CoreStart - column.WindowStart) * scale;
        int srcY = (row.CoreStart - row.WindowStart) * scale;
        int dstX = column.CoreStart * scale;
        int dstY = row.CoreStart * scale;
        int width = column.CoreLength * scale;
        int height = row.CoreLength * scale;
        if (half)
            _rowScratch ??= new float[3 * outWidth];

        for (int y = 0; y < height; y++)
        {
            int o = (srcY + y) * outWidth + srcX;
            ReadOnlySpan<float> r, g, b;
            if (!half)
            {
                r = floats.Slice(o, width);
                g = floats.Slice(plane + o, width);
                b = floats.Slice(2 * plane + o, width);
            }
            else
            {
                // Only the core's rows are converted from fp16, not the whole output with its context around the core.
                var scratch = _rowScratch.AsSpan(0, 3 * width);
                for (int c = 0; c < 3; c++)
                {
                    var source = halves.Slice(c * plane + o, width);
                    for (int x = 0; x < width; x++)
                        scratch[c * width + x] = (float)source[x];
                }
                r = scratch[..width];
                g = scratch.Slice(width, width);
                b = scratch.Slice(2 * width, width);
            }
            Pack(r, g, b, MemoryMarshal.Cast<byte, uint>(destination.Pixels.AsSpan(
                ((dstY + y) * destination.Width + dstX) * ImageBuffer.BytesPerPixel, width * ImageBuffer.BytesPerPixel)));
        }
    }

    /// <summary>
    /// Planar RGB 0..1 → BGRA bytes, opaque. The vector path does the same float operations as <see cref="ToByte"/>
    /// (a multiply, then an add, a saturating conversion and a clamp), so the bytes are the same, NaN and infinities included.
    /// </summary>
    private static void Pack(ReadOnlySpan<float> r, ReadOnlySpan<float> g, ReadOnlySpan<float> b, Span<uint> bgra)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var scale = new Vector<float>(255f);
            var half = new Vector<float>(0.5f);
            var max = new Vector<int>(255);
            var alpha = new Vector<uint>(0xFF000000);
            for (; x <= bgra.Length - Vector<uint>.Count; x += Vector<uint>.Count)
            {
                var vr = Vector.AsVectorUInt32(Vector.Min(Vector.Max(Vector.ConvertToInt32(new Vector<float>(r[x..]) * scale + half), Vector<int>.Zero), max));
                var vg = Vector.AsVectorUInt32(Vector.Min(Vector.Max(Vector.ConvertToInt32(new Vector<float>(g[x..]) * scale + half), Vector<int>.Zero), max));
                var vb = Vector.AsVectorUInt32(Vector.Min(Vector.Max(Vector.ConvertToInt32(new Vector<float>(b[x..]) * scale + half), Vector<int>.Zero), max));
                (vb | Vector.ShiftLeft(vg, 8) | Vector.ShiftLeft(vr, 16) | alpha).CopyTo(bgra[x..]);
            }
        }
        for (; x < bgra.Length; x++)
            bgra[x] = ToByte(b[x]) | (uint)ToByte(g[x]) << 8 | (uint)ToByte(r[x]) << 16 | 0xFF000000;
    }

    public void Dispose()
    {
        _outputs?.Dispose();
        foreach (var value in _inputValues)
            value.Dispose();
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)(value * 255f + 0.5f), 0, 255);
}
