namespace TlaLink;

public interface IAnalysisResult
{
    int SchemaVersion { get; }
    string Kind { get; }
    string Status { get; }
    List<Issue> Diagnostics { get; }
}
public interface IInputEvidence
{
    Dictionary<string, string> Fingerprints { get; }
}
public interface IArtifactEvidence : IInputEvidence
{
    Dictionary<string, string> Artifacts { get; }
}
public sealed record RequirementResult(string Id, string Status, List<string> Missing);
public sealed record ReviewResult(string Obligation, string Status, ReviewDecision? Decision);
public sealed record AcceptanceMethodSummary(string Scope, string Binding, string Abstraction,
    List<string> Assumptions, List<string> Limits, string Inspection, string InspectionStatus, string Boundary);
public sealed record AcceptanceResult(int SchemaVersion, string Kind, string Project, string Scenario,
    string Run, string RunSha256, string ExecutionStatus, string ArchiveStatus, string CurrentApplicability,
    string AcceptanceStatus, List<RequirementResult> Requirements, List<Issue> Diagnostics,
    List<CheckCoverage> CheckCoverage, List<AcceptanceMethodSummary> MethodSummaries,
    List<ReviewResult> Reviews, List<string> NextActions, string Boundary);
public sealed record PlannedMethodSummary(string Scope, MethodSummary Summary, string Method,
    Endpoint Implementation, Endpoint Specification, Endpoint? Precondition, string InspectionStatus,
    ReviewOrigin InspectionOrigin, string Boundary);
public sealed record StoredOutcome(int Fingerprint, string Outcome, double DurationSeconds, DateTimeOffset StoredAt);
public sealed record PlannedStage(string Id, string Kind, List<string> DependsOn, int TimeoutSeconds,
    List<MethodSummaryUse>? MethodSummaries, TlcResources? RequestedResources, TlcResources? Resources,
    string Capability, int? Fingerprint, List<StoredOutcome>? Stored, List<AnalysisRequirement> Requirements);
public sealed record PlannedScenario(string Id, List<PlannedStage> Stages);
public sealed record PlannedReview(string Id, ReviewOrigin Origin, string ClaimSha256, string Status);
public sealed record ProjectPlan(int SchemaVersion, string Kind, string Project, string Status,
    CompilerProfile Compiler, int AnnotationVersion, List<PlannedMethodSummary> MethodSummaries,
    List<CheckCoverage> CheckCoverage, List<Issue> Diagnostics, Dictionary<string, string> Fingerprints,
    List<PlannedScenario> Scenarios, List<PlannedReview> ReviewStatus, string Boundary);
public sealed record RecordedReview(int SchemaVersion, string Kind, string Path, ReviewDecision Decision, string Boundary);
public sealed record CommandFailure(int SchemaVersion, string Kind, string? Command, string Status, List<Issue> Diagnostics) : IAnalysisResult;
public sealed record CommandInvocation(string[] Arguments, string WorkingDirectory, string ToolAssembly);
public sealed record ModelInputsResult(int SchemaVersion, string Kind, string Command, string Status,
    Dictionary<string, string> Fingerprints, List<Issue> Diagnostics, CommandInvocation Invocation) : IAnalysisResult, IInputEvidence;
public sealed record ProjectSchemaResult(int SchemaVersion, string Kind, string Status, string Path);
public sealed record PreparedSourcesInspection(int SchemaVersion, string Kind, string Status,
    int FileCount, int MutatedFileCount, string Boundary);
public sealed record ReportResult(int SchemaVersion, string Kind, string Status, string CurrentApplicability,
    string Output, int Nodes, int CodeRegions, int InputFiles, List<Issue> Diagnostics, string Boundary);
public sealed record StageEvidenceInspection(int SchemaVersion, string Kind, string ArchiveStatus,
    string SemanticStatus, string CurrentApplicability, string RecordedStatus, string AcceptanceStatus,
    List<Issue> Diagnostics, string Boundary);
public sealed record NativeProcessInvocation(int SchemaVersion, string Kind, string Executable,
    List<string> Arguments, string WorkingDirectory, int TimeoutSeconds);
