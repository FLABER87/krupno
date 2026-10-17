using System.Globalization;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;

namespace Krupno.Core.Inference;

/// <summary>A vendor's execution provider that Windows ML offers for this computer.</summary>
/// <param name="Provider">The provider's name in ONNX Runtime ("NvTensorRtRtxExecutionProvider").</param>
/// <param name="IsInstalled">Whether it is ready for use in this process; one that isn't (not downloaded yet, or present but not
/// ready, and making it ready at startup failed) is made ready by <see cref="Accelerators.InstallAsync"/>.</param>
public sealed record AcceleratorPackage(string Provider, bool IsInstalled)
{
    public string Name => Accelerators.ProviderName(Provider);
}

/// <summary>
/// The GPU and NPU vendors' execution providers that Windows ML brings on Windows 11 24H2 and later: NVIDIA TensorRT-RTX,
/// Intel OpenVINO, AMD MIGraphX and Vitis AI, Qualcomm QNN. Windows downloads and updates them itself (from Microsoft, as
/// packages); Krupno registers the ones that are there with ONNX Runtime and downloads one only when asked to. On Windows 10,
/// or where Windows ML has nothing for the hardware, the list is empty and DirectML and the CPU remain.
/// </summary>
public static class Accelerators
{
    /// <summary>The providers built into ONNX Runtime itself; Krupno uses them as "GPU" (DirectML) and "CPU".</summary>
    private static readonly HashSet<string> BuiltIn = ["CPUExecutionProvider", "DmlExecutionProvider"];

    private static readonly Dictionary<string, string> Names = new()
    {
        ["NvTensorRtRtxExecutionProvider"] = "TensorRT-RTX",
        ["OpenVINOExecutionProvider"] = "OpenVINO",
        ["MIGraphXExecutionProvider"] = "MIGraphX",
        ["VitisAIExecutionProvider"] = "Vitis AI",
        ["QNNExecutionProvider"] = "QNN",
    };

    /// <summary>Registering loads the providers' libraries (hundreds of megabytes for some): a hung one must not hang the app.</summary>
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The key of an OrtHardwareDevice's metadata with the GPU's DXGI adapter index, which tells two identical cards apart.</summary>
    private const string AdapterNumberKey = "DxgiAdapterNumber";

    private static readonly Lazy<string?> Registration = new(RegisterInstalledCore);

    /// <summary>Why the installed providers couldn't be registered (no Windows ML catalog on this Windows, for example); null if they were.</summary>
    public static string? RegistrationError => Registration.Value;

    /// <summary>
    /// A registration that gave no answer within <see cref="RegistrationTimeout"/> goes on in the background: this completes when
    /// it ends (successful or not), and <see cref="Devices"/> may then list more. Already complete otherwise.
    /// </summary>
    public static Task LateRegistration { get; private set; } = Task.CompletedTask;

    /// <summary>"TensorRT-RTX" for "NvTensorRtRtxExecutionProvider"; an unknown provider by its name without the suffix.</summary>
    public static string ProviderName(string provider) =>
        Names.TryGetValue(provider, out string? name) ? name : provider.Replace("ExecutionProvider", "", StringComparison.Ordinal);

    /// <summary>Registers the providers Windows already has with ONNX Runtime, without downloading anything. The first call does it.</summary>
    public static void RegisterInstalled() => _ = Registration.Value;

    /// <summary>The devices the registered providers can run on, as <see cref="DeviceKind.Accelerator"/> devices.</summary>
    public static IReadOnlyList<ExecutionDevice> Devices()
    {
        RegisterInstalled();
        try
        {
            var adapters = GpuAdapters.Enumerate();
            // Distinct: a provider registered twice (at startup and again after a download) lists its devices twice.
            return OnnxEnvironment.Instance.GetEpDevices()
                .Where(d => !BuiltIn.Contains(d.EpName))
                .Select(d => new ExecutionDevice(DeviceKind.Accelerator, AdapterNumber(d.HardwareDevice),
                    DeviceName(d.HardwareDevice, adapters), d.EpName, d.HardwareDevice.VendorId, d.HardwareDevice.DeviceId))
                .Distinct()
                .ToList();
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    /// <summary>The providers Windows ML offers for this computer, installed or not; empty where it offers none.</summary>
    public static IReadOnlyList<AcceleratorPackage> Packages()
    {
        try
        {
            // Only certified ones: those are what the registration at startup takes, so one installed here is there next time too.
            return ExecutionProviderCatalog.GetDefault().FindAllProviders()
                .Where(p => p.Certification == ExecutionProviderCertification.Certified)
                .Select(p => new AcceleratorPackage(p.Name, p.ReadyState == ExecutionProviderReadyState.Ready))
                .ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No catalog on this Windows (before 11 24H2 the class may not even be registered), or it failed: nothing to offer.
            return [];
        }
    }

    /// <summary>
    /// Downloads a provider through Windows (it can take minutes: some are hundreds of megabytes) and registers it with
    /// ONNX Runtime, so that <see cref="Devices"/> lists its devices.
    /// </summary>
    /// <param name="progress">0..1.</param>
    public static Task InstallAsync(string provider, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        // Off the caller's thread throughout (the window's, from the app): the catalog is a COM call, and registering loads the
        // provider's libraries, hundreds of megabytes for some.
        Task.Run(async () =>
        {
            // The environment first, as for the registration at startup: the provider is registered into the one the sessions use.
            _ = OnnxEnvironment.Instance;
            var package = ExecutionProviderCatalog.GetDefault().FindAllProviders().FirstOrDefault(p => p.Name == provider)
                ?? throw new InvalidOperationException($"Windows ML doesn't offer {provider} for this computer.");
            var result = await package.EnsureReadyAsync()
                .AsTask(cancellationToken, new Progress<double>(percent => progress?.Report(percent / 100)));
            if (result.Status != ExecutionProviderReadyResultState.Success)
            {
                string detail = !string.IsNullOrWhiteSpace(result.DiagnosticText) ? result.DiagnosticText
                    : result.ExtendedError is { } error ? $"{error.Message} (0x{error.HResult:X8})" : result.Status.ToString();
                throw new InvalidOperationException(detail);
            }
            if (!package.TryRegister())
                throw new InvalidOperationException($"{provider} was installed but couldn't be registered with ONNX Runtime.");
        }, cancellationToken);

    /// <summary>The provider's device for <paramref name="device"/>, or null if it is gone (the provider was removed or updated away).</summary>
    internal static OrtEpDevice? Find(ExecutionDevice device)
    {
        RegisterInstalled();
        return OnnxEnvironment.Instance.GetEpDevices().FirstOrDefault(d => d.EpName == device.Provider
            && d.HardwareDevice.VendorId == device.VendorId && d.HardwareDevice.DeviceId == device.DeviceId
            && AdapterNumber(d.HardwareDevice) == device.AdapterIndex);
    }

    private static string? RegisterInstalledCore()
    {
        try
        {
            // The environment first, so that the providers are registered into the one the sessions use.
            _ = OnnxEnvironment.Instance;
            var catalog = ExecutionProviderCatalog.GetDefault();
            var registration = Task.Run(async () =>
            {
                await catalog.RegisterCertifiedAsync().AsTask();
                // "Present but not ready in the current process" (installed by Windows or another app, or just updated): the
                // package is there, it only has to be made available to this process, which downloads nothing. Without this
                // such a provider would never be registered, and it isn't offered for download either, since Windows has it.
                foreach (var provider in catalog.FindAllProviders().Where(p =>
                             p.Certification == ExecutionProviderCertification.Certified && p.ReadyState == ExecutionProviderReadyState.NotReady))
                {
                    if ((await provider.EnsureReadyAsync().AsTask()).Status == ExecutionProviderReadyResultState.Success)
                        provider.TryRegister();
                }
            });
            if (registration.Wait(RegistrationTimeout))
                return null;
            LateRegistration = registration.ContinueWith(_ => { }, TaskScheduler.Default);
            return $"no answer from Windows ML in {RegistrationTimeout.TotalSeconds:0} s";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (ex as AggregateException)?.InnerException?.Message ?? ex.Message;
        }
    }

    /// <summary>The GPU's DXGI adapter index as ONNX Runtime reports it; -1 for an NPU or if it doesn't say.</summary>
    private static int AdapterNumber(OrtHardwareDevice device) =>
        device.Metadata.Entries.TryGetValue(AdapterNumberKey, out string? number)
        && int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ? index : -1;

    /// <summary>
    /// The device's name: DXGI's for a GPU (the one the GPU list shows), the description ONNX Runtime reports otherwise
    /// (an NPU), or the vendor and the device type.
    /// </summary>
    private static string DeviceName(OrtHardwareDevice device, IReadOnlyList<GpuAdapter> adapters)
    {
        if (adapters.FirstOrDefault(a => a.VendorId == device.VendorId && a.DeviceId == device.DeviceId) is { } adapter)
            return adapter.Name.Replace("(R)", "").Replace("(TM)", "").Trim();
        if (device.Metadata.Entries.TryGetValue("Description", out string? description) && !string.IsNullOrWhiteSpace(description))
            return description.Trim();
        return $"{device.Vendor} {device.Type}".Trim();
    }
}
