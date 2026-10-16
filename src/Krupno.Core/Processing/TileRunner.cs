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
    private float[]? _outputScratch;
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

    /// <summary>Copies the tile core from the network output into the final image.</summary>
    public void CopyCore(ImageBuffer destination, TileSpan column, TileSpan row)
    {
        var output = _outputs?[0] ?? throw new InvalidOperationException("Run must be called first.");
        int scale = _info.Scale;
        int outWidth = _width * scale;
        int plane = outWidth * _height * scale;

        ReadOnlySpan<float> data;
        if (_info.OutputIsHalf)
        {
            var half = MemoryMarshal.Cast<Float16, Half>(output.GetTensorDataAsSpan<Float16>());
            _outputScratch ??= new float[half.Length];
            for (int i = 0; i < half.Length; i++)
                _outputScratch[i] = (float)half[i];
            data = _outputScratch;
        }
        else
        {
            data = output.GetTensorDataAsSpan<float>();
        }

        int srcX = (column.CoreStart - column.WindowStart) * scale;
        int srcY = (row.CoreStart - row.WindowStart) * scale;
        int dstX = column.CoreStart * scale;
        int dstY = row.CoreStart * scale;
        int width = column.CoreLength * scale;
        int height = row.CoreLength * scale;
        var dst = destination.Pixels;

        for (int y = 0; y < height; y++)
        {
            int o = (srcY + y) * outWidth + srcX;
            int d = ((dstY + y) * destination.Width + dstX) * ImageBuffer.BytesPerPixel;
            for (int x = 0; x < width; x++, o++, d += ImageBuffer.BytesPerPixel)
            {
                dst[d] = ToByte(data[2 * plane + o]);
                dst[d + 1] = ToByte(data[plane + o]);
                dst[d + 2] = ToByte(data[o]);
                dst[d + 3] = 255;
            }
        }
    }

    public void Dispose()
    {
        _outputs?.Dispose();
        foreach (var value in _inputValues)
            value.Dispose();
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)(value * 255f + 0.5f), 0, 255);
}
