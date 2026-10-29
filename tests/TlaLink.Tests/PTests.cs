using System.Text.Json;
using System.Text.Json.Nodes;

namespace TlaLink.Tests;

public static class PTests
{
    public static void Register(Action<string, Action> test, string root, string work)
    {
        test("P results distinguish sampled exploration from exhaustion and partial exploration", () =>
        {
            CoreTests.Equal("explored-schedules", PResults.Classify("pchecker", 0, false,
                "Found 0 bugs.\nExplored 10 schedules\n", 10).Claim);
            foreach (string text in new[] { "", "Found 0 bugs.", "Found 0 bugs.\nExplored 1 schedule",
                "Found 0 bugs.\nExplored 10 schedules\nReached the max scheduling steps bound" })
                CoreTests.Equal("incomplete", PResults.Classify("pchecker", 0, false, text, 10).Status);
            CoreTests.Equal("exhaustive-finite-model", PResults.Classify("pex", 0, false,
                "Found 0 bugs.\nResult: correct for any depth\n", 1).Claim);
            foreach (string text in new[] { "Result: correct up to step 10,001", "Result: partially correct with 2 choices remaining" })
                CoreTests.Equal("incomplete", PResults.Classify("pex", 0, false, text, 1).Status);
            CoreTests.Equal("exhaustive-symbolic-model", PResults.Classify("psym", 0, false, "Result: correct for any depth\n", 1).Claim);
            CoreTests.Equal("rejected", PResults.Classify("psym", 1, false, "Result: found cex of symbolic length 12\n", 1).Status);
            CoreTests.Equal("incomplete", PResults.Classify("psym", 0, false, "Result: partially safe with 1820 backtracks remaining", 1).Status);
        });
        test("P bounds timeouts and tool errors never satisfy property rejection controls", () =>
        {
            foreach (string engine in new[] { "pchecker", "pex", "psym", "pverifier" })
            {
                CoreTests.Equal("incomplete", PResults.Classify(engine, null, true, "Found 1 bug.\nassert false", 1).Status);
                CoreTests.Equal("incomplete", PResults.Classify(engine, 2, false, "Compilation failed", 1).Status);
            }
            CoreTests.Equal("incomplete", PResults.Classify("pchecker", 1, false,
                "Found 1 bug.\nassert reached the max scheduling steps bound", 1).Status);
            CoreTests.Equal(ErrorCode.PAssertionFailed, PResults.Classify("pchecker", 1, false, "Found 1 bug.\nAssertion failed: credit-return", 1).Diagnostic!.Value);
            CoreTests.Equal(ErrorCode.PLivenessFailed, PResults.Classify("pchecker", 1, false, "Found 1 bug.\nLiveness violation", 1).Diagnostic!.Value);
        });
        test("PVerifier requires all goals and distinguishes unknowns from counterexamples", () =>
        {
            const string passed = "Verified 20 invariants!\ndefault P proof obligations\nCompilation succeeded.";
            CoreTests.Equal("inductive-invariants", PResults.Classify("pverifier", 0, false, passed, 1).Claim);
            foreach (string suffix in new[] { "\nRemaining Goals:", "\nUNDEF -> goal, line 2",
                "\n\u2753 Failed to verify invariant I\nFailed to verify 1 property!" })
                CoreTests.Equal("incomplete", PResults.Classify("pverifier", 0, false, passed + suffix, 1).Status);
            CoreTests.Equal("rejected", PResults.Classify("pverifier", 0, false, "Remaining Goals:\nFailed to verify 1 property!", 1).Status);
            CoreTests.Equal("incomplete", PResults.Classify("pverifier", 0, false, "Verified 0 invariants!\nCompilation succeeded.", 1).Status);
        });
        test("PObserve requires nonempty completed monitoring and rejects parser or ordering failures", () =>
        {
            string Metrics(string counts, string errors) =>
                "Total Events Read   Total Verified Events   Total Verified Keys   Total Partition Keys\n" +
                "----------------------\n" + counts + "\nTotal Parser Errors   Total Spec Errors   Total Out of Order Errors   Total Unknown Errors\n" +
                "----------------------\n" + errors;
            const string log = "PObserve run completed with 0 error(s)";
            CoreTests.Equal("observed-properties", PResults.Observe(0, false, log, Metrics("3 3 1 1", "0 0 0 0"), []).Claim);
            CoreTests.Equal("rejected", PResults.Observe(1, false, log, Metrics("3 2 1 1", "0 1 0 0"), []).Status);
            foreach (string counts in new[] { "0 0 0 0", "3 0 0 1", "3 3 1 2" })
                CoreTests.Equal("incomplete", PResults.Observe(0, false, log, Metrics(counts, "0 0 0 0"), []).Status);
            CoreTests.Equal("incomplete", PResults.Observe(0, false, log, Metrics("3 3 1 1", "0 0 0 0"), ["ParserError_1.txt"]).Status);
            CoreTests.Equal("incomplete", PResults.Observe(0, false, log, Metrics("3 3 1 1", "0 0 1 0"), []).Status);
        });
        test("P native process deadlines terminate and retain incomplete evidence", () =>
        {
            string output = Path.Combine(work, "p-deadline");
            PProcessResult result = PProcess.Run(PProcess.Executable("dotnet"),
                [typeof(Program).Assembly.Location, "--timeout-probe"], output, "deadline", 1, []);
            CoreTests.True(result.TimedOut && result.ExitCode == null);
            CoreTests.True(File.Exists(result.Stdout) && File.Exists(result.Stderr));
        });

        string compiler = TestTools.PCompiler(root);
        string fixture = Path.Combine(work, "p-fixture");
        Directory.CreateDirectory(fixture);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "examples", "P")))
            File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)));
        string configPath = Path.Combine(fixture, "credit.json");
        PAnalysis original = ProjectInputs.Read<PAnalysis>(configPath);
        void Save<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Files.Json));
        string? healthy = null;
        string Run(PAnalysis config, int expected, string command = "check-p")
        {
            Save(configPath, config);
            string output = Path.Combine(work, "p-" + Guid.NewGuid().ToString("N"));
            var response = TestCli.Run([command, "--config", configPath, "--p", compiler, "--output", output, "--timeout", "60"]);
            if (response.Code != expected) throw new InvalidOperationException(response.Json);
            return output;
        }
        string Healthy() => healthy ??= Run(original, 0);
        test("P inline bindings retain distinct native event and function endpoint kinds", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "p-panel-bindings")).FullName;
            File.WriteAllText(Path.Combine(directory, "Panel.cs"), """
                namespace PanelCases;
                public static class Panel {
                  /* @link {"bind":[{"id":"panel::command"}]} */
                  public static void Send(bool requested) {}
                  /* @link {"bind":[{"id":"panel::decision"}]} */
                  public static bool Apply(bool requested) => requested;
                }
                """);
            File.WriteAllText(Path.Combine(directory, "Panel.p"), """
                /* @link {"bind":[{"id":"panel::command"}]} */
                event eSet: bool;
                machine Panel {
                  start state Ready {
                    entry { assert Apply(true), "requested-on"; }
                    on eSet do (requested: bool) { assert Apply(requested) == requested, "set-result"; }
                  }
                  /* @link {"bind":[{"id":"panel::decision"}]} */
                  fun Apply(requested: bool): bool { return requested; }
                }
                test tcPanel [main = Panel]: { Panel };
                """);
            Files.WriteNew(Path.Combine(directory, "scope.json"),
                new CorrespondenceConfiguration(Schema.Version, "correspondence-scope", Markup.Version,
                    [new("panel", "p", ["Panel.p"])], []));
            Files.WriteNew(Path.Combine(directory, "analysis.json"),
                new PAnalysis(Schema.Version, "p-analysis", "Panel", "inspect", ["Panel.p"], ["Panel.cs"],
                    [new("panel::command", "", "", "Authored command correspondence.", ["No C# execution claim."]),
                     new("panel::decision", "", "", "Authored decision correspondence.", ["No refinement claim."])],
                    [], Scope: "scope.json"));
            string output = Path.Combine(directory, "result");
            var response = TestCli.Run(["inspect-p", "--config", Path.Combine(directory, "analysis.json"),
                "--p", compiler, "--output", output, "--timeout", "60"]);
            if (response.Code != 0) throw new InvalidOperationException(response.Json);
            PPacket packet = ProjectInputs.Read<PPacket>(Path.Combine(output, "result.json"));
            CoreTests.Equal("event", packet.Links.Single(l => l.Id == "panel::command").Specification.Kind);
            CoreTests.Equal("function", packet.Links.Single(l => l.Id == "panel::decision").Specification.Kind);
            CoreTests.Equal(0, TestCli.Run(["read-p", "--result", Path.Combine(output, "result.json")]).Code);
        });
        test("P configuration rejects unknown fields duplicate IDs and missing external artifacts", () =>
        {
            try
            {
                JsonObject unknown = JsonSerializer.SerializeToNode(original, Files.Json)!.AsObject();
                unknown["guess"] = true;
                Save(configPath, unknown);
                CoreTests.ThrowsAny<JsonException>(() => PInputs.Load(configPath, []));
                Save(configPath, original with { Bindings = [original.Bindings[0], original.Bindings[0]] });
                CoreTests.ThrowsAny<InputException>(() => PInputs.Load(configPath, []));
                foreach (string field in new[] { "bindings", "dependencies" })
                {
                    JsonObject nullElement = JsonSerializer.SerializeToNode(original, Files.Json)!.AsObject();
                    nullElement[field] = new JsonArray((JsonNode?)null);
                    Save(configPath, nullElement);
                    CoreTests.ThrowsAny<InputException>(() => PInputs.Load(configPath, []));
                }
                foreach (string engine in new[] { "pex", "psym", "pobserve" })
                {
                    Save(configPath, original with { Engine = engine });
                    CoreTests.ThrowsAny<InputException>(() => PInputs.Load(configPath, []));
                }
            }
            finally { Save(configPath, original); }
        });
        test("P native source inventory resolves C# bindings and executes requested schedules", () =>
        {
            string output = Healthy();
            PPacket packet = ProjectInputs.Read<PPacket>(Path.Combine(output, "result.json"));
            CoreTests.Equal("passed", packet.Status);
            CoreTests.Equal("explored-schedules", packet.Claim);
            CoreTests.Equal("Credit.ReturnCredit", packet.Links.Single().Specification.Name);
            CoreTests.True(packet.Inventory.Any(d => d.Kind == "testcase" && d.Name == "tcCredit"));
            CoreTests.Equal(0, TestCli.Run(["read-p", "--result", Path.Combine(output, "result.json"), "--current", "true"]).Code);
        });
        test("P native extra-credit mutation is a property rejection not a tool failure", () =>
        {
            string scope = Path.Combine(fixture, "extra-scope.json");
            Files.WriteNew(scope, new CorrespondenceConfiguration(Schema.Version, "correspondence-scope", Markup.Version,
                [new("credit-p", "p", ["ExtraCredit.p"])], []));
            string output = Run(original with { Models = ["ExtraCredit.p"], Scope = "extra-scope.json" }, 1);
            PPacket packet = ProjectInputs.Read<PPacket>(Path.Combine(output, "result.json"));
            CoreTests.Equal("rejected", packet.Status);
            CoreTests.True(packet.Diagnostics.Any(d => d.Is(ErrorCode.PAssertionFailed)));
            CoreTests.Equal(0, TestCli.Run(["read-p", "--result", Path.Combine(output, "result.json")]).Code);
            Save(configPath, original);
        });
        test("P native source inspection does not imply exploration", () =>
        {
            string output = Run(original, 0, "inspect-p");
            PPacket packet = ProjectInputs.Read<PPacket>(Path.Combine(output, "result.json"));
            CoreTests.Equal("ready", packet.Status);
            CoreTests.Equal("structural-links", packet.Claim);
            CoreTests.True(!packet.Processes.Any(p => p.Label == "p-check"));
        });
        test("P unresolved source endpoint and invalid model remain incomplete", () =>
        {
            string source = Path.Combine(fixture, "Credit.cs");
            string originalSource = Files.Read(source);
            try
            {
                File.WriteAllText(source, originalSource.Replace("ReturnCredit", "MissingReturnCredit", StringComparison.Ordinal));
                Run(original, 2);
            }
            finally { File.WriteAllText(source, originalSource); }
            string malformed = Path.Combine(fixture, "Malformed.p");
            File.WriteAllText(malformed, "machine {");
            Run(original with { Models = ["Malformed.p"] }, 2);
            Save(configPath, original);
        });
        test("P native testcase prefix cannot silently select additional cases", () =>
        {
            Run(original with { TestCase = "tc" }, 2);
            Save(configPath, original);
        });
        test("P missing verifier dependencies are explicit and cannot count as rejected invariants", () =>
        {
            string output = Run(original with { Engine = "pverifier" }, 2);
            PPacket packet = ProjectInputs.Read<PPacket>(Path.Combine(output, "result.json"));
            CoreTests.Equal("incomplete", packet.Status);
            CoreTests.True(packet.Diagnostics.Any(d => d.Is(ErrorCode.PPrerequisiteMissing)));
            Save(configPath, original);
        });
        test("P evidence detects changed logs forged claims and stale sources independently", () =>
        {
            string output = Healthy();
            string resultPath = Path.Combine(output, "result.json");
            string originalResult = Files.Read(resultPath);
            PPacket packet = ProjectInputs.Read<PPacket>(resultPath);
            try
            {
                packet.Claim = "exhaustive-finite-model";
                Save(resultPath, packet);
                CoreTests.Equal(1, TestCli.Run(["read-p", "--result", resultPath]).Code);
            }
            finally { File.WriteAllText(resultPath, originalResult); }
            string log = Path.Combine(output, "p-check.stdout.log");
            string before = Files.Read(log);
            try
            {
                File.AppendAllText(log, "\nchanged");
                CoreTests.Equal(1, TestCli.Run(["read-p", "--result", resultPath]).Code);
            }
            finally { File.WriteAllText(log, before); }
            string source = Path.Combine(fixture, "Credit.cs");
            before = Files.Read(source);
            try
            {
                File.AppendAllText(source, "\n");
                CoreTests.Equal(0, TestCli.Run(["read-p", "--result", resultPath]).Code);
                CoreTests.Equal(2, TestCli.Run(["read-p", "--result", resultPath, "--current", "true"]).Code);
            }
            finally { File.WriteAllText(source, before); Save(configPath, original); }
        });
        test("P workflow pins configuration and accepts only its declared claim and bindings", () =>
        {
            Save(configPath, original);
            string projectPath = Path.Combine(fixture, "project.json");
            string policyPath = Path.Combine(fixture, "requirements.json");
            AnalysisProject project = new(Schema.Version, "analysis-project", "p-credit",
                new(null, null, new(compiler, Files.Hash(compiler))),
                new("net10.0", "14", "enable", false, true, null, []), "requirements.json",
                [new("credit", [new("model", "p-pchecker", [], 60, Config: "credit.json")])], [], "reviews");
            AcceptancePolicy policy = new(Schema.Version, "acceptance-policy", "p-credit",
                [new("sample", "credit", "model", "passed", Bindings: ["credit-p::credit-return"], ConfigSha256: Files.Hash(configPath), Claim: "explored-schedules")]);
            Save(projectPath, project); Save(policyPath, policy);
            CoreTests.Equal(0, TestCli.Run(["doctor", "--project", projectPath]).Code);
            string output = Path.Combine(work, "p-workflow");
            var response = TestCli.Run(["run", "--project", projectPath, "--scenario", "credit", "--output", output]);
            if (response.Code != 0) throw new InvalidOperationException(response.Json);
            var accepted = TestCli.Run(["accept", "--project", projectPath, "--run", output]);
            if (accepted.Code != 0) throw new InvalidOperationException(accepted.Json);
            try
            {
                Save(policyPath, policy with { Requirements = [policy.Requirements[0] with { Claim = "exhaustive-finite-model" }] });
                CoreTests.True(TestCli.Run(["accept", "--project", projectPath, "--run", output]).Code != 0);
                Save(configPath, original with { Schedules = 1 });
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", projectPath]).Code);
            }
            finally { Save(configPath, original); Save(policyPath, policy); }
        });
    }
}
