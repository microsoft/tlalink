using System.Text.Json;
using System.Text.Json.Serialization;

namespace TlaLink;

public abstract record CommandSummaryResult(string ResultKind)
{
    public int SchemaVersion => Schema.Version;
    public string Kind => "command-summary";
    public bool PresentationOnly => true;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Output { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Result { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Run { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Runs { get; init; }
    public string ToJsonString(JsonSerializerOptions? options = null) =>
        JsonSerializer.Serialize(this, GetType(), options ?? Files.Json);
}

public sealed record FailureSummary(string? Command, string Status, List<Issue> Diagnostics)
    : CommandSummaryResult("command-error");
public sealed record RequirementSummary(string Id, string Status, List<string> Missing,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, int>? Omitted = null);
public sealed record CoverageSummary(string Id, string Kind, string Scenario, string Status,
    List<string> Requirements, List<string> Gaps,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, int>? Omitted = null);
public sealed record ReviewSummary(string Id, string Status, string? Disposition, string? Reviewer,
    string? Rationale, DateTimeOffset? RecordedAt, ReviewOrigin? Origin);
public sealed record AcceptanceSummary(string Project, string Scenario, string ExecutionStatus,
    string ArchiveStatus, string CurrentApplicability, string AcceptanceStatus,
    List<RequirementSummary> Requirements, List<Issue> Diagnostics, List<CoverageSummary> CheckCoverage,
    List<AcceptanceMethodSummary> MethodSummaries, List<ReviewSummary> ReviewStatus,
    List<string> NextActions, string Boundary) : CommandSummaryResult("acceptance-result")
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AcceptanceWorklistResult? Worklist { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? DiagnosticCounts { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? RequirementsCounts { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? ReviewStatusCounts { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? Counts { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, int>? Omitted { get; init; }
}
public sealed record WorkflowStageSummary(string Id, string Kind, string ExecutionStatus, string Outcome,
    double DurationSeconds, string Evidence, List<string> Diagnostics, TlcStatistics? Statistics,
    TlcResources? RequestedResources, TlcResources? Resources, bool Reused);
public sealed record ReuseSummary(int Reused, int Executed);
public sealed record WorkflowRunSummary(string Project, string Scenario, string ExecutionStatus,
    string AcceptanceStatus, List<WorkflowStageSummary> Stages, List<Issue> Diagnostics,
    ReuseSummary Reuse, List<ResolvedMethodSummaryView> MethodSummaries) : CommandSummaryResult("analysis-run");
public sealed record ResolvedMethodSummaryView(string Scope, MethodSummary Summary, string Method,
    EndpointSummary? Precondition);
public sealed record PlannedMethodSummaryView(string Scope, MethodSummary Summary, string Method,
    string InspectionStatus, ReviewOrigin InspectionOrigin, string Boundary);
public sealed record PlannedStageSummary(string Id, string Kind, List<string> DependsOn,
    TlcResources? RequestedResources, TlcResources? Resources, int? Fingerprint,
    List<StoredOutcome>? Stored, List<MethodSummaryUse>? MethodSummaries);
public sealed record PlannedScenarioSummary(string Id, List<PlannedStageSummary> Stages);
public sealed record ProjectPlanSummary([property: JsonIgnore] string NativeKind, string Project, string Status, int AnnotationVersion,
    List<PlannedMethodSummaryView> MethodSummaries, List<CoverageSummary> CheckCoverage,
    List<Issue> Diagnostics, List<PlannedScenarioSummary> Scenarios, List<PlannedReview> ReviewStatus,
    string Boundary) : CommandSummaryResult(NativeKind);
public sealed record SourceInventorySummary(string Status, int AnnotationVersion, int SourceCount,
    int MemberCount, int CallCount, int AwaitCount, SourceCompilerContext Compiler,
    List<Issue> Diagnostics, List<string> Boundaries) : CommandSummaryResult("source-inventory");
public sealed record SourceSymbolsSummary(string Status, SourceSymbolQuery Query, int MatchCount,
    List<SourceSymbol> Symbols, SourceCompilerContext Compiler, List<string> Sources,
    SourceQueryDetails? Details, int? ReferenceCount, int? CallCount, List<Issue> Diagnostics,
    List<string> Boundaries) : CommandSummaryResult("source-symbols");
public sealed record SourceReviewSummary(string Command, string LinksStatus, string CoverageStatus,
    string ConformanceStatus, List<Issue> Diagnostics) : CommandSummaryResult("source-review");
public sealed record SourceObligationSummary(SourceObligation Obligation, string Status, string Reason);
public sealed record IntegratedSummary(string Command, string Status, string LinksStatus,
    string CoverageStatus, string ProofStatus, string AuditStatus, string ConformanceStatus,
    string CorrespondenceStatus, string? SourceObligationsStatus, string ProvenanceStatus,
    List<SourceObligationSummary> SourceObligations, Dictionary<string, StageResult> Stages,
    List<Issue> Diagnostics, List<string> Boundaries) : CommandSummaryResult("integrated-result");
public sealed record ProcessSummary(string Id, int? ExitCode, bool? TimedOut, double? DurationSeconds);
public sealed record ApalacheSummary(string Status, string Claim, string Mode, string Directory,
    List<ProcessSummary> Processes, List<Issue> Diagnostics, List<string> Boundaries)
    : CommandSummaryResult("apalache-result");
public sealed record PAnalysisSummary(string Command, string Status, string Claim, string LinksStatus,
    string ProvenanceStatus, List<ProcessSummary> Processes, List<Issue> Diagnostics, List<string> Boundaries)
    : CommandSummaryResult("p-result");
public sealed record DafnySummary(string Status, string Claim, string Directory, string ProofStatus,
    string AuditStatus, Dictionary<string, StageResult> Stages, List<Issue> Diagnostics, string Boundary)
    : CommandSummaryResult("dafny-result");
public sealed record RegressionSummary(string Status, string Claim, string Directory,
    List<Issue> Diagnostics, string Boundary) : CommandSummaryResult("csharp-regression-result");
public sealed record EvidenceInspectionSummary([property: JsonIgnore] string NativeKind, string ArchiveStatus,
    string CurrentApplicability, List<Issue> Diagnostics, string? SemanticStatus = null,
    string? RecordedStatus = null, string? AcceptanceStatus = null, string? AnalysisStatus = null,
    string? Status = null, string? Boundary = null) : CommandSummaryResult(NativeKind);
public sealed record PreparedSummary([property: JsonIgnore] string NativeKind, string Status, int FileCount, int MutatedFileCount,
    string Boundary) : CommandSummaryResult(NativeKind);
public sealed record ReportSummary(string Status, string CurrentApplicability, int Nodes, int CodeRegions,
    int InputFiles, List<Issue> Diagnostics, string Boundary) : CommandSummaryResult("report-result");
public sealed record SchemaSummary(string Status, string Path) : CommandSummaryResult("project-schema-result");
public sealed record RecordedReviewSummary(string Path, ReviewDecisionSummary Decision, string Boundary)
    : CommandSummaryResult("recorded-review");
public sealed record ReviewDecisionSummary(string Obligation, string Disposition, string Reviewer,
    string Rationale, ReviewOrigin Origin);
public sealed record ModelInputsSummary(string Command, string Status, List<Issue> Diagnostics)
    : CommandSummaryResult("model-inputs");
public sealed record InstrumentationSummary(int AnnotationVersion, string Boundary)
    : CommandSummaryResult("instrumentation-manifest");
public sealed record ProjectExpansionSummary(string Project) : CommandSummaryResult("project-expansion");
public sealed record ExecutionSummary(string Boundary) : CommandSummaryResult("execution-receipt");
public sealed record TraceSummary(string Command, string Status, List<Issue> Diagnostics,
    TlcResources? RequestedResources, TlcResources Resources) : CommandSummaryResult("trace-validation");
public sealed record StageCheckSummary(string Project, string Scenario, string Stage, string Status,
    string Outcome, int Fingerprint, string Key, string Family, string? Entry, double DurationSeconds,
    TlcResources? RequestedResources, TlcResources Resources, int TimeoutSeconds,
    TlcStatistics? Statistics, List<Issue>? Diagnostics, string Boundary) : CommandSummaryResult("stage-check");
public sealed record TlcSummary(string Status, List<Issue> Diagnostics, int Fingerprint,
    TlcResources? RequestedResources, TlcResources Resources, TlcStatistics Statistics, List<string> Boundaries)
    : CommandSummaryResult("tlc-result");
public sealed record EndpointSummary(string Name, string Scope, string Kind, Place Location);
public sealed record MaintainedLinkSummary(string Id, string? Reason, PreparationTarget? Source,
    EndpointSummary Endpoint, List<CaptureAnnotation>? Captures);
public sealed record LinkMaintenanceSummary(string Action, string Status, string Target, string Language,
    List<MaintainedLinkSummary> Links, LinkEdit? Edit, List<Issue> Diagnostics, string Boundary)
    : CommandSummaryResult("link-maintenance-result");
public sealed record LinkMigrationSummary(string Status, string Scope, bool ReadOnly, bool StripSource,
    int BindingCount, int ExclusionCount, List<LinkMigrationFile> Files, List<string> Applied, string Boundary)
    : CommandSummaryResult("link-migration-result");
public sealed record GcEntrySummary(string Path, string Status, long? LogicalBytes, bool RecheckRequired,
    int ReasonCount, int DependencyCount);
public sealed record GcRecheckSummary(string Path, string Kind, string Scope, int ChangedInputCount,
    int AffectedDependencyCount, List<string> Actions);
public sealed record GcInputSummary(string Path, string Status, int ConsumerCount);
public sealed record GcPlanSummary(string Status, string Store, string Retention, DateTimeOffset PlannedAt,
    int GraceHours, bool ReadOnly, bool DeletionAuthorized, GcCounts Counts, List<GcEntrySummary> Entries,
    List<string> ProtectedPaths, List<GcInputSummary> ChangedInputs, List<GcRecheckSummary> Recheck,
    List<Issue> Diagnostics, List<string> Boundaries, Dictionary<string, int> Omitted)
    : CommandSummaryResult("gc-plan");
public sealed record GcApplySummary(string Status, string Store, string Plan, DateTimeOffset StartedAt,
    bool ReadOnly, bool DeletionAuthorized, List<string> DeletedUnits, List<string> PendingUnits,
    string? PartialUnit, int RemovedFileCount, List<Issue> Diagnostics, List<string> Boundaries)
    : CommandSummaryResult("gc-apply-result");

public sealed record AcceptanceWorklistResult(int ItemCount, List<WorklistGroup> Groups);
[JsonDerivedType(typeof(WorklistGroup<IncompleteStageItem>))]
[JsonDerivedType(typeof(WorklistGroup<PendingReviewItem>))]
[JsonDerivedType(typeof(WorklistGroup<UnmetRequirementItem>))]
[JsonDerivedType(typeof(WorklistGroup<DiagnosticItem>))]
public abstract record WorklistGroup(string Kind, int Count, string NextAction, int Omitted);
public sealed record WorklistGroup<T> : WorklistGroup
{
    public WorklistGroup(string kind, int count, string nextAction, List<T> items, int omitted)
        : base(kind, count, nextAction, omitted) => Items = items;
    public List<T> Items { get; init; }
}
public sealed record IncompleteStageItem(string Id, string Kind, string ExecutionStatus, string? Outcome,
    string Reason, int RequirementCount);
public sealed record PendingReviewItem(string Id, string Status, int StageCount, int RequirementCount);
public sealed record UnmetRequirementItem(string Id, string Stage, List<string> Missing, int OmittedMissing);
public sealed record DiagnosticItem(string Code, string Severity, int Count, Issue Example);
