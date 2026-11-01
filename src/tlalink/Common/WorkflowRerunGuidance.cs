namespace TlaLink;

internal static class WorkflowRerunGuidance
{
    internal static List<WorkflowNextStep> Create(string path, AnalysisProject project,
        AnalysisScenario scenario, WorkflowRun run, AcceptanceResult report)
    {
        List<WorkflowNextStep> steps = [];
        void Add(string kind, string? stage, string reason, params string[] arguments) =>
            steps.Add(new(kind, stage, reason, [.. arguments], Environment.CurrentDirectory));
        void Accept() => Add("accept", null,
            "Reassess this explicit run after resolving review or requirement decisions; acceptance never replays execution.",
            "accept", "--project", path, "--run", report.Run, "--format", "summary");
        if (report.ArchiveStatus != "consistent" ||
            report.PolicyReassessment?.Status == "unavailable" ||
            report.Diagnostics.Any(d => d.Is(ErrorCode.WorkflowStageInconsistent)))
        {
            Add("investigate", null,
                "Archive or evidence validation is blocked. Inspect the recorded diagnostics and artifacts; rerunning does not repair historical evidence.");
            return steps;
        }
        if (report.PolicyReassessment?.CheckerExpectationsUnchanged == false)
        {
            Add("investigate", null,
                "Independent checker expectations changed. Review that policy decision before collecting new evidence; do not adjust requirements to fit outcomes.");
            return steps;
        }
        bool stale = report.CurrentApplicability != "current";
        List<AnalysisStage> incomplete = scenario.Stages.Where(s =>
            !run.Stages.Any(r => r.Id == s.Id && r.ExecutionStatus == "completed" &&
                r.Outcome != "incomplete" && r.Evidence.Length > 0)).ToList();
        if (stale || incomplete.Count > 0)
        {
            Add("doctor", null,
                stale ? "Execution inputs or definitions changed. Preflight only the selected scenario."
                    : "Inspect incomplete-stage diagnostics and prerequisites before retrying; retain approved bounds and expected controls.",
                "doctor", "--project", path, "--scenario", scenario.Id, "--format", "summary");
            if (!stale && project.ResultStore != null)
                foreach (AnalysisStage stage in incomplete.Where(s => s.Kind == "tlc" &&
                    run.Stages.Any(r => r.Id == s.Id && r.ExecutionStatus != "blocked")))
                    Add("check-stage", stage.Id,
                        "Optional isolated TLC search into the declared result store. It does not repair this run or establish acceptance. Keep the authored budget unless explicitly changed.",
                        "check-stage", "--project", path, "--scenario", scenario.Id,
                        "--stage", stage.Id, "--format", "summary");
            Add("run", null,
                "Collect a new selected-scenario run; no stage-level dependency coverage or non-TLC evidence reuse is inferred. Use a new output directory.",
                "run", "--project", path, "--scenario", scenario.Id,
                "--output", NewOutput(report.Run), "--format", "summary");
            return steps;
        }
        if (report.Reviews.Any(r => r.Status != "accepted") || report.ChangedReviewInputs.Count > 0)
            Add("review", null,
                "Execution remains applicable. Revisit the scoped review decisions and changed review inputs; no execution replay or human approval is inferred.");
        if (report.Requirements.Any(r => r.Status != "satisfied" &&
            r.Missing.Any(m => !m.StartsWith("current accepted review ", StringComparison.Ordinal))))
            Add("investigate", null,
                "Completed evidence does not meet an independent requirement. Investigate the counterexample or missing claim before changing inputs; an unchanged rerun is not a fix.");
        if (report.AcceptanceStatus != "accepted") Accept();
        return steps;
    }

    private static string NewOutput(string run)
    {
        string candidate = run + ".rerun";
        for (int suffix = 2; File.Exists(candidate) || Directory.Exists(candidate); suffix++)
            candidate = run + ".rerun-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return candidate;
    }
}
