namespace Krupno.Core.Inference;

public enum DeviceKind
{
    /// <summary>The GPU with the most memory, or the CPU if DirectML is unavailable.</summary>
    Auto,
    Gpu,
    Cpu,
    /// <summary>A GPU or NPU through its vendor's execution provider from Windows ML (TensorRT-RTX, OpenVINO…), see <see cref="Accelerators"/>.</summary>
    Accelerator,
}

/// <param name="AdapterIndex">The DXGI adapter index for DirectML (<see cref="DeviceKind.Gpu"/>).</param>
/// <param name="Provider">The execution provider's name for an <see cref="DeviceKind.Accelerator"/> ("NvTensorRtRtxExecutionProvider").</param>
/// <param name="VendorId">With <paramref name="DeviceId"/>, which device of the provider's: the PCI IDs ONNX Runtime reports.</param>
public sealed record ExecutionDevice(DeviceKind Kind, int AdapterIndex = -1, string? AdapterName = null, string? Provider = null,
    uint VendorId = 0, uint DeviceId = 0)
{
    public static ExecutionDevice Auto { get; } = new(DeviceKind.Auto);
    public static ExecutionDevice Cpu { get; } = new(DeviceKind.Cpu);

    public static ExecutionDevice Gpu(GpuAdapter adapter) => new(DeviceKind.Gpu, adapter.Index, adapter.Name);

    /// <summary>"Auto", all GPUs through DirectML, the vendors' accelerators Windows has installed, and the CPU, in that order.</summary>
    public static IReadOnlyList<ExecutionDevice> List() =>
        [Auto, .. GpuAdapters.Enumerate().Select(Gpu), .. Accelerators.Devices(), Cpu];

    /// <summary>Turns <see cref="DeviceKind.Auto"/> into a specific device.</summary>
    public ExecutionDevice Resolve()
    {
        if (Kind != DeviceKind.Auto)
            return this;

        var best = GpuAdapters.Enumerate().MaxBy(a => a.DedicatedMemory);
        return best is null ? Cpu : Gpu(best);
    }

    public override string ToString() => Kind switch
    {
        DeviceKind.Auto => "Auto",
        DeviceKind.Cpu => "CPU",
        DeviceKind.Accelerator => $"{AdapterName} · {Accelerators.ProviderName(Provider!)}",
        _ => $"GPU {AdapterIndex}: {AdapterName}",
    };
}
