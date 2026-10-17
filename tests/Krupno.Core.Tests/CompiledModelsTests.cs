using Microsoft.ML.OnnxRuntime;
using Krupno.Core.Inference;
using Krupno.Core.Processing;

namespace Krupno.Core.Tests;

/// <summary>
/// The cache of compiled models. No accelerator runs here, so the CPU stands in for one: it compiles nothing, but the compile call
/// still writes a model ORT can load, which checks the managed API of the ONNX Runtime that ships with the app.
/// </summary>
public class CompiledModelsTests
{
    [Fact]
    public void CompiledModelGivesTheSameResult()
    {
        string folder = TempFolder();
        try
        {
            string compiled = Path.Combine(folder, "compiled.onnx");
            using (var options = new SessionOptions())
                CompiledModels.Compile(TestImages.Model("nearest_red_x4.onnx"), options, compiled, requireCompiledNodes: false);

            var input = TestImages.Noise(70, 50, seed: 21);
            using var model = UpscaleModel.Load(compiled, ExecutionDevice.Cpu);
            var result = new Upscaler(model).Upscale(input, new UpscaleOptions { TileSize = 32, TilePadding = 4 });
            Assert.Null(TestImages.Difference(TestImages.Nearest(input, 4, false, false, true), result));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void ProviderThatCompilesNothingIsAnError()
    {
        // This is how CreateSession learns that a provider can't compile: it then falls back to an ordinary session.
        string folder = TempFolder();
        try
        {
            using var options = new SessionOptions();
            Assert.Throws<OnnxRuntimeException>(() => CompiledModels.Compile(
                TestImages.Model("nearest_red_x4.onnx"), options, Path.Combine(folder, "compiled.onnx"), requireCompiledNodes: true));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void FileNameChangesWithWhatTheEngineDependsOn()
    {
        var info = UpscaleModel.Inspect(TestImages.Model("nearest_red_x4.onnx"));
        string name = CompiledModels.FileName(info, "NvTensorRTRTXExecutionProvider", 512, 384, "NVIDIA|4318|9988|version=1");

        Assert.StartsWith("nearest_red_x4-512x384-NvTensorRTRTXExecutionProvider-", name);
        Assert.EndsWith(".onnx", name);
        Assert.Equal(name, CompiledModels.FileName(info, "NvTensorRTRTXExecutionProvider", 512, 384, "NVIDIA|4318|9988|version=1"));
        Assert.NotEqual(name, CompiledModels.FileName(info, "NvTensorRTRTXExecutionProvider", 384, 512, "NVIDIA|4318|9988|version=1"));
        Assert.NotEqual(name, CompiledModels.FileName(info, "NvTensorRTRTXExecutionProvider", 512, 384, "NVIDIA|4318|9988|version=2"));
        Assert.NotEqual(name, CompiledModels.FileName(info, "OpenVINOExecutionProvider", 512, 384, "NVIDIA|4318|9988|version=1"));

        var other = UpscaleModel.Inspect(TestImages.Model("legacy_x2.onnx"));
        Assert.DoesNotContain(name[^22..], CompiledModels.FileName(other, "NvTensorRTRTXExecutionProvider", 512, 384, "NVIDIA|4318|9988|version=1"));
    }

    [Fact]
    public void TrimRemovesTheLeastRecentlyUsed()
    {
        string folder = TempFolder();
        try
        {
            var now = DateTime.UtcNow;
            void Write(string name, int size, int hoursAgo)
            {
                string path = Path.Combine(folder, name);
                File.WriteAllBytes(path, new byte[size]);
                File.SetLastWriteTimeUtc(path, now.AddHours(-hoursAgo));
            }
            Write("newest.onnx", 600, 0);
            Write("recent.onnx", 300, 1);
            Write("old.onnx", 300, 2);
            Write("oldest.onnx", 100, 3);
            Write("crashed.onnx.123.tmp", 50, 48);
            Write("compiling.onnx.456.tmp", 50, 0);

            CompiledModels.Trim(folder, 1000);
            Assert.Equal(["compiling.onnx.456.tmp", "newest.onnx", "recent.onnx"],
                Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));

            // A single file larger than the limit is still kept: it is the one just compiled.
            CompiledModels.Trim(folder, 10);
            Assert.Equal(["compiling.onnx.456.tmp", "newest.onnx"],
                Directory.GetFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string TempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "krupno-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
