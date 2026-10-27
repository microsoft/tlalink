using System.Text.Json;
using System.Text.Json.Nodes;

namespace TlaLink;

public static class CommandSummary
{
    public static JsonObject Create(object value, CliOptions options) => Create(value, options, null);

    internal static JsonObject Create(object value, CliOptions options, AcceptanceWorklist? worklist)
    {
        JsonObject source = JsonSerializer.SerializeToNode(value, Files.Json)!.AsObject();
        JsonObject result = Select(source, [
            "command", "project", "scenario", "status", "analysis_status", "recorded_status",
            "execution_status", "archive_status", "semantic_status", "current_applicability",
            "acceptance_status", "claim", "mode", "links_status", "proof_status", "audit_status",
            "conformance_status", "coverage_status", "correspondence_status", "provenance_status",
            "directory", "path", "boundary", "boundaries", "diagnostics", "next_actions", "statistics",
            "nodes", "code_regions", "input_files", "requested_resources", "resources", "annotation_version",
            "source_count", "member_count", "call_count", "await_count"]);
        if (source["kind"]?.GetValue<string>() == "source-inventory")
            result["compiler"] = source["compiler"]?.DeepClone();
        if (source["kind"]?.GetValue<string>() == "gc-plan")
        {
            foreach (string field in new[] { "store", "retention", "planned_at", "grace_hours", "read_only",
                "deletion_authorized", "counts" })
                result[field] = source[field]?.DeepClone();
            JsonObject omitted = new();
            foreach (string field in new[] { "entries", "protected_paths", "recheck", "changed_inputs", "diagnostics" })
            {
                JsonArray items = source[field]!.AsArray();
                omitted[field] = Math.Max(0, items.Count - 10);
                result[field] = new JsonArray(items.Take(10).Select(item => {
                    if (field == "protected_paths" || field == "diagnostics") return item!.DeepClone();
                    JsonObject row = Select(item!.AsObject(), ["path", "status", "logical_bytes", "recheck_required", "kind", "scope"]);
                    if (field == "entries")
                    {
                        row["reason_count"] = item["reasons"]!.AsArray().Count;
                        row["dependency_count"] = item["dependencies"]!.AsArray().Count;
                    }
                    if (field == "recheck")
                    {
                        row["changed_input_count"] = item["changed_inputs"]!.AsArray().Count;
                        row["affected_dependency_count"] = item["affected_by"]!.AsArray().Count;
                        row["actions"] = item["actions"]!.DeepClone();
                    }
                    if (field == "changed_inputs") row["consumer_count"] = item["consumers"]!.AsArray().Count;
                    return (JsonNode)row;
                }).ToArray());
            }
            result["omitted"] = omitted;
        }
        if (source["kind"]?.GetValue<string>() == "gc-apply-result")
            foreach (string field in new[] { "store", "plan", "started_at", "read_only", "deletion_authorized",
                "deleted_units", "pending_units", "partial_unit", "removed_file_count" })
                result[field] = source[field]?.DeepClone();
        result["schema_version"] = Schema.Version;
        result["kind"] = "command-summary";
        result["result_kind"] = source["kind"]?.DeepClone();
        result["presentation_only"] = true;
        string[] stageFields = ["id", "kind", "depends_on", "execution_status", "outcome", "duration_seconds", "evidence", "diagnostics", "statistics", "requested_resources", "resources", "fingerprint", "stored"];
        if (source["scenarios"] is JsonArray scenarios)
            result["scenarios"] = new JsonArray(scenarios.Select(item => {
                JsonObject scenario = Select(item!.AsObject(), ["id"]);
                scenario["stages"] = Rows(item["stages"], stageFields);
                return (JsonNode)scenario;
            }).ToArray());
        if (source["stages"] is JsonArray stages)
        {
            JsonArray rows = Rows(stages, stageFields);
            if (source["kind"]?.GetValue<string>() == "analysis-run")
            {
                int reused = 0;
                for (int i = 0; i < stages.Count; i++)
                {
                    bool primaryReused = stages[i]!["stored_results"] is JsonArray stored && stored.Any(s =>
                        s!["primary"]!.GetValue<bool>() && s["origin"]!.GetValue<string>() == "reused");
                    rows[i]!["reused"] = primaryReused;
                    if (primaryReused) reused++;
                }
                result["reuse"] = new JsonObject { ["reused"] = reused, ["executed"] = stages.Count - reused };
            }
            result["stages"] = rows;
        }
        else if (source["stages"] is JsonObject statuses) result["stages"] = statuses.DeepClone();
        if (source["kind"]?.GetValue<string>() == "stage-check")
            foreach (string field in new[] { "stage", "outcome", "fingerprint", "key", "family", "entry", "duration_seconds", "timeout_seconds" })
                result[field] = source[field]?.DeepClone();
        if (source["requirements"] is JsonArray requirements)
            result["requirements"] = Rows(requirements, ["id", "status", "missing"]);
        if (source["check_coverage"] is JsonArray coverage)
            result["check_coverage"] = Rows(coverage, ["id", "kind", "scenario", "status", "requirements", "gaps"]);
        if (source["processes"] is JsonArray processes)
            result["processes"] = Rows(processes, ["id", "exit_code", "timed_out", "duration_seconds"]);
        if (source["review_status"] is JsonArray reviews)
            result["review_status"] = Rows(reviews, ["id", "status"]);
        else if (source["reviews"] is JsonArray decisions)
            result["review_status"] = new JsonArray(decisions.Select(item => {
                JsonObject row = new() { ["id"] = item!["obligation"]?.DeepClone(), ["status"] = item["status"]?.DeepClone() };
                if (item["decision"] is JsonObject recorded)
                    foreach (string field in new[] { "disposition", "reviewer", "rationale", "recorded_at" })
                        if (recorded.TryGetPropertyValue(field, out JsonNode? value)) row[field] = value?.DeepClone();
                return (JsonNode)row;
            }).ToArray());
        if (source["decision"] is JsonObject decision)
            result["decision"] = Select(decision, ["obligation", "disposition", "reviewer", "rationale"]);
        if (options.Optional("output") is string output) result["output"] = FilePaths.Full(output);
        if (options.Optional("result") is string receipt) result["result"] = FilePaths.Full(receipt);
        if (options.Optional("run") is string run) result["run"] = FilePaths.Full(run);
        worklist?.Apply(result);
        return result;
    }

    private static JsonObject Select(JsonObject source, string[] fields)
    {
        JsonObject result = new();
        foreach (string field in fields)
            if (source.TryGetPropertyValue(field, out JsonNode? value)) result[field] = value?.DeepClone();
        return result;
    }

    private static JsonArray Rows(JsonNode? source, string[] fields) =>
        new(source!.AsArray().Select(row => (JsonNode)Select(row!.AsObject(), fields)).ToArray());
}
