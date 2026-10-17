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
        // This is how CreateSession learns that a provider took none of the model (the CPU stands in for one here).
        string folder = TempFolder();
        try
        {
            using var options = new SessionOptions();
            var error = Assert.Throws<OnnxRuntimeException>(() => CompiledModels.Compile(
                TestImages.Model("nearest_red_x4.onnx"), options, Path.Combine(folder, "compiled.onnx"), requireCompiledNodes: true));
            // An accelerator that takes none of the model is then an error of its own, not a session on the CPU: ONNX Runtime's
            // wording of it must still be recognized.
            Assert.True(CompiledModels.NoNodesCompiled(error), error.Message);
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
    public void ExternalWeightsChangeTheFileName()
    {
        // The engine embeds the weights: new weights next to an unchanged graph need a new engine. A model without external data
        // keeps the name it had before external data was looked for, so existing caches stay valid.
        string folder = TempFolder();
        try
        {
            string model = Path.Combine(folder, "m.onnx");
            File.Copy(TestImages.Model("nearest_red_x4.onnx"), model);
            var info = UpscaleModel.Inspect(model);
            Assert.Empty(CompiledModels.ReadLocations(model));
            Assert.Equal("", CompiledModels.ExternalData(new FileInfo(model)));

            // A tensor's external_data entry { key = "location", value = "weights/w.bin" }, the way protobuf writes it: field 13
            // of TensorProto (0x6A) with the entry's length, then its key and value.
            string external = Path.Combine(folder, "external.onnx");
            File.WriteAllBytes(external, [.. File.ReadAllBytes(model), 0x6A, 25, 0x0A, 0x08, .. "location"u8, 0x12, 13, .. "weights/w.bin"u8]);
            Assert.Equal(["weights/w.bin"], CompiledModels.ReadLocations(external));
            // Protobuf allows the fields in either order. The value then comes first, and the 0x12 field after the entry
            // (GraphProto's name, say) is not its value.
            string reversed = Path.Combine(folder, "reversed.onnx");
            File.WriteAllBytes(reversed,
                [.. File.ReadAllBytes(model), 0x6A, 17, 0x12, 5, .. "x.bin"u8, 0x0A, 0x08, .. "location"u8, 0x12, 4, .. "name"u8]);
            Assert.Equal(["x.bin"], CompiledModels.ReadLocations(reversed));
            // Without the entry's frame it is just bytes that look like one.
            string loose = Path.Combine(folder, "loose.onnx");
            File.WriteAllBytes(loose, [.. File.ReadAllBytes(model), 0x0A, 0x08, .. "location"u8, 0x12, 5, .. "y.bin"u8]);
            Assert.Empty(CompiledModels.ReadLocations(loose));
            var externalInfo = info with { Path = external };
            string Name() => CompiledModels.FileName(externalInfo, "OpenVINOExecutionProvider", 256, 256, "Intel|32902|1|");

            string missing = Name();
            Directory.CreateDirectory(Path.Combine(folder, "weights"));
            File.WriteAllBytes(Path.Combine(folder, "weights", "w.bin"), new byte[10]);
            string withData = Name();
            Assert.NotEqual(missing, withData);
            File.WriteAllBytes(Path.Combine(folder, "weights", "w.bin"), new byte[20]);
            Assert.NotEqual(withData, Name());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
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
