using System.Runtime.InteropServices;

namespace Krupno.Core.Inference;

/// <param name="Index">Adapter index in DXGI; DirectML accepts the same index as device_id.</param>
/// <param name="DriverVersion">The user-mode driver version ("32.0.15.6094"), for bug reports; null if DXGI doesn't report it.</param>
public sealed record GpuAdapter(int Index, string Name, ulong DedicatedMemory, uint VendorId, string? DriverVersion = null,
    uint DeviceId = 0);

/// <summary>Enumerates GPUs directly via DXGI (COM calls through the vtable, no wrappers).</summary>
public static unsafe partial class GpuAdapters
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 2;
    private const uint MicrosoftVendorId = 0x1414;
    private const uint BasicRenderDriverDeviceId = 0x8C;

    /// <summary>KRUPNO_BASIC_RENDER_DRIVER=1 keeps Basic Render Driver in the list, so the DirectML → CPU fallback can be tested on a machine without a GPU.</summary>
    private static readonly bool KeepBasicRenderDriver = Environment.GetEnvironmentVariable("KRUPNO_BASIC_RENDER_DRIVER") == "1";

    // Method slots in the vtable.
    private const int ReleaseSlot = 2;
    private const int FactoryEnumAdapters1Slot = 12;
    private const int AdapterCheckInterfaceSupportSlot = 9;
    private const int AdapterGetDesc1Slot = 10;

    private static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    public static IReadOnlyList<GpuAdapter> Enumerate()
    {
        try
        {
            return EnumerateCore();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    private static List<GpuAdapter> EnumerateCore()
    {
        var result = new List<GpuAdapter>();
        Guid iid = IidDxgiFactory1;
        nint factory;
        if (CreateDXGIFactory1(&iid, &factory) < 0)
            return result;

        try
        {
            var enumAdapters1 = (delegate* unmanaged<nint, uint, nint*, int>)(*(nint**)factory)[FactoryEnumAdapters1Slot];
            for (uint i = 0; ; i++)
            {
                nint adapter;
                int hr = enumAdapters1(factory, i, &adapter);
                if (hr == DxgiErrorNotFound || hr < 0)
                    break;

                try
                {
                    var getDesc1 = (delegate* unmanaged<nint, AdapterDesc1*, int>)(*(nint**)adapter)[AdapterGetDesc1Slot];
                    AdapterDesc1 desc;
                    if (getDesc1(adapter, &desc) >= 0 && !IsSoftware(desc))
                    {
                        result.Add(new GpuAdapter((int)i, new string(desc.Description), desc.DedicatedVideoMemory, desc.VendorId,
                            DriverVersion(adapter), desc.DeviceId));
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }
        return result;
    }

    /// <summary>
    /// "Microsoft Basic Render Driver" (WARP, VendorId 0x1414, DeviceId 0x8C) is always present and is usually flagged SOFTWARE.
    /// But on a machine without a GPU (a VM, a CI runner) the primary adapter has the same name and comes without the flag, so it
    /// is recognized by VendorId and DeviceId. It renders on the same CPU, and DirectML does not start on it on the runner.
    /// </summary>
    private static bool IsSoftware(in AdapterDesc1 desc) =>
        (desc.Flags & DxgiAdapterFlagSoftware) != 0
        || (desc.VendorId == MicrosoftVendorId && desc.DeviceId == BasicRenderDriverDeviceId && !KeepBasicRenderDriver);

    /// <summary>
    /// IDXGIAdapter::CheckInterfaceSupport for IDXGIDevice returns the user-mode driver version as four 16-bit parts (the same
    /// way Chromium reads it for its GPU info).
    /// </summary>
    private static string? DriverVersion(nint adapter)
    {
        var checkInterfaceSupport = (delegate* unmanaged<nint, Guid*, long*, int>)(*(nint**)adapter)[AdapterCheckInterfaceSupportSlot];
        Guid iid = IidDxgiDevice;
        long version;
        return checkInterfaceSupport(adapter, &iid, &version) >= 0
            ? $"{(version >> 48) & 0xFFFF}.{(version >> 32) & 0xFFFF}.{(version >> 16) & 0xFFFF}.{version & 0xFFFF}"
            : null;
    }

    private static void Release(nint unknown) =>
        ((delegate* unmanaged<nint, uint>)(*(nint**)unknown)[ReleaseSlot])(unknown);

    [LibraryImport("dxgi.dll")]
    private static partial int CreateDXGIFactory1(Guid* riid, nint* factory);

    /// <summary>DXGI_ADAPTER_DESC1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint AdapterLuidLow;
        public int AdapterLuidHigh;
        public uint Flags;
    }
}
