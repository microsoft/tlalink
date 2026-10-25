using System.Text.Json;
using System.Text.Json.Nodes;
using TlaLink;

namespace TlaLink.Tests;

public static class ProjectWorkflowTests
{
    public static void Register(Action<string, Action> test, string root, string work)
    {
        string fixture = Path.Combine(work, "workflow-project");
        string example = Path.Combine(fixture, "Workflow");
        Directory.CreateDirectory(example);
        Directory.CreateDirectory(Path.Combine(fixture, "Integrated"));
        foreach (string area in new[] { "Workflow", "Integrated" })
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "examples", area)))
                File.Copy(file, Path.Combine(fixture, area, Path.GetFileName(file)));
        string path = Path.Combine(example, "project.json");
        AnalysisProject original = ProjectInputs.Read<AnalysisProject>(path);
        string dafny = TestTools.Dafny(root);
        string jar = TestTools.Jar(root);
        original = original with { Tools = new(new(dafny, Files.Hash(dafny)), new(jar, Files.Hash(jar))) };
        Save(path, original);
        string policyPath = Path.Combine(example, "requirements.json");
        AcceptancePolicy policy = ProjectInputs.Read<AcceptancePolicy>(policyPath);
        Save(policyPath, policy);
        string? creditRun = null;
        string RunScenario(string scenario, int expected = 0)
        {
            string output = Path.Combine(work, "workflow-" + scenario + "-" + Guid.NewGuid().ToString("N"));
            var response = TestCli.Run(["run", "--project", path, "--scenario", scenario, "--output", output]);
            if (response.Code != expected)
            {
                if (!File.Exists(Path.Combine(output, "run.json")))
                    throw new InvalidOperationException(response.Json);
                WorkflowRun failed = ProjectInputs.Read<WorkflowRun>(Path.Combine(output, "run.json"));
                string details = string.Join("\n", failed.Stages.Select(s => s.Id + ": " + s.Outcome +
                    (s.Evidence.Length == 0 ? "" : "\n" + Files.Read(Path.Combine(output, s.Evidence)))));
                throw new InvalidOperationException(details);
            }
            CoreTests.True(File.Exists(Path.Combine(output, "run.json")));
            return output;
        }
        string Credits() => creditRun ??= RunScenario("credits");
        int Accept(string output) => TestCli.Run(["accept", "--project", path, "--run", output]).Code;

        test("project doctor and plan resolve project-relative inputs from another directory", () =>
        {
            var doctor = TestCli.Run(["doctor", "--project", path]);
            if (doctor.Code != 0) throw new InvalidOperationException(doctor.Json);
            CoreTests.True(doctor.Json.Contains("pending", StringComparison.Ordinal));
            var plan = TestCli.Run(["plan", "--project", path, "--scenario", "lanes"]);
            if (plan.Code != 0) throw new InvalidOperationException(plan.Json);
            CoreTests.True(plan.Json.Contains("explicit handoffs", StringComparison.Ordinal));
            var text = TestCli.Run(["doctor", "--project", path, "--format", "text"]);
            CoreTests.Equal(0, text.Code);
            CoreTests.True(text.Json.Length < 4000 && text.Json.Contains("project-preflight", StringComparison.Ordinal));
        });
        test("project rejects obsolete schema unknown fields invalid compiler and cyclic dependencies", () =>
        {
            try
            {
                Save(path, original with { SchemaVersion = 1 });
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path]).Code);
                Save(path, original with { Compiler = original.Compiler with { Checked = true } });
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path]).Code);
                AnalysisScenario cycle = new("credits", [
                    new("a", "oracle", ["b"], 60, @"..\Integrated\bindings.json"),
                    new("b", "oracle", ["a"], 60, @"..\Integrated\bindings.json")]);
                Save(path, original with { Scenarios = [cycle] });
                CoreTests.Equal(2, TestCli.Run(["plan", "--project", path]).Code);
                JsonObject unknown = JsonSerializer.SerializeToNode(original, Files.Json)!.AsObject();
                unknown["guess"] = true;
                Save(path, unknown);
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path]).Code);
            }
            finally { Save(path, original); }
        });
        test("project tool version pins cannot silently follow changed executables", () =>
        {
            try
            {
                Save(path, original with { Tools = original.Tools with { Dafny = original.Tools.Dafny! with { Sha256 = new string('0', 64) } } });
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path]).Code);
            }
            finally { Save(path, original); }
        });
        test("binding and case IDs cannot alias requirement selector delimiters", () =>
        {
            string manifestPath = Path.Combine(fixture, "Integrated", "bindings.json");
            string before = Files.Read(manifestPath);
            IntegratedManifest manifest = ProjectInputs.Read<IntegratedManifest>(manifestPath);
            try
            {
                Save(manifestPath, manifest with { Bindings = [
                    manifest.Bindings[0] with { Id = "credit/return" }, .. manifest.Bindings.Skip(1)] });
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path, "--scenario", "credits"]).Code);
                Save(manifestPath, manifest with { Bindings = [
                    manifest.Bindings[0] with { Cases = [
                        manifest.Bindings[0].Cases[0] with { Id = "return/one" }] }, .. manifest.Bindings.Skip(1)] });
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path, "--scenario", "credits"]).Code);
            }
            finally { File.WriteAllText(manifestPath, before); }
        });
        test("credit workflow produces complete accepted evidence with all required cases", () =>
        {
            string output = Credits();
            CoreTests.Equal(0, Accept(output));
            var explanation = TestCli.Run(["explain", "--project", path, "--run", output]);
            CoreTests.Equal(0, explanation.Code);
            CoreTests.True(explanation.Json.Contains("\"accepted\"", StringComparison.Ordinal));
            CoreTests.Equal(2, TestCli.Run(["run", "--project", path, "--scenario", "credits", "--output", output]).Code);
        });
        test("workflow acceptance rejects missing required cases even with a successful oracle run", () =>
        {
            string output = Credits();
            try
            {
                AnalysisRequirement requirement = policy.Requirements[0];
                Save(policyPath, policy with { Requirements = [requirement with { Cases = [.. requirement.Cases!, "credit-return/missing"] }, .. policy.Requirements.Skip(1)] });
                var rejected = TestCli.Run(["accept", "--project", path, "--run", output]);
                CoreTests.True(rejected.Code != 0);
                CoreTests.True(rejected.Json.Contains("credit-return/missing", StringComparison.Ordinal));
            }
            finally { Save(policyPath, policy); }
        });
        test("workflow acceptance rejects changed stage artifacts and stale source inputs separately", () =>
        {
            string output = Credits();
            string artifact = Path.Combine(output, "decisions", "result.json");
            string before = Files.Read(artifact);
            try
            {
                File.AppendAllText(artifact, "\n");
                var bad = TestCli.Run(["accept", "--project", path, "--run", output]);
                CoreTests.Equal(1, bad.Code);
                CoreTests.True(bad.Json.Contains("WF012", StringComparison.Ordinal));
            }
            finally { File.WriteAllText(artifact, before); }
            string source = Path.Combine(fixture, "Integrated", "Decisions.cs");
            before = Files.Read(source);
            try
            {
                File.AppendAllText(source, "\n");
                var stale = TestCli.Run(["accept", "--project", path, "--run", output]);
                CoreTests.Equal(2, stale.Code);
                CoreTests.True(stale.Json.Contains("WF013", StringComparison.Ordinal));
            }
            finally { File.WriteAllText(source, before); }
            CoreTests.Equal(0, Accept(output));
        });
        test("workflow acceptance cannot turn incomplete execution into a passed control", () =>
        {
            string output = Credits();
            string manifest = Path.Combine(output, "run.json");
            string before = Files.Read(manifest);
            try
            {
                JsonObject interrupted = JsonNode.Parse(before)!.AsObject();
                interrupted["execution_status"] = "incomplete";
                Save(manifest, interrupted);
                var rejected = TestCli.Run(["accept", "--project", path, "--run", output]);
                CoreTests.Equal(2, rejected.Code);
                CoreTests.True(rejected.Json.Contains("cannot satisfy a negative control", StringComparison.Ordinal));
            }
            finally { File.WriteAllText(manifest, before); }
        });
        test("workflow lane model and entity-aware compiled execution both satisfy their requirements", () =>
        {
            string output = RunScenario("lanes");
            var accepted = TestCli.Run(["accept", "--project", path, "--run", output]);
            if (accepted.Code != 0) throw new InvalidOperationException(accepted.Json);
        });
        test("workflow cancellation cleanup and unsafe proof need explicit current adapter review", () =>
        {
            string output = RunScenario("cancellation");
            CoreTests.Equal(1, Accept(output));
            var reviewed = TestCli.Run(["record-review", "--project", path, "--obligation", "cleanup-adapter",
                "--reviewer", "test-control", "--rationale", "Synthetic review gate regression; not a product approval.",
                "--evidence", Path.GetRelativePath(example, Path.Combine(output, "cleanup", "result.json"))]);
            CoreTests.Equal(0, reviewed.Code);
            var accepted = TestCli.Run(["accept", "--project", path, "--run", output]);
            if (accepted.Code != 0) throw new InvalidOperationException(accepted.Json);
            CoreTests.Equal("accepted", ProjectAcceptance.ReviewStatus(path, original, "cleanup-adapter"));
            AnalysisProject changedClaim = original with { Obligations = [
                original.Obligations[0] with { Claim = "A different abstraction claim." }] };
            CoreTests.Equal("stale", ProjectAcceptance.ReviewStatus(path, changedClaim, "cleanup-adapter"));
            string source = Path.Combine(example, "Cancellation.cs");
            string before = Files.Read(source);
            try
            {
                File.AppendAllText(source, "\n");
                CoreTests.Equal("stale", ProjectAcceptance.ReviewStatus(path, original, "cleanup-adapter"));
                CoreTests.Equal(2, Accept(output));
            }
            finally { File.WriteAllText(source, before); }
        });
        test("native TLC rejection satisfies only its specifically required diagnostic", () =>
        {
            string nativeProject = Path.Combine(example, "native-project.json");
            File.WriteAllText(Path.Combine(example, "Broken.tla"), """
                ---- MODULE Broken ----
                EXTENDS Naturals
                VARIABLE x
                Init == x = 0
                Next == x' = x + 1
                Bounded == x < 1
                ====
                """);
            File.WriteAllText(Path.Combine(example, "Broken.cfg"), "INIT Init\nNEXT Next\nINVARIANT Bounded\nCHECK_DEADLOCK FALSE\n");
            AnalysisScenario scenario = new("negative", [new("counterexample", "tlc", [], 30,
                Models: ["Broken.tla"], Config: "Broken.cfg")]);
            AnalysisProject project = original with {
                Id = "native-controls", Requirements = "native-policy.json", Scenarios = [scenario], Obligations = []
            };
            Save(nativeProject, project);
            AcceptancePolicy acceptance = new(Schema.Version, "acceptance-policy", project.Id, [
                new("required-counterexample", "negative", "counterexample", "rejected", "TLC2110",
                    ConfigSha256: Files.Hash(Path.Combine(example, "Broken.cfg")))
            ]);
            Save(Path.Combine(example, project.Requirements), acceptance);
            string output = Path.Combine(work, "native-counterexample");
            var run = TestCli.Run(["run", "--project", nativeProject, "--scenario", "negative", "--output", output]);
            if (run.Code != 0) throw new InvalidOperationException(run.Json);
            var accepted = TestCli.Run(["accept", "--project", nativeProject, "--run", output]);
            if (accepted.Code != 0) throw new InvalidOperationException(accepted.Json);
            File.WriteAllText(Path.Combine(example, "Broken.cfg"), "INIT Init\nNEXT Next\nCHECK_DEADLOCK FALSE\n");
            var weakened = TestCli.Run(["doctor", "--project", nativeProject]);
            CoreTests.Equal(2, weakened.Code);
            CoreTests.True(weakened.Json.Contains("independently approved", StringComparison.Ordinal));
        });
    }

    private static void Save<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Files.Json));
}
