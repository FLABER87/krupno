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

    /// <summary>
    /// The compiled models (by their file in the cache, so by the model's version, the window and the device) that the provider
    /// failed to compile in this run: trying one again would cost another engine build each time. Per window: a build that fails
    /// for one size (say, too large for the device's limits) may well work for another; and a model replaced by a new version is
    /// compiled again.
    /// </summary>
    private static readonly ConcurrentDictionary<string, (string Provider, string Model, string Message, DateTime At)> Failures =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When a failed compile is tried again after all: a failure that wasn't the provider's (the disk filled up while the engine was
    /// written, say) mustn't keep the cache off for the rest of a long session, and an hour costs a provider that really can't
    /// compile the model only one more attempt an hour.
    /// </summary>
    private static readonly TimeSpan RetryFailedAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// Why the provider couldn't compile the model in this run, for one of its windows (it then builds the engine on every session);
    /// null if it could.
    /// </summary>
    public static string? Failure(string provider, string model) =>
        Failures.Values.FirstOrDefault(f => f.Provider == provider && f.Model == model).Message;

    /// <summary>
    /// ONNX Runtime's error when no provider exported a compiled part of the model: the provider took none of it, or it runs it but
    /// can't export what it compiles. A session with the same options tells the two apart (see <see cref="CpuFallbackRefused"/>).
    /// </summary>
    internal static bool NoNodesCompiled(OnnxRuntimeException ex) =>
        ex.Message.Contains("Unable to compile any nodes", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// ONNX Runtime's error when some operation would go to its CPU provider while "session.disable_cpu_ep_fallback" forbids it:
    /// the accelerator doesn't take the whole model (or DirectML doesn't, see UpscaleModel.CreateGpuSession).
    /// </summary>
    internal static bool CpuFallbackRefused(OnnxRuntimeException ex) =>
        ex.Message.Contains("fallback to CPU EP has been explicitly disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A session from the compiled model for this window (0×0: a model whose input stays dynamic), compiling it first if there is
    /// none yet or it no longer fits the provider or the driver. Null if the provider can't compile it: then the caller builds a
    /// session as before. Throws the <see cref="NoNodesCompiled"/> and <see cref="CpuFallbackRefused"/> errors, which say something
    /// about the model rather than the cache. Without a cache folder with room to write to, the model is still compiled (the engine
    /// would be built for the session anyway) into a temporary file that is loaded once.
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

        if (Failures.TryGetValue(path, out var failure) && DateTime.UtcNow - failure.At < RetryFailedAfter)
            return null;
        if (!HasRoom(Folder, info))
            return HasRoom(Path.GetTempPath(), info) ? CompileOnce(info, device, options, path) : null;
        // Unique per build: a canceled run's build may still be writing while the next run compiles the same window.
        string temp = $"{path}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        try
        {
            Compile(info.Path, options, temp, requireCompiledNodes: true);
        }
        catch (OnnxRuntimeException ex) when (IsCompileFailure(ex))
        {
            // The folder had room a moment ago, so this is the provider failing to compile, not the disk: not tried again.
            Failures[path] = (device.EpName, info.Path, ex.Message, DateTime.UtcNow);
            TryDelete(temp);
            return null;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        // The engine is built: it is used whatever happens to the file. Moving it fails if another Krupno process is reading the
        // same file, which it has just compiled too; then this one loads its own copy once.
        string load = path;
        try
        {
            File.Move(temp, path, overwrite: true);
            Trim(Folder, MaxCacheBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            load = temp;
        }

        try
        {
            return new InferenceSession(load, options);
        }
        catch (OnnxRuntimeException ex) when (IsCompileFailure(ex))
        {
            Failures[path] = (device.EpName, info.Path, ex.Message, DateTime.UtcNow);
            TryDelete(load);
            return null;
        }
        finally
        {
            if (load == temp)
                TryDelete(temp);
        }
    }

    /// <summary>A failure of the provider to compile the model, not running out of memory or one that says something about the model.</summary>
    private static bool IsCompileFailure(OnnxRuntimeException ex) =>
        !UpscaleModel.LooksLikeOutOfMemory(ex.Message) && !NoNodesCompiled(ex) && !CpuFallbackRefused(ex);

    /// <summary>
    /// The model compiled into a temporary file and loaded from it once, for when the cache folder can't be written or has no room.
    /// Null if the provider can't compile it.
    /// </summary>
    private static InferenceSession? CompileOnce(ModelInfo info, OrtEpDevice device, SessionOptions options, string cacheFile)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"krupno-compiled-{Guid.NewGuid():N}.onnx");
        try
        {
            Compile(info.Path, options, temp, requireCompiledNodes: true);
            return new InferenceSession(temp, options);
        }
        catch (OnnxRuntimeException ex) when (IsCompileFailure(ex))
        {
            Failures[cacheFile] = (device.EpName, info.Path, ex.Message, DateTime.UtcNow);
            return null;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>
    /// Whether a compiled model can be written to <paramref name="folder"/> (created if needed): a file can be created there, and
    /// the disk has room for one about the size of the model, twice over. A compile that fails for want of either isn't the
    /// provider's failure, and isn't remembered as one.
    /// </summary>
    private static bool HasRoom(string folder, ModelInfo info)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, $"probe-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            long needed = 2 * (new FileInfo(info.Path).Length + ExternalSize(new FileInfo(info.Path))) + (64L << 20);
            return Path.GetPathRoot(Path.GetFullPath(folder)) is not { Length: > 0 } root || root.StartsWith(@"\\", StringComparison.Ordinal)
                   || new DriveInfo(root).AvailableFreeSpace >= needed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
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
    /// (path, size and time, like <see cref="UpscaleModel.Inspect"/>) and its external weights (<see cref="ExternalData"/>), the
    /// window, the device, the provider with its version and ONNX Runtime's version. A driver update is caught when the file is
    /// opened (<see cref="IsCompatible"/>).
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
            width, height, provider, deviceKey, OnnxEnvironment.Instance.GetVersionString());
        // Only when there are such files, so that the cache of a model without them keeps its names.
        if (ExternalData(file) is { Length: > 0 } external)
            key += "|" + external;
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        string name = string.Concat(Path.GetFileNameWithoutExtension(info.Path).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return $"{name}-{width}x{height}-{provider}-{hash}.onnx";
    }

    /// <summary>
    /// The files the model keeps weights in outside the .onnx file (ONNX external data), each with its size and time: the engine
    /// embeds the weights, so new weights next to an unchanged graph need a new one. Krupno's own converter keeps the weights
    /// inside the model, and then this is empty.
    /// </summary>
    internal static string ExternalData(FileInfo model)
    {
        try
        {
            var locations = Locations.GetOrAdd((model.FullName.ToUpperInvariant(), model.Length, model.LastWriteTimeUtc.Ticks),
                _ => ReadLocations(model.FullName));
            return string.Join(";", locations.Select(location =>
            {
                var file = new FileInfo(Path.Combine(model.DirectoryName!, location));
                return file.Exists ? $"{location}:{file.Length}:{file.LastWriteTimeUtc.Ticks}" : $"{location}:missing";
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>How many bytes the model's external weights take (see <see cref="ExternalData"/>), the engine embeds them too.</summary>
    private static long ExternalSize(FileInfo model)
    {
        try
        {
            return Locations.GetOrAdd((model.FullName.ToUpperInvariant(), model.Length, model.LastWriteTimeUtc.Ticks), _ => ReadLocations(model.FullName))
                .Select(location => new FileInfo(Path.Combine(model.DirectoryName!, location)))
                .Sum(file => file.Exists ? file.Length : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>The external data files of a model, by the model file's path, size and time: reading them means reading the file.</summary>
    private static readonly ConcurrentDictionary<(string Path, long Length, long Ticks), string[]> Locations = new();

    /// <summary>
    /// The "location" entries of the tensors' external_data in the model, relative to its folder. Each entry is a
    /// StringStringEntryProto { key = "location", value = … } in TensorProto's field 13, which protobuf writes as 0x6A &lt;entry
    /// length&gt;, then the two fields 0x0A 0x08 "location" and 0x12 &lt;length&gt; &lt;value&gt; in either order. An entry is taken only
    /// when its frame adds up exactly, so a field of the next message is never read as its value; and the entries are found
    /// without parsing the whole graph.
    /// </summary>
    internal static string[] ReadLocations(string model)
    {
        ReadOnlySpan<byte> key = [0x0A, 0x08, .. "location"u8];
        var bytes = File.ReadAllBytes(model).AsSpan();
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int from = 0, at; (at = bytes[from..].IndexOf(key)) >= 0; from += at + key.Length)
        {
            int start = from + at, end = start + key.Length;
            // The key first: the value follows it, and the entry's frame is right before the key.
            if (Value(bytes, end) is (string after, int afterEnd) && Framed(bytes, start, afterEnd - start))
                found.Add(after);
            // The value first: it ends at the key, and the frame is right before the value.
            else if (ValueEndingAt(bytes, start) is (string before, int beforeStart) && Framed(bytes, beforeStart, end - beforeStart))
                found.Add(before);
        }
        return [.. found];
    }

    private const int MaxLocationLength = 4096;

    /// <summary>The value field (0x12, varint length, UTF-8) that starts at <paramref name="at"/>, with where it ends; or null.</summary>
    private static (string Value, int End)? Value(ReadOnlySpan<byte> bytes, int at)
    {
        if (at < 0 || at >= bytes.Length || bytes[at] != 0x12 || ReadVarint(bytes, at + 1) is not (int length, int i))
            return null;
        return length is > 0 and <= MaxLocationLength && i + length <= bytes.Length
            ? (Encoding.UTF8.GetString(bytes.Slice(i, length)), i + length)
            : null;
    }

    /// <summary>The value field that ends right at <paramref name="end"/>, with where it starts; or null.</summary>
    private static (string Value, int Start)? ValueEndingAt(ReadOnlySpan<byte> bytes, int end)
    {
        for (int length = 1; length <= MaxLocationLength && length < end; length++)
        {
            int at = end - length - VarintLength(length) - 1;
            if (Value(bytes, at) is (string value, int valueEnd) && valueEnd == end)
                return (value, at);
        }
        return null;
    }

    /// <summary>Whether the <paramref name="length"/> bytes at <paramref name="start"/> are exactly one external_data entry: 0x6A and that length before them.</summary>
    private static bool Framed(ReadOnlySpan<byte> bytes, int start, int length)
    {
        int at = start - VarintLength(length) - 1;
        return at >= 0 && bytes[at] == 0x6A && ReadVarint(bytes, at + 1) is (int framed, int next) && framed == length && next == start;
    }

    private static (int Value, int Next)? ReadVarint(ReadOnlySpan<byte> bytes, int at)
    {
        int value = 0;
        for (int shift = 0, i = at; i < bytes.Length && shift < 28; shift += 7)
        {
            byte b = bytes[i++];
            value |= (b & 0x7F) << shift;
            if (b < 0x80)
                return (value, i);
        }
        return null;
    }

    private static int VarintLength(int value) => value < 1 << 7 ? 1 : value < 1 << 14 ? 2 : value < 1 << 21 ? 3 : 4;

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
            var env = OnnxEnvironment.Instance;
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
