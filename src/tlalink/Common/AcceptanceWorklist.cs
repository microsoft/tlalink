using System.Text.Json;
using System.Text.Json.Nodes;

namespace TlaLink;

internal sealed class AcceptanceWorklist
{
    private sealed record StageItem(string Id, string Kind, string ExecutionStatus, string? Outcome,
        string Reason, int RequirementCount);
    private sealed record ReviewItem(string Id, string Status, HashSet<string> Stages, HashSet<string> Requirements);
    private sealed record RequirementItem(string Id, string Stage, List<string> Missing);

    private readonly List<StageItem> stages;
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

    internal void Apply(JsonObject summary)
    {
        const int itemLimit = 10, detailLimit = 3;
        JsonArray groups = [];
        JsonArray actions = [];
        int itemCount = 0;
        void Group<T>(string kind, string action, List<T> items)
        {
            if (items.Count == 0) return;
            itemCount += items.Count;
            groups.Add(new JsonObject {
                ["kind"] = kind, ["count"] = items.Count, ["next_action"] = action,
                ["items"] = JsonSerializer.SerializeToNode(items.Take(itemLimit), Files.Json),
                ["omitted"] = Math.Max(0, items.Count - itemLimit)
            });
            actions.Add(action);
        }
        Group("incomplete_stages", "Inspect the recorded stage failure/logs and rerun the affected analysis with unchanged requirements.",
            stages.OrderBy(s => s.Id, StringComparer.Ordinal).ToList());
        Group("reviews", "Review the scoped claims and evidence, then record a reasoned decision; do not approve a review merely to clear acceptance.",
            reviews.Values.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new {
                r.Id, r.Status, StageCount = r.Stages.Count, RequirementCount = r.Requirements.Count
            }).ToList());
        Group("requirements", "Inspect the unmet evidence requirements; preserve approved configuration pins, expected outcomes and controls.",
            requirements.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => new {
                r.Id, r.Stage, Missing = r.Missing.Take(detailLimit).ToList(),
                OmittedMissing = Math.Max(0, r.Missing.Count - detailLimit)
            }).ToList());
        Group("diagnostics", "Inspect the remaining diagnostics and their examples; preserve their recorded severity and the separate acceptance verdict.",
            diagnostics.Where(i => !groupedDiagnostics.Contains(i)).GroupBy(i => (i.Code, i.Severity))
                .OrderBy(g => g.Key.Severity == "error" ? 0 : 1).ThenBy(g => g.Key.Code, StringComparer.Ordinal)
                .Select(g => new { g.Key.Code, g.Key.Severity, Count = g.Count(), Example = g.First() }).ToList());
        summary["worklist"] = new JsonObject { ["item_count"] = itemCount, ["groups"] = groups };
        summary["next_actions"] = actions;
        summary["diagnostic_counts"] = JsonSerializer.SerializeToNode(diagnostics.GroupBy(i => i.Severity)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()), Files.Json);
        JsonObject counts = new(), omitted = new();
        foreach (string field in new[] { "diagnostics", "requirements", "check_coverage", "review_status" })
        {
            if (summary[field] is not JsonArray rows) continue;
            counts[field] = rows.Count;
            omitted[field] = Math.Max(0, rows.Count - detailLimit);
            if (field is "requirements" or "review_status")
                summary[field + "_counts"] = JsonSerializer.SerializeToNode(rows.GroupBy(r => r!["status"]!.GetValue<string>())
                    .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()), Files.Json);
            summary[field] = new JsonArray(rows.Take(detailLimit).Select(row => {
                JsonObject copy = row!.DeepClone().AsObject();
                JsonObject nestedOmitted = new();
                foreach (string nested in new[] { "missing", "requirements", "gaps" })
                    if (copy[nested] is JsonArray details && details.Count > detailLimit)
                    {
                        nestedOmitted[nested] = details.Count - detailLimit;
                        copy[nested] = new JsonArray(details.Take(detailLimit).Select(d => d!.DeepClone()).ToArray());
                    }
                if (nestedOmitted.Count > 0) copy["omitted"] = nestedOmitted;
                return (JsonNode)copy;
            }).ToArray());
        }
        summary["counts"] = counts;
        summary["omitted"] = omitted;
    }
}
