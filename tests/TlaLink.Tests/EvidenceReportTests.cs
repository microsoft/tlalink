using System.Text.Json;
using System.Text.Json.Nodes;
using TlaLink;

namespace TlaLink.Tests;

public static class EvidenceReportTests
{
    public static void Register(Action<string, Action> test, string work)
    {
        (string Directory, string Manifest, string Source, EvidenceReportManifest Value) Fixture()
        {
            string directory = Path.Combine(work, "report-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string source = Path.Combine(directory, "Counter #1.cs");
            File.WriteAllText(source, "internal static class Counter { internal static int Value => 1; }\n");
            EvidenceReportManifest manifest = new(Schema.Version, "evidence-report", "Selected counter evidence",
                "Synthetic scoped evidence, not arbitrary execution refinement.",
                new() { [Path.GetFileName(source)] = Files.Hash(source) },
                [
                    new("code", "code", "Counter.Value", "Assigned source region; correspondence remains pending.",
                        "assigned", null, [], [Path.GetFileName(source)], [], ["Review source/model correspondence."]),
                    new("check", "check", "Finite counter check", "Recorded finite native check.",
                        "passed", "passed", [], [Path.GetFileName(source)], ["Finite bounds."], []),
                    new("control", "check", "Wrong-value control", "Expected rejection, not a tool failure.",
                        "rejected", "rejected", [], [Path.GetFileName(source)], [], []),
                    new("closure", "milestone", "Closure", "Needs code correspondence and native checks.",
                        "pending", "accepted", ["code", "check", "control"], [Path.GetFileName(source)], [], ["Approval missing."])
                ],
                [new(Path.GetFileName(source), 1, 1, "Counter.Value", "synthetic", "assigned",
                    ["Finite counter contract"], "Not a theorem about every code path.")],
                ["Trusted producing host."], ["Closure remains open."]);
            string path = Path.Combine(directory, "report.json");
            Files.WriteNew(path, manifest);
            return (directory, path, source, manifest);
        }
        test("multi-run summaries retain every explicit path and preserve command-error diagnostics", () =>
        {
            string project = Path.Combine(work, "absent-report-project.json");
            string first = Path.Combine(work, "first-run"), second = Path.Combine(work, "second-run");
            string[] arguments = ["report", "--project", project, "--run", first, "--run", second, "--format", "summary"];
            JsonObject summary = CommandSummary.Create(new { SchemaVersion = Schema.Version, Kind = "report-result" },
                new CliOptions(arguments));
            CoreTests.Equal(2, summary["runs"]!.AsArray().Count);
            CoreTests.Equal(first, summary["runs"]![0]!.GetValue<string>());
            CoreTests.Equal(second, summary["runs"]![1]!.GetValue<string>());
            CoreTests.True(!summary.ContainsKey("run"));
            var failure = TestCli.Run(arguments);
            CoreTests.Equal(2, failure.Code);
            using JsonDocument document = JsonDocument.Parse(failure.Json);
            CoreTests.Equal(2, document.RootElement.GetProperty("runs").GetArrayLength());
            CoreTests.True(document.RootElement.GetProperty("diagnostics").GetArrayLength() > 0);
        });
        test("whole evidence report emits Markdown Mermaid code inventory and separate claim dimensions", () =>
        {
            var fixture = Fixture();
            string output = Path.Combine(fixture.Directory, "proof report.md");
            var result = TestCli.Run(["report", "--manifest", fixture.Manifest, "--output", output, "--format", "summary"]);
            if (result.Code != 0) throw new InvalidOperationException(result.Json);
            string markdown = Files.Read(output);
            CoreTests.Equal(2, markdown.Split("```mermaid", StringSplitOptions.None).Length - 1);
            CoreTests.True(markdown.Contains("n0[\"Counter.Value", StringComparison.Ordinal));
            CoreTests.True(markdown.Contains("n1 --> n3", StringComparison.Ordinal));
            CoreTests.True(markdown.Contains("n2[\"Wrong-value control #124; recorded: rejected\"]:::matched", StringComparison.Ordinal));
            CoreTests.True(markdown.Contains("Counter%20%231.cs", StringComparison.Ordinal));
            CoreTests.True(markdown.Contains("Code coverage inventory", StringComparison.Ordinal));
            CoreTests.True(markdown.Contains("Closure remains open.", StringComparison.Ordinal));
            CoreTests.True(markdown.Contains("not per-method validation", StringComparison.Ordinal));
            using JsonDocument summary = JsonDocument.Parse(result.Json);
            CoreTests.Equal("report-result", summary.RootElement.GetProperty("result_kind").GetString());
            CoreTests.Equal(4, summary.RootElement.GetProperty("nodes").GetInt32());
            CoreTests.Equal(1, summary.RootElement.GetProperty("code_regions").GetInt32());
            CoreTests.Equal("current", summary.RootElement.GetProperty("current_applicability").GetString());
            CoreTests.Equal(2, TestCli.Run(["report", "--manifest", fixture.Manifest, "--output", output]).Code);
            CoreTests.Equal(markdown, Files.Read(output));
        });
        test("report renders stale or missing inputs explicitly and removes all green matches", () =>
        {
            var fixture = Fixture();
            File.AppendAllText(fixture.Source, "// changed\n");
            string output = Path.Combine(fixture.Directory, "stale.md");
            var result = TestCli.Run(["report", "--manifest", fixture.Manifest, "--output", output]);
            CoreTests.Equal(2, result.Code);
            CoreTests.True(result.Json.Contains("RP002", StringComparison.Ordinal));
            string markdown = Files.Read(output);
            CoreTests.True(markdown.Contains("STALE snapshot", StringComparison.Ordinal));
            CoreTests.True(!markdown.Contains("]:::matched", StringComparison.Ordinal));
            File.Delete(fixture.Source);
            CoreTests.Equal(2, TestCli.Run(["report", "--manifest", fixture.Manifest,
                "--output", Path.Combine(fixture.Directory, "missing.md")]).Code);
        });
        test("report rejects cycles duplicate aliases unpinned evidence and invalid source spans", () =>
        {
            var fixture = Fixture();
            void Reject(EvidenceReportManifest value, string? detail = null)
            {
                string input = Path.Combine(fixture.Directory, Guid.NewGuid().ToString("N") + ".json");
                string output = Path.ChangeExtension(input, ".md");
                Files.WriteNew(input, value);
                var result = TestCli.Run(["report", "--manifest", input, "--output", output]);
                CoreTests.Equal(2, result.Code);
                CoreTests.True(result.Json.Contains("RP001", StringComparison.Ordinal));
                if (detail is not null)
                    CoreTests.True(JsonNode.Parse(result.Json)!["diagnostics"]!.AsArray()
                        .Any(d => d!["message"]!.GetValue<string>().Contains(detail, StringComparison.Ordinal)));
                CoreTests.True(!File.Exists(output));
            }
            Reject(fixture.Value with { Nodes = [fixture.Value.Nodes[0] with { DependsOn = ["closure"] }, .. fixture.Value.Nodes.Skip(1)] });
            Reject(fixture.Value with { Nodes = [fixture.Value.Nodes[0] with { DependsOn = ["absent"] }, .. fixture.Value.Nodes.Skip(1)] },
                fixture.Value.Nodes[0].Id + " -> absent");
            Reject(fixture.Value with { Nodes = [fixture.Value.Nodes[0] with { Evidence = ["untracked.txt"] }, .. fixture.Value.Nodes.Skip(1)] });
            Reject(fixture.Value with { CodeRegions = [fixture.Value.CodeRegions[0] with { Line = 2, EndLine = 1 }] });
            Reject(fixture.Value with { Fingerprints = new(fixture.Value.Fingerprints) { [fixture.Source] = Files.Hash(fixture.Source) } });
        });
        test("report escapes diagram fences HTML directives labels and Markdown table delimiters", () =>
        {
            var fixture = Fixture();
            string attack = "\" ]\n```\n%%{init: {securityLevel: 'loose'}}%%\n<script>x</script>|&`";
            string path = Path.Combine(fixture.Directory, "escaped.json");
            Files.WriteNew(path, fixture.Value with {
                Title = attack, Nodes = [fixture.Value.Nodes[0] with { Title = attack }, .. fixture.Value.Nodes.Skip(1)],
                CodeRegions = [fixture.Value.CodeRegions[0] with { Symbol = attack, Reason = attack }]
            });
            string output = Path.Combine(fixture.Directory, "escaped.md");
            CoreTests.Equal(0, TestCli.Run(["report", "--manifest", path, "--output", output]).Code);
            string markdown = Files.Read(output);
            CoreTests.Equal(2, markdown.Split("```mermaid", StringSplitOptions.None).Length - 1);
            CoreTests.True(!markdown.Contains("<script>", StringComparison.Ordinal));
            CoreTests.True(!markdown.Contains("%%{init:", StringComparison.Ordinal));
            CoreTests.True(markdown.Contains("#34;", StringComparison.Ordinal));
            CoreTests.True(markdown.Contains("&#124;", StringComparison.Ordinal));
        });
        test("report strict input rejects duplicate fields unknown options and null nodes", () =>
        {
            var fixture = Fixture();
            JsonObject document = JsonNode.Parse(Files.Read(fixture.Manifest))!.AsObject();
            document["nodes"]!.AsArray()[0] = null;
            string input = Path.Combine(fixture.Directory, "null-node.json");
            File.WriteAllText(input, document.ToJsonString());
            CoreTests.Equal(2, TestCli.Run(["report", "--manifest", input,
                "--output", Path.Combine(fixture.Directory, "null.md")]).Code);
            File.WriteAllText(input, "{\"schema_version\":4,\"schema_version\":4}");
            CoreTests.Equal(2, TestCli.Run(["report", "--manifest", input,
                "--output", Path.Combine(fixture.Directory, "duplicate.md")]).Code);
            CoreTests.Equal(2, TestCli.Run(["report", "--manifest", fixture.Manifest, "--unexpected", "value"]).Code);
        });
    }
}
