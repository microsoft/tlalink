using System.Text.Json;
using TlaLink.Runtime;

namespace TlaLink;

public sealed record TraceValidationInvocation(string Executable, string[] Arguments, string WorkingDirectory);

public sealed record TraceValidationMapping(string Init, string Next, List<string> Hidden);

public sealed record TraceValidationResult(int SchemaVersion, string Kind, string Command, string Status,
    int EventCount, string BindingId, int ExitCode, bool TimedOut, TlcResources? RequestedResources, TlcResources Resources,
    int TimeoutSeconds, TraceValidationInvocation Invocation, TraceValidationMapping Mapping, List<Issue> Diagnostics,
    string WrapperSHA256, string ConfigSHA256, Dictionary<string, string> Fingerprints, string LogSHA256, string Scope)
    : IAnalysisResult, IInputEvidence;

public static class TraceDocuments
{
    private static readonly JsonSerializerOptions Json = new(Files.Json) { AllowDuplicateProperties = false };

    public static TraceEnvelope Read(string path)
    {
        TraceEnvelope trace = JsonSerializer.Deserialize<TraceEnvelope>(Files.Read(path), Json)
            ?? throw new InputException(ErrorCode.InvalidTrace, "Invalid trace envelope.");
        if (trace.SchemaVersion != Schema.Version || trace.Kind != "trace")
            throw new InputException(ErrorCode.InvalidTrace, $"Unsupported document version or kind; expected schema {Schema.Version} trace.");
        if (trace.Events.Any(entry => entry is null) || trace.Binaries.Values.Any(hash => hash is null))
            throw new InputException(ErrorCode.InvalidTrace, "Trace events and binary hashes cannot be null.");
        return trace;
    }
}
