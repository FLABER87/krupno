using Krupno.Core.Inference;

namespace Krupno.Core.Tests;

/// <summary>The vendors' accelerators as devices; the Windows ML catalog itself needs Windows 11 24H2 and isn't tested here.</summary>
public class AcceleratorTests
{
    [Theory]
    [InlineData("NvTensorRtRtxExecutionProvider", "TensorRT-RTX")]
    [InlineData("OpenVINOExecutionProvider", "OpenVINO")]
    [InlineData("MIGraphXExecutionProvider", "MIGraphX")]
    [InlineData("VitisAIExecutionProvider", "Vitis AI")]
    [InlineData("QNNExecutionProvider", "QNN")]
    [InlineData("SomeNewExecutionProvider", "SomeNew")]
    public void ProviderNames(string provider, string name)
    {
        Assert.Equal(name, Accelerators.ProviderName(provider));
        Assert.Equal(name, new AcceleratorPackage(provider, IsInstalled: false).Name);
    }

    [Fact]
    public void AcceleratorDevice()
    {
        var device = new ExecutionDevice(DeviceKind.Accelerator, AdapterName: "NVIDIA GeForce RTX 3080",
            Provider: "NvTensorRtRtxExecutionProvider", VendorId: 0x10DE, DeviceId: 0x2206);
        Assert.Equal("NVIDIA GeForce RTX 3080 · TensorRT-RTX", device.ToString());
        // An accelerator is not resolved like "Auto", and is found again by value (the app keeps the selection by it).
        Assert.Same(device, device.Resolve());
        Assert.Equal(device, device with { });
        Assert.NotEqual(device, device with { DeviceId = 0x2208 });
    }
}
