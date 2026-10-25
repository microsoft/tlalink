using System.Text.Json;
using System.Text.Json.Nodes;
using TlaLink;

namespace TlaLink.Tests;

public static class ApalacheTests
{
    public static void RegisterExample(Action<string, Action> test, string root)
    {
        test("maintained Apalache example preserves approved configuration bytes", () =>
        {
            string path = Path.Combine(root, "examples", "Apalache", "project.json");
            var (project, policy) = ProjectInputs.Load(path);
            foreach (AnalysisRequirement requirement in policy.Requirements)
            {
                AnalysisStage stage = project.Scenarios.Single(s => s.Id == requirement.Scenario)
                    .Stages.Single(s => s.Id == requirement.Stage);
                string config = ProjectInputs.Resolve(path, stage.Config!);
                if (!string.Equals(requirement.ConfigSha256, Files.Hash(config), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Maintained example configuration differs from approved bytes: " + config);
            }
        });
    }

    private static ApalacheAnalysis Analysis(string model, string mode = "bounded") =>
        new(Schema.Version, "apalache-analysis", "credit", mode, model, "Init", "Next",
            mode == "typecheck" ? [] : ["Safety"], [], mode == "bounded" ? 5 : null,
            mode == "inductive" ? "TypeOK" : null, mode == "typecheck" ? null : "ConstInit");

    public static void RegisterCore(Action<string, Action> test, string work)
    {
        test("Apalache modes retain distinct scopes and complete induction obligations", () =>
        {
            foreach (string mode in new[] { "typecheck", "bounded", "inductive" }) ApalacheInputs.Validate(Analysis("Credit.tla", mode));
            CoreTests.True(ApalacheInputs.Claim("bounded") != ApalacheInputs.Claim("inductive"));
            List<ApalacheQuery> queries = ApalacheInputs.Queries(Analysis("Credit.tla", "inductive"));
            CoreTests.Equal(3, queries.Count);
            CoreTests.Equal("Init", queries[0].Init);
            CoreTests.Equal(0, queries[0].Length);
            CoreTests.Equal("TypeOK", queries[1].Init);
            CoreTests.Equal(1, queries[1].Length);
            CoreTests.Equal("TypeOK", queries[1].Properties.Single());
            CoreTests.Equal("TypeOK", queries[2].Init);
            CoreTests.Equal("Safety", queries[2].Properties.Single());
            CoreTests.Equal(0, queries[2].Length);
        });
        test("Apalache rejects implicit bounds incompatible modes and expression options", () =>
        {
            foreach (ApalacheAnalysis bad in new[] {
                Analysis("Credit.tla") with { Length = null }, Analysis("Credit.tla") with { Properties = [] },
                Analysis("Credit.tla") with { Next = "Next /\\ TRUE" }, Analysis("Credit.tla") with { Properties = ["Safety", "Safety"] },
                Analysis("Credit.tla", "inductive") with { Invariant = null },
                Analysis("Credit.tla", "inductive") with { Length = 1 },
                Analysis("Credit.tla", "typecheck") with { Properties = ["Safety"] },
                Analysis("Credit.tla") with { SchemaVersion = 3 }, Analysis("Credit.tla") with { Mode = "simulate" }
            }) CoreTests.ThrowsAny<InputException>(() => ApalacheInputs.Validate(bad));
        });
        test("Apalache unknown and duplicate manifest fields cannot introduce hidden TLC options", () =>
        {
            string model = Path.Combine(work, "apalache-core.tla");
            File.WriteAllText(model, "---- MODULE apalache_core ----\n====");
            string path = Path.Combine(work, "apalache-core.json");
            JsonObject config = JsonSerializer.SerializeToNode(Analysis(model), Files.Json)!.AsObject();
            config["tlc_config"] = "ignored.cfg";
            Files.WriteNew(path, config);
            CoreTests.ThrowsAny<JsonException>(() => ApalacheInputs.Load(path, new(FilePaths.Comparer)));
            File.WriteAllText(path, JsonSerializer.Serialize(Analysis(model), Files.Json).Replace(
                "\"length\": 5", "\"length\": 5, \"length\": 1", StringComparison.Ordinal));
            CoreTests.ThrowsAny<JsonException>(() => ApalacheInputs.Load(path, new(FilePaths.Comparer)));
        });
        test("Apalache exit zero timeout unknown output and deadlocks never establish safety", () =>
        {
            ApalacheQuery query = new("bounded", "check", "Init", ["Safety"], 5);
            string success = "The outcome is: NoError\nChecker reports no error up to computation length 5";
            string Assess(int? exit, bool timedOut, string text, bool witness = false, bool example = true) =>
                ApalacheBackend.Assess(new("bounded", [], exit, timedOut), query, text, witness, example);
            CoreTests.Equal("passed", Assess(0, false, success));
            CoreTests.Equal("incomplete", Assess(0, false, success, example: false));
            CoreTests.Equal("incomplete", Assess(0, false, "Checker reports no error up to computation length 5"));
            CoreTests.Equal("incomplete", Assess(0, false, success + "\nThe outcome is: SmtTimeout"));
            CoreTests.Equal("incomplete", Assess(0, false, success.Replace("length 5", "length 50", StringComparison.Ordinal)));
            CoreTests.Equal("incomplete", Assess(0, false, success.Replace("NoError", "SmtTimeout", StringComparison.Ordinal)));
            CoreTests.Equal("incomplete", Assess(0, true, success));
            CoreTests.Equal("incomplete", Assess(75, false, success));
            CoreTests.Equal("incomplete", Assess(12, false, "The outcome is: Deadlock\nChecker has found an error", true));
            string rejection = "The outcome is: Error\nChecker has found an error";
            CoreTests.Equal("incomplete", Assess(12, false, rejection));
            CoreTests.Equal("rejected", Assess(12, false, rejection, true));
        });
        test("Apalache malformed evidence and path escapes fail closed", () =>
        {
            using JsonDocument document = JsonDocument.Parse("""{"schema_version":4,"kind":"apalache-result"}""");
            CoreTests.True(ApalacheEvidence.Validate(document.RootElement, work).Any(i => i.Severity == "error"));
            CoreTests.True(!ApalacheInputs.IsStage("tlc"));
            CoreTests.True(!ApalacheInputs.IsStage("apalache-simulate"));
        });
        test("Apalache failure summaries retain native phase location explanation and execution status", () =>
        {
            string text = """
                PASS #1: TypeCheckerSnowcat I@13:57:58.064
                PASS #10: TransitionFinderPass I@13:57:59.130
                Assignment error: Ingress.tla:207:10-207:13: wire' is used before it is assigned. E@13:57:59.153
                """;
            string summary = ApalacheBackend.FailureSummary(new("preservation", [], 255, false), "incomplete", text);
            foreach (string expected in new[] { "preservation", "incomplete", "exit 255", "TransitionFinderPass",
                "Ingress.tla:207:10-207:13", "used before it is assigned", "preservation.stdout.log" })
                CoreTests.True(summary.Contains(expected, StringComparison.Ordinal));
            CoreTests.True(!summary.Contains("E@13:", StringComparison.Ordinal));
            CoreTests.True(ApalacheBackend.FailureSummary(new("bounded", [], null, true), "incomplete", "")
                .Contains("deadline exceeded", StringComparison.Ordinal));
            CoreTests.True(ApalacheBackend.FailureSummary(new("bounded", [], 0, false), "incomplete", "unexpected output")
                .Contains("No recognized native error", StringComparison.Ordinal));
        });
    }

    public static void RegisterNative(Action<string, Action> test, string root, string work)
    {
        string? jar = null, sany = null;
        test("Apalache native prerequisites are explicit", () => { jar = TestTools.Apalache(root); sany = TestTools.Jar(root); });
        if (jar == null || sany == null) return;
        string directory = Path.Combine(work, "apalache-native");
        Directory.CreateDirectory(directory);
        string model = Path.Combine(directory, "Credit.tla");
        File.Copy(Path.Combine(root, "examples", "Apalache", "Credit.tla"), model);
        string? boundedRun = null;
        string? inductiveRun = null;
        string Run(ApalacheAnalysis config, int expected)
        {
            string text = JsonSerializer.Serialize(config, Files.Json);
            string configPath = Path.Combine(directory, Files.HashText(text) + ".json");
            if (!File.Exists(configPath)) File.WriteAllText(configPath, text);
            string output = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            var result = TestCli.Run(["check-apalache", "--config", configPath, "--apalache", jar, "--jar", sany,
                "--output", output, "--timeout", "60"]);
            if (result.Code != expected) throw new InvalidOperationException(result.Json + "\n" +
                string.Join("\n", Directory.GetFiles(output, "*.log").Select(File.ReadAllText)));
            using JsonDocument packet = JsonDocument.Parse(Files.Read(Path.Combine(output, "result.json")));
            List<Issue> issues = ApalacheEvidence.Validate(packet.RootElement, output, current: true);
            if (issues.Count != 0) throw new InvalidOperationException(string.Join("; ", issues.Select(i => i.Message)));
            return output;
        }
        test("Apalache native typechecking and complete induction pass", () =>
        {
            Run(Analysis(model, "typecheck"), 0);
            string output = inductiveRun = Run(Analysis(model, "inductive"), 0);
            ApalachePacket packet = ProjectInputs.Read<ApalachePacket>(Path.Combine(output, "result.json"));
            CoreTests.Equal("inductive-invariants", packet.Claim);
            CoreTests.Equal(4, packet.Processes.Count);
        });
        test("Apalache native bounded safety does not become induction or hide deeper bugs", () =>
        {
            Run(Analysis(model) with { Properties = ["TooSmall"], Length = 1 }, 0);
            Run(Analysis(model) with { Properties = ["TooSmall"], Length = 2 }, 1);
            boundedRun = Run(Analysis(model), 0);
        });
        test("Apalache rejects failed initiation preservation and safety implication independently", () =>
        {
            Run(Analysis(model, "inductive") with { Invariant = "PositiveInvariant" }, 1);
            Run(Analysis(model, "inductive") with { Invariant = "WeakInvariant" }, 1);
            Run(Analysis(model, "inductive") with { Properties = ["TooSmall"] }, 1);
        });
        test("Apalache type errors empty initialization and imported assumptions remain incomplete", () =>
        {
            string before = Files.Read(model);
            try
            {
                File.WriteAllText(model, before.Replace("Init == credit = 0", "Init == credit = TRUE", StringComparison.Ordinal));
                Run(Analysis(model, "typecheck"), 2);
                File.WriteAllText(model, before.Replace("Init == credit = 0", "Init == credit = 0 /\\ credit = 1", StringComparison.Ordinal));
                Run(Analysis(model), 2);
                File.WriteAllText(Path.Combine(directory, "Assumptions.tla"), "---- MODULE Assumptions ----\nASSUME TRUE\n====\n");
                File.WriteAllText(model, before.Replace("EXTENDS Integers", "EXTENDS Integers, Assumptions", StringComparison.Ordinal));
                Run(Analysis(model), 2);
            }
            finally { File.WriteAllText(model, before); }
        });
        test("Apalache non-generative induction domains retain actionable native assignment diagnostics", () =>
        {
            string unassigned = Path.Combine(directory, "Unassigned.tla");
            File.WriteAllText(unassigned, """
                ---- MODULE Unassigned ----
                EXTENDS Naturals, Sequences
                VARIABLE
                  \* @type: Seq(Int);
                  wire
                Init == wire = <<>>
                Next == UNCHANGED wire
                TypeOK == Len(wire) <= 1
                Safety == TypeOK
                ====
                """);
            string output = Run(Analysis(unassigned, "inductive") with { ConstInit = null }, 2);
            ApalachePacket packet = ProjectInputs.Read<ApalachePacket>(Path.Combine(output, "result.json"));
            CoreTests.Equal("incomplete", packet.Status);
            CoreTests.Equal("not-established", packet.Claim);
            Issue issue = packet.Diagnostics.Single();
            CoreTests.True(issue.Is(ErrorCode.ApalacheToolFailure));
            foreach (string expected in new[] { "preservation", "TransitionFinderPass", "Unassigned.tla:", "used before it is assigned" })
                if (!issue.Message.Contains(expected, StringComparison.Ordinal))
                    throw new InvalidOperationException("Missing " + expected + ": " + issue.Message);
        });
        test("Apalache independent induction reader requires every obligation and exact native settings", () =>
        {
            string output = inductiveRun ??= Run(Analysis(model, "inductive"), 0);
            string resultPath = Path.Combine(output, "result.json");
            ApalachePacket packet = ProjectInputs.Read<ApalachePacket>(resultPath);
            packet.Processes.RemoveAt(2);
            CoreTests.True(ApalacheEvidence.Validate(JsonSerializer.SerializeToElement(packet, Files.Json), output).Any(i => i.Severity == "error"));
            packet = ProjectInputs.Read<ApalachePacket>(resultPath);
            string nativePath = Path.Combine(output, "preservation.json");
            JsonObject native = JsonNode.Parse(Files.Read(nativePath))!.AsObject();
            native["checker"]!["length"] = 0;
            File.WriteAllText(nativePath, native.ToJsonString(Files.Json));
            packet.Artifacts["preservation.json"] = Files.Hash(nativePath);
            CoreTests.True(ApalacheEvidence.Validate(JsonSerializer.SerializeToElement(packet, Files.Json), output).Any(i => i.Severity == "error"));
        });
        test("Apalache reader distinguishes archived validity current drift and semantic tampering", () =>
        {
            string output = boundedRun ??= Run(Analysis(model), 0);
            string result = Path.Combine(output, "result.json");
            CoreTests.Equal(0, TestCli.Run(["read-apalache", "--result", result]).Code);
            string before = Files.Read(model);
            try
            {
                File.AppendAllText(model, "\n");
                CoreTests.Equal(0, TestCli.Run(["read-apalache", "--result", result]).Code);
                CoreTests.Equal(2, TestCli.Run(["read-apalache", "--result", result, "--current", "true"]).Code);
            }
            finally { File.WriteAllText(model, before); }
            ApalachePacket packet = ProjectInputs.Read<ApalachePacket>(result);
            packet.Claim = "inductive-invariants";
            File.WriteAllText(result, JsonSerializer.Serialize(packet, Files.Json));
            CoreTests.Equal(1, TestCli.Run(["read-apalache", "--result", result]).Code);
        });
        test("Apalache project workflow enforces exact claims configuration pins and negative controls", () =>
        {
            string bounded = Path.Combine(directory, "bounded.json"), induction = Path.Combine(directory, "induction.json");
            string negative = Path.Combine(directory, "negative.json");
            Files.WriteNew(bounded, Analysis(model));
            Files.WriteNew(induction, Analysis(model, "inductive"));
            Files.WriteNew(negative, Analysis(model) with { Properties = ["TooSmall"] });
            string projectPath = Path.Combine(directory, "project.json"), requirementsPath = Path.Combine(directory, "requirements.json");
            AnalysisProject project = new(Schema.Version, "analysis-project", "apalache-pilot",
                new(null, new(sany, Files.Hash(sany)), Apalache: new(jar, Files.Hash(jar))),
                new("net10.0", "14", "enable", false, true, null, []), requirementsPath,
                [new("credit", [
                    new("bounded", "apalache-check", [], 60, Config: bounded),
                    new("inductive", "apalache-inductive", [], 60, Config: induction),
                    new("control", "apalache-check", [], 60, Config: negative)])], [], "reviews",
                [new("safety", "safety", "credit", ["inductive"])]);
            AcceptancePolicy policy = new(Schema.Version, "acceptance-policy", project.Id, [
                new("bounded", "credit", "bounded", "passed", ConfigSha256: Files.Hash(bounded), Claim: "bounded-safety"),
                new("inductive", "credit", "inductive", "passed", ConfigSha256: Files.Hash(induction), Claim: "inductive-invariants"),
                new("control", "credit", "control", "rejected", ErrorCode.ApalachePropertyRejected.Id(),
                    ConfigSha256: Files.Hash(negative), Claim: "not-established")]);
            Files.WriteNew(projectPath, project);
            Files.WriteNew(requirementsPath, policy);
            CoreTests.Equal(0, TestCli.Run(["doctor", "--project", projectPath]).Code);
            CoreTests.Equal(0, TestCli.Run(["plan", "--project", projectPath]).Code);
            foreach (CheckObligation unsupported in new[] {
                new CheckObligation("progress", "progress", "credit", ["bounded"]),
                new CheckObligation("deadlock", "deadlock", "credit", ["inductive"]),
                new CheckObligation("negative-safety", "safety", "credit", ["control"]) })
            {
                File.WriteAllText(projectPath, JsonSerializer.Serialize(project with { CheckObligations = [unsupported] }, Files.Json));
                var missing = TestCli.Run(["plan", "--project", projectPath]);
                CoreTests.Equal(2, missing.Code);
                CoreTests.True(missing.Json.Contains("\"missing\"", StringComparison.Ordinal));
            }
            File.WriteAllText(projectPath, JsonSerializer.Serialize(project, Files.Json));
            string output = Path.Combine(directory, "project-run");
            var run = TestCli.Run(["run", "--project", projectPath, "--output", output]);
            if (run.Code != 0) throw new InvalidOperationException(run.Json);
            var accept = TestCli.Run(["accept", "--project", projectPath, "--run", output]);
            if (accept.Code != 0) throw new InvalidOperationException(accept.Json);
            CoreTests.Equal(0, TestCli.Run(["explain", "--project", projectPath, "--run", output, "--format", "text"]).Code);
            File.WriteAllText(requirementsPath, JsonSerializer.Serialize(policy with {
                Requirements = [policy.Requirements[0] with { Claim = "inductive-invariants" }, .. policy.Requirements.Skip(1)]
            }, Files.Json));
            CoreTests.Equal(2, TestCli.Run(["doctor", "--project", projectPath]).Code);
            File.WriteAllText(requirementsPath, JsonSerializer.Serialize(policy, Files.Json));
            File.AppendAllText(bounded, "\n");
            CoreTests.Equal(2, TestCli.Run(["doctor", "--project", projectPath]).Code);
        });
        test("Apalache project preflight rejects unsupported model declarations before execution and pins imports", () =>
        {
            string dependency = Path.Combine(directory, "PreflightDependency.tla");
            string pilot = Path.Combine(directory, "Preflight.tla");
            string configPath = Path.Combine(directory, "preflight.json");
            string projectPath = Path.Combine(directory, "preflight-project.json");
            string requirementsPath = Path.Combine(directory, "preflight-requirements.json");
            string source = """
                ---- MODULE Preflight ----
                EXTENDS Integers, PreflightDependency
                VARIABLE x
                Init == x = 0
                Next == x' = x
                Safety == x = 0
                TemporalSafety == []Safety
                Parameterized(value) == x = value
                ====
                """;
            Files.WriteNew(projectPath, new AnalysisProject(Schema.Version, "analysis-project", "apalache-preflight",
                new(null, new(sany, Files.Hash(sany)), Apalache: new(jar, Files.Hash(jar))),
                new("net10.0", "14", "enable", false, true, null, []), requirementsPath,
                [new("scope", [new("safety", "apalache-check", [], 60, Config: configPath)])], [], "reviews"));
            File.WriteAllText(pilot, source);
            void Configure(ApalacheAnalysis analysis)
            {
                File.WriteAllText(configPath, JsonSerializer.Serialize(analysis, Files.Json));
                File.WriteAllText(requirementsPath, JsonSerializer.Serialize(
                    new AcceptancePolicy(Schema.Version, "acceptance-policy", "apalache-preflight",
                        [new("safety", "scope", "safety", "passed", ConfigSha256: Files.Hash(configPath), Claim: "bounded-safety")]),
                    Files.Json));
            }
            ApalacheAnalysis scope = Analysis(pilot) with { ConstInit = null };
            File.WriteAllText(dependency, "---- MODULE PreflightDependency ----\nASSUME TRUE\n====\n");
            Configure(scope);
            foreach (string command in new[] { "doctor", "plan", "run" })
            {
                string output = Path.Combine(directory, "blocked-" + command);
                var result = TestCli.Run(command == "run"
                    ? [command, "--project", projectPath, "--output", output]
                    : [command, "--project", projectPath]);
                CoreTests.Equal(2, result.Code);
                CoreTests.True(result.Json.Contains("PreflightDependency", StringComparison.Ordinal));
                CoreTests.True(!Directory.Exists(output));
            }
            File.WriteAllText(dependency, "---- MODULE PreflightDependency ----\n====\n");
            foreach (string property in new[] { "TemporalSafety", "Parameterized", "Missing" })
            {
                Configure(scope with { Properties = [property] });
                var result = TestCli.Run(["doctor", "--project", projectPath]);
                CoreTests.Equal(2, result.Code);
                CoreTests.True(result.Json.Contains(property, StringComparison.Ordinal));
            }
            Configure(scope);
            var ready = TestCli.Run(["doctor", "--project", projectPath]);
            CoreTests.Equal(0, ready.Code);
            using JsonDocument report = JsonDocument.Parse(ready.Json);
            CoreTests.Equal(Files.Hash(dependency), report.RootElement.GetProperty("fingerprints").GetProperty(dependency).GetString()!);
            File.WriteAllText(pilot, source.Replace("Init == x = 0", "Init == Missing", StringComparison.Ordinal));
            CoreTests.Equal(2, TestCli.Run(["doctor", "--project", projectPath]).Code);
        });
    }
}
