using System.Text.Json;

namespace TlaLink;

public static class ProjectAcceptance
{
    public static int Run(string path, AnalysisProject project, AcceptancePolicy policy, CliOptions options)
    {
        var evaluation = Evaluate(path, project, policy, FilePaths.Full(options.Required("run")));
        if (options.Optional("output") is string output) Files.WriteNew(FilePaths.Full(output), evaluation.Report);
        ProjectWorkflow.Render(evaluation.Report, options, evaluation.Worklist);
        return evaluation.ExitCode;
    }

    internal static (AcceptanceResult Report, int ExitCode, AcceptanceWorklist Worklist) Evaluate(string path, AnalysisProject project, AcceptancePolicy policy, string directory)
    {
        string runPath = Path.Combine(directory, "run.json");
        WorkflowRun run = ProjectInputs.Read<WorkflowRun>(runPath);
        if (run.SchemaVersion != Schema.Version || run.Kind != "analysis-run")
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Expected a current analysis-run document.");
        List<Issue> issues = [];
        HashSet<Issue> groupedDiagnostics = new(ReferenceEqualityComparer.Instance);
        void Error(ErrorCode code, string message, bool grouped = false)
        {
            Issue issue = new(code, message);
            issues.Add(issue);
            if (grouped) groupedDiagnostics.Add(issue);
        }
        if (run.Project != project.Id || run.ProjectSha256 != Files.Hash(path) ||
            run.RequirementsSha256 != Files.Hash(ProjectInputs.Resolve(path, project.Requirements)))
            Error(ErrorCode.WorkflowProjectChanged, "Project or acceptance requirements differ from the run.");
        issues.AddRange(ProjectComposition.ValidateArchive(path, project, run, directory));
        AnalysisScenario? scenario = project.Scenarios.SingleOrDefault(s => s.Id == run.Scenario);
        if (scenario == null) throw new InputException(ErrorCode.WorkflowProjectChanged, "Run scenario does not exist in this project.");
        List<SelectedMethodSummary> selectedSummaries = MethodSummaries.Select(path, project, scenario.Stages);
        string RecordedSummaryPath(string file) => FilePaths.Full(MethodSummaries.ReviewPath(path, file), run.ProjectDirectory);
        if (run.MethodSummaries.Count != selectedSummaries.Count ||
            selectedSummaries.Any(s => run.MethodSummaries.Count(r =>
                FilePaths.Comparer.Equals(r.Scope, RecordedSummaryPath(s.Scope)) &&
                FilePaths.Comparer.Equals(r.Specification.Location.Path, RecordedSummaryPath(CorrespondenceInputs.Target(
                    s.Configuration, Markup.Name(s.Summary.Binding).Alias, "tla").Inputs.Single())) &&
                Schema.Hash(r.Summary) == Schema.Hash(s.Summary)) != 1))
            Error(ErrorCode.WorkflowStageInconsistent, "Run method summaries differ from the declared dependencies.");
        foreach (SelectedMethodSummary summary in selectedSummaries)
        {
            FormalTarget target = CorrespondenceInputs.Target(summary.Configuration, Markup.Name(summary.Summary.Binding).Alias, "tla");
            foreach (string input in summary.Summary.Sources.Select(p => ProjectInputs.Resolve(summary.Scope, p))
                .Concat(target.Inputs).Append(summary.Scope))
                if (!run.Fingerprints.ContainsKey(RecordedSummaryPath(input)))
                    Error(ErrorCode.WorkflowProvenanceInconsistent, "Run is missing a method-summary input pin: " + input);
        }
        AcceptanceWorklist worklist = new(run, scenario, policy.Requirements.Where(r => r.Scenario == run.Scenario).ToList(), issues, groupedDiagnostics);
        List<CheckCoverage> coverage = ProjectInputs.Coverage(project, policy, [run.Scenario]);
        foreach (CheckCoverage obligation in coverage)
            foreach (string gap in obligation.Gaps)
                Error(ErrorCode.WorkflowPrerequisiteMissing, obligation.Id + ": " + gap);
        if (run.ExecutionStatus != "completed") Error(ErrorCode.WorkflowExecutionIncomplete, "Execution did not complete; a timeout/build failure cannot satisfy a negative control.", worklist.HasIncompleteStages);
        if (run.Stages.Select(s => s.Id).Distinct().Count() != run.Stages.Count ||
            !run.Stages.Select(s => s.Id).Order().SequenceEqual(scenario.Stages.Select(s => s.Id).Order()))
            Error(ErrorCode.WorkflowExecutionIncomplete, "The run does not contain exactly the declared stages.");
        if (run.Fingerprints.Count == 0 || run.Artifacts.Count == 0) Error(ErrorCode.WorkflowProvenanceInconsistent, "Missing run provenance.");
        foreach (var pin in run.Fingerprints.Where(p => FilePaths.IsAbsolute(p.Key)))
        {
            string archived = SafePath(directory, Path.Combine("inputs", pin.Value));
            if (!File.Exists(archived) || Files.Hash(archived) != pin.Value)
                Error(ErrorCode.WorkflowProvenanceInconsistent, "Missing/changed archived input: " + pin.Key);
            if (!FilePaths.IsNativeAbsolute(pin.Key))
            {
                Error(ErrorCode.WorkflowEvidenceStale, "Input uses a foreign filesystem root: " + pin.Key);
                continue;
            }
            string current = pin.Key;
            string relative = Path.GetRelativePath(run.ProjectDirectory, pin.Key);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                current = ProjectInputs.Resolve(path, relative);
            if (!File.Exists(current) || Files.Hash(current) != pin.Value)
                Error(ErrorCode.WorkflowEvidenceStale, "Stale current input: " + current);
        }
        foreach (var artifact in run.Artifacts)
        {
            string file = SafePath(directory, artifact.Key);
            if (!File.Exists(file) || Files.Hash(file) != artifact.Value)
                Error(ErrorCode.WorkflowProvenanceInconsistent, "Missing/changed run artifact: " + artifact.Key);
        }
        Dictionary<string, IAnalysisResult> evidence = new(StringComparer.Ordinal);
        foreach (WorkflowStageResult stage in run.Stages)
        {
            AnalysisStage? selectedStage = scenario.Stages.SingleOrDefault(s => s.Id == stage.Id);
            if (selectedStage == null)
            {
                Error(ErrorCode.WorkflowStageInconsistent, "Run contains an undeclared stage: " + stage.Id);
                continue;
            }
            if (stage.ExecutionStatus != "completed" || string.IsNullOrEmpty(stage.Evidence))
            {
                Error(ErrorCode.WorkflowExecutionIncomplete, "Stage incomplete or blocked: " + stage.Id, true);
                continue;
            }
            string file = SafePath(directory, stage.Evidence);
            if (stage.Evidence != Path.Combine(stage.Id, "result.json") ||
                !run.Artifacts.TryGetValue(stage.Evidence, out string? hash) || hash != stage.Sha256 ||
                !File.Exists(file) || Files.Hash(file) != hash)
            {
                Error(ErrorCode.WorkflowProvenanceInconsistent, "Stage evidence is missing or inconsistent: " + stage.Id);
                continue;
            }
            IAnalysisResult result = AnalysisDocuments.Read(file);
            if (result.SchemaVersion != Schema.Version ||
                ProjectWorkflow.Outcome(stage.Kind, result) != stage.Outcome ||
                selectedStage.Kind != stage.Kind || !AnalysisDocuments.MatchesStage(stage.Kind, result))
            {
                Error(ErrorCode.WorkflowStageInconsistent, "Stage outcome/schema/kind contradicts its evidence: " + stage.Id);
                continue;
            }
            if (result is IntegratedResult integrated)
                issues.AddRange(IntegratedEvidence.Validate(integrated, Path.Combine(directory, "inputs")));
            if (stage.Kind is "tlc" or "trace")
            {
                AnalysisStage evidenceStage = selectedStage;
                if (result is TlcResult tlc)
                    issues.AddRange(TlcResultStore.ValidateRunStage(path, project, selectedStage, stage,
                        Path.Combine(directory, stage.Id), tlc, run.Fingerprints, out evidenceStage));
                issues.AddRange(TlcResources.ValidateEvidence(result, evidenceStage, Path.Combine(directory, stage.Id)));
                if (stage.RequestedResources != evidenceStage.Resources || stage.Resources != TlcResources.Effective(stage.Kind, evidenceStage.Resources))
                    Error(ErrorCode.WorkflowStageInconsistent, "Run-stage resource summary differs from its declaration.");
            }
            if (stage.Kind != "tlc" && stage.StoredResults != null)
                Error(ErrorCode.WorkflowStageInconsistent, "Only tlc stages can carry stored results: " + stage.Id);
            if (result is CSharpRegressionResult or DafnyResult)
            {
                string engine = Path.Combine(directory, stage.Id, "engine");
                IArtifactEvidence artifact = (IArtifactEvidence)result;
                List<Issue> snapshot = StageEvidence.Validate(artifact, engine, false);
                issues.AddRange(snapshot);
                if (snapshot.Count == 0)
                    issues.AddRange(result is CSharpRegressionResult regression ? CSharpRegression.Validate(regression, engine, true) :
                        DafnyAnalysisBackend.Validate((DafnyResult)result, engine));
                (int timeout, string configHash, string config) = result switch {
                    CSharpRegressionResult r => (r.TimeoutSeconds, r.ConfigSha256, r.Config),
                    DafnyResult r => (r.TimeoutSeconds, r.ConfigSha256, r.Config),
                    _ => throw new InvalidOperationException("Unsupported native stage.")
                };
                if (timeout != selectedStage.TimeoutSeconds ||
                    configHash != Files.Hash(ProjectInputs.Resolve(path, selectedStage.Config!)) ||
                    !FilePaths.Comparer.Equals(config, ProjectInputs.Resolve(path, selectedStage.Config!)) ||
                    result is DafnyResult dafny && !FilePaths.Comparer.Equals(dafny.Dafny,
                        ProjectInputs.Resolve(path, project.Tools.Dafny!.Path)))
                    Error(ErrorCode.WorkflowStageInconsistent, "Native regression/Dafny evidence differs from the declared config/tool/deadline.");
            }
            if (result is PPacket p)
            {
                issues.AddRange(PEvidence.Validate(p, Path.Combine(directory, stage.Id, "engine")));
                if (p.TimeoutSeconds != selectedStage.TimeoutSeconds)
                    Error(ErrorCode.WorkflowStageInconsistent, "P stage deadline differs from the declared stage.");
            }
            if (result is ApalachePacket apalache)
            {
                issues.AddRange(ApalacheEvidence.Validate(apalache, Path.Combine(directory, stage.Id, "engine")));
                ApalacheAnalysis analysis = ApalacheInputs.Load(ProjectInputs.Resolve(path, selectedStage.Config!), new(FilePaths.Comparer));
                if (apalache.TimeoutSeconds != selectedStage.TimeoutSeconds ||
                    apalache.Mode != analysis.Mode ||
                    apalache.ConfigSha256 != Files.Hash(ProjectInputs.Resolve(path, selectedStage.Config!)) ||
                    !FilePaths.Comparer.Equals(apalache.Jar, ProjectInputs.Resolve(path, project.Tools.Apalache!.Path)))
                    Error(ErrorCode.WorkflowStageInconsistent, "Apalache evidence differs from the declared stage/configuration/tool/deadline.");
            }
            evidence[stage.Id] = result;
        }
        List<RequirementResult> requirements = [];
        foreach (AnalysisRequirement requirement in policy.Requirements.Where(r => r.Scenario == run.Scenario))
        {
            List<string> missing = [];
            List<string> actionable = [];
            void Missing(string reason, bool grouped = false)
            {
                missing.Add(reason);
                if (!grouped) actionable.Add(reason);
            }
            WorkflowStageResult? stage = run.Stages.FirstOrDefault(s => s.Id == requirement.Stage);
            AnalysisStage selected = scenario.Stages.Single(s => s.Id == requirement.Stage);
            if (selected.Config != null)
            {
                string recordedConfig = FilePaths.Full(selected.Config, run.ProjectDirectory);
                if (!run.Fingerprints.TryGetValue(recordedConfig, out string? hash) ||
                    !hash.Equals(requirement.ConfigSha256, StringComparison.OrdinalIgnoreCase))
                    Missing("independently approved configuration bounds/properties");
            }
            if (stage == null || stage.ExecutionStatus != "completed" || stage.Outcome != requirement.Expected)
                Missing("expected completed outcome " + requirement.Expected, worklist.IsIncomplete(requirement.Stage));
            if (!evidence.TryGetValue(requirement.Stage, out IAnalysisResult? result))
                Missing("validated stage evidence", worklist.IsIncomplete(requirement.Stage));
            else
            {
                string? claim = result switch {
                    PPacket p => p.Claim, ApalachePacket a => a.Claim,
                    CSharpRegressionResult r => r.Claim, DafnyResult d => d.Claim, _ => null
                };
                if ((PInputs.IsStage(selected.Kind) || ApalacheInputs.IsStage(selected.Kind) || selected.Kind is "csharp-regression" or "dafny-verify") &&
                    claim != requirement.Claim)
                    Missing("evidence claim " + requirement.Claim);
                List<string> diagnostics = result.Diagnostics.Select(d => d.Code).ToList();
                if (requirement.Diagnostic != null && !diagnostics.Contains(requirement.Diagnostic))
                    Missing("diagnostic " + requirement.Diagnostic);
                if (requirement.DiagnosticText != null &&
                    !result.Diagnostics.Any(d => d.Code == requirement.Diagnostic &&
                        d.Message.Contains(requirement.DiagnosticText, StringComparison.Ordinal)))
                    Missing("diagnostic " + requirement.Diagnostic + " containing literal text " + requirement.DiagnosticText);
                if (requirement.DiagnosticCount is int count &&
                    result.Diagnostics.Count(d => d.Code == requirement.Diagnostic &&
                        (requirement.DiagnosticText == null || d.Message.Contains(requirement.DiagnosticText, StringComparison.Ordinal))) != count)
                    Missing("exactly " + count + " matching rejection diagnostics");
                if (requirement.FingerprintFunctions is int required && stage != null && result is TlcResult tlc &&
                    TlcResultStore.PassedFingerprints(stage, tlc).Count is int passes && passes < required)
                    Missing("passes under " + required + " distinct fingerprint functions (found " + passes + ")");
                HashSet<string> tested = [];
                HashSet<string> cases = [];
                if (result is PPacket pEvidence)
                    foreach (PSourceLink link in pEvidence.Links) tested.Add(link.Id);
                if (result is IntegratedResult { Execution: { } execution })
                {
                    tested.UnionWith(execution.TestedBindings);
                    foreach (CaseComparison item in execution.Cases) cases.Add(item.Binding + "/" + item.Id);
                }
                foreach (string binding in requirement.Bindings ?? [])
                    if (!tested.Contains(binding)) Missing("tested binding " + binding);
                foreach (string id in requirement.Cases ?? [])
                    if (!cases.Contains(id)) Missing("case " + id);
                if (requirement.Tests != null)
                {
                    List<string> ids = result is CSharpRegressionResult regression
                        ? regression.Tests.Select(t => t.Id).ToList() : [];
                    if (!ids.Order(StringComparer.Ordinal).SequenceEqual(requirement.Tests.Order(StringComparer.Ordinal)))
                        Missing("exact selected test IDs");
                }
            }
            foreach (string review in (requirement.Reviews ?? []).Concat(MethodSummaries.Inspections(path, project, [selected]))
                .Distinct(StringComparer.Ordinal))
            {
                string status = ReviewStatus(path, project, review);
                if (status != "accepted")
                {
                    Missing("current accepted review " + review, true);
                    worklist.Review(review, status, requirement);
                }
            }
            worklist.Requirement(requirement, actionable);
            if (missing.Count > 0) Error(ErrorCode.WorkflowRequirementUnsatisfied, requirement.Id + ": missing " + string.Join(", ", missing), true);
            requirements.Add(new RequirementResult(requirement.Id, missing.Count == 0 ? "satisfied" : "unsatisfied", missing));
        }
        if (requirements.Count == 0) Error(ErrorCode.WorkflowRequirementUnsatisfied, "No requirements for this scenario.");
        bool accepted = issues.All(i => i.Severity != "error");
        AcceptanceResult report = new(Schema.Version, "acceptance-result", project.Id, run.Scenario,
            directory, Files.Hash(runPath), run.ExecutionStatus,
            issues.Any(i => i.Code == ErrorCode.WorkflowProvenanceInconsistent.Id()) ? "inconsistent" : "consistent",
            issues.Any(i => i.Is(ErrorCode.WorkflowProjectChanged, ErrorCode.WorkflowEvidenceStale)) ? "stale" : "current",
            accepted ? "accepted" : "not-accepted", requirements, issues, coverage,
            selectedSummaries.Select(s => new AcceptanceMethodSummary(
                s.Scope, s.Summary.Binding, s.Summary.Abstraction, s.Summary.Assumptions, s.Summary.Limits,
                s.Summary.Inspection, ReviewStatus(path, project, s.Summary.Inspection),
                "Caller result is conditional on this reviewed abstraction; not machine-checked C# refinement."
            )).ToList(),
            policy.Requirements.Where(r => r.Scenario == run.Scenario).SelectMany(r => r.Reviews ?? [])
                .Concat(selectedSummaries.Select(s => s.Summary.Inspection))
                .Distinct().Select(id => new ReviewResult(id, ReviewStatus(path, project, id),
                    LatestReview(path, project, id))).ToList(),
            issues.Select(i => i.Message).Distinct().ToList(),
            "Acceptance of declared bounded evidence and explicit reviews, not general C# refinement or host authentication.");
        return (report,
            accepted ? 0 : issues.Any(i => i.Is(ErrorCode.WorkflowProjectChanged, ErrorCode.WorkflowExecutionIncomplete, ErrorCode.WorkflowEvidenceStale)) ? 2 : 1,
            worklist);
    }

    public static string ReviewStatus(string path, AnalysisProject project, string id)
    {
        ReviewObligation obligation = project.Obligations.Single(o => o.Id == id);
        ReviewDecision? review = LatestReview(path, project, id);
        if (review == null) return "pending";
        if (review.ObligationSha256 != Schema.Hash(obligation)) return "stale";
        if (review.Origin != obligation.Origin) return "origin-mismatch";
        List<string> required = obligation.Inputs.Concat(review.Evidence).Concat(review.SummarySupport?.Inputs ?? [])
            .Select(p => ProjectInputs.Resolve(path, p))
            .Distinct(FilePaths.Comparer).ToList();
        Dictionary<string, string> fingerprints = new(FilePaths.Comparer);
        foreach (var pin in review.Fingerprints)
            if (!fingerprints.TryAdd(ProjectInputs.Resolve(path, pin.Key), pin.Value))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Review fingerprints contain aliased input paths.");
        if (fingerprints.Count != required.Count) return "stale";
        foreach (string input in required)
            if (!fingerprints.TryGetValue(input, out string? hash) ||
                !File.Exists(input) || Files.Hash(input) != hash)
                return "stale";
        Dictionary<string, string> summaryPins = new(FilePaths.Comparer);
        MethodSummarySupport? currentSupport = MethodSummaries.Support(path, project, id, summaryPins, validate: false);
        if (currentSupport != null && (review.SummarySupport == null ||
            currentSupport.SelectionSha256 != review.SummarySupport.SelectionSha256 ||
            currentSupport.SourceContextSha256 != review.SummarySupport.SourceContextSha256 ||
            currentSupport.Inputs.Any(p => !review.SummarySupport.Inputs
                .Select(i => ProjectInputs.Resolve(path, i)).Contains(ProjectInputs.Resolve(path, p), FilePaths.Comparer))) ||
            currentSupport == null && review.SummarySupport != null)
            return "stale";
        return review.Disposition;
    }

    public static int RecordReview(string path, AnalysisProject project, CliOptions options)
    {
        string id = options.Required("obligation");
        ReviewObligation obligation = project.Obligations.SingleOrDefault(o => o.Id == id)
            ?? throw new InputException(ErrorCode.InvalidWorkflowInput, "Unknown obligation: " + id);
        string reviewer = options.Required("reviewer"), rationale = options.Required("rationale");
        string disposition = options.Get("disposition", "accepted");
        ReviewOrigin origin = options.Get("origin", "human") switch {
            "human" => ReviewOrigin.Human, "agent" => ReviewOrigin.Agent,
            _ => throw new InputException(ErrorCode.InvalidWorkflowInput, "Review origin must be human or agent.")
        };
        if (origin != obligation.Origin)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Review origin must match the obligation; agent inspection cannot satisfy human review.");
        List<string> evidence = options.Many("evidence");
        if (string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(rationale) ||
            disposition is not ("accepted" or "rejected" or "pending") || evidence.Count == 0)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Record an explicit reviewer, rationale, disposition and supporting evidence paths relative to the project.");
        string directory = Path.GetDirectoryName(path)!;
        evidence = evidence.Select(p => FilePaths.Relative(p, directory)).Distinct(FilePaths.Comparer).ToList();
        Dictionary<string, string> fingerprints = new(FilePaths.Comparer);
        foreach (string input in obligation.Inputs.Concat(evidence).Select(p => FilePaths.Relative(p, directory)).Distinct(FilePaths.Comparer))
            fingerprints.Add(input, Files.Hash(ProjectInputs.Resolve(path, input)));
        Dictionary<string, string> summaryPins = new(FilePaths.Comparer);
        MethodSummarySupport? support = MethodSummaries.Support(path, project, id, summaryPins, validate: true);
        foreach (var pin in summaryPins)
        {
            string key = fingerprints.Keys.SingleOrDefault(k =>
                FilePaths.Comparer.Equals(ProjectInputs.Resolve(path, k), ProjectInputs.Resolve(path, pin.Key))) ?? pin.Key;
            Files.Pin(fingerprints, key, pin.Value);
        }
        if (support != null) support = support with {
            Inputs = support.Inputs.Select(p => fingerprints.Keys.Single(k =>
                FilePaths.Comparer.Equals(ProjectInputs.Resolve(path, k), ProjectInputs.Resolve(path, p)))).ToList()
        };
        ReviewDecision review = new(Schema.Version, "review-decision", id, Schema.Hash(obligation), disposition, reviewer,
            rationale, DateTimeOffset.UtcNow, fingerprints, evidence, origin, support);
        directory = Path.Combine(ProjectInputs.Resolve(path, project.ReviewDirectory), id);
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        Files.WriteNew(output, review);
        ProjectWorkflow.Render(new RecordedReview(Schema.Version, "recorded-review", output, review,
            "An explicit review assertion, not authenticated reviewer identity or a machine proof."), options);
        return 0;
    }

    private static ReviewDecision? LatestReview(string path, AnalysisProject project, string id)
    {
        string directory = Path.Combine(ProjectInputs.Resolve(path, project.ReviewDirectory), id);
        if (!Directory.Exists(directory)) return null;
        List<ReviewDecision> decisions = [];
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            ReviewDecision review = ProjectInputs.Read<ReviewDecision>(file);
            if (review.SchemaVersion != Schema.Version || review.Kind != "review-decision" || review.Obligation != id ||
                !Enum.IsDefined(review.Origin) ||
                review.Disposition is not ("accepted" or "rejected" or "pending") || review.Fingerprints.Count == 0 ||
                review.Evidence.Count == 0 || string.IsNullOrWhiteSpace(review.Reviewer) || string.IsNullOrWhiteSpace(review.Rationale))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Invalid review decision: " + file);
            if (review.SummarySupport is { } support && (support.Inputs.Count == 0 ||
                support.Inputs.Distinct(FilePaths.Comparer).Count() != support.Inputs.Count ||
                new[] { support.SelectionSha256, support.SourceContextSha256 }.Any(h => h.Length != 64 || h.Any(c => !char.IsAsciiHexDigit(c)))))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Invalid method-summary support in review: " + file);
            decisions.Add(review);
        }
        return decisions.OrderByDescending(r => r.RecordedAt).FirstOrDefault();
    }

    internal static string SafePath(string root, string relative)
    {
        string path = FilePaths.Full(relative, root);
        if (FilePaths.IsAbsolute(relative) || !path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            FilePaths.Comparison))
            throw new InputException(ErrorCode.WorkflowProvenanceInconsistent, "Artifact paths must remain inside the run directory.");
        return path;
    }

}
