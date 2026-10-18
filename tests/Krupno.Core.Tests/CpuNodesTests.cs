using Microsoft.ML.OnnxRuntime;
using Krupno.Core.Inference;
using Krupno.Core.Processing;

namespace Krupno.Core.Tests;

/// <summary>
/// Operations DirectML can't run, which ONNX Runtime would quietly run on the CPU. Where each node goes is read through the C API
/// (NodePlacement): with the CPU provider alone every node goes to the CPU, which checks the function table of the ONNX Runtime
/// that ships with the app on any machine. With a GPU, DirectML runs the bicubic fixture (tests/make_fixtures.py) with its bicubic
/// Resize on the CPU; without one those tests are skipped.
/// </summary>
public class CpuNodesTests
{
    [Fact]
    public void PlacementOfTheNodesOnTheCpuProvider()
    {
        using var options = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_FATAL };
        var operators = NodePlacement.CpuOperators(TestImages.Model("cubic_x2.onnx"), options);
        Assert.NotNull(operators);
        Assert.Equal(new Dictionary<string, int> { ["Add"] = 1, ["Mul"] = 2, ["Resize"] = 2 }, operators);
        Assert.Equal("Mul ×2, Resize ×2, Add", NodePlacement.Describe(operators));
    }

    [Fact]
    public void ModelDirectMLRunsWhole()
    {
        var input = TestImages.Noise(150, 100, seed: 3);
        using var model = LoadOnGpu("nearest_red_x4.onnx");
        var result = new Upscaler(model).Upscale(input, new UpscaleOptions { TileSize = 64, TilePadding = 8 });
        SkipOnCpu(model);
        Assert.Null(TestImages.Difference(TestImages.Nearest(input, 4, false, false, true), result));
        Assert.Null(model.CpuNodes);
    }

    [Fact]
    public void ModelDirectMLRunsInPart()
    {
        var input = TestImages.Noise(150, 100, seed: 4);
        var expected = TestImages.Nearest(input, 2, true, false, true);
        var options = new UpscaleOptions { TileSize = 64, TilePadding = 8 };
        using (var model = LoadOnGpu("cubic_x2.onnx"))
        {
            // Nothing is known before a session is built.
            Assert.Null(model.CpuNodes);
            var result = new Upscaler(model).Upscale(input, options);
            SkipOnCpu(model);
            // It still runs, and gives the same result; the nearest-neighbor Resize stays on DirectML.
            Assert.Null(TestImages.Difference(expected, result));
            Assert.Equal("Resize", model.CpuNodes);

            // Sessions built again for the same windows (as after running out of memory) know it as well.
            model.ReleaseSessions();
            Assert.Null(TestImages.Difference(expected, new Upscaler(model).Upscale(input, options)));
            Assert.Equal("Resize", model.CpuNodes);
        }

        // And so does the model loaded again, for the next run.
        using var again = LoadOnGpu("cubic_x2.onnx");
        Assert.Null(TestImages.Difference(expected, new Upscaler(again).Upscale(input, options)));
        Assert.Equal("Resize", again.CpuNodes);
    }

    /// <summary>"Auto": the GPU with the most memory, or the CPU without one (or where DirectML doesn't start).</summary>
    private static UpscaleModel LoadOnGpu(string model) => UpscaleModel.Load(TestImages.Model(model), ExecutionDevice.Auto);

    private static void SkipOnCpu(UpscaleModel model) =>
        Assert.SkipWhen(model.Device.Kind == DeviceKind.Cpu, $"DirectML doesn't run here: {model.FallbackReason ?? "no GPU"}");
}
