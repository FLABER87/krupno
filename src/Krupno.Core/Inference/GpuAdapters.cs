using System.Runtime.InteropServices;

namespace Krupno.Core.Inference;

/// <param name="Index">Adapter index in DXGI; DirectML accepts the same index as device_id.</param>
public sealed record GpuAdapter(int Index, string Name, ulong DedicatedMemory, uint VendorId);

/// <summary>Enumerates GPUs directly via DXGI (COM calls through the vtable, no wrappers).</summary>
public static unsafe partial class GpuAdapters
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 2;

    // Method slots in the vtable.
    private const int ReleaseSlot = 2;
    private const int FactoryEnumAdapters1Slot = 12;
    private const int AdapterGetDesc1Slot = 10;

    private static readonly Guid IidDxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

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
                    if (getDesc1(adapter, &desc) >= 0 && (desc.Flags & DxgiAdapterFlagSoftware) == 0)
                        result.Add(new GpuAdapter((int)i, new string(desc.Description), desc.DedicatedVideoMemory, desc.VendorId));
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
