using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Krupno.Core.Inference;

namespace Krupno.App.Infrastructure;

/// <summary>What a bug report needs to know about the computer: the versions of everything involved, and the GPUs.</summary>
public static class SystemInfo
{
    public static string Describe()
    {
        var lines = new List<string>
        {
            $"Krupno {AppVersion()} · {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture} · .NET {Environment.Version}",
            $"Windows ML {FileVersion("Microsoft.Windows.AI.MachineLearning.dll")} · ONNX Runtime {FileVersion("onnxruntime.dll")} · " +
            $"DirectML {FileVersion("DirectML.dll")}",
        };
        var gpus = GpuAdapters.Enumerate();
        foreach (var gpu in gpus)
            lines.Add($"GPU {gpu.Index}: {gpu.Name}, {Text.Bytes((long)gpu.DedicatedMemory, Log.Culture)}, driver {gpu.DriverVersion ?? "unknown"}");
        if (gpus.Count == 0)
            lines.Add("No GPU: processing runs on the CPU");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The version with the commit it was built from ("0.1.0+5f1c2c4ab"), shortened to a readable length.</summary>
    private static string AppVersion()
    {
        string version = typeof(SystemInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        int plus = version.IndexOf('+');
        return plus >= 0 && version.Length > plus + 10 ? version[..(plus + 10)] : version;
    }

    /// <summary>
    /// The version of a DLL next to the program, read from the file: DirectML.dll from System32 is never used (see
    /// Directory.Build.props), and asking ONNX Runtime itself would start its environment here rather than through OnnxEnvironment.
    /// </summary>
    private static string FileVersion(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).FileVersion ?? "?" : "missing";
    }
}
