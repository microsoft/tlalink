using System.Text.Json;

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
        List<EvidenceReportNode> nodes = [];
        List<(string File, JsonElement Report)> pending = [];
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
            List<string> reviews = requirements.SelectMany(r => r.Reviews ?? []).Distinct(StringComparer.Ordinal).ToList();
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
            foreach (JsonElement review in evaluation.Report.GetProperty("reviews").EnumerateArray())
                if (review.GetProperty("decision").ValueKind == JsonValueKind.Object)
                    foreach (JsonProperty input in review.GetProperty("decision").GetProperty("fingerprints").EnumerateObject())
                        Observe(ProjectInputs.Resolve(path, input.Name));
            List<string> finalReviews = Directory.Exists(reviewDirectory)
                ? Directory.EnumerateFiles(reviewDirectory, "*.json", SearchOption.AllDirectories).Order(FilePaths.Comparer).ToList() : [];
            if (!reviewFiles.SequenceEqual(finalReviews, FilePaths.Comparer))
                throw new InputException(ErrorCode.ReportSnapshotChanged, "Review history changed during project report generation.");
            pending.Add((evaluations[i], evaluation.Report));
            Dictionary<string, string> stageIds = scenario.Stages.Select((s, n) => (s.Id, ReportId: prefix + ".stage-" + (n + 1)))
                .ToDictionary(p => p.Id, p => p.ReportId, StringComparer.Ordinal);
            Dictionary<string, string> reviewIds = reviews.Select((id, n) => (Id: id, ReportId: prefix + ".review-" + (n + 1)))
                .ToDictionary(p => p.Id, p => p.ReportId, StringComparer.Ordinal);
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
                    stage.DependsOn.Select(id => stageIds[id]).ToList(), evidence, [], []));
            }
            foreach (string id in reviews)
            {
                ReviewObligation obligation = project.Obligations.Single(o => o.Id == id);
                JsonElement review = evaluation.Report.GetProperty("reviews").EnumerateArray().Single(r => r.GetProperty("obligation").GetString() == id);
                nodes.Add(new(reviewIds[id], "review", id, obligation.Claim, review.GetProperty("status").GetString()!,
                    "accepted", [], [evaluations[i], path], obligation.Assumptions, []));
            }
            List<string> requirementIds = [];
            for (int n = 0; n < requirements.Count; n++)
            {
                AnalysisRequirement requirement = requirements[n];
                JsonElement disposition = evaluation.Report.GetProperty("requirements").EnumerateArray().Single(r => r.GetProperty("id").GetString() == requirement.Id);
                string id = prefix + ".requirement-" + (n + 1);
                requirementIds.Add(id);
                nodes.Add(new(id, "requirement", requirement.Id,
                    "Requires " + requirement.Expected + (requirement.Claim == null ? "" : " (" + requirement.Claim + ")"),
                    disposition.GetProperty("status").GetString()!, "satisfied",
                    new[] { stageIds[requirement.Stage] }.Concat((requirement.Reviews ?? []).Select(r => reviewIds[r])).ToList(),
                    [evaluations[i], ProjectInputs.Resolve(path, project.Requirements)], [],
                    disposition.GetProperty("missing").EnumerateArray().Select(m => m.GetString()!).ToList()));
            }
            List<string> missing = evaluation.Report.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("message").GetString()!).ToList();
            nodes.Add(new(prefix, "milestone", scenario.Id,
                "Independent acceptance of this explicit run. Archive: " + evaluation.Report.GetProperty("archive_status").GetString() +
                "; current applicability: " + evaluation.Report.GetProperty("current_applicability").GetString() +
                "; execution: " + run.ExecutionStatus + ".",
                evaluation.Report.GetProperty("acceptance_status").GetString()!, "accepted", requirementIds,
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
        EvidenceReportManifest manifest = new(Schema.Version, "evidence-report", project.Id,
            "Explicit project-run snapshots. Milestone nodes preserve each independently checked archive/applicability/acceptance result. Report freshness concerns this presentation snapshot, not acceptance of every run.",
            pins, nodes, [], ["Producing hosts, build projects and reviewer identities are trusted, not authenticated."], gaps);
        Files.WriteNew(manifestPath, manifest);
        return EvidenceReport.Run(new(["report", "--manifest", manifestPath, "--output", output, "--format", options.Get("format", "json")]));
    }
}
