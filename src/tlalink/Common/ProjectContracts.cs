using System.Text.Json;
using System.Text.Json.Serialization;

namespace TlaLink;

public sealed record ToolPin(string Path, string Sha256);
public sealed record ProjectTools(ToolPin? Dafny, ToolPin? Jar, ToolPin? P = null, ToolPin? Apalache = null);
public sealed record CompilerProfile(string TargetFramework, string LanguageVersion, string Nullable,
    bool Checked, bool AllowUnsafe, List<string>? Defines, List<string> References);
public sealed record AnalysisStage(string Id, string Kind, List<string> DependsOn, int TimeoutSeconds,
    string? Bindings = null, List<string>? Sources = null, List<string>? Models = null,
    string? Config = null, List<string>? Arguments = null);
public sealed record AnalysisScenario(string Id, List<AnalysisStage> Stages);
public sealed record ReviewObligation(string Id, string Claim, List<string> Assumptions, List<string> Inputs);
public sealed record ReviewDecision(int SchemaVersion, string Kind, string Obligation, string ObligationSha256,
    string Disposition, string Reviewer, string Rationale, DateTimeOffset RecordedAt,
    Dictionary<string, string> Fingerprints, List<string> Evidence);
public sealed record AnalysisProject(int SchemaVersion, string Kind, string Id, ProjectTools Tools,
    CompilerProfile Compiler, string Requirements, List<AnalysisScenario> Scenarios,
    List<ReviewObligation> Obligations, string ReviewDirectory);
public sealed record AnalysisRequirement(string Id, string Scenario, string Stage, string Expected,
    string? Diagnostic = null, List<string>? Bindings = null, List<string>? Cases = null,
    List<string>? Reviews = null, string? ConfigSha256 = null, string? Claim = null);
public sealed record AcceptancePolicy(int SchemaVersion, string Kind, string Project,
    List<AnalysisRequirement> Requirements);
public sealed record WorkflowStageResult(string Id, string Kind, string ExecutionStatus, string Outcome,
    string Evidence, string Sha256, List<string> Diagnostics, double DurationSeconds);
public sealed class WorkflowRun
{
    public int SchemaVersion => Schema.Version;
    public string Kind => "analysis-run";
    public required string Project { get; init; }
    public required string ProjectDirectory { get; init; }
    public required string Scenario { get; init; }
    public required string RequirementsSha256 { get; init; }
    public required string ProjectSha256 { get; init; }
    public string ExecutionStatus { get; set; } = "incomplete";
    public string AcceptanceStatus => "not-evaluated";
    public List<WorkflowStageResult> Stages { get; init; } = [];
    public Dictionary<string, string> Fingerprints { get; init; } = new(FilePaths.Comparer);
    public Dictionary<string, string> Artifacts { get; init; } = new(StringComparer.Ordinal);
    public List<Issue> Diagnostics { get; init; } = [];
}

public static class ProjectInputs
{
    public static readonly JsonSerializerOptions Json = new(Files.Json)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(Files.Read(path), Json) ?? throw new InputException(ErrorCode.InvalidWorkflowInput, "Empty document: " + path);

    public static string Resolve(string project, string path) => FilePaths.Full(path, Path.GetDirectoryName(project)!);

    public static (AnalysisProject Project, AcceptancePolicy Policy) Load(string path)
    {
        AnalysisProject project = Read<AnalysisProject>(path);
        if (project.SchemaVersion != Schema.Version || project.Kind != "analysis-project")
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Expected the current analysis-project schema.");
        Id(project.Id);
        CompilerProfile compiler = project.Compiler;
        if (compiler.TargetFramework != "net10.0" || compiler.LanguageVersion != "14" ||
            compiler.Nullable != "enable" || compiler.Checked || !compiler.AllowUnsafe || compiler.Defines is { Count: 0 })
            throw new InputException(ErrorCode.WorkflowPrerequisiteMissing, "Supported compiler profile is net10.0, C# 14, nullable enable, unchecked, unsafe enabled.");
        AcceptancePolicy policy = Read<AcceptancePolicy>(Resolve(path, project.Requirements));
        if (policy.SchemaVersion != Schema.Version || policy.Kind != "acceptance-policy" || policy.Project != project.Id)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Acceptance policy schema/kind/project mismatch.");
        Unique(project.Scenarios.Select(s => s.Id), "scenarios");
        Unique(project.Obligations.Select(o => o.Id), "obligations");
        Unique(policy.Requirements.Select(r => r.Id), "requirements");
        if (project.Scenarios.Count is 0 or > 100 || policy.Requirements.Count is 0 or > 10000)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Projects and policies cannot be empty.");
        foreach (AnalysisScenario scenario in project.Scenarios)
        {
            Unique(scenario.Stages.Select(s => s.Id), "stages");
            if (scenario.Stages.Count is 0 or > 1000) throw new InputException(ErrorCode.InvalidWorkflowInput, "Scenario needs 1..1000 stages: " + scenario.Id);
            foreach (AnalysisStage stage in scenario.Stages)
            {
                if (stage.Kind is not ("inspect" or "verify" or "oracle" or "inputs" or "tlc" or "trace") &&
                    !PInputs.IsStage(stage.Kind) && !ApalacheInputs.IsStage(stage.Kind) ||
                    stage.TimeoutSeconds is < 1 or > 7200)
                    throw new InputException(ErrorCode.WorkflowPrerequisiteMissing, "Unsupported stage kind or timeout: " + stage.Id);
                Unique(stage.DependsOn, "dependencies");
                if (stage.DependsOn.Any(d => !scenario.Stages.Any(s => s.Id == d) || d == stage.Id))
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "Missing/self stage dependency: " + stage.Id);
                if (ApalacheInputs.IsStage(stage.Kind))
                {
                    if (project.Tools.Apalache == null || project.Tools.Jar == null || stage.Config == null ||
                        stage.Bindings != null || stage.Models != null || stage.Sources != null || stage.Arguments != null)
                        throw new InputException(ErrorCode.InvalidWorkflowInput, "Apalache stages require pinned Apalache/SANY tools and one explicit analysis config.");
                    ApalacheAnalysis analysis = ApalacheInputs.Load(Resolve(path, stage.Config), new(FilePaths.Comparer));
                    if (stage.Kind != ApalacheInputs.Stage(analysis.Mode))
                        throw new InputException(ErrorCode.InvalidWorkflowInput, "Apalache stage kind must match its configured mode.");
                }
                else if (PInputs.IsStage(stage.Kind))
                {
                    if (project.Tools.P == null || stage.Config == null || stage.Bindings != null ||
                        stage.Models != null || stage.Sources != null || stage.Arguments != null)
                        throw new InputException(ErrorCode.InvalidWorkflowInput, "P stages require pinned P and one explicit analysis config; scope comes from that config.");
                    PAnalysis analysis = PInputs.Load(Resolve(path, stage.Config), new(FilePaths.Comparer));
                    if (stage.Kind != "p-" + analysis.Engine)
                        throw new InputException(ErrorCode.InvalidWorkflowInput, "P stage kind must match its configured engine.");
                    if (analysis.Engine is "pex" or "psym" or "pobserve" &&
                        !policy.Requirements.Where(r => r.Scenario == scenario.Id && r.Stage == stage.Id)
                            .All(r => r.Reviews is { Count: > 0 }))
                        throw new InputException(ErrorCode.InvalidWorkflowInput, "External P artifacts require explicit build/source review obligations.");
                    if (analysis.Engine is "pex" or "psym" or "pobserve")
                    {
                        HashSet<string> reviewIds = policy.Requirements.Where(r => r.Scenario == scenario.Id && r.Stage == stage.Id)
                            .SelectMany(r => r.Reviews ?? []).ToHashSet(StringComparer.Ordinal);
                        HashSet<string> inputs = project.Obligations.Where(o => reviewIds.Contains(o.Id))
                            .SelectMany(o => o.Inputs).Select(p => Resolve(path, p)).ToHashSet(FilePaths.Comparer);
                        IEnumerable<string> required = analysis.Models.Append(Resolve(path, stage.Config));
                        required = analysis.Artifact != null ? required.Append(analysis.Artifact.Path) :
                            required.Concat(new[] { analysis.Observation!.RuntimeJar.Path, analysis.Observation.MonitorJar.Path })
                                .Concat(analysis.Observation.Logs);
                        if (required.Any(p => !inputs.Contains(p)))
                            throw new InputException(ErrorCode.InvalidWorkflowInput, "External artifact reviews must pin the config, P sources, JARs and selected logs.");
                    }
                }
                else if (stage.Kind is "inspect" or "verify" or "oracle")
                {
                    if (stage.Bindings == null || project.Tools.Dafny == null)
                        throw new InputException(ErrorCode.InvalidWorkflowInput, "Integrated stages require bindings and pinned Dafny.");
                    if (stage.Sources != null || stage.Models != null || stage.Config != null || stage.Arguments != null)
                        throw new InputException(ErrorCode.InvalidWorkflowInput, "Integrated stages take source/model scope only from their binding manifest.");
                }
                else if (stage.Models is not { Count: > 0 } || project.Tools.Jar == null)
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "TLA stages require explicit models and a pinned JAR.");
                if (stage.Kind is "tlc" or "trace" && (stage.Config == null || stage.Models?.Count != 1))
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "TLC/trace stages require one root model and config.");
                if (stage.Kind == "trace" && stage.Sources is not { Count: > 0 })
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "Trace stages require copied executable sources including their harness.");
                if (stage.Kind is "inputs" or "tlc" or "trace" && stage.Bindings != null ||
                    stage.Kind is "inputs" or "tlc" && (stage.Sources != null || stage.Arguments != null) ||
                    stage.Kind == "inputs" && stage.Config != null)
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "Stage contains options unsupported for its kind: " + stage.Id);
                if (!policy.Requirements.Any(r => r.Scenario == scenario.Id && r.Stage == stage.Id))
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "Every stage needs an acceptance requirement: " + stage.Id);
            }
            Order(scenario);
            if (!policy.Requirements.Any(r => r.Scenario == scenario.Id))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Scenario has no acceptance requirements: " + scenario.Id);
        }
        foreach (ReviewObligation obligation in project.Obligations)
        {
            if (string.IsNullOrWhiteSpace(obligation.Claim) || obligation.Inputs.Count == 0 ||
                obligation.Assumptions.Any(string.IsNullOrWhiteSpace))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Review obligations require a claim, explicit inputs and nonempty assumptions.");
            foreach (string input in obligation.Inputs) _ = Resolve(path, input);
        }
        if (string.IsNullOrWhiteSpace(project.ReviewDirectory))
            throw new InputException(ErrorCode.InvalidWorkflowInput, "A review directory must be explicitly selected.");
        foreach (AnalysisRequirement requirement in policy.Requirements)
        {
            AnalysisScenario? scenario = project.Scenarios.SingleOrDefault(s => s.Id == requirement.Scenario);
            if (scenario == null || !scenario.Stages.Any(s => s.Id == requirement.Stage) ||
                requirement.Expected is not ("passed" or "rejected") ||
                requirement.Expected == "rejected" && string.IsNullOrWhiteSpace(requirement.Diagnostic))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Requirements need existing stages and an explicit expected outcome; rejections need a diagnostic.");
            foreach (string review in requirement.Reviews ?? [])
                if (!project.Obligations.Any(o => o.Id == review))
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "Unknown required review: " + review);
            AnalysisStage selected = scenario.Stages.Single(s => s.Id == requirement.Stage);
            bool nativeClaim = PInputs.IsStage(selected.Kind) || ApalacheInputs.IsStage(selected.Kind);
            if (nativeClaim && string.IsNullOrWhiteSpace(requirement.Claim) ||
                !nativeClaim && requirement.Claim != null)
                throw new InputException(ErrorCode.InvalidWorkflowInput, "P/Apalache requirements must select an explicit evidence claim; other backends retain their own contracts.");
            if (ApalacheInputs.IsStage(selected.Kind))
            {
                ApalacheAnalysis analysis = ApalacheInputs.Load(Resolve(path, selected.Config!), new(FilePaths.Comparer));
                if (requirement.Claim != (requirement.Expected == "passed" ? ApalacheInputs.Claim(analysis.Mode) : "not-established") ||
                    requirement.Expected == "rejected" && (analysis.Mode == "typecheck" ||
                        requirement.Diagnostic != ApalacheBackend.RejectionCode(analysis.Mode).Id()))
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "Apalache requirements must name the exact mode claim or its property-specific rejection.");
            }
            if (selected.Config != null && (requirement.ConfigSha256 is not { Length: 64 } ||
                requirement.ConfigSha256.Any(c => !char.IsAsciiHexDigit(c))))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Configured stages must pin the independently approved configuration, including bounds and properties.");
            if (selected.Config == null && requirement.ConfigSha256 != null)
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Configuration pins require a configured TLC/trace stage.");
            if (requirement.Bindings != null || requirement.Cases != null)
                if (selected.Kind != "oracle" && (!PInputs.IsStage(selected.Kind) || requirement.Cases != null))
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "Binding coverage needs an oracle or P stage; case coverage needs an oracle stage.");
            if ((requirement.Bindings ?? []).Concat(requirement.Cases ?? []).Any(string.IsNullOrWhiteSpace))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Required binding/case IDs cannot be empty.");
            foreach (string binding in requirement.Bindings ?? []) Id(binding);
            foreach (string selector in requirement.Cases ?? [])
            {
                string[] parts = selector.Split('/');
                if (parts.Length != 2 || parts.Any(p => !Schema.IsStableId(p)))
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "Case requirements use exactly binding-id/case-id.");
            }
        }
        return (project, policy);
    }

    public static List<AnalysisStage> Order(AnalysisScenario scenario)
    {
        List<AnalysisStage> ordered = [];
        List<AnalysisStage> pending = [.. scenario.Stages];
        while (pending.Count > 0)
        {
            AnalysisStage? ready = pending.FirstOrDefault(s => s.DependsOn.All(d => ordered.Any(o => o.Id == d)));
            if (ready == null) throw new InputException(ErrorCode.InvalidWorkflowInput, "Cyclic stage dependencies in " + scenario.Id);
            ordered.Add(ready);
            pending.Remove(ready);
        }
        return ordered;
    }

    private static void Unique(IEnumerable<string> values, string label)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (string value in values)
        {
            Id(value);
            if (!ids.Add(value)) throw new InputException(ErrorCode.InvalidWorkflowInput, "Duplicate " + label + ": " + value);
        }
    }

    private static void Id(string value)
    {
        if (!Schema.IsStableId(value))
            throw new InputException(ErrorCode.InvalidWorkflowInput, "IDs must be 1..100 ASCII letters/digits, dots, underscores or hyphens; not path segments.");
    }
}
