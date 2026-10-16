namespace Krupno.Core.Inference;

public enum DeviceKind
{
    /// <summary>The GPU with the most memory, or the CPU if DirectML is unavailable.</summary>
    Auto,
    Gpu,
    Cpu,
}

public sealed record ExecutionDevice(DeviceKind Kind, int AdapterIndex = -1, string? AdapterName = null)
{
    public static ExecutionDevice Auto { get; } = new(DeviceKind.Auto);
    public static ExecutionDevice Cpu { get; } = new(DeviceKind.Cpu);

    public static ExecutionDevice Gpu(GpuAdapter adapter) => new(DeviceKind.Gpu, adapter.Index, adapter.Name);

    /// <summary>"Auto", all GPUs and the CPU, in that order.</summary>
    public static IReadOnlyList<ExecutionDevice> List() =>
        [Auto, .. GpuAdapters.Enumerate().Select(Gpu), Cpu];

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
        _ => $"GPU {AdapterIndex}: {AdapterName}",
    };
}
