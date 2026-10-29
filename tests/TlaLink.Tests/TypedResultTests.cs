using System.Text.Json;
using System.Text.Json.Nodes;
using TlaLink;

namespace TlaLink.Tests;

public static class TypedResultTests
{
    public static void Register(Action<string, Action> test, string work)
    {
        TlcResult Fixture() => new(Schema.Version, "tlc-result", "passed", [], "Counter.tla", "Counter.cfg",
            0, 0, null, new(1, 512), new(Schema.Version, "process-invocation", "java",
                ["-Xmx512m", "-workers", "1"], work, 30), new(3, 2, 0, 2, null), ["Finite model only."]);
        test("analysis documents decode once to their concrete contract and preserve native outcomes", () =>
        {
            IAnalysisResult decoded = AnalysisDocuments.Parse(JsonSerializer.Serialize(Fixture(), Files.Json));
            CoreTests.True(decoded is TlcResult { Fingerprint: 0, Statistics.DistinctStates: 2 });
            CoreTests.True(AnalysisDocuments.MatchesStage("tlc", decoded));
            CoreTests.True(!AnalysisDocuments.MatchesStage("trace", decoded));
            CoreTests.Equal("passed", ProjectWorkflow.Outcome("tlc", decoded));
            CoreTests.Equal("incomplete", ProjectWorkflow.Outcome("trace", decoded));
        });
        test("analysis documents reject unsupported envelopes and malformed fixed contracts", () =>
        {
            JsonObject valid = JsonSerializer.SerializeToNode(Fixture(), Files.Json)!.AsObject();
            foreach (Action<JsonObject> corrupt in new Action<JsonObject>[] {
                d => d["kind"] = "unknown-result",
                d => d["schema_version"] = Schema.Version - 1,
                d => d["unexpected"] = true,
                d => d.Remove("fingerprint"),
                d => d["resources"]!["workers"] = "one",
                d => d["diagnostics"] = null
            })
            {
                JsonObject document = valid.DeepClone().AsObject();
                corrupt(document);
                bool rejected = false;
                try { AnalysisDocuments.Parse(document.ToJsonString()); }
                catch (Exception error) when (error is JsonException or InputException) { rejected = true; }
                CoreTests.True(rejected);
            }
            string duplicate = valid.ToJsonString().Replace("\"fingerprint\":0", "\"fingerprint\":0,\"fingerprint\":1", StringComparison.Ordinal);
            bool duplicateRejected = false;
            try { AnalysisDocuments.Parse(duplicate); }
            catch (JsonException) { duplicateRejected = true; }
            CoreTests.True(duplicateRejected);
        });
        test("typed summaries never silently project an undeclared result shape", () =>
        {
            CoreTests.ThrowsAny<InputException>(() => CommandSummary.Create(
                new { Kind = "unregistered", Status = "passed" }, new(["doctor", "--format", "summary"])));
            CommandFailure failure = new(Schema.Version, "command-error", "run", "incomplete",
                [new(ErrorCode.WorkflowStageFailed, "Synthetic native failure.")]);
            CoreTests.Equal("incomplete", ProjectWorkflow.Outcome("tlc", failure));
            CoreTests.True(!AnalysisDocuments.MatchesStage("tlc", failure));
            CommandSummaryResult summary = CommandSummary.Create(failure, new(["run", "--format", "summary"]));
            CoreTests.True(summary is FailureSummary { Status: "incomplete", Diagnostics.Count: 1 });
        });
        test("source review baselines decode the complete typed packet without losing pins or diagnostics", () =>
        {
            string path = Path.Combine(work, "review.json");
            Packet previous = new("check") { LinksStatus = "passed",
                Fingerprints = new(StringComparer.Ordinal) { ["synthetic-input"] = new('a', 64) },
                Diagnostics = [new(ErrorCode.WorkflowStageFailed, "Synthetic recorded diagnostic.")] };
            Files.WriteNew(path, previous);
            Packet decoded = ProjectInputs.Read<Packet>(path);
            CoreTests.Equal("check", decoded.Command);
            CoreTests.Equal(1, decoded.Diagnostics.Count);
            CoreTests.Equal(previous.Fingerprints["synthetic-input"], decoded.Fingerprints["synthetic-input"]);
            Packet current = new("check") { LinksStatus = "passed",
                Fingerprints = new(StringComparer.Ordinal) { ["synthetic-input"] = new('a', 64) } };
            TlaLink.Program.Baseline(path, current);
            CoreTests.Equal(0, current.Diagnostics.Count);
            current.Fingerprints["synthetic-input"] = new('b', 64);
            TlaLink.Program.Baseline(path, current);
            CoreTests.Equal("incomplete", current.LinksStatus);
            CoreTests.True(current.Diagnostics.Any(d => d.Is(ErrorCode.ReviewInputChanged)));
        });
        test("typed inspection serialization preserves selected nulls and omits only unselected members", () =>
        {
            Place place = new("Synthetic.cs", 1, 1);
            InspectedLink link = new("sample::step", null, null, "exact", "linked", null, [], []);
            LinkInspectionResult result = new(Schema.Version, "link-inspection", "ready", "sample", "tla",
                "lexical", [new(new("Step", "Counter", "operator", place, "value' = value + 1"),
                    "linked", null, [link])], [], null, "Presentation only.") {
                Query = new([], [], [], [], [], [], [], [], null, ["source"], 0, null)
            };
            string before = JsonSerializer.Serialize(result, Files.Json);
            using JsonDocument document = JsonDocument.Parse(InspectionFiltering.Json(result));
            JsonElement endpoint = document.RootElement.GetProperty("endpoints")[0];
            CoreTests.True(!endpoint.GetProperty("model").TryGetProperty("source", out _));
            JsonElement selected = endpoint.GetProperty("links")[0];
            CoreTests.Equal(JsonValueKind.Null, selected.GetProperty("selector").ValueKind);
            CoreTests.Equal(JsonValueKind.Null, selected.GetProperty("source").ValueKind);
            CoreTests.True(!selected.TryGetProperty("captures", out _));
            CoreTests.True(!selected.TryGetProperty("observations", out _));
            CoreTests.Equal(before, JsonSerializer.Serialize(result, Files.Json));
        });
    }
}
