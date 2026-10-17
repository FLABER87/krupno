using Microsoft.ML.OnnxRuntime;

namespace Krupno.Core.Inference;

/// <summary>
/// ONNX Runtime's environment, the one for the whole process, set up once before anything else uses it: a session, the list
/// of the providers' devices, or Windows ML registering the vendors' providers into it (Microsoft's samples create the
/// environment before that, so the registrations land in the environment the sessions use).
/// </summary>
internal static class OnnxEnvironment
{
    private static readonly Lazy<OrtEnv> Env = new(() =>
    {
        var env = OrtEnv.Instance();
        // Official ONNX Runtime builds for Windows write telemetry via TraceLogging by default, and Windows may send it to
        // Microsoft. Apart from the update check, Krupno sends nothing over the network (see "Privacy" in
        // installer/signpath/README.md), so turn it off before the first session or device list, not only when a model is loaded.
        env.DisableTelemetryEvents();
        return env;
    });

    public static OrtEnv Instance => Env.Value;
}
