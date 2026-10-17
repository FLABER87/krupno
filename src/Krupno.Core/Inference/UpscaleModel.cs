using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Krupno.Core.Localization;
using Krupno.Core.Processing;
using Krupno.Core.Properties;

namespace Krupno.Core.Inference;

/// <summary>
/// An ONNX upscaling model (input and output are NCHW, RGB, 0..1) and its ONNX Runtime sessions.
/// </summary>
public sealed class UpscaleModel : IDisposable
{
    /// <summary>Prefix of the metadata keys written by tools/convert_to_onnx.py.</summary>
    public const string MetadataPrefix = "krupno.";

    /// <summary>Prefix of models converted before the project was renamed (Upscalr); it is read as well.</summary>
    public const string LegacyMetadataPrefix = "upscalr.";

    /// <summary>Side multiple for models without metadata: leaves headroom for pixel-unshuffle and window attention.</summary>
    private const int DefaultSizeMultiple = 8;
    private const int ProbeSize = 32;
    private const int MaxCachedSessions = 2;

    /// <summary>
    /// <see cref="Inspect"/> creates a full CPU session: about 3 s for DAT2 or UltraSharpV2. The app inspects a model when it is
    /// selected and again when it is loaded, so the result is kept for as long as the file stays the same.
    /// </summary>
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime WriteTime), Lazy<ModelInfo>> InfoCache = new();

    private readonly Dictionary<(int Width, int Height), InferenceSession> _sessions = [];
    private readonly Lock _lock = new();
    private readonly bool _allowCpuFallback;
    private (ExecutionDevice Device, int Tile)? _tileLimit;
    private bool _gpuSessionCreated;
    private bool _disposed;

    // Official ONNX Runtime builds for Windows write telemetry via TraceLogging by default, and Windows may
    // send it to Microsoft. Krupno sends nothing over the network (see the code signing policy in the README), so turn it off.
    static UpscaleModel() => OrtEnv.Instance().DisableTelemetryEvents();

    private UpscaleModel(ModelInfo info, ExecutionDevice device, bool allowCpuFallback)
    {
        Info = info;
        Device = device;
        _allowCpuFallback = allowCpuFallback;
    }

    public ModelInfo Info { get; }

    /// <summary>The device the model actually runs on (after resolving "Auto" and a possible fallback to CPU).</summary>
    public ExecutionDevice Device { get; private set; }

    /// <summary>Why DirectML failed to start and we fell back to CPU.</summary>
    public string? FallbackReason { get; private set; }

    /// <summary>
    /// Why the accelerator couldn't compile the model for the cache (see <see cref="CompiledModels"/>): it then builds its engine
    /// for every session, that is on every start. Null if it could, or if the model doesn't run on an accelerator.
    /// </summary>
    public string? CompileFailure => Device.Kind == DeviceKind.Accelerator ? CompiledModels.Failure(Device.Provider!, Info.Path) : null;

    /// <summary>
    /// On the GPU the input shape is fixed when the session is created: DirectML compiles the graph for a specific size, and so do
    /// the vendors' providers (TensorRT-RTX builds an engine for it). All tiles of one image share the same window size, so there is
    /// one compilation per image size.
    /// </summary>
    internal bool UsesStaticWindow =>
        Device.Kind is DeviceKind.Gpu or DeviceKind.Accelerator && Info.FixedWidth is null && Info.HasNamedSpatialDims;

    /// <summary>
    /// The tile the window had to be shrunk to after running out of memory on the current device; null if it wasn't shrunk.
    /// Lives as long as the model: subsequent frames and passes start from it and don't waste time on attempts bound to fail.
    /// </summary>
    internal int? TileLimit
    {
        get
        {
            lock (_lock)
                return _tileLimit is { } limit && limit.Device == Device ? limit.Tile : null;
        }
    }

    /// <summary>Remembers that windows larger than tile <paramref name="tile"/> don't fit on the current device.</summary>
    internal void LimitTile(int tile)
    {
        lock (_lock)
        {
            if (_tileLimit is not { } limit || limit.Device != Device || tile < limit.Tile)
                _tileLimit = (Device, tile);
        }
    }

    public static UpscaleModel Load(string path, ExecutionDevice device)
    {
        var info = Inspect(path);
        return new UpscaleModel(info, device.Resolve(), allowCpuFallback: device.Kind == DeviceKind.Auto);
    }

    /// <summary>Reads the metadata and the input/output shape without touching the GPU.</summary>
    public static ModelInfo Inspect(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            throw new FileNotFoundException(string.Format(Strings.Model_FileNotFound, path), path);

        // Lazy shares an inspection that is already running; a failure isn't cached, so the next call tries again.
        var key = (file.FullName.ToUpperInvariant(), file.Length, file.LastWriteTimeUtc);
        var entry = InfoCache.GetOrAdd(key, _ => new Lazy<ModelInfo>(() => InspectCore(file.FullName)));
        try
        {
            return entry.Value;
        }
        catch
        {
            InfoCache.TryRemove(new KeyValuePair<(string, long, DateTime), Lazy<ModelInfo>>(key, entry));
            throw;
        }
    }

    private static ModelInfo InspectCore(string path)
    {
        using var options = new SessionOptions
        {
            // Not DISABLE_ALL: without basic optimizations the CPU provider breaks on fp16 models with explicit Casts
            // (DAT2: "Type Error ... InsertedPrecisionFreeCast"). BASIC costs almost nothing extra.
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_BASIC,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL,
        };
        using var session = new InferenceSession(path, options);

        if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count < 1)
            throw new NotSupportedException(Strings.Model_SingleInput);

        var (inputName, input) = session.InputMetadata.First();
        var (outputName, output) = session.OutputMetadata.First();
        if (!input.IsTensor || input.Dimensions.Length != 4 || !output.IsTensor || output.Dimensions.Length != 4)
            throw new NotSupportedException(Strings.Model_Shape);
        if (input.Dimensions[1] > 0 && input.Dimensions[1] != 3)
            throw new NotSupportedException(Plurals.Format(Strings.Model_RgbOnly, input.Dimensions[1]));

        bool inputIsHalf = IsHalf(input.ElementDataType);
        bool outputIsHalf = IsHalf(output.ElementDataType);
        var meta = session.ModelMetadata.CustomMetadataMap;

        int? fixedHeight = input.Dimensions[2] > 0 ? input.Dimensions[2] : null;
        int? fixedWidth = input.Dimensions[3] > 0 ? input.Dimensions[3] : null;
        if (fixedHeight.HasValue != fixedWidth.HasValue)
            throw new NotSupportedException(Strings.Model_OneFixedSide);

        int multiple = ReadInt(meta, "size_multiple") ?? (fixedWidth.HasValue ? 1 : DefaultSizeMultiple);
        int minimum = ReadInt(meta, "size_minimum") ?? 1;
        int? maximum = ReadInt(meta, "size_maximum") is int limit ? TilePlanner.AlignDown(limit, multiple) : null;
        if (maximum < TilePlanner.AlignUp(minimum, multiple))
            throw new NotSupportedException(string.Format(Strings.Model_ContradictoryMetadata, maximum, minimum));

        int scale = ReadInt(meta, "scale")
            ?? StaticScale(input.Dimensions, output.Dimensions)
            ?? ProbeScale(session, inputName, outputName, inputIsHalf,
                fixedWidth ?? TilePlanner.AlignUp(Math.Max(ProbeSize, minimum), multiple),
                fixedHeight ?? TilePlanner.AlignUp(Math.Max(ProbeSize, minimum), multiple));

        return new ModelInfo
        {
            Path = Path.GetFullPath(path),
            Name = ReadText(meta, "name") ?? Path.GetFileNameWithoutExtension(path),
            Scale = scale,
            Architecture = ReadText(meta, "architecture"),
            License = ReadText(meta, "license"),
            IsHalfPrecision = ReadText(meta, "precision") == "fp16" || inputIsHalf,
            FixedWidth = fixedWidth,
            FixedHeight = fixedHeight,
            SizeMultiple = Math.Max(1, multiple),
            MinimumSize = Math.Max(1, minimum),
            MaximumSize = maximum,
            RequiresSquare = ReadInt(meta, "square") == 1,
            InputName = inputName,
            OutputName = outputName,
            InputIsHalf = inputIsHalf,
            OutputIsHalf = outputIsHalf,
            BatchDimName = SymbolicDim(input, 0),
            HeightDimName = SymbolicDim(input, 2),
            WidthDimName = SymbolicDim(input, 3),
        };
    }

    /// <summary>The window (network input) size for an image of the given size.</summary>
    internal (int Width, int Height) ChooseWindow(int imageWidth, int imageHeight, int tileSize)
    {
        if (Info.FixedWidth is int fixedWidth && Info.FixedHeight is int fixedHeight)
            return (fixedWidth, fixedHeight);

        int multiple = Info.SizeMultiple;
        int minimum = TilePlanner.AlignUp(Info.MinimumSize, multiple);
        int maximum = Info.MaximumSize ?? int.MaxValue;
        int Fit(int size) => Math.Max(TilePlanner.AlignUp(size, multiple), minimum);

        // Without tiling the window is the whole image, but the model's limit always applies: above it the model computes wrong results.
        int tile = tileSize <= 0 ? maximum : Math.Clamp(TilePlanner.AlignDown(tileSize, multiple), minimum, maximum);
        // The window must not extend past the image: padding (mirrored or zero) changes what
        // the network sees at the edge, and deep models like ESRGAN visibly spoil the edge.
        int width = Math.Min(tile, Fit(imageWidth));
        int height = Math.Min(tile, Fit(imageHeight));

        if (Info.RequiresSquare)
            width = height = Math.Max(width, height);
        return (width, height);
    }

    internal InferenceSession GetSession(int width, int height)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var key = UsesStaticWindow ? (width, height) : (0, 0);
            if (_sessions.TryGetValue(key, out var session))
                return session;

            if (_sessions.Count >= MaxCachedSessions)
                ReleaseSessionsCore();

            session = CreateSession(UsesStaticWindow ? (width, height) : null);
            // After a fallback to CPU the shape becomes dynamic, so recompute the key.
            _sessions[UsesStaticWindow ? (width, height) : (0, 0)] = session;
            return session;
        }
    }

    /// <summary>Releases the sessions (and GPU memory). The next run creates them again.</summary>
    public void ReleaseSessions()
    {
        lock (_lock)
            ReleaseSessionsCore();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            ReleaseSessionsCore();
        }
    }

    private void ReleaseSessionsCore()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }

    private InferenceSession CreateSession((int Width, int Height)? shape)
    {
        if (Device.Kind == DeviceKind.Gpu)
        {
            try
            {
                using var gpuOptions = CreateOptions(gpu: true, shape);
                var session = new InferenceSession(Info.Path, gpuOptions);
                _gpuSessionCreated = true;
                return session;
            }
            catch (Exception ex) when (_allowCpuFallback && ShouldFallBackToCpu(ex))
            {
                FallbackReason = ex.Message;
                Device = ExecutionDevice.Cpu;
                // GPU sessions created earlier would never be used again, but would keep holding video memory.
                ReleaseSessionsCore();
            }
        }

        if (Device.Kind == DeviceKind.Accelerator)
        {
            // Chosen on purpose, so no fallback: an error says what is wrong with the accelerator instead of a slow CPU run.
            using var acceleratorOptions = CreateOptions(gpu: false, shape);
            // The engine is built for a fixed window, so only then is there something to keep.
            var window = shape ?? (Info.FixedWidth is int fixedWidth && Info.FixedHeight is int fixedHeight ? (fixedWidth, fixedHeight) : null);
            if (window is var (width, height) && Accelerators.Find(Device) is { } epDevice &&
                CompiledModels.CreateSession(Info, epDevice, acceleratorOptions, width, height) is { } compiled)
            {
                return compiled;
            }
            return new InferenceSession(Info.Path, acceleratorOptions);
        }

        using var cpuOptions = CreateOptions(gpu: false, shape: null);
        return new InferenceSession(Info.Path, cpuOptions);
    }

    /// <summary>
    /// "DirectML doesn't work here" is a reason to move to the CPU; running out of video memory is not: the caller shrinks the tile.
    /// The two can't be told apart by the exception type, so once DirectML has created a session for this model, a later failure
    /// is never treated as "doesn't work", and on the first attempt an out-of-memory message is recognized by its text.
    /// </summary>
    private bool ShouldFallBackToCpu(Exception ex) => ex switch
    {
        DllNotFoundException or EntryPointNotFoundException => true,
        OnnxRuntimeException => !_gpuSessionCreated && !LooksLikeOutOfMemory(ex.Message),
        _ => false,
    };

    internal static bool LooksLikeOutOfMemory(string message) =>
        message.Contains("8007000E", StringComparison.OrdinalIgnoreCase)
        || message.Contains("E_OUTOFMEMORY", StringComparison.OrdinalIgnoreCase)
        || message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
        || message.Contains("not enough memory", StringComparison.OrdinalIgnoreCase);

    private SessionOptions CreateOptions(bool gpu, (int Width, int Height)? shape)
    {
        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // Errors arrive as exceptions anyway (and are handled); ORT's ERROR logs only clutter the console.
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL,
        };
        try
        {
            if (shape.HasValue)
            {
                var (width, height) = shape.Value;
                if (Info.BatchDimName is { } batch)
                    options.AddFreeDimensionOverrideByName(batch, 1);
                options.AddFreeDimensionOverrideByName(Info.HeightDimName!, height);
                options.AddFreeDimensionOverrideByName(Info.WidthDimName!, width);
            }

            if (gpu)
            {
                // DirectML EP requirements.
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.AppendExecutionProvider_DML(Device.AdapterIndex);
            }
            else if (Device.Kind == DeviceKind.Accelerator)
            {
                var epDevice = Accelerators.Find(Device)
                    ?? throw new InvalidOperationException(string.Format(Strings.Error_AcceleratorMissing, Device));
                options.AppendExecutionProvider(OrtEnv.Instance(), [epDevice], new Dictionary<string, string>());
            }
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }

    private static bool IsHalf(TensorElementType type) => type switch
    {
        TensorElementType.Float => false,
        TensorElementType.Float16 => true,
        _ => throw new NotSupportedException(string.Format(Strings.Model_TensorType, type)),
    };

    private static string? SymbolicDim(NodeMetadata node, int axis) =>
        node.Dimensions[axis] <= 0 && node.SymbolicDimensions.Length > axis && !string.IsNullOrEmpty(node.SymbolicDimensions[axis])
            ? node.SymbolicDimensions[axis]
            : null;

    /// <summary>The metadata key with the current prefix, or with the legacy one if it is missing.</summary>
    private static string? ReadText(Dictionary<string, string> meta, string key) =>
        meta.TryGetValue(MetadataPrefix + key, out var value) || meta.TryGetValue(LegacyMetadataPrefix + key, out value)
            ? value
            : null;

    private static int? ReadInt(Dictionary<string, string> meta, string key) =>
        int.TryParse(ReadText(meta, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : null;

    private static int? StaticScale(int[] input, int[] output)
    {
        if (input[2] <= 0 || input[3] <= 0 || output[2] <= 0 || output[3] <= 0)
            return null;
        if (output[2] % input[2] != 0 || output[2] / input[2] != output[3] / input[3])
            return null;
        return output[2] / input[2];
    }

    private static int ProbeScale(InferenceSession session, string inputName, string outputName, bool inputIsHalf, int width, int height)
    {
        long[] shape = [1, 3, height, width];
        int length = 3 * width * height;
        using var input = inputIsHalf
            ? OrtValue.CreateTensorValueFromMemory(new Float16[length], shape)
            : OrtValue.CreateTensorValueFromMemory(new float[length], shape);
        using var runOptions = new RunOptions();
        using var outputs = session.Run(runOptions, [inputName], [input], [outputName]);

        long[] outShape = outputs[0].GetTensorTypeAndShape().Shape;
        if (outShape.Length != 4 || outShape[2] % height != 0 || outShape[3] % width != 0 ||
            outShape[2] / height != outShape[3] / width || outShape[2] < height)
        {
            throw new NotSupportedException(string.Format(Strings.Model_NoScale, string.Join(", ", outShape)));
        }
        return (int)(outShape[2] / height);
    }
}
