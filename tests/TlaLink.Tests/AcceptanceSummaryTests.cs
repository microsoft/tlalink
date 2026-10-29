using System.Text.Json;
using System.Text.Json.Nodes;
using TlaLink;

namespace TlaLink.Tests;

public static class AcceptanceSummaryTests
{
    internal static AcceptanceResult Packet(List<Issue>? diagnostics = null,
        List<RequirementResult>? requirements = null, List<ReviewResult>? reviews = null,
        List<CheckCoverage>? coverage = null, string acceptance = "not-accepted") =>
        new(Schema.Version, "acceptance-result", "summary-control", "complete", "", new('0', 64),
            "incomplete", "consistent", "current", acceptance, requirements ?? [], diagnostics ?? [],
            coverage ?? [], [], reviews ?? [], diagnostics?.Select(d => d.Message).ToList() ?? [], "Synthetic acceptance fixture.");

    private static JsonObject Summary(AcceptanceResult result, CliOptions options, AcceptanceWorklist worklist) =>
        JsonNode.Parse(CommandSummary.Create(result, options, worklist).ToJsonString())!.AsObject();

    public static void Register(Action<string, Action> test, string work)
    {
        (WorkflowRun Run, AnalysisScenario Scenario, List<AnalysisRequirement> Policy) Fixture(int count, int incomplete)
        {
            List<AnalysisStage> stages = Enumerable.Range(0, count).Select(i =>
                new AnalysisStage("stage-" + i, "tlc", [], 30, Models: ["Model.tla"], Config: "Model.cfg")).ToList();
            WorkflowRun run = new() {
                Project = "summary-control", ProjectDirectory = work, Scenario = "complete",
                ProjectSha256 = new('0', 64), RequirementsSha256 = new('0', 64),
                Stages = stages.Select((s, i) => new WorkflowStageResult(s.Id, s.Kind,
                    i < incomplete ? "incomplete" : "completed", i < incomplete ? "incomplete" : "rejected",
                    i < incomplete ? "" : Path.Combine(s.Id, "result.json"), "", [], 1)).ToList()
            };
            return (run, new("complete", stages), stages.Select(s =>
                new AnalysisRequirement("requirement-" + s.Id, "complete", s.Id, "rejected", Diagnostic: "TLC2114")).ToList());
        }
        test("acceptance worklist collapses 96 diagnostics into three searches and eight shared reviews", () =>
        {
            var f = Fixture(92, 3);
            List<Issue> diagnostics = Enumerable.Range(0, 96).Select(i =>
                new Issue(i < 4 ? ErrorCode.WorkflowExecutionIncomplete : ErrorCode.WorkflowRequirementUnsatisfied,
                    "Original diagnostic " + i + ": " + string.Join(", ", Enumerable.Range(0, 8).Select(r => "missing current accepted review review-" + r)))).ToList();
            HashSet<Issue> grouped = new(diagnostics, ReferenceEqualityComparer.Instance);
            AcceptanceWorklist worklist = new(f.Run, f.Scenario, f.Policy, diagnostics, grouped);
            foreach (AnalysisRequirement requirement in f.Policy)
                for (int i = 0; i < 8; i++) worklist.Review("review-" + i, "pending", requirement);
            AcceptanceResult packet = Packet(diagnostics,
                f.Policy.Select(r => new RequirementResult(r.Id, "unsatisfied",
                    Enumerable.Range(0, 8).Select(i => "current accepted review review-" + i).ToList())).ToList(),
                Enumerable.Range(0, 8).Select(i => new ReviewResult("review-" + i, "pending", null)).ToList(),
                Enumerable.Range(0, 15).Select(i => new CheckCoverage("check-" + i, "safety", "complete",
                    "mapped", f.Policy.Select(r => r.Id).ToList(), [])).ToList());
            CliOptions options = new(["accept", "--format", "summary"]);
            string before = JsonSerializer.Serialize(packet, Files.Json);
            JsonObject summary = Summary(packet, options, worklist);
            CoreTests.Equal(11, summary["worklist"]!["item_count"]!.GetValue<int>());
            JsonArray groups = summary["worklist"]!["groups"]!.AsArray();
            CoreTests.Equal(2, groups.Count);
            CoreTests.Equal(3, groups[0]!["count"]!.GetValue<int>());
            CoreTests.Equal(8, groups[1]!["count"]!.GetValue<int>());
            CoreTests.Equal(92, groups[1]!["items"]![0]!["requirement_count"]!.GetValue<int>());
            CoreTests.Equal(96, summary["counts"]!["diagnostics"]!.GetValue<int>());
            CoreTests.Equal(93, summary["omitted"]!["diagnostics"]!.GetValue<int>());
            CoreTests.Equal(89, summary["omitted"]!["requirements"]!.GetValue<int>());
            CoreTests.Equal(5, summary["requirements"]![0]!["omitted"]!["missing"]!.GetValue<int>());
            CoreTests.Equal(89, summary["check_coverage"]![0]!["omitted"]!["requirements"]!.GetValue<int>());
            CoreTests.Equal(2, summary["next_actions"]!.AsArray().Count);
            CoreTests.True(summary.ToJsonString(Files.Json).Length < 12000);
            CoreTests.True(summary.ToJsonString(Files.Json).Length < CommandSummary.Create(packet, options).ToJsonString(Files.Json).Length / 3);
            CoreTests.Equal(before, JsonSerializer.Serialize(packet, Files.Json));
        });
        test("acceptance worklist bounds each group and keeps unclassified diagnostics visible", () =>
        {
            var f = Fixture(14, 14);
            List<Issue> diagnostics = Enumerable.Range(0, 14).Select(i => new Issue("CONTROL" + i, "error", "unclassified " + i)).ToList();
            AcceptanceWorklist worklist = new(f.Run, f.Scenario, f.Policy, diagnostics, new(ReferenceEqualityComparer.Instance));
            for (int i = 0; i < 14; i++)
            {
                worklist.Review("review-" + i, i == 0 ? "stale" : i == 1 ? "rejected" : "pending", f.Policy[i]);
                worklist.Review("review-" + i, "pending", f.Policy[i]);
                worklist.Requirement(f.Policy[i], ["unmet claim", "unmet diagnostic", "unmet case", "unmet test"]);
            }
            JsonObject summary = Summary(Packet(diagnostics, reviews: [
                new("review-0", "stale", new(Schema.Version, "review-decision", "review-0", new('0', 64),
                    "accepted", "test-only", "old decision", DateTimeOffset.UtcNow, [], []))
            ]), new(["explain", "--format", "summary"]), worklist);
            foreach (JsonNode? group in summary["worklist"]!["groups"]!.AsArray())
            {
                CoreTests.Equal(14, group!["count"]!.GetValue<int>());
                CoreTests.Equal(10, group["items"]!.AsArray().Count);
                CoreTests.Equal(4, group["omitted"]!.GetValue<int>());
            }
            CoreTests.Equal(56, summary["worklist"]!["item_count"]!.GetValue<int>());
            CoreTests.Equal("stale", summary["review_status"]![0]!["status"]!.GetValue<string>());
            CoreTests.Equal("accepted", summary["review_status"]![0]!["disposition"]!.GetValue<string>());
            CoreTests.Equal(1, summary["worklist"]!["groups"]![1]!["items"]![0]!["requirement_count"]!.GetValue<int>());
            CoreTests.Equal(1, summary["worklist"]!["groups"]![2]!["items"]![0]!["omitted_missing"]!.GetValue<int>());
            CoreTests.Equal("CONTROL0", summary["worklist"]!["groups"]![3]!["items"]![0]!["code"]!.GetValue<string>());
            CoreTests.Equal("unclassified 0", summary["worklist"]!["groups"]![3]!["items"]![0]!["example"]!["message"]!.GetValue<string>());
        });
        test("completed expected rejection controls do not become acceptance work items", () =>
        {
            var f = Fixture(1, 0);
            AcceptanceWorklist worklist = new(f.Run, f.Scenario, f.Policy, [], new(ReferenceEqualityComparer.Instance));
            JsonObject summary = Summary(Packet(requirements: [new("control", "satisfied", [])], acceptance: "accepted"),
                new(["accept", "--format", "summary"]), worklist);
            CoreTests.Equal(0, summary["worklist"]!["item_count"]!.GetValue<int>());
            CoreTests.Equal(0, summary["next_actions"]!.AsArray().Count);
            CoreTests.Equal("accepted", summary["acceptance_status"]!.GetValue<string>());
        });
        test("unclassified warnings remain visible without changing an accepted verdict", () =>
        {
            var f = Fixture(1, 0);
            List<Issue> diagnostics = [new("CONTROL", "warning", "Inspect this warning.")];
            AcceptanceWorklist worklist = new(f.Run, f.Scenario, f.Policy, diagnostics, new(ReferenceEqualityComparer.Instance));
            JsonObject summary = Summary(Packet(diagnostics, acceptance: "accepted"),
                new(["accept", "--format", "summary"]), worklist);
            CoreTests.Equal("accepted", summary["acceptance_status"]!.GetValue<string>());
            CoreTests.Equal(1, summary["diagnostic_counts"]!["warning"]!.GetValue<int>());
            CoreTests.Equal("warning", summary["worklist"]!["groups"]![0]!["items"]![0]!["severity"]!.GetValue<string>());
        });
        test("accept and explain group shared gates without changing full receipts or exit codes", () =>
        {
            string directory = Path.Combine(work, "acceptance-summary-cli");
            string runDirectory = Path.Combine(directory, "run");
            Directory.CreateDirectory(Path.Combine(runDirectory, "inputs"));
            string projectPath = Path.Combine(directory, "project.json"), policyPath = Path.Combine(directory, "requirements.json");
            string configPath = Path.Combine(directory, "Model.cfg");
            File.WriteAllText(configPath, "INIT Init\nNEXT Next\n");
            File.WriteAllText(Path.Combine(directory, "Model.tla"), "synthetic unexecuted input");
            File.WriteAllText(Path.Combine(directory, "tool.jar"), "synthetic unexecuted tool");
            var f = Fixture(2, 2);
            AnalysisProject project = new(Schema.Version, "analysis-project", "summary-control",
                new(Jar: new("tool.jar", Files.Hash(Path.Combine(directory, "tool.jar")))),
                new("net10.0", "14", "enable", false, true, []), "requirements.json", [f.Scenario],
                [new("shared", "Synthetic review control.", [], ["Model.tla"])], "reviews");
            AcceptancePolicy policy = new(Schema.Version, "acceptance-policy", project.Id,
                f.Policy.Select(r => r with { Reviews = ["shared"], ConfigSha256 = Files.Hash(configPath) }).ToList());
            File.WriteAllText(projectPath, JsonSerializer.Serialize(project, Files.Json));
            File.WriteAllText(policyPath, JsonSerializer.Serialize(policy, Files.Json));
            WorkflowRun run = new() {
                Project = project.Id, ProjectDirectory = directory, Scenario = f.Scenario.Id,
                ProjectSha256 = Files.Hash(projectPath), RequirementsSha256 = Files.Hash(policyPath), Stages = f.Run.Stages
            };
            foreach (string path in new[] { projectPath, policyPath, configPath })
            {
                string hash = Files.Hash(path);
                run.Fingerprints.Add(path, hash);
                File.Copy(path, Path.Combine(runDirectory, "inputs", hash));
            }
            File.WriteAllText(Path.Combine(runDirectory, "marker.txt"), "synthetic archive marker");
            run.Artifacts.Add("marker.txt", Files.Hash(Path.Combine(runDirectory, "marker.txt")));
            File.WriteAllText(Path.Combine(runDirectory, "run.json"), JsonSerializer.Serialize(run, Files.Json));
            var full = TestCli.Run(["accept", "--project", projectPath, "--run", runDirectory]);
            CoreTests.Equal(2, full.Code);
            using JsonDocument fullReport = JsonDocument.Parse(full.Json);
            CoreTests.Equal("consistent", fullReport.RootElement.GetProperty("archive_status").GetString());
            CoreTests.Equal("current", fullReport.RootElement.GetProperty("current_applicability").GetString());
            foreach (string command in new[] { "accept", "explain" })
            {
                string receipt = Path.Combine(directory, command + "-receipt.json");
                var response = TestCli.Run([command, "--project", projectPath, "--run", runDirectory,
                    "--format", "summary", "--output", receipt]);
                CoreTests.Equal(full.Code, response.Code);
                JsonObject summary = JsonNode.Parse(response.Json)!.AsObject();
                CoreTests.Equal(3, summary["worklist"]!["item_count"]!.GetValue<int>());
                CoreTests.Equal(5, summary["counts"]!["diagnostics"]!.GetValue<int>());
                CoreTests.Equal("not-accepted", summary["acceptance_status"]!.GetValue<string>());
                CoreTests.True(JsonNode.DeepEquals(JsonNode.Parse(full.Json), JsonNode.Parse(Files.Read(receipt))));
                CoreTests.True(!Files.Read(receipt).Contains("\"worklist\"", StringComparison.Ordinal));
            }
        });
    }
}
