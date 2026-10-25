using System.Text.Json;

namespace TlaLink;

public static class ProjectAcceptance
{
    public static int Run(string path, AnalysisProject project, AcceptancePolicy policy, CliOptions options)
    {
        string directory = Path.GetFullPath(options.Required("run"));
        string runPath = Path.Combine(directory, "run.json");
        JsonElement document = Read(runPath);
        if (document.GetProperty("schema_version").GetInt32() != Schema.Version || document.GetProperty("kind").GetString() != "analysis-run")
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Expected a current analysis-run document.");
        WorkflowRun run = ProjectInputs.Read<WorkflowRun>(runPath);
        List<Issue> issues = [];
        void Error(ErrorCode code, string message) => issues.Add(new(code, message));
        if (run.Project != project.Id || run.ProjectSha256 != Files.Hash(path) ||
            run.RequirementsSha256 != Files.Hash(ProjectInputs.Resolve(path, project.Requirements)))
            Error(ErrorCode.WorkflowProjectChanged, "Project or acceptance requirements differ from the run.");
        AnalysisScenario? scenario = project.Scenarios.SingleOrDefault(s => s.Id == run.Scenario);
        if (scenario == null) throw new InputException(ErrorCode.WorkflowProjectChanged, "Run scenario does not exist in this project.");
        if (run.ExecutionStatus != "completed") Error(ErrorCode.WorkflowExecutionIncomplete, "Execution did not complete; a timeout/build failure cannot satisfy a negative control.");
        if (run.Stages.Select(s => s.Id).Distinct().Count() != run.Stages.Count ||
            !run.Stages.Select(s => s.Id).Order().SequenceEqual(scenario.Stages.Select(s => s.Id).Order()))
            Error(ErrorCode.WorkflowExecutionIncomplete, "The run does not contain exactly the declared stages.");
        if (run.Fingerprints.Count == 0 || run.Artifacts.Count == 0) Error(ErrorCode.WorkflowProvenanceInconsistent, "Missing run provenance.");
        foreach (var pin in run.Fingerprints.Where(p => Path.IsPathFullyQualified(p.Key)))
        {
            string archived = SafePath(directory, Path.Combine("inputs", pin.Value));
            if (!File.Exists(archived) || Files.Hash(archived) != pin.Value)
                Error(ErrorCode.WorkflowProvenanceInconsistent, "Missing/changed archived input: " + pin.Key);
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
            if (stage.ExecutionStatus != "completed" || string.IsNullOrEmpty(stage.Evidence))
            {
                Error(ErrorCode.WorkflowExecutionIncomplete, "Stage incomplete or blocked: " + stage.Id);
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
                scenario.Stages.Single(s => s.Id == stage.Id).Kind != stage.Kind)
                Error(ErrorCode.WorkflowStageInconsistent, "Stage outcome/schema/kind contradicts its evidence: " + stage.Id);
            if (stage.Kind is "inspect" or "verify" or "oracle")
                issues.AddRange(IntegratedEvidence.Validate(result));
            if (PInputs.IsStage(stage.Kind))
            {
                issues.AddRange(PEvidence.Validate(result, Path.Combine(directory, stage.Id, "engine")));
                if (!result.TryGetProperty("timeout_seconds", out JsonElement timeout) ||
                    timeout.GetInt32() != scenario.Stages.Single(s => s.Id == stage.Id).TimeoutSeconds)
                    Error(ErrorCode.WorkflowStageInconsistent, "P stage deadline differs from the declared stage.");
            }
            evidence[stage.Id] = result;
        }
        List<object> requirements = [];
        foreach (AnalysisRequirement requirement in policy.Requirements.Where(r => r.Scenario == run.Scenario))
        {
            List<string> missing = [];
            WorkflowStageResult? stage = run.Stages.SingleOrDefault(s => s.Id == requirement.Stage);
            AnalysisStage selected = scenario.Stages.Single(s => s.Id == requirement.Stage);
            if (selected.Config != null)
            {
                string recordedConfig = Path.GetFullPath(selected.Config, run.ProjectDirectory);
                if (!run.Fingerprints.TryGetValue(recordedConfig, out string? hash) ||
                    !hash.Equals(requirement.ConfigSha256, StringComparison.OrdinalIgnoreCase))
                    missing.Add("independently approved configuration bounds/properties");
            }
            if (stage == null || stage.ExecutionStatus != "completed" || stage.Outcome != requirement.Expected)
                missing.Add("expected completed outcome " + requirement.Expected);
            if (!evidence.TryGetValue(requirement.Stage, out JsonElement result))
                missing.Add("validated stage evidence");
            else
            {
                if (PInputs.IsStage(selected.Kind) && (!result.TryGetProperty("claim", out JsonElement claim) ||
                    claim.GetString() != requirement.Claim))
                    missing.Add("evidence claim " + requirement.Claim);
                List<string> diagnostics = result.TryGetProperty("diagnostics", out JsonElement found)
                    ? found.EnumerateArray().Select(d => d.GetProperty("code").GetString()!).ToList() : [];
                if (requirement.Diagnostic != null && !diagnostics.Contains(requirement.Diagnostic))
                    missing.Add("diagnostic " + requirement.Diagnostic);
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
                    if (!tested.Contains(binding)) missing.Add("tested binding " + binding);
                foreach (string id in requirement.Cases ?? [])
                    if (!cases.Contains(id)) missing.Add("case " + id);
            }
            foreach (string review in requirement.Reviews ?? [])
                if (ReviewStatus(path, project, review) != "accepted") missing.Add("current accepted review " + review);
            if (missing.Count > 0) Error(ErrorCode.WorkflowRequirementUnsatisfied, requirement.Id + ": missing " + string.Join(", ", missing));
            requirements.Add(new { requirement.Id, Status = missing.Count == 0 ? "satisfied" : "unsatisfied", Missing = missing });
        }
        if (requirements.Count == 0) Error(ErrorCode.WorkflowRequirementUnsatisfied, "No requirements for this scenario.");
        bool accepted = issues.All(i => i.Severity != "error");
        object report = new {
            SchemaVersion = Schema.Version, Kind = "acceptance-result", Project = project.Id, run.Scenario,
            ExecutionStatus = run.ExecutionStatus, ArchiveStatus = issues.Any(i => i.Code == ErrorCode.WorkflowProvenanceInconsistent.Id()) ? "inconsistent" : "consistent",
            CurrentApplicability = issues.Any(i => i.Is(ErrorCode.WorkflowProjectChanged, ErrorCode.WorkflowEvidenceStale)) ? "stale" : "current",
            AcceptanceStatus = accepted ? "accepted" : "not-accepted", Requirements = requirements, Diagnostics = issues,
            Reviews = policy.Requirements.Where(r => r.Scenario == run.Scenario).SelectMany(r => r.Reviews ?? [])
                .Distinct().Select(id => new { Obligation = id, Decision = LatestReview(path, project, id) }),
            NextActions = issues.Select(i => i.Message).Distinct().ToList(),
            Boundary = "Acceptance of declared bounded evidence and explicit reviews, not general C# refinement or host authentication."
        };
        if (options.Optional("output") is string output) Files.WriteNew(Path.GetFullPath(output), report);
        ProjectWorkflow.Render(report, options);
        return accepted ? 0 : issues.Any(i => i.Is(ErrorCode.WorkflowProjectChanged, ErrorCode.WorkflowExecutionIncomplete, ErrorCode.WorkflowEvidenceStale)) ? 2 : 1;
    }

    public static string ReviewStatus(string path, AnalysisProject project, string id)
    {
        ReviewObligation obligation = project.Obligations.Single(o => o.Id == id);
        ReviewDecision? review = LatestReview(path, project, id);
        if (review == null) return "pending";
        if (review.ObligationSha256 != Schema.Hash(obligation)) return "stale";
        List<string> required = obligation.Inputs.Concat(review.Evidence).Distinct(StringComparer.Ordinal).ToList();
        if (review.Fingerprints.Count != required.Count) return "stale";
        foreach (string input in required)
            if (!review.Fingerprints.TryGetValue(input, out string? hash) ||
                !File.Exists(ProjectInputs.Resolve(path, input)) || Files.Hash(ProjectInputs.Resolve(path, input)) != hash)
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
        Dictionary<string, string> fingerprints = new(StringComparer.Ordinal);
        foreach (string input in obligation.Inputs.Concat(evidence).Distinct(StringComparer.Ordinal))
            fingerprints.Add(input, Files.Hash(ProjectInputs.Resolve(path, input)));
        ReviewDecision review = new(Schema.Version, "review-decision", id, Schema.Hash(obligation), disposition, reviewer,
            rationale, DateTimeOffset.UtcNow, fingerprints, evidence);
        string directory = Path.Combine(ProjectInputs.Resolve(path, project.ReviewDirectory), id);
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

    private static string SafePath(string root, string relative)
    {
        string path = Path.GetFullPath(relative, root);
        if (Path.IsPathFullyQualified(relative) || !path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InputException(ErrorCode.WorkflowProvenanceInconsistent, "Artifact paths must remain inside the run directory.");
        return path;
    }

    private static JsonElement Read(string path)
    {
        using JsonDocument document = JsonDocument.Parse(Files.Read(path), new JsonDocumentOptions { AllowDuplicateProperties = false });
        return document.RootElement.Clone();
    }
}
