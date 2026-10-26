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

    internal static (JsonElement Report, int ExitCode, AcceptanceWorklist Worklist) Evaluate(string path, AnalysisProject project, AcceptancePolicy policy, string directory)
    {
        string runPath = Path.Combine(directory, "run.json");
        JsonElement document = Read(runPath);
        if (document.GetProperty("schema_version").GetInt32() != Schema.Version || document.GetProperty("kind").GetString() != "analysis-run")
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Expected a current analysis-run document.");
        WorkflowRun run = ProjectInputs.Read<WorkflowRun>(runPath);
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
        Dictionary<string, JsonElement> evidence = new(StringComparer.Ordinal);
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
            JsonElement result = Read(file);
            if (result.GetProperty("schema_version").GetInt32() != Schema.Version ||
                ProjectWorkflow.Outcome(stage.Kind, result) != stage.Outcome ||
                selectedStage.Kind != stage.Kind)
                Error(ErrorCode.WorkflowStageInconsistent, "Stage outcome/schema/kind contradicts its evidence: " + stage.Id);
            if (stage.Kind is "inspect" or "verify" or "oracle")
                issues.AddRange(IntegratedEvidence.Validate(result));
            if (stage.Kind is "tlc" or "trace")
            {
                AnalysisStage evidenceStage = selectedStage;
                if (stage.Kind == "tlc")
                    issues.AddRange(TlcResultStore.ValidateRunStage(path, project, selectedStage, stage,
                        Path.Combine(directory, stage.Id), result, run.Fingerprints, out evidenceStage));
                issues.AddRange(TlcResources.ValidateEvidence(result, evidenceStage, Path.Combine(directory, stage.Id)));
                if (stage.RequestedResources != evidenceStage.Resources || stage.Resources != TlcResources.Effective(stage.Kind, evidenceStage.Resources))
                    Error(ErrorCode.WorkflowStageInconsistent, "Run-stage resource summary differs from its declaration.");
            }
            if (stage.Kind != "tlc" && stage.StoredResults != null)
                Error(ErrorCode.WorkflowStageInconsistent, "Only tlc stages can carry stored results: " + stage.Id);
            if (stage.Kind is "csharp-regression" or "dafny-verify")
            {
                string engine = Path.Combine(directory, stage.Id, "engine");
                List<Issue> snapshot = StageEvidence.Validate(result, engine, false);
                issues.AddRange(snapshot);
                if (snapshot.Count == 0)
                    issues.AddRange(stage.Kind == "csharp-regression" ? CSharpRegression.Validate(result, engine, true) :
                        DafnyAnalysisBackend.Validate(result, engine));
                if (result.GetProperty("timeout_seconds").GetInt32() != selectedStage.TimeoutSeconds ||
                    result.GetProperty("config_sha256").GetString() != Files.Hash(ProjectInputs.Resolve(path, selectedStage.Config!)) ||
                    !FilePaths.Comparer.Equals(result.GetProperty("config").GetString(), ProjectInputs.Resolve(path, selectedStage.Config!)) ||
                    stage.Kind == "dafny-verify" && !FilePaths.Comparer.Equals(result.GetProperty("dafny").GetString(),
                        ProjectInputs.Resolve(path, project.Tools.Dafny!.Path)))
                    Error(ErrorCode.WorkflowStageInconsistent, "Native regression/Dafny evidence differs from the declared config/tool/deadline.");
            }
            if (PInputs.IsStage(stage.Kind))
            {
                issues.AddRange(PEvidence.Validate(result, Path.Combine(directory, stage.Id, "engine")));
                if (!result.TryGetProperty("timeout_seconds", out JsonElement timeout) ||
                    timeout.GetInt32() != selectedStage.TimeoutSeconds)
                    Error(ErrorCode.WorkflowStageInconsistent, "P stage deadline differs from the declared stage.");
            }
            if (ApalacheInputs.IsStage(stage.Kind))
            {
                issues.AddRange(ApalacheEvidence.Validate(result, Path.Combine(directory, stage.Id, "engine")));
                ApalacheAnalysis analysis = ApalacheInputs.Load(ProjectInputs.Resolve(path, selectedStage.Config!), new(FilePaths.Comparer));
                if (!result.TryGetProperty("timeout_seconds", out JsonElement timeout) || timeout.GetInt32() != selectedStage.TimeoutSeconds ||
                    !result.TryGetProperty("mode", out JsonElement mode) || mode.GetString() != analysis.Mode ||
                    !result.TryGetProperty("config_sha256", out JsonElement configHash) ||
                    configHash.GetString() != Files.Hash(ProjectInputs.Resolve(path, selectedStage.Config!)) ||
                    !result.TryGetProperty("jar", out JsonElement jar) ||
                    !FilePaths.Comparer.Equals(jar.GetString(), ProjectInputs.Resolve(path, project.Tools.Apalache!.Path)))
                    Error(ErrorCode.WorkflowStageInconsistent, "Apalache evidence differs from the declared stage/configuration/tool/deadline.");
            }
            evidence[stage.Id] = result;
        }
        List<object> requirements = [];
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
            if (!evidence.TryGetValue(requirement.Stage, out JsonElement result))
                Missing("validated stage evidence", worklist.IsIncomplete(requirement.Stage));
            else
            {
                if ((PInputs.IsStage(selected.Kind) || ApalacheInputs.IsStage(selected.Kind) || selected.Kind is "csharp-regression" or "dafny-verify") && (!result.TryGetProperty("claim", out JsonElement claim) ||
                    claim.GetString() != requirement.Claim))
                    Missing("evidence claim " + requirement.Claim);
                List<string> diagnostics = result.TryGetProperty("diagnostics", out JsonElement found)
                    ? found.EnumerateArray().Select(d => d.GetProperty("code").GetString()!).ToList() : [];
                if (requirement.Diagnostic != null && !diagnostics.Contains(requirement.Diagnostic))
                    Missing("diagnostic " + requirement.Diagnostic);
                if (requirement.DiagnosticText != null && (!result.TryGetProperty("diagnostics", out JsonElement messages) ||
                    !messages.EnumerateArray().Any(d => d.GetProperty("code").GetString() == requirement.Diagnostic &&
                        d.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String &&
                        message.GetString()!.Contains(requirement.DiagnosticText, StringComparison.Ordinal))))
                    Missing("diagnostic " + requirement.Diagnostic + " containing literal text " + requirement.DiagnosticText);
                if (requirement.DiagnosticCount is int count && (!result.TryGetProperty("diagnostics", out JsonElement counted) ||
                    counted.EnumerateArray().Count(d => d.GetProperty("code").GetString() == requirement.Diagnostic &&
                        (requirement.DiagnosticText == null || d.GetProperty("message").GetString()!.Contains(requirement.DiagnosticText, StringComparison.Ordinal))) != count))
                    Missing("exactly " + count + " matching rejection diagnostics");
                if (requirement.FingerprintFunctions is int required && stage != null &&
                    TlcResultStore.PassedFingerprints(stage, result).Count is int passes && passes < required)
                    Missing("passes under " + required + " distinct fingerprint functions (found " + passes + ")");
                HashSet<string> tested = [];
                HashSet<string> cases = [];
                if (PInputs.IsStage(selected.Kind) && result.TryGetProperty("links", out JsonElement links))
                    foreach (JsonElement link in links.EnumerateArray()) tested.Add(link.GetProperty("id").GetString()!);
                if (result.TryGetProperty("execution", out JsonElement execution) && execution.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonElement binding in execution.GetProperty("tested_bindings").EnumerateArray()) tested.Add(binding.GetString()!);
                    foreach (JsonElement item in execution.GetProperty("cases").EnumerateArray())
                    {
                        string binding = item.GetProperty("binding").GetString()!;
                        string id = item.GetProperty("id").GetString()!;
                        cases.Add(binding + "/" + id);
                    }
                }
                foreach (string binding in requirement.Bindings ?? [])
                    if (!tested.Contains(binding)) Missing("tested binding " + binding);
                foreach (string id in requirement.Cases ?? [])
                    if (!cases.Contains(id)) Missing("case " + id);
                if (requirement.Tests != null)
                {
                    List<string> ids = result.GetProperty("tests").EnumerateArray().Select(t => t.GetProperty("id").GetString()!).ToList();
                    if (!ids.Order(StringComparer.Ordinal).SequenceEqual(requirement.Tests.Order(StringComparer.Ordinal)))
                        Missing("exact selected test IDs");
                }
            }
            foreach (string review in requirement.Reviews ?? [])
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
            requirements.Add(new { requirement.Id, Status = missing.Count == 0 ? "satisfied" : "unsatisfied", Missing = missing });
        }
        if (requirements.Count == 0) Error(ErrorCode.WorkflowRequirementUnsatisfied, "No requirements for this scenario.");
        bool accepted = issues.All(i => i.Severity != "error");
        object report = new {
            SchemaVersion = Schema.Version, Kind = "acceptance-result", Project = project.Id, run.Scenario,
            Run = directory, RunSha256 = Files.Hash(runPath),
            ExecutionStatus = run.ExecutionStatus, ArchiveStatus = issues.Any(i => i.Code == ErrorCode.WorkflowProvenanceInconsistent.Id()) ? "inconsistent" : "consistent",
            CurrentApplicability = issues.Any(i => i.Is(ErrorCode.WorkflowProjectChanged, ErrorCode.WorkflowEvidenceStale)) ? "stale" : "current",
            AcceptanceStatus = accepted ? "accepted" : "not-accepted", Requirements = requirements, Diagnostics = issues,
            CheckCoverage = coverage,
            Reviews = policy.Requirements.Where(r => r.Scenario == run.Scenario).SelectMany(r => r.Reviews ?? [])
                .Distinct().Select(id => new { Obligation = id, Status = ReviewStatus(path, project, id),
                    Decision = LatestReview(path, project, id) }),
            NextActions = issues.Select(i => i.Message).Distinct().ToList(),
            Boundary = "Acceptance of declared bounded evidence and explicit reviews, not general C# refinement or host authentication."
        };
        return (JsonSerializer.SerializeToElement(report, Files.Json),
            accepted ? 0 : issues.Any(i => i.Is(ErrorCode.WorkflowProjectChanged, ErrorCode.WorkflowExecutionIncomplete, ErrorCode.WorkflowEvidenceStale)) ? 2 : 1,
            worklist);
    }

    public static string ReviewStatus(string path, AnalysisProject project, string id)
    {
        ReviewObligation obligation = project.Obligations.Single(o => o.Id == id);
        ReviewDecision? review = LatestReview(path, project, id);
        if (review == null) return "pending";
        if (review.ObligationSha256 != Schema.Hash(obligation)) return "stale";
        List<string> required = obligation.Inputs.Concat(review.Evidence).Select(p => ProjectInputs.Resolve(path, p))
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
        return review.Disposition;
    }

    public static int RecordReview(string path, AnalysisProject project, CliOptions options)
    {
        string id = options.Required("obligation");
        ReviewObligation obligation = project.Obligations.SingleOrDefault(o => o.Id == id)
            ?? throw new InputException(ErrorCode.InvalidWorkflowInput, "Unknown obligation: " + id);
        string reviewer = options.Required("reviewer"), rationale = options.Required("rationale");
        string disposition = options.Get("disposition", "accepted");
        List<string> evidence = options.Many("evidence");
        if (string.IsNullOrWhiteSpace(reviewer) || string.IsNullOrWhiteSpace(rationale) ||
            disposition is not ("accepted" or "rejected" or "pending") || evidence.Count == 0)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Record an explicit reviewer, rationale, disposition and supporting evidence paths relative to the project.");
        string directory = Path.GetDirectoryName(path)!;
        evidence = evidence.Select(p => FilePaths.Relative(p, directory)).Distinct(FilePaths.Comparer).ToList();
        Dictionary<string, string> fingerprints = new(FilePaths.Comparer);
        foreach (string input in obligation.Inputs.Concat(evidence).Select(p => FilePaths.Relative(p, directory)).Distinct(FilePaths.Comparer))
            fingerprints.Add(input, Files.Hash(ProjectInputs.Resolve(path, input)));
        ReviewDecision review = new(Schema.Version, "review-decision", id, Schema.Hash(obligation), disposition, reviewer,
            rationale, DateTimeOffset.UtcNow, fingerprints, evidence);
        directory = Path.Combine(ProjectInputs.Resolve(path, project.ReviewDirectory), id);
        Directory.CreateDirectory(directory);
        string output = Path.Combine(directory, DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + Guid.NewGuid().ToString("N") + ".json");
        Files.WriteNew(output, review);
        ProjectWorkflow.Render(new { SchemaVersion = Schema.Version, Kind = "recorded-review", Path = output, Decision = review,
            Boundary = "An explicit review assertion, not authenticated reviewer identity or a machine proof." }, options);
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
                review.Disposition is not ("accepted" or "rejected" or "pending") || review.Fingerprints.Count == 0 ||
                review.Evidence.Count == 0 || string.IsNullOrWhiteSpace(review.Reviewer) || string.IsNullOrWhiteSpace(review.Rationale))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Invalid review decision: " + file);
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

    private static JsonElement Read(string path)
    {
        using JsonDocument document = JsonDocument.Parse(Files.Read(path), new JsonDocumentOptions { AllowDuplicateProperties = false });
        return document.RootElement.Clone();
    }
}
