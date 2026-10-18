using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace Krupno.Core.Inference;

/// <summary>
/// Which execution provider ONNX Runtime gives each node of a model: its Session_GetEpGraphAssignmentInfo (1.24 and later), which
/// the .NET API doesn't wrap. It is called through the C API's function table (OrtGetApiBase), on a session created there from
/// the environment and the options, whose handles are public (both are SafeHandles); an InferenceSession keeps its own private.
/// </summary>
internal static unsafe partial class NodePlacement
{
    /// <summary>The version of the C API that has the functions below.</summary>
    private const uint ApiVersion = 24;

    // Their slots in OrtApi (onnxruntime_c_api.h). ONNX Runtime only ever appends to the table, so a slot holds in later versions.
    private const int CreateSessionSlot = 7;
    private const int ReleaseStatusSlot = 93;
    private const int ReleaseSessionSlot = 95;
    private const int SessionGetEpGraphAssignmentInfoSlot = 407;
    private const int EpAssignedSubgraphGetEpNameSlot = 408;
    private const int EpAssignedSubgraphGetNodesSlot = 409;
    private const int EpAssignedNodeGetOperatorTypeSlot = 412;

    private const string CpuProvider = "CPUExecutionProvider";

    /// <summary>
    /// The operator types of the nodes that a session of <paramref name="model"/> with <paramref name="options"/> gives to ONNX
    /// Runtime's CPU provider, with how many there are of each; empty if none. A session is created for it (one that records the
    /// assignment, which takes adding an entry to the options) and released. Null if ONNX Runtime can't tell or the session
    /// couldn't be created.
    /// </summary>
    public static SortedDictionary<string, int>? CpuOperators(string model, SessionOptions options)
    {
        options.AddSessionConfigEntry("session.record_ep_graph_assignment_info", "1");
        try
        {
            var getApi = (delegate* unmanaged<uint, nint*>)((nint*)OrtGetApiBase())[0];
            nint* api = getApi(ApiVersion);
            if (api == null)
                return null;

            var createSession = (delegate* unmanaged<nint, char*, nint, nint*, nint>)api[CreateSessionSlot];
            var releaseSession = (delegate* unmanaged<nint, void>)api[ReleaseSessionSlot];
            var getAssignment = (delegate* unmanaged<nint, nint**, nuint*, nint>)api[SessionGetEpGraphAssignmentInfoSlot];
            var getProvider = (delegate* unmanaged<nint, byte**, nint>)api[EpAssignedSubgraphGetEpNameSlot];
            var getNodes = (delegate* unmanaged<nint, nint**, nuint*, nint>)api[EpAssignedSubgraphGetNodesSlot];
            var getOperator = (delegate* unmanaged<nint, byte**, nint>)api[EpAssignedNodeGetOperatorTypeSlot];

            nint session;
            // ORTCHAR_T is wchar_t on Windows. The environment lives as long as the process, the options as long as the caller's call.
            fixed (char* path = Path.GetFullPath(model))
            {
                if (Failed(api, createSession(OnnxEnvironment.Instance.DangerousGetHandle(), path, options.DangerousGetHandle(), &session)))
                    return null;
            }
            try
            {
                nint* subgraphs;
                nuint subgraphCount;
                if (Failed(api, getAssignment(session, &subgraphs, &subgraphCount)))
                    return null;

                var operators = new SortedDictionary<string, int>(StringComparer.Ordinal);
                for (nuint i = 0; i < subgraphCount; i++)
                {
                    byte* provider;
                    if (Failed(api, getProvider(subgraphs[i], &provider)))
                        return null;
                    if (Marshal.PtrToStringUTF8((nint)provider) != CpuProvider)
                        continue;

                    nint* nodes;
                    nuint nodeCount;
                    if (Failed(api, getNodes(subgraphs[i], &nodes, &nodeCount)))
                        return null;
                    for (nuint j = 0; j < nodeCount; j++)
                    {
                        byte* type;
                        if (Failed(api, getOperator(nodes[j], &type)))
                            return null;
                        string name = Marshal.PtrToStringUTF8((nint)type) ?? "?";
                        operators[name] = operators.GetValueOrDefault(name) + 1;
                    }
                }
                return operators;
            }
            finally
            {
                releaseSession(session);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>"Resize", "Resize ×2, GridSample": the operators by type, the most frequent first.</summary>
    public static string Describe(IReadOnlyDictionary<string, int> operators) =>
        string.Join(", ", operators.OrderByDescending(o => o.Value).ThenBy(o => o.Key, StringComparer.Ordinal)
            .Select(o => o.Value > 1 ? $"{o.Key} ×{o.Value}" : o.Key));

    /// <summary>Whether a call failed; its status (the error) is released.</summary>
    private static bool Failed(nint* api, nint status)
    {
        if (status == 0)
            return false;
        ((delegate* unmanaged<nint, void>)api[ReleaseStatusSlot])(status);
        return true;
    }

    [LibraryImport("onnxruntime")]
    private static partial nint OrtGetApiBase();
}
