using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.OnnxRuntime;

namespace Krupno.Core.Inference;

/// <summary>
/// Models compiled for a vendor's execution provider, kept on disk. A session for TensorRT-RTX builds an engine for the window size
/// (OpenVINO and QNN compile a binary the same way), which takes from seconds to minutes; without the cache that would happen again
/// on every start. The compiled model is an ONNX file with the engine embedded in EPContext nodes, so ORT loads it in place of the
/// original one and the provider only deserializes it.
/// </summary>
internal static class CompiledModels
{
    /// <summary>An engine holds the model's weights, so each file is about the size of the model; the oldest go above this.</summary>
    internal const long MaxCacheBytes = 2L << 30;

    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Krupno", "cache", "compiled");

    /// <summary>The providers and models that failed to compile in this run: trying again would cost another engine build each time.</summary>
    private static readonly ConcurrentDictionary<(string Provider, string Model), string> Failures = new();

    /// <summary>Why the provider couldn't compile the model in this run (it then builds the engine on every session); null if it could.</summary>
    public static string? Failure(string provider, string model) => Failures.GetValueOrDefault((provider, model));

    /// <summary>
    /// A session from the compiled model for this window, compiling it first if there is none yet or it no longer fits the provider
    /// or the driver. Null if the provider can't compile it or the cache can't be written: then the caller builds a session as before.
    /// </summary>
    public static InferenceSession? CreateSession(ModelInfo info, OrtEpDevice device, SessionOptions options, int width, int height)
    {
        string path = Path.Combine(Folder, FileName(info, device, width, height));
        if (File.Exists(path))
        {
            if (IsCompatible(path, device))
            {
                try
                {
                    var session = new InferenceSession(path, options);
                    Touch(path);
                    return session;
                }
                catch (OnnxRuntimeException ex) when (!UpscaleModel.LooksLikeOutOfMemory(ex.Message))
                {
                    // Written by another version of the provider or for another driver: compile it again.
                }
            }
            TryDelete(path);
        }

        if (Failures.ContainsKey((device.EpName, info.Path)))
            return null;
        string temp = $"{path}.{Environment.ProcessId}.tmp";
        try
        {
            Directory.CreateDirectory(Folder);
            Compile(info.Path, options, temp, requireCompiledNodes: true);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException ||
                                   ex is OnnxRuntimeException && !UpscaleModel.LooksLikeOutOfMemory(ex.Message))
        {
            // Not even the cache folder being read-only is worth an extra engine build each time, so it isn't tried again either.
            Failures[(device.EpName, info.Path)] = ex.Message;
            TryDelete(temp);
            return null;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        Trim(Folder, MaxCacheBytes);

        try
        {
            return new InferenceSession(path, options);
        }
        catch (OnnxRuntimeException ex) when (!UpscaleModel.LooksLikeOutOfMemory(ex.Message))
        {
            Failures[(device.EpName, info.Path)] = ex.Message;
            TryDelete(path);
            return null;
        }
    }

    /// <summary>
    /// Compiles the model with the providers and settings of <paramref name="options"/> into <paramref name="output"/>, with the
    /// providers' binaries embedded. <paramref name="requireCompiledNodes"/>: fail if no provider compiled anything.
    /// </summary>
    internal static void Compile(string model, SessionOptions options, string output, bool requireCompiledNodes)
    {
        using var compile = new OrtModelCompilationOptions(options);
        compile.SetInputModelPath(model);
        compile.SetOutputModelPath(output);
        compile.SetEpContextEmbedMode(true);
        if (requireCompiledNodes)
            compile.SetFlags(OrtCompileApiFlags.ERROR_IF_NO_NODES_COMPILED);
        compile.CompileModel();
    }

    /// <summary>
    /// The file for a model, a provider on a device and a window. The hash covers what makes an engine different: the model file
    /// (path, size and time, like <see cref="UpscaleModel.Inspect"/>), the window, the device, the provider with its version and
    /// ONNX Runtime's version. A driver update is caught when the file is opened (<see cref="IsCompatible"/>).
    /// </summary>
    internal static string FileName(ModelInfo info, OrtEpDevice device, int width, int height)
    {
        var hardware = device.HardwareDevice;
        return FileName(info, device.EpName, width, height,
            $"{device.EpVendor}|{hardware.VendorId}|{hardware.DeviceId}|{Entries(device.EpMetadata)}|{Entries(hardware.Metadata)}");
    }

    internal static string FileName(ModelInfo info, string provider, int width, int height, string deviceKey)
    {
        var file = new FileInfo(info.Path);
        string key = string.Join("|", file.FullName.ToUpperInvariant(), file.Length, file.LastWriteTimeUtc.Ticks,
            width, height, provider, deviceKey, OrtEnv.Instance().GetVersionString());
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        string name = string.Concat(Path.GetFileNameWithoutExtension(info.Path).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return $"{name}-{width}x{height}-{provider}-{hash}.onnx";
    }

    /// <summary>Removes the least recently used files above <paramref name="limit"/> bytes in all; the newest one always stays.</summary>
    internal static void Trim(string folder, long limit)
    {
        try
        {
            var files = new DirectoryInfo(folder).GetFiles("*.onnx").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            for (int i = 0; i < files.Count; i++)
            {
                total += files[i].Length;
                if (i > 0 && total > limit)
                    TryDelete(files[i].FullName);
            }
            // Left behind by a process that was closed while compiling.
            foreach (var temp in new DirectoryInfo(folder).GetFiles("*.tmp").Where(f => f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)))
                TryDelete(temp.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsCompatible(string path, OrtEpDevice device)
    {
        try
        {
            var env = OrtEnv.Instance();
            // A provider that doesn't write compatibility info leaves the answer to loading the file.
            return env.GetCompatibilityInfoFromModel(path, device.EpName) is not { } compatibility
                || env.GetModelCompatibilityForEpDevices([device], compatibility) is not
                    (OrtCompiledModelCompatibility.EP_UNSUPPORTED or OrtCompiledModelCompatibility.EP_SUPPORTED_PREFER_RECOMPILATION);
        }
        catch (OnnxRuntimeException)
        {
            return false;
        }
    }

    private static string Entries(OrtKeyValuePairs? pairs) =>
        pairs is null ? "" : string.Join(";", pairs.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key}={e.Value}"));

    /// <summary>The time of the last use, for <see cref="Trim"/>.</summary>
    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use by another Krupno process.
        }
    }
}
