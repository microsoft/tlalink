namespace TlaLink;

internal sealed class AcceptanceWorklist
{
    private sealed record ReviewItem(string Id, string Status, HashSet<string> Stages, HashSet<string> Requirements);
    private sealed record RequirementItem(string Id, string Stage, List<string> Missing);

    private readonly List<IncompleteStageItem> stages;
    private readonly Dictionary<string, ReviewItem> reviews = new(StringComparer.Ordinal);
    private readonly List<RequirementItem> requirements = [];
    private readonly List<Issue> diagnostics;
    private readonly HashSet<Issue> groupedDiagnostics;

    internal AcceptanceWorklist(WorkflowRun run, AnalysisScenario scenario, List<AnalysisRequirement> policy,
        List<Issue> diagnostics, HashSet<Issue> groupedDiagnostics)
    {
        this.diagnostics = diagnostics;
        this.groupedDiagnostics = groupedDiagnostics;
        stages = [];
        foreach (AnalysisStage declared in scenario.Stages)
        {
            WorkflowStageResult? stage = run.Stages.FirstOrDefault(s => s.Id == declared.Id);
            if (stage == null || stage.ExecutionStatus != "completed" || string.IsNullOrEmpty(stage.Evidence))
                stages.Add(new(declared.Id, declared.Kind, stage?.ExecutionStatus ?? "missing", stage?.Outcome,
                    stage == null ? "missing-stage" : stage.ExecutionStatus != "completed" ? "incomplete-execution" : "missing-evidence",
                    policy.Count(r => r.Stage == declared.Id)));
        }
    }

    internal bool HasIncompleteStages => stages.Count > 0;
    internal bool IsIncomplete(string stage) => stages.Any(s => s.Id == stage);

    internal void Review(string id, string status, AnalysisRequirement requirement)
    {
        if (!reviews.TryGetValue(id, out ReviewItem? item))
            reviews.Add(id, item = new(id, status, new(StringComparer.Ordinal), new(StringComparer.Ordinal)));
        item.Stages.Add(requirement.Stage);
        item.Requirements.Add(requirement.Id);
    }

    internal void Requirement(AnalysisRequirement requirement, List<string> missing)
    {
        if (missing.Count > 0) requirements.Add(new(requirement.Id, requirement.Stage, missing));
    }

    internal AcceptanceSummary Apply(AcceptanceSummary summary)
    {
        const int itemLimit = 10, detailLimit = 3;
        List<WorklistGroup> groups = [];
        List<string> actions = [];
        int itemCount = 0;
        void Group<T>(string kind, string action, List<T> items)
        {
            if (items.Count == 0) return;
            itemCount += items.Count;
            groups.Add(new WorklistGroup<T>(kind, items.Count, action, items.Take(itemLimit).ToList(),
                Math.Max(0, items.Count - itemLimit)));
            actions.Add(action);
        }
        Group("incomplete_stages", "Inspect the recorded stage failure/logs and rerun the affected analysis with unchanged requirements.",
            stages.OrderBy(s => s.Id, StringComparer.Ordinal).ToList());
        Group("reviews", "Review the scoped claims and evidence, then record a reasoned decision; do not approve a review merely to clear acceptance.",
            reviews.Values.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r =>
                new PendingReviewItem(r.Id, r.Status, r.Stages.Count, r.Requirements.Count)).ToList());
        Group("requirements", "Inspect the unmet evidence requirements; preserve approved configuration pins, expected outcomes and controls.",
            requirements.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new UnmetRequirementItem(
                r.Id, r.Stage, r.Missing.Take(detailLimit).ToList(),
                Math.Max(0, r.Missing.Count - detailLimit))).ToList());
        Group("diagnostics", "Inspect the remaining diagnostics and their examples; preserve their recorded severity and the separate acceptance verdict.",
            diagnostics.Where(i => !groupedDiagnostics.Contains(i)).GroupBy(i => (i.Code, i.Severity))
                .OrderBy(g => g.Key.Severity == "error" ? 0 : 1).ThenBy(g => g.Key.Code, StringComparer.Ordinal)
                .Select(g => new DiagnosticItem(g.Key.Code, g.Key.Severity, g.Count(), g.First())).ToList());
        Dictionary<string, int> counts = new(StringComparer.Ordinal) {
            ["diagnostics"] = summary.Diagnostics.Count, ["requirements"] = summary.Requirements.Count,
            ["check_coverage"] = summary.CheckCoverage.Count, ["review_status"] = summary.ReviewStatus.Count
        };
        Dictionary<string, int>? Omitted(params (string Name, int Count)[] fields)
        {
            Dictionary<string, int> result = fields.Where(f => f.Count > detailLimit)
                .ToDictionary(f => f.Name, f => f.Count - detailLimit, StringComparer.Ordinal);
            return result.Count == 0 ? null : result;
        }
        static Dictionary<string, int> StatusCounts(IEnumerable<string> statuses) => statuses
            .GroupBy(s => s, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        return summary with {
            Worklist = new(itemCount, groups), NextActions = actions,
            DiagnosticCounts = StatusCounts(diagnostics.Select(d => d.Severity)),
            RequirementsCounts = StatusCounts(summary.Requirements.Select(r => r.Status)),
            ReviewStatusCounts = StatusCounts(summary.ReviewStatus.Select(r => r.Status)),
            Counts = counts, Omitted = counts.ToDictionary(p => p.Key, p => Math.Max(0, p.Value - detailLimit), StringComparer.Ordinal),
            Diagnostics = summary.Diagnostics.Take(detailLimit).ToList(),
            Requirements = summary.Requirements.Take(detailLimit).Select(r => r with {
                Missing = r.Missing.Take(detailLimit).ToList(), Omitted = Omitted(("missing", r.Missing.Count))
            }).ToList(),
            CheckCoverage = summary.CheckCoverage.Take(detailLimit).Select(c => c with {
                Requirements = c.Requirements.Take(detailLimit).ToList(), Gaps = c.Gaps.Take(detailLimit).ToList(),
                Omitted = Omitted(("requirements", c.Requirements.Count), ("gaps", c.Gaps.Count))
            }).ToList(),
            ReviewStatus = summary.ReviewStatus.Take(detailLimit).ToList()
        };
    }
}
