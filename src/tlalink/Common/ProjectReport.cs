namespace TlaLink;

public static class ProjectReport
{
    public static int Run(CliOptions options)
    {
        if (options.Has("manifest") || !options.Has("project") || options.Many("run").Count is < 1 or > 100)
            throw new InputException(ErrorCode.InvalidReportInput, "Choose --manifest, or --project with 1..100 explicit --run directories.");
        string path = FilePaths.Full(options.Required("project")), output = FilePaths.Full(options.Required("output"));
        string manifestPath = output + ".json";
        List<string> runs = options.Many("run").Select(p => FilePaths.Full(p)).ToList();
        if (runs.Distinct(FilePaths.Comparer).Count() != runs.Count)
            throw new InputException(ErrorCode.InvalidReportInput, "Report runs must be distinct explicit directories; there is no automatic latest-run selection.");
        List<string> evaluations = runs.Select((_, i) => output + ".run-" + (i + 1) + ".acceptance.json").ToList();
        foreach (string file in evaluations.Append(output).Append(manifestPath))
            if (File.Exists(file) || Directory.Exists(file))
                throw new InputException(ErrorCode.InvalidReportInput, "Report outputs must be new: " + file);
        var (project, policy) = ProjectInputs.Load(path);
        Dictionary<string, string?> snapshot = new(FilePaths.Comparer);
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        void Observe(string file)
        {
            if (!snapshot.ContainsKey(file)) snapshot.Add(file, File.Exists(file) ? Files.Hash(file) : null);
        }
        void Pin(string file)
        {
            Observe(file);
            if (snapshot[file] is string hash) Files.Pin(pins, file, hash);
        }
        Pin(path);
        Pin(ProjectInputs.Resolve(path, project.Requirements));
        EvidenceReportManifest? sourceReport = null;
        if (options.Has("source-inventory") && !options.Has("source-report"))
            throw new InputException(ErrorCode.InvalidReportInput, "--source-inventory requires an explicit --source-report policy.");
        if (options.Optional("source-report") is string sourcePath)
        {
            sourcePath = FilePaths.Full(sourcePath);
            Pin(sourcePath);
            sourceReport = EvidenceReport.Rebase(ProjectInputs.Read<EvidenceReportManifest>(sourcePath), Path.GetDirectoryName(sourcePath)!);
            if (options.Optional("source-inventory") is string inventoryPath)
            {
                if (sourceReport.SourceInventories is not { Count: 1 } ||
                    sourceReport.SourceInventories[0].Inventory != null)
                    throw new InputException(ErrorCode.InvalidReportInput,
                        "--source-inventory binds exactly one null inventory slot; it cannot replace a declared inventory.");
                inventoryPath = FilePaths.Full(inventoryPath);
                sourceReport.SourceInventories[0] = sourceReport.SourceInventories[0] with { Inventory = inventoryPath };
                Observe(inventoryPath);
                Files.Pin(sourceReport.Fingerprints, inventoryPath, Files.Hash(inventoryPath));
            }
            foreach (var pin in sourceReport.Fingerprints)
            {
                Observe(pin.Key);
                Files.Pin(pins, pin.Key, pin.Value);
            }
        }
        List<EvidenceReportNode> nodes = [];
        List<(string File, AcceptanceResult Report)> pending = [];
        List<string> gaps = ["Source dispositions and source/model correspondence are not inferred from project stages. No code inventory is manufactured."];
        for (int i = 0; i < runs.Count; i++)
        {
            string runPath = Path.Combine(runs[i], "run.json"), prefix = "run-" + (i + 1);
            Pin(runPath);
            WorkflowRun run = ProjectInputs.Read<WorkflowRun>(runPath);
            foreach (var artifact in run.Artifacts) Pin(ProjectAcceptance.SafePath(runs[i], artifact.Key));
            foreach (var pin in run.Fingerprints.Where(p => FilePaths.IsAbsolute(p.Key)))
            {
                Pin(StageEvidence.Archived(runs[i], pin.Value));
                if (FilePaths.IsNativeAbsolute(pin.Key)) Observe(pin.Key);
            }
            AnalysisScenario scenario = project.Scenarios.SingleOrDefault(s => s.Id == run.Scenario)
                ?? throw new InputException(ErrorCode.InvalidReportInput, "Run scenario is not declared by this project.");
            List<AnalysisRequirement> requirements = policy.Requirements.Where(r => r.Scenario == scenario.Id).ToList();
            List<string> reviews = requirements.SelectMany(r => r.Reviews ?? [])
                .Concat(MethodSummaries.Inspections(path, project, scenario.Stages)).Distinct(StringComparer.Ordinal).ToList();
            foreach (string id in reviews)
                foreach (string file in project.Obligations.Single(o => o.Id == id).Inputs)
                    Observe(ProjectInputs.Resolve(path, file));
            string reviewDirectory = ProjectInputs.Resolve(path, project.ReviewDirectory);
            List<string> reviewFiles = Directory.Exists(reviewDirectory)
                ? Directory.EnumerateFiles(reviewDirectory, "*.json", SearchOption.AllDirectories).Order(FilePaths.Comparer).ToList() : [];
            foreach (string review in reviewFiles)
            {
                Pin(review);
                ReviewDecision decision = ProjectInputs.Read<ReviewDecision>(review);
                foreach (string input in decision.Fingerprints.Keys) Observe(ProjectInputs.Resolve(path, input));
            }
            var evaluation = ProjectAcceptance.Evaluate(path, project, policy, runs[i]);
            foreach (ReviewResult review in evaluation.Report.Reviews)
                if (review.Decision is { } decision)
                    foreach (string input in decision.Fingerprints.Keys)
                        Observe(ProjectInputs.Resolve(path, input));
            List<string> finalReviews = Directory.Exists(reviewDirectory)
                ? Directory.EnumerateFiles(reviewDirectory, "*.json", SearchOption.AllDirectories).Order(FilePaths.Comparer).ToList() : [];
            if (!reviewFiles.SequenceEqual(finalReviews, FilePaths.Comparer))
                throw new InputException(ErrorCode.ReportSnapshotChanged, "Review history changed during project report generation.");
            pending.Add((evaluations[i], evaluation.Report));
            Dictionary<string, string> stageIds = scenario.Stages.Select((s, n) => (s.Id, ReportId: prefix + ".stage-" + (n + 1)))
                .ToDictionary(p => p.Id, p => p.ReportId, StringComparer.Ordinal);
            Dictionary<string, string> reviewIds = reviews.Select((id, n) => (Id: id, ReportId: prefix + ".review-" + (n + 1)))
                .ToDictionary(p => p.Id, p => p.ReportId, StringComparer.Ordinal);
            List<SelectedMethodSummary> summaries = MethodSummaries.Select(path, project, scenario.Stages);
            Dictionary<string, string> summaryIds = summaries.Select((s, n) => (s.Summary.Binding, Id: prefix + ".summary-" + (n + 1)))
                .ToDictionary(p => p.Binding, p => p.Id, StringComparer.Ordinal);
            foreach (SelectedMethodSummary summary in summaries)
            {
                string status = evaluation.Report.Reviews.Single(r => r.Obligation == summary.Summary.Inspection).Status;
                nodes.Add(new(summaryIds[summary.Summary.Binding], "code", summary.Summary.Binding,
                    "Declared " + summary.Summary.Abstraction + " method abstraction; no machine-checked C# refinement.",
                    status, "accepted", new[] { reviewIds[summary.Summary.Inspection] }
                        .Concat(summary.Summary.DependsOn.Select(id => summaryIds[id])).ToList(),
                    [runPath, evaluations[i]], summary.Summary.Assumptions, summary.Summary.Limits));
            }
            foreach (AnalysisStage stage in scenario.Stages)
            {
                WorkflowStageResult? recorded = run.Stages.FirstOrDefault(s => s.Id == stage.Id);
                List<string> evidence = [runPath];
                if (recorded?.Evidence is { Length: > 0 }) evidence.Add(ProjectAcceptance.SafePath(runs[i], recorded.Evidence));
                List<string> outcomes = requirements.Where(r => r.Stage == stage.Id).Select(r => r.Expected).Distinct().ToList();
                nodes.Add(new(stageIds[stage.Id], stage.Kind == "csharp-regression" ? "test" :
                    stage.Kind == "dafny-verify" ? "proof" : stage.Kind == "trace" ? "trace" : "check",
                    scenario.Id + ": " + stage.Id, "Recorded " + stage.Kind + " result; scope remains backend-specific.",
                    recorded?.Outcome ?? "incomplete", outcomes.Count == 1 ? outcomes[0] : null,
                    stage.DependsOn.Select(id => stageIds[id]).Concat(MethodSummaries.Select(path, project, [stage])
                        .Select(s => summaryIds[s.Summary.Binding])).ToList(), evidence, [], []));
            }
            foreach (string id in reviews)
            {
                ReviewObligation obligation = project.Obligations.Single(o => o.Id == id);
                ReviewResult review = evaluation.Report.Reviews.Single(r => r.Obligation == id);
                nodes.Add(new(reviewIds[id], "review", id, obligation.Claim, review.Status,
                    "accepted", [], [evaluations[i], path], obligation.Assumptions,
                    ["Review origin: " + obligation.Origin.ToString().ToLowerInvariant() + "; an assertion, not an authenticated identity or machine proof."]));
            }
            List<string> requirementIds = [];
            for (int n = 0; n < requirements.Count; n++)
            {
                AnalysisRequirement requirement = requirements[n];
                RequirementResult disposition = evaluation.Report.Requirements.Single(r => r.Id == requirement.Id);
                string id = prefix + ".requirement-" + (n + 1);
                requirementIds.Add(id);
                nodes.Add(new(id, "requirement", requirement.Id,
                    "Requires " + requirement.Expected + (requirement.Claim == null ? "" : " (" + requirement.Claim + ")"),
                    disposition.Status, "satisfied",
                    new[] { stageIds[requirement.Stage] }.Concat((requirement.Reviews ?? [])
                        .Concat(MethodSummaries.Inspections(path, project, [scenario.Stages.Single(s => s.Id == requirement.Stage)]))
                        .Distinct(StringComparer.Ordinal).Select(r => reviewIds[r])).ToList(),
                    [evaluations[i], ProjectInputs.Resolve(path, project.Requirements)], [],
                    disposition.Missing));
            }
            List<string> missing = evaluation.Report.Diagnostics.Select(d => d.Message).ToList();
            nodes.Add(new(prefix, "milestone", scenario.Id,
                "Independent acceptance of this explicit run. Archive: " + evaluation.Report.ArchiveStatus +
                "; current applicability: " + evaluation.Report.CurrentApplicability +
                "; execution: " + run.ExecutionStatus + ".",
                evaluation.Report.AcceptanceStatus, "accepted", requirementIds,
                [runPath, evaluations[i], path], [], missing));
        }
        foreach (var observed in snapshot)
            if ((File.Exists(observed.Key) ? Files.Hash(observed.Key) : null) != observed.Value)
                throw new InputException(ErrorCode.ReportSnapshotChanged, "Evidence/review/input changed during project report generation: " + observed.Key);
        foreach (var evaluation in pending)
        {
            Files.WriteNew(evaluation.File, evaluation.Report);
            Files.Pin(pins, evaluation.File, Files.Hash(evaluation.File));
        }
        EvidenceReportManifest manifest = new(Schema.Version, "evidence-report", sourceReport?.Title ?? project.Id,
            sourceReport?.Scope ?? "Explicit project-run snapshots. Milestone nodes preserve each independently checked archive/applicability/acceptance result. Report freshness concerns this presentation snapshot, not acceptance of every run.",
            pins, [.. nodes, .. sourceReport?.Nodes ?? []], sourceReport?.CodeRegions ?? [],
            ["Producing hosts, build projects and reviewer identities are trusted, not authenticated.", .. sourceReport?.Assumptions ?? []],
            [.. gaps, .. sourceReport?.Gaps ?? []], sourceReport?.SourceInventories);
        Files.WriteNew(manifestPath, manifest);
        return EvidenceReport.Run(new(["report", "--manifest", manifestPath, "--output", output, "--format", options.Get("format", "json")]));
    }
}
