namespace TlaLink;

public sealed record TlcResult(int SchemaVersion, string Kind, string Status, List<Issue> Diagnostics,
    string Model, string Config, int ExitCode, int Fingerprint, TlcResources? RequestedResources,
    TlcResources Resources, NativeProcessInvocation Invocation, TlcStatistics Statistics, List<string> Boundaries)
    : IAnalysisResult;
public sealed record StageCheckResult(int SchemaVersion, string Kind, string Project, string Scenario,
    string Stage, string Status, string Outcome, int Fingerprint, string Key, string Family, string? Entry,
    double DurationSeconds, TlcResources? RequestedResources, TlcResources Resources, int TimeoutSeconds,
    TlcStatistics? Statistics, List<Issue>? Diagnostics, string Boundary);
