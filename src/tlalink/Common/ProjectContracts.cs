using System.Text.Json;
using System.Text.Json.Serialization;

namespace TlaLink;

public sealed record ToolPin(string Path, string Sha256);
public sealed record ProjectTools(ToolPin? Dafny = null, ToolPin? Jar = null, ToolPin? P = null, ToolPin? Apalache = null);
public sealed record CompilerProfile(string TargetFramework, string LanguageVersion, string Nullable,
    bool Checked, bool AllowUnsafe, List<string>? Defines, List<string> References)
{
    [JsonConstructor]
    public CompilerProfile(string targetFramework, string languageVersion, string nullable,
        bool @checked, bool allowUnsafe, List<string> references)
        : this(targetFramework, languageVersion, nullable, @checked, allowUnsafe, null, references) { }
}
public sealed record AnalysisStage(string Id, string Kind, List<string> DependsOn, int TimeoutSeconds,
    string? Bindings = null, List<string>? Sources = null, List<string>? Models = null,
    string? Config = null, List<string>? Arguments = null, TlcResources? Resources = null, int? Fingerprint = null,
    string? Scope = null, string? Target = null);
public sealed record AnalysisScenario(string Id, List<AnalysisStage> Stages);
public sealed record ReviewObligation(string Id, string Claim, List<string> Assumptions, List<string> Inputs);
public sealed record CheckObligation(string Id, string Kind, string Scenario, List<string> Requirements);
public sealed record CheckCoverage(string Id, string Kind, string Scenario, string Status,
    List<string> Requirements, List<string> Gaps);
public sealed record ReviewDecision(int SchemaVersion, string Kind, string Obligation, string ObligationSha256,
    string Disposition, string Reviewer, string Rationale, DateTimeOffset RecordedAt,
    Dictionary<string, string> Fingerprints, List<string> Evidence);
public sealed record AnalysisProject(int SchemaVersion, string Kind, string Id, ProjectTools Tools,
    CompilerProfile Compiler, string Requirements, List<AnalysisScenario> Scenarios,
    List<ReviewObligation> Obligations, string ReviewDirectory, List<CheckObligation>? CheckObligations = null,
    string? ResultStore = null, int AnnotationVersion = Markup.Version)
{
    [JsonIgnore]
    internal ProjectAuthoringMetadata? Authoring { get; init; }
}
public sealed record AnalysisRequirement(string Id, string Scenario, string Stage, string Expected,
    string? Diagnostic = null, List<string>? Bindings = null, List<string>? Cases = null,
    List<string>? Reviews = null, string? ConfigSha256 = null, string? Claim = null, string? DiagnosticText = null,
    List<string>? Tests = null, int? DiagnosticCount = null, int? FingerprintFunctions = null);
public sealed record AcceptancePolicy(int SchemaVersion, string Kind, string Project,
    List<AnalysisRequirement> Requirements);
public sealed record WorkflowStageResult(string Id, string Kind, string ExecutionStatus, string Outcome,
    string Evidence, string Sha256, List<string> Diagnostics, double DurationSeconds, TlcStatistics? Statistics = null,
    TlcResources? RequestedResources = null, TlcResources? Resources = null, List<StoredStageResult>? StoredResults = null);
public sealed record StoredStageResult(int Fingerprint, string Key, string Family, string Outcome, string Origin,
    bool Primary, string Directory, string EntrySha256);
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
    internal static readonly string[] StageKinds = [
        "inspect", "verify", "oracle", "inputs", "tlc", "trace", "csharp-regression", "dafny-verify",
        "apalache-typecheck", "apalache-check", "apalache-inductive",
        "p-inspect", "p-pchecker", "p-pex", "p-psym", "p-pverifier", "p-pobserve"
    ];
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
        AnalysisProject project = ProjectComposition.Read(path);
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
        Unique((project.CheckObligations ?? []).Select(o => o.Id), "check obligations");
        foreach (CheckObligation obligation in project.CheckObligations ?? [])
        {
            if (obligation.Kind is not ("safety" or "deadlock" or "progress") ||
                !project.Scenarios.Any(s => s.Id == obligation.Scenario))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Check obligations require safety/deadlock/progress and an existing scenario.");
            Unique(obligation.Requirements, "check requirement references");
            if (obligation.Requirements.Any(id => !policy.Requirements.Any(r => r.Id == id && r.Scenario == obligation.Scenario)))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Check obligations must reference requirements in their own scenario.");
        }
        if (project.Scenarios.Count is 0 or > 100 || policy.Requirements.Count is 0 or > 10000)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Projects and policies cannot be empty.");
        foreach (AnalysisScenario scenario in project.Scenarios)
        {
            Unique(scenario.Stages.Select(s => s.Id), "stages");
            if (scenario.Stages.Count is 0 or > 1000) throw new InputException(ErrorCode.InvalidWorkflowInput, "Scenario needs 1..1000 stages: " + scenario.Id);
            foreach (AnalysisStage stage in scenario.Stages)
            {
                try
                {
                    if (stage.Resources != null && stage.Kind is not ("tlc" or "trace"))
                        throw new InputException(ErrorCode.InvalidWorkflowInput, "TLC resources are supported only on tlc/trace stages.");
                    if (stage.Fingerprint != null && (stage.Kind != "tlc" || stage.Fingerprint is < 0 or > TlcResultStore.MaxFingerprint))
                        throw new InputException(ErrorCode.InvalidWorkflowInput, "Fingerprint functions are supported only on tlc stages, as an index 0.." + TlcResultStore.MaxFingerprint + ": " + stage.Id);
                    if (stage.Kind is "tlc" or "trace") _ = TlcResources.Effective(stage.Kind, stage.Resources);
                    if (!StageKinds.Contains(stage.Kind, StringComparer.Ordinal) ||
                        stage.TimeoutSeconds is < 1 or > 7200)
                        throw new InputException(ErrorCode.WorkflowPrerequisiteMissing, "Unsupported stage kind or timeout: " + stage.Id);
                    Unique(stage.DependsOn, "dependencies");
                    if (stage.DependsOn.Any(d => !scenario.Stages.Any(s => s.Id == d) || d == stage.Id))
                        throw new InputException(ErrorCode.InvalidWorkflowInput, "Missing/self stage dependency: " + stage.Id);
                    if (stage.Kind is "csharp-regression" or "dafny-verify")
                    {
                        if (stage.Config == null || stage.Bindings != null || stage.Models != null ||
                            stage.Sources != null || stage.Arguments != null)
                            throw new InputException(ErrorCode.InvalidWorkflowInput, "Regression/native Dafny scope comes only from an explicit analysis config.");
                        if (stage.Kind == "csharp-regression") _ = CSharpRegression.Load(Resolve(path, stage.Config), new(FilePaths.Comparer));
                        else
                        {
                            if (project.Tools.Dafny == null)
                                throw new InputException(ErrorCode.InvalidWorkflowInput, "Native Dafny stages require pinned Dafny.");
                            _ = DafnyAnalysisBackend.Load(Resolve(path, stage.Config), new(FilePaths.Comparer));
                        }
                    }
                    else if (ApalacheInputs.IsStage(stage.Kind))
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
                    if (stage.Kind == "trace")
                    {
                        if (stage.Scope == null || stage.Target == null)
                            throw new InputException(ErrorCode.InvalidWorkflowInput, "Trace stages require an explicit correspondence scope and target.");
                        CorrespondenceConfiguration scope = CorrespondenceInputs.Load(Resolve(path, stage.Scope), new(FilePaths.Comparer));
                        FormalTarget target = CorrespondenceInputs.Target(scope, stage.Target, "tla");
                        if (!target.Inputs.SequenceEqual(stage.Models!.Select(p => Resolve(path, p)), FilePaths.Comparer))
                            throw new InputException(ErrorCode.InvalidWorkflowInput, "Trace model contradicts the selected correspondence target.");
                    }
                    else if (stage.Scope != null || stage.Target != null)
                        throw new InputException(ErrorCode.InvalidWorkflowInput, "Only trace stages take scope/target directly; other native stages use their own configuration.");
                    if (stage.Kind is "inputs" or "tlc" or "trace" && stage.Bindings != null ||
                        stage.Kind is "inputs" or "tlc" && (stage.Sources != null || stage.Arguments != null) ||
                        stage.Kind == "inputs" && stage.Config != null)
                        throw new InputException(ErrorCode.InvalidWorkflowInput, "Stage contains options unsupported for its kind: " + stage.Id);
                    if (!policy.Requirements.Any(r => r.Scenario == scenario.Id && r.Stage == stage.Id))
                        throw new InputException(ErrorCode.InvalidWorkflowInput, "Every stage needs an acceptance requirement: " + stage.Id);
                }
                catch (InputException error)
                {
                    string origin = project.Authoring!.StageOrigins[scenario.Id + "/" + stage.Id];
                    throw new InputException(error.Code ?? ErrorCode.InvalidWorkflowInput, origin + ": " + stage.Id + ": " + error.Message);
                }
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
        if (project.ResultStore != null && string.IsNullOrWhiteSpace(project.ResultStore))
            throw new InputException(ErrorCode.InvalidWorkflowInput, "A declared result store must name a directory.");
        foreach (AnalysisRequirement requirement in policy.Requirements)
        {
            AnalysisScenario? scenario = project.Scenarios.SingleOrDefault(s => s.Id == requirement.Scenario);
            if (scenario == null || !scenario.Stages.Any(s => s.Id == requirement.Stage) ||
                requirement.Expected is not ("passed" or "rejected") ||
                requirement.Expected == "rejected" && string.IsNullOrWhiteSpace(requirement.Diagnostic))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Requirements need existing stages and an explicit expected outcome; rejections need a diagnostic.");
            if (requirement.DiagnosticText != null && (requirement.Expected != "rejected" ||
                string.IsNullOrWhiteSpace(requirement.Diagnostic) || string.IsNullOrWhiteSpace(requirement.DiagnosticText)))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Diagnostic text selectors require a rejected outcome, diagnostic code and nonempty literal text.");
            if (requirement.DiagnosticCount != null && (requirement.Expected != "rejected" ||
                string.IsNullOrWhiteSpace(requirement.Diagnostic) || requirement.DiagnosticCount is < 1 or > 1000))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Diagnostic counts require a rejection diagnostic and an exact count of 1..1000.");
            foreach (string review in requirement.Reviews ?? [])
                if (!project.Obligations.Any(o => o.Id == review))
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "Unknown required review: " + review);
            AnalysisStage selected = scenario.Stages.Single(s => s.Id == requirement.Stage);
            bool nativeClaim = PInputs.IsStage(selected.Kind) || ApalacheInputs.IsStage(selected.Kind) ||
                selected.Kind is "csharp-regression" or "dafny-verify";
            if (nativeClaim && string.IsNullOrWhiteSpace(requirement.Claim) ||
                !nativeClaim && requirement.Claim != null)
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Native analysis requirements must select an explicit backend claim.");
            if (selected.Kind is "csharp-regression" or "dafny-verify")
            {
                string claim = requirement.Expected == "rejected" ? "not-established" :
                    selected.Kind == "csharp-regression" ? "selected-csharp-regressions" : "dafny-contracts";
                ErrorCode rejection = selected.Kind == "csharp-regression" ? ErrorCode.RegressionAssertionFailed : ErrorCode.DafnyVerificationFailed;
                if (requirement.Claim != claim || requirement.Expected == "rejected" && requirement.Diagnostic != rejection.Id())
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "Native regression/Dafny requirements need the exact claim and assertion/verifier rejection diagnostic.");
            }
            if (selected.Kind == "csharp-regression")
            {
                CSharpRegressionAnalysis analysis = CSharpRegression.Load(Resolve(path, selected.Config!), new(FilePaths.Comparer));
                if (requirement.Tests == null || !requirement.Tests.Order(StringComparer.Ordinal).SequenceEqual(analysis.Tests.Order(StringComparer.Ordinal)))
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "Regression requirements must independently declare the exact selected test IDs.");
            }
            else if (requirement.Tests != null)
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Test IDs are supported only on regression requirements.");
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
            if (requirement.FingerprintFunctions != null && (selected.Kind != "tlc" || requirement.Expected != "passed" ||
                requirement.FingerprintFunctions is < 1 or > TlcResultStore.MaxFingerprint + 1))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Fingerprint-function counts require a passed tlc requirement and a count of 1.." + (TlcResultStore.MaxFingerprint + 1) + ".");
            if (requirement.Bindings != null || requirement.Cases != null)
                if (selected.Kind != "oracle" && (!PInputs.IsStage(selected.Kind) || requirement.Cases != null))
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "Binding coverage needs an oracle or P stage; case coverage needs an oracle stage.");
            if ((requirement.Bindings ?? []).Concat(requirement.Cases ?? []).Any(string.IsNullOrWhiteSpace))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Required binding/case IDs cannot be empty.");
            foreach (string binding in requirement.Bindings ?? [])
                if (!Markup.IsCorrespondenceId(binding))
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "Required bindings use scope::name IDs.");
            foreach (string selector in requirement.Cases ?? [])
            {
                string[] parts = selector.Split('/');
                if (parts.Length != 2 || !Markup.IsCorrespondenceId(parts[0]) || !Schema.IsStableId(parts[1]))
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "Case requirements use exactly binding-id/case-id.");
            }
        }
        return (project, policy);
    }

    public static List<CheckCoverage> Coverage(AnalysisProject project, AcceptancePolicy policy, IEnumerable<string> scenarios)
    {
        HashSet<string> selected = scenarios.ToHashSet(StringComparer.Ordinal);
        return (project.CheckObligations ?? []).Where(o => selected.Contains(o.Scenario)).Select(obligation =>
        {
            List<string> gaps = [];
            if (obligation.Requirements.Count == 0) gaps.Add("No required check is mapped.");
            foreach (string id in obligation.Requirements)
            {
                AnalysisRequirement requirement = policy.Requirements.Single(r => r.Id == id);
                AnalysisStage stage = project.Scenarios.Single(s => s.Id == obligation.Scenario)
                    .Stages.Single(s => s.Id == requirement.Stage);
                if (requirement.Expected != "passed")
                    gaps.Add(id + ": a rejection control cannot discharge a positive obligation.");
                else if (stage.Kind != "tlc" && !(obligation.Kind == "safety" &&
                    stage.Kind is "apalache-check" or "apalache-inductive"))
                    gaps.Add(id + ": " + stage.Kind + " does not supply the selected " + obligation.Kind +
                        " check in this TLC/Apalache coverage profile.");
            }
            return new CheckCoverage(obligation.Id, obligation.Kind, obligation.Scenario,
                gaps.Count == 0 ? "mapped" : "missing", obligation.Requirements, gaps);
        }).ToList();
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

    internal static void Unique(IEnumerable<string> values, string label)
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
