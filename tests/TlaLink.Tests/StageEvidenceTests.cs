using System.Text.Json;
using System.Text.Json.Nodes;

namespace TlaLink.Tests;

public static class StageEvidenceTests
{
    public static void RegisterCore(Action<string, Action> test, string root, string work)
    {
        test("code-adjacent examples declare the implementation endpoints they ask humans to review", () =>
        {
            foreach (var example in new[] {
                (Path: "Components", Source: Path.Combine("src", "Counter", "Counter.cs"),
                    Ids: new[] { "counter::value", "counter::step" }),
                (Path: "Evidence", Source: Path.Combine("src", "Increment", "Increment.cs"),
                    Ids: new[] { "increment-spec::increment" })
            })
            {
                CSharpScope source = CSharpScope.Parse([Path.Combine(root, "examples", example.Path, example.Source)]);
                CoreTests.True(source.Diagnostics.All(d => d.Severity != "error"));
                CoreTests.True(source.Bindings.Select(b => b.BindingId).ToHashSet(StringComparer.Ordinal).SetEquals(example.Ids));
            }
        });
        test("public project examples retain their approved configuration bytes after checkout", () =>
        {
            foreach (string relative in new[] { "Workflow", "Evidence", "Components" })
            {
                string path = Path.Combine(root, "examples", relative, "tlalink.project.json");
                AnalysisProject project = ProjectComposition.Read(path);
                AcceptancePolicy policy = ProjectInputs.Read<AcceptancePolicy>(ProjectInputs.Resolve(path, project.Requirements));
                foreach (AnalysisRequirement requirement in policy.Requirements.Where(r => r.ConfigSha256 != null))
                {
                    AnalysisStage stage = project.Scenarios.Single(s => s.Id == requirement.Scenario)
                        .Stages.Single(s => s.Id == requirement.Stage);
                    CoreTests.True(string.Equals(requirement.ConfigSha256,
                        Files.Hash(ProjectInputs.Resolve(path, stage.Config!)), StringComparison.OrdinalIgnoreCase));
                }
            }
        });
        test("TLC resource profiles preserve defaults and reject invalid budgets", () =>
        {
            CoreTests.Equal(new TlcResources(1, 512), TlcResources.Effective("tlc", null));
            CoreTests.Equal(new TlcResources(1, 1024), TlcResources.Effective("trace", null));
            foreach (TlcResources invalid in new[] { new TlcResources(0, 512), new(65, 512), new(1, 127), new(1, 65537) })
                CoreTests.ThrowsAny<InputException>(() => TlcResources.Effective("tlc", invalid));
            CoreTests.Equal(new TlcResources(2, 2048), TlcResources.FromCli(new(["validate-trace", "--annotation-version", "4", "--workers", "2", "--max-heap-mib", "2048"])));
            CoreTests.ThrowsAny<InputException>(() => TlcResources.FromCli(new(["validate-trace", "--annotation-version", "4", "--workers", "auto"])));
        });
        string fixture = Path.Combine(work, "regression-stage");
        Directory.CreateDirectory(fixture);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "examples", "Evidence", "src", "Increment")))
            File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)));
        string config = Path.Combine(fixture, "regression.json");
        test("regression path arguments resolve and pin explicit file indices without interpreting opaque arguments", () =>
        {
            CSharpRegressionAnalysis original = ProjectInputs.Read<CSharpRegressionAnalysis>(config);
            string path = Path.Combine(fixture, "path-arguments.json"), input = Path.Combine(fixture, "argument-input.json");
            File.WriteAllText(input, "{}");
            void Save(CSharpRegressionAnalysis value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Files.Json));
            CSharpRegressionAnalysis selected = original with { Arguments = ["opaque/value", "argument-input.json"], PathArguments = [1] };
            Save(selected);
            Dictionary<string, string> pins = new(FilePaths.Comparer);
            CSharpRegressionAnalysis loaded = CSharpRegression.Load(path, pins);
            CoreTests.Equal("opaque/value", loaded.Arguments[0]);
            CoreTests.Equal(input, loaded.Arguments[1]);
            CoreTests.Equal(Files.Hash(input), pins[input]);
            foreach (List<int> invalid in new List<int>[] { [-1], [2], [1, 1] })
            {
                Save(selected with { PathArguments = invalid });
                CoreTests.ThrowsAny<InputException>(() => CSharpRegression.Load(path, new(FilePaths.Comparer)));
            }
        });
        string? good = null;
        string Good()
        {
            if (good != null) return good;
            string setup = Path.Combine(fixture, "NuGet.Config");
            File.WriteAllText(setup, "<configuration><packageSources><clear /></packageSources></configuration>");
            ObservedExecution.RequireSuccess(ObservedExecution.Invoke(["restore", Path.Combine(fixture, "Regression.csproj"),
                "--configfile", setup, "-p:NuGetAudit=false"], fixture, 60, "restore"));
            string output = Path.Combine(work, "regression-good");
            var response = TestCli.Run(["check-regression", "--config", config, "--output", output, "--timeout", "60"]);
            if (response.Code != 0) throw new InvalidOperationException(response.Json +
                (File.Exists(Path.Combine(output, "build", "build.log")) ? Files.Read(Path.Combine(output, "build", "build.log")) : ""));
            good = output;
            return good;
        }
        test("console regressions retain exact compiler sources loaded binary and independent evidence", () =>
        {
            string output = Good();
            var read = TestCli.Run(["read-regression", "--result", Path.Combine(output, "result.json"), "--current", "true"]);
            if (read.Code != 0) throw new InvalidOperationException(read.Json);
            CSharpRegressionResult result = ProjectInputs.Read<CSharpRegressionResult>(Path.Combine(output, "result.json"));
            CoreTests.Equal(2, result.Tests.Count);
            CoreTests.True(result.Build.CompileInputs.ContainsKey(Path.Combine(fixture, "Program.cs")));
            CoreTests.True(result.Build.CompileInputs.ContainsKey(Path.Combine(fixture, "Increment.cs")));
            CoreTests.True(result.Build.BuildInputs.ContainsKey(Path.Combine(fixture, "Regression.csproj")));
            CoreTests.Equal("passed", result.Status);
        });
        test("source inventory reuses a verified observed compiler context without command-line reference expansion", () =>
        {
            string output = Good(), resultPath = Path.Combine(output, "result.json");
            CSharpRegressionResult result = ProjectInputs.Read<CSharpRegressionResult>(resultPath);
            string source = result.Build.CompileInputs.Keys.Single(p => p.EndsWith(".AssemblyInfo.cs", StringComparison.Ordinal));
            string inventoryPath = Path.Combine(work, "observed-inventory.json");
            var response = TestCli.Run(["inspect-source", "--annotation-version", "4", "--source", source,
                "--regression-result", resultPath, "--output", inventoryPath]);
            if (response.Code != 0) throw new InvalidOperationException(response.Json);
            SourceInventoryResult inventory = ProjectInputs.Read<SourceInventoryResult>(inventoryPath);
            CoreTests.Equal("explicit", inventory.Compiler.FrameworkReferences);
            CoreTests.Equal(resultPath, inventory.Compiler.RegressionResult);
            CoreTests.Equal(result.Build.CompilerContext["Nullable"], inventory.Compiler.Nullable);
            CoreTests.Equal(source, inventory.Sources.Single());
            CoreTests.Equal(Files.Hash(resultPath), inventory.Fingerprints[resultPath]);
            foreach (var pin in result.Build.References) CoreTests.Equal(pin.Value, inventory.Fingerprints[pin.Key]);
            CoreTests.Equal(2, TestCli.Run(["inspect-source", "--annotation-version", "4", "--source", source,
                "--regression-result", resultPath, "--define", "OTHER"]).Code);
            string unobserved = Path.Combine(work, "unobserved.cs");
            File.WriteAllText(unobserved, "class Unobserved {}");
            CoreTests.Equal(2, TestCli.Run(["inspect-source", "--annotation-version", "4", "--source", unobserved,
                "--regression-result", resultPath]).Code);
            string damaged = Path.Combine(work, "inventory-context-damaged");
            Copy(output, damaged);
            File.AppendAllText(Path.Combine(damaged, "build", "context.txt"), "changed");
            CoreTests.Equal(2, TestCli.Run(["inspect-source", "--annotation-version", "4", "--source", source,
                "--regression-result", Path.Combine(damaged, "result.json")]).Code);
        });
        void Mutate(string name, Action<JsonObject, JsonObject> change)
        {
            string original = Good(), copy = Path.Combine(work, "regression-" + name);
            Copy(original, copy);
            string receiptPath = Path.Combine(copy, "result.json");
            JsonObject receipt = JsonNode.Parse(Files.Read(receiptPath))!.AsObject();
            string observationsPath = Path.Combine(copy, receipt["observations"]!.GetValue<string>());
            JsonObject observations = JsonNode.Parse(Files.Read(observationsPath))!.AsObject();
            change(receipt, observations);
            File.WriteAllText(observationsPath, observations.ToJsonString(Files.Json));
            receipt["artifacts"]![receipt["observations"]!.GetValue<string>()] = Files.Hash(observationsPath);
            File.WriteAllText(receiptPath, receipt.ToJsonString(Files.Json));
            var read = TestCli.Run(["read-regression", "--result", receiptPath]);
            if (read.Code == 0) throw new InvalidOperationException(read.Json);
        }
        test("regression reader rejects missing duplicate skipped and zero-result observations", () =>
        {
            Mutate("missing", (_, o) => o["results"]!.AsArray().RemoveAt(0));
            Mutate("duplicate", (_, o) => o["results"]![1]!["id"] = o["results"]![0]!["id"]!.DeepClone());
            Mutate("skipped", (_, o) => o["results"]![0]!["status"] = "skipped");
            Mutate("empty", (_, o) => o["results"] = new JsonArray());
        });
        test("regression reader rejects wrong loaded assembly selection and contradictory exit code", () =>
        {
            Mutate("wrong-binary", (_, o) => o["binary_sha256"] = new string('0', 64));
            Mutate("selection", (_, o) => o["selection"] = new JsonArray("other"));
            Mutate("exit", (r, _) => r["process"]!["exit_code"] = 1);
        });
        test("regression reader distinguishes corrupted archives from stale current sources", () =>
        {
            string output = Good(), copy = Path.Combine(work, "regression-corrupt");
            Copy(output, copy);
            CSharpRegressionResult receipt = ProjectInputs.Read<CSharpRegressionResult>(Path.Combine(copy, "result.json"));
            File.AppendAllText(Path.Combine(copy, "inputs", receipt.Fingerprints[Path.Combine(fixture, "Program.cs")]), "corrupt");
            CoreTests.Equal(1, TestCli.Run(["read-regression", "--result", Path.Combine(copy, "result.json")]).Code);
            string source = Path.Combine(fixture, "Program.cs"), saved = Files.Read(source);
            try
            {
                File.AppendAllText(source, "\n// stale source control");
                CoreTests.Equal(0, TestCli.Run(["read-regression", "--result", Path.Combine(output, "result.json")]).Code);
                CoreTests.Equal(2, TestCli.Run(["read-regression", "--result", Path.Combine(output, "result.json"), "--current", "true"]).Code);
            }
            finally { File.WriteAllText(source, saved); }
        });
        test("absent optional compiler configuration is checked for later appearance", () =>
        {
            string output = Good(), probe = Path.Combine(fixture, ".globalconfig");
            CSharpRegressionResult result = ProjectInputs.Read<CSharpRegressionResult>(Path.Combine(output, "result.json"));
            CoreTests.True(result.Build.MissingBuildInputs.Contains(probe, FilePaths.Comparer));
            try
            {
                File.WriteAllText(probe, "is_global = true\n");
                CoreTests.Equal(2, TestCli.Run(["read-regression", "--result", Path.Combine(output, "result.json"), "--current", "true"]).Code);
                CoreTests.Equal(0, TestCli.Run(["read-regression", "--result", Path.Combine(output, "result.json")]).Code);
            }
            finally { File.Delete(probe); }
        });
        test("regression assertions reject with CS100 while missing compiler scope is incomplete", () =>
        {
            _ = Good();
            CSharpRegressionAnalysis original = ProjectInputs.Read<CSharpRegressionAnalysis>(config);
            try
            {
                Save(config, original with { Arguments = ["reject"] });
                string rejected = Path.Combine(work, "regression-rejected");
                var response = TestCli.Run(["check-regression", "--config", config, "--output", rejected, "--timeout", "60"]);
                if (response.Code != 1) throw new InvalidOperationException(response.Json);
                CoreTests.True(response.Json.Contains("CS100", StringComparison.Ordinal));
                CoreTests.Equal(0, TestCli.Run(["read-regression", "--result", Path.Combine(rejected, "result.json")]).Code);
                File.WriteAllText(Path.Combine(fixture, "Unexpected.cs"), "internal class Unexpected {}");
                var unexpected = TestCli.Run(["check-regression", "--config", config, "--output", Path.Combine(work, "regression-unexpected"), "--timeout", "60"]);
                CoreTests.Equal(2, unexpected.Code);
                CoreTests.True(unexpected.Json.Contains("Missing/unexpected compiler", StringComparison.Ordinal));
            }
            finally { Save(config, original); File.Delete(Path.Combine(fixture, "Unexpected.cs")); }
        });
        test("observed .NET deadlines are incomplete and retain a null exit code", () =>
        {
            string output = Directory.CreateDirectory(Path.Combine(work, "observed-deadline")).FullName;
            ObservedProcess result = ObservedExecution.Invoke([typeof(Program).Assembly.Location, "--timeout-probe"], output, 1, "deadline");
            CoreTests.True(result.TimedOut && result.ExitCode == null);
            CoreTests.ThrowsAny<InputException>(() => ObservedExecution.RequireSuccess(result));
        });
        test("source changes during build and execution cannot produce regression evidence", () =>
        {
            _ = Good();
            string source = Path.Combine(fixture, "Program.cs"), project = Path.Combine(fixture, "Regression.csproj");
            string savedSource = Files.Read(source), savedProject = Files.Read(project), savedConfig = Files.Read(config);
            try
            {
                File.WriteAllText(project, savedProject.Replace("</Project>",
                    """<Target Name="MutateCapturedSource" AfterTargets="CoreCompile"><WriteLinesToFile File="Program.cs" Lines="// source drift" Overwrite="false" /></Target></Project>""", StringComparison.Ordinal));
                var buildDrift = TestCli.Run(["check-regression", "--config", config,
                    "--output", Path.Combine(work, "regression-build-drift"), "--timeout", "60"]);
                CoreTests.Equal(2, buildDrift.Code);
                CoreTests.True(buildDrift.Json.Contains("Build input changed", StringComparison.Ordinal));
                File.WriteAllText(project, savedProject);
                File.WriteAllText(source, savedSource.Replace("string observations = args[0];",
                    "if (args.Skip(2).Contains(\"mutate\")) File.AppendAllText(args[3], \"\\n\");\nstring observations = args[0];", StringComparison.Ordinal));
                CSharpRegressionAnalysis analysis = ProjectInputs.Read<CSharpRegressionAnalysis>(config);
                Save(config, analysis with { Arguments = ["mutate", source] });
                var runDrift = TestCli.Run(["check-regression", "--config", config,
                    "--output", Path.Combine(work, "regression-run-drift"), "--timeout", "60"]);
                CoreTests.Equal(2, runDrift.Code);
                CoreTests.True(runDrift.Json.Contains("Build input changed", StringComparison.Ordinal));
            }
            finally
            {
                File.WriteAllText(source, savedSource);
                File.WriteAllText(project, savedProject);
                File.WriteAllText(config, savedConfig);
            }
        });
    }

    public static void RegisterNative(Action<string, Action> test, string root, string work)
    {
        string dafny = TestTools.Dafny(root), jar = TestTools.Jar(root);
        test("code-adjacent examples resolve their authored endpoints without fixture-generated markers", () =>
        {
            string counter = Path.Combine(root, "examples", "Components", "src", "Counter");
            var links = TestCli.Run(["inspect", "--annotation-version", "4", "--source", Path.Combine(counter, "Counter.cs"),
                "--scope", Path.Combine(counter, "verification", "scope.json"), "--target", "counter", "--jar", jar]);
            if (links.Code != 0) throw new InvalidOperationException(links.Json);
            string increment = Path.Combine(root, "examples", "Evidence", "src", "Increment", "verification", "bindings.json");
            var evidence = TestCli.Run(["inspect-integrated", "--annotation-version", "4", "--bindings", increment,
                "--dafny", dafny, "--output", Path.Combine(work, "increment-links")]);
            if (evidence.Code != 0) throw new InvalidOperationException(evidence.Json);
        });
        test("integrated unit-return model reaches an accepted return from an empty window", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "integrated-reachability")).FullName;
            string model = Path.Combine(directory, "Credits.tla"), config = Path.Combine(directory, "Credits.cfg");
            string original = Files.Read(Path.Combine(root, "examples", "Integrated", "Credits.tla"));
            int end = original.LastIndexOf("\n====", StringComparison.Ordinal);
            File.WriteAllText(model, original[..end] + "\nNeverReturned == available # 1\n====\n");
            File.WriteAllText(config, "CONSTANT Window = 2\nINIT Init\nNEXT Next\nINVARIANT NeverReturned\nCHECK_DEADLOCK FALSE\n");
            string project = Path.Combine(directory, "project.json");
            Save(project, new AnalysisProject(Schema.Version, "analysis-project", "unit-return-reachability",
                new(Jar: new(jar, Files.Hash(jar))), new("net10.0", "14", "enable", false, true, []),
                "requirements.json", [new("reachability", [new("return", "tlc", [], 60, Models: [model], Config: config)])],
                [], "reviews"));
            Save(Path.Combine(directory, "requirements.json"), new AcceptancePolicy(Schema.Version, "acceptance-policy",
                "unit-return-reachability", [new("reachable", "reachability", "return", "rejected",
                    Diagnostic: "TLC2110", DiagnosticText: "Invariant NeverReturned is violated.", ConfigSha256: Files.Hash(config))]));
            string output = Path.Combine(directory, "run");
            var run = TestCli.Run(["run", "--project", project, "--scenario", "reachability", "--output", output]);
            if (run.Code != 0) throw new InvalidOperationException(run.Json);
            var accepted = TestCli.Run(["accept", "--project", project, "--run", output]);
            if (accepted.Code != 0) throw new InvalidOperationException(accepted.Json);
        });
        test("project acceptance distinguishes a completed trace rejection from an incomplete control", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "trace-rejection-control")).FullName;
            string lanes = Path.Combine(root, "examples", "Workflow", "src", "Lanes");
            string config = Path.Combine(lanes, "verification", "Lanes.cfg");
            string path = Path.Combine(directory, "project.json");
            AnalysisStage stage = new("control", "trace", [], 60, Sources: [Path.Combine(lanes, "Lanes.cs")],
                Models: [Path.Combine(lanes, "verification", "Lanes.tla")], Config: config, Arguments: ["corrupt"],
                Scope: Path.Combine(lanes, "verification", "scope.json"), Target: "WorkflowLanes");
            Save(path, new AnalysisProject(Schema.Version, "analysis-project", "trace-control",
                new(Jar: new(jar, Files.Hash(jar))), new("net10.0", "14", "enable", false, true, []),
                "requirements.json", [new("control", [stage])], [], "reviews"));
            string requirements = Path.Combine(directory, "requirements.json");
            AcceptancePolicy policy = new(Schema.Version, "acceptance-policy", "trace-control",
                [new("trace-rejection", "control", "control", "rejected", ErrorCode.TraceRejected.Id(),
                    ConfigSha256: Files.Hash(config))]);
            Save(requirements, policy);
            string output = Path.Combine(directory, "run");
            var run = TestCli.Run(["run", "--project", path, "--scenario", "control", "--output", output]);
            if (run.Code != 0) throw new InvalidOperationException(run.Json);
            var accepted = TestCli.Run(["accept", "--project", path, "--run", output]);
            if (accepted.Code != 0) throw new InvalidOperationException(accepted.Json);
            Save(requirements, policy with { Requirements = [policy.Requirements[0] with { Diagnostic = "TL510" }] });
            string wrong = Path.Combine(directory, "wrong-diagnostic-run");
            CoreTests.Equal(0, TestCli.Run(["run", "--project", path, "--scenario", "control", "--output", wrong]).Code);
            var rejected = TestCli.Run(["accept", "--project", path, "--run", wrong]);
            CoreTests.Equal(1, rejected.Code);
            CoreTests.True(rejected.Json.Contains("diagnostic TL510", StringComparison.Ordinal));
        });
        test("native proof-only Dafny verifies without C# bindings and independently rereads its audit", () =>
        {
            string output = Path.Combine(work, "native-dafny-good");
            var response = TestCli.Run(["check-dafny", "--config", Path.Combine(root, "examples", "Evidence", "src", "Increment", "verification", "dafny.json"),
                "--dafny", dafny, "--output", output, "--timeout", "60"]);
            if (response.Code != 0) throw new InvalidOperationException(response.Json);
            var read = TestCli.Run(["read-dafny", "--result", Path.Combine(output, "result.json"), "--current", "true"]);
            if (read.Code != 0) throw new InvalidOperationException(read.Json);
            string copy = Path.Combine(work, "native-dafny-forged-audit");
            Copy(output, copy);
            JsonObject result = JsonNode.Parse(Files.Read(Path.Combine(copy, "result.json")))!.AsObject();
            File.WriteAllText(Path.Combine(copy, "dafny-audit.txt"), "Unreviewed assumption");
            result["artifacts"]!["dafny-audit.txt"] = Files.Hash(Path.Combine(copy, "dafny-audit.txt"));
            Save(Path.Combine(copy, "result.json"), result);
            CoreTests.True(TestCli.Run(["read-dafny", "--result", Path.Combine(copy, "result.json")]).Code != 0);
        });
        test("native Dafny assertion controls reject but parser errors and assumptions cannot pass", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "native-dafny-controls")).FullName;
            string proof = Path.Combine(directory, "Control.dfy"), config = Path.Combine(directory, "analysis.json");
            Save(config, new DafnyAnalysis(Schema.Version, "dafny-analysis", ["Control.dfy"]));
            File.WriteAllText(proof, "lemma FalseAssertion() { assert false; }");
            string output = Path.Combine(work, "native-dafny-rejected");
            var rejected = TestCli.Run(["check-dafny", "--config", config, "--dafny", dafny, "--output", output, "--timeout", "60"]);
            if (rejected.Code != 1) throw new InvalidOperationException(rejected.Json);
            CoreTests.Equal(0, TestCli.Run(["read-dafny", "--result", Path.Combine(output, "result.json")]).Code);
            File.WriteAllText(proof, "lemma Invalid( {");
            CoreTests.Equal(2, TestCli.Run(["check-dafny", "--config", config, "--dafny", dafny,
                "--output", Path.Combine(work, "native-dafny-parser"), "--timeout", "60"]).Code);
            File.WriteAllText(proof, "lemma Assumed() ensures false { assume {:axiom} false; }");
            var audit = TestCli.Run(["check-dafny", "--config", config, "--dafny", dafny,
                "--output", Path.Combine(work, "native-dafny-assumption"), "--timeout", "60"]);
            CoreTests.Equal(2, audit.Code);
            if (!audit.Json.Contains("DF101", StringComparison.Ordinal)) throw new InvalidOperationException(audit.Json);
        });
        test("Dafny rejection acceptance distinguishes exact compound diagnostic counts", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "compound-controls")).FullName;
            string proof = Path.Combine(directory, "Controls.dfy"), config = Path.Combine(directory, "dafny.json");
            File.WriteAllText(proof, "lemma First() { assert false; }\nlemma Second() { assert false; }\n");
            Save(config, new DafnyAnalysis(Schema.Version, "dafny-analysis", [proof]));
            string projectPath = Path.Combine(directory, "project.json"), policyPath = Path.Combine(directory, "requirements.json");
            AnalysisStage stage = new("controls", "dafny-verify", [], 60, Config: config);
            AnalysisProject project = new(Schema.Version, "analysis-project", "compound-controls", new(Dafny: new(dafny, Files.Hash(dafny))),
                new("net10.0", "14", "enable", false, true, []), policyPath, [new("controls", [stage])], [], "reviews");
            Save(projectPath, project);
            Save(policyPath, new AcceptancePolicy(Schema.Version, "acceptance-policy", project.Id,
                Enumerable.Range(1, 3).Select(count => new AnalysisRequirement("count-" + count, "controls", "controls", "rejected",
                    Diagnostic: "DF100", ConfigSha256: Files.Hash(config), DiagnosticText: "assertion might not hold",
                    Claim: "not-established", DiagnosticCount: count)).ToList()));
            string output = Path.Combine(directory, "run");
            var run = TestCli.Run(["run", "--project", projectPath, "--scenario", "controls", "--output", output]);
            if (run.Code != 0) throw new InvalidOperationException(run.Json);
            var acceptance = TestCli.Run(["accept", "--project", projectPath, "--run", output]);
            CoreTests.Equal(1, acceptance.Code);
            using JsonDocument report = JsonDocument.Parse(acceptance.Json);
            JsonElement[] requirements = report.RootElement.GetProperty("requirements").EnumerateArray().ToArray();
            foreach (JsonElement requirement in requirements)
                CoreTests.Equal(requirement.GetProperty("id").GetString() == "count-2" ? "satisfied" : "unsatisfied",
                    requirement.GetProperty("status").GetString());
        });
        test("project TLC resources preserve approved properties and acceptance detects tampered invocations", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "resource-project")).FullName;
            string projectPath = Path.Combine(directory, "project.json"), policyPath = Path.Combine(directory, "requirements.json");
            string lanes = Path.Combine(root, "examples", "Workflow", "src", "Lanes", "verification");
            string model = Path.Combine(lanes, "Lanes.tla"), config = Path.Combine(lanes, "Lanes.cfg");
            CompilerProfile compiler = new("net10.0", "14", "enable", false, true, []);
            AnalysisStage stage = new("model", "tlc", [], 60, Models: [model], Config: config, Resources: new(2, 512));
            AnalysisProject project = new(Schema.Version, "analysis-project", "resources", new(Jar: new(jar, Files.Hash(jar))),
                compiler, policyPath, [new("small", [stage])], [], "reviews");
            Save(projectPath, project);
            Save(policyPath, new AcceptancePolicy(Schema.Version, "acceptance-policy", project.Id,
                [new("model", "small", "model", "passed", ConfigSha256: Files.Hash(config))]));
            var plan = TestCli.Run(["plan", "--project", projectPath, "--scenario", "small", "--format", "summary"]);
            CoreTests.Equal(0, plan.Code);
            CoreTests.True(plan.Json.Contains("\"workers\": 2", StringComparison.Ordinal));
            string output = Path.Combine(work, "resource-run");
            var run = TestCli.Run(["run", "--project", projectPath, "--scenario", "small", "--output", output]);
            if (run.Code != 0) throw new InvalidOperationException(run.Json);
            CoreTests.Equal(0, TestCli.Run(["accept", "--project", projectPath, "--run", output]).Code);
            string resultPath = Path.Combine(output, "model", "result.json");
            JsonElement result = ProjectInputs.Read<JsonElement>(resultPath);
            CoreTests.Equal(0, TlcResources.ValidateEvidence(result, stage, Path.Combine(output, "model")).Count);
            CoreTests.True(TlcResources.ValidateEvidence(result, stage with { Resources = new(1, 512) }, Path.Combine(output, "model")).Count > 0);
            Save(projectPath, project with { Scenarios = [new("small", [stage with { Resources = new(1, 256) }])] });
            string second = Path.Combine(work, "resource-run-second");
            CoreTests.Equal(0, TestCli.Run(["run", "--project", projectPath, "--scenario", "small", "--output", second]).Code);
            CoreTests.Equal(0, TestCli.Run(["accept", "--project", projectPath, "--run", second]).Code);
            CoreTests.Equal(Files.Hash(config), ProjectInputs.Read<WorkflowRun>(Path.Combine(second, "run.json")).Fingerprints[config]);
            string invocation = Path.Combine(output, "model", "tlc.invocation.json");
            JsonObject changed = JsonNode.Parse(Files.Read(invocation))!.AsObject();
            JsonArray arguments = changed["arguments"]!.AsArray();
            int workers = arguments.Select(a => a!.GetValue<string>()).ToList().IndexOf("-workers");
            arguments[workers + 1] = "9";
            Save(invocation, changed);
            CoreTests.True(TlcResources.ValidateEvidence(result, stage, Path.Combine(output, "model")).Count > 0);
            string unboundedModel = Path.Combine(directory, "Unbounded.tla"), unboundedConfig = Path.Combine(directory, "Unbounded.cfg");
            File.WriteAllText(unboundedModel, "---- MODULE Unbounded ----\nEXTENDS Naturals\nVARIABLE n\nInit == n = 0\nNext == n' = n + 1\nSpec == Init /\\ [][Next]_n\nTypeOK == n \\in Nat\n====\n");
            File.WriteAllText(unboundedConfig, "SPECIFICATION Spec\nINVARIANT TypeOK\n");
            AnalysisStage deadline = stage with { TimeoutSeconds = 10, Models = [unboundedModel], Config = unboundedConfig, Resources = new(2, 2048) };
            AnalysisStage blocked = stage with { Id = "blocked", DependsOn = ["model"], Resources = new(3, 1024) };
            Save(projectPath, project with { Scenarios = [new("small", [deadline, blocked])] });
            Save(policyPath, new AcceptancePolicy(Schema.Version, "acceptance-policy", project.Id,
                [new("model", "small", "model", "passed", ConfigSha256: Files.Hash(unboundedConfig)),
                 new("blocked", "small", "blocked", "passed", ConfigSha256: Files.Hash(config))]));
            string interrupted = Path.Combine(work, "resource-run-interrupted");
            var interruptedResponse = TestCli.Run(["run", "--project", projectPath, "--scenario", "small", "--output", interrupted]);
            if (interruptedResponse.Code != 2 || !File.Exists(Path.Combine(interrupted, "run.json")))
                throw new InvalidOperationException(interruptedResponse.Json);
            WorkflowRun interruptedRun = ProjectInputs.Read<WorkflowRun>(Path.Combine(interrupted, "run.json"));
            CoreTests.Equal("incomplete", interruptedRun.Stages.Single(s => s.Id == "model").ExecutionStatus);
            CoreTests.Equal("blocked", interruptedRun.Stages.Single(s => s.Id == "blocked").ExecutionStatus);
            CoreTests.True(interruptedRun.Diagnostics.Any(d => d.Code == ErrorCode.WorkflowStageFailed.Id()));
            foreach (WorkflowStageResult recorded in interruptedRun.Stages)
            {
                TlcResources expected = recorded.Id == "model" ? new(2, 2048) : new(3, 1024);
                CoreTests.Equal(expected, recorded.RequestedResources);
                CoreTests.Equal(expected, recorded.Resources);
            }
        });
        test("native regression and proof stages use ordinary acceptance reporting and retention", () =>
        {
            string fixture = Path.Combine(work, "evidence-project");
            Copy(Path.Combine(root, "examples", "Evidence"), fixture);
            string path = Path.Combine(fixture, "tlalink.project.json");
            JsonObject authored = JsonNode.Parse(Files.Read(path))!.AsObject();
            authored["tools"] = JsonSerializer.SerializeToNode(new ProjectTools(Dafny: new(dafny, Files.Hash(dafny))), Files.Json);
            Save(path, authored);
            string increment = Path.Combine(fixture, "src", "Increment");
            File.WriteAllText(Path.Combine(fixture, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>");
            ObservedExecution.RequireSuccess(ObservedExecution.Invoke(["restore", Path.Combine(increment, "Regression.csproj"),
                "--configfile", Path.Combine(fixture, "NuGet.Config"), "-p:NuGetAudit=false"], increment, 60, "restore"));
            string store = Directory.CreateDirectory(Path.Combine(work, "evidence-stage-store")).FullName;
            string output = Path.Combine(store, "arithmetic");
            var run = TestCli.Run(["run", "--project", path, "--scenario", "arithmetic", "--output", output]);
            if (run.Code != 0) throw new InvalidOperationException(run.Json);
            CoreTests.Equal(1, TestCli.Run(["accept", "--project", path, "--run", output]).Code);
            string pending = Path.Combine(work, "evidence-pending.md");
            var report = TestCli.Run(["report", "--project", path, "--run", output, "--output", pending]);
            if (report.Code != 0) throw new InvalidOperationException(report.Json);
            CoreTests.True(Files.Read(pending).Contains("not-accepted", StringComparison.Ordinal));
            CoreTests.True(Files.Read(pending + ".json").Contains("evidence-report", StringComparison.Ordinal));
            string presentation = Directory.CreateDirectory(Path.Combine(work, "source-presentation")).FullName;
            string source = Path.Combine(presentation, "Coverage.cs"), inventory = Path.Combine(presentation, "inventory.json");
            File.WriteAllText(source, "class Coverage { void Run() {} }");
            Files.WriteNew(inventory, SourceInventory.Build([source]));
            string supplement = Path.Combine(presentation, "source-report.json");
            Files.WriteNew(supplement, new EvidenceReportManifest(Schema.Version, "evidence-report",
                "Explicit source presentation", "Independent authored dispositions, not a composition theorem.",
                new() { ["Coverage.cs"] = Files.Hash(source), ["inventory.json"] = Files.Hash(inventory) },
                [], [], [], ["Source correspondence remains a review obligation."],
                [new(null, [new("Coverage.cs", "synthetic", "inventory-only", [], "Not validated code.")])]));
            string combined = Path.Combine(work, "combined-evidence.md");
            var combinedReport = TestCli.Run(["report", "--project", path, "--run", output,
                "--source-report", supplement, "--source-inventory", inventory, "--output", combined, "--format", "summary"]);
            if (combinedReport.Code != 0) throw new InvalidOperationException(combinedReport.Json);
            CoreTests.True(Files.Read(combined).Contains("Explicit source presentation", StringComparison.Ordinal));
            CoreTests.True(Files.Read(combined).Contains("inventory-only", StringComparison.Ordinal));
            CoreTests.True(Files.Read(combined).Contains("not-accepted", StringComparison.Ordinal));
            CoreTests.Equal(0, TestCli.Run(["record-review", "--project", path, "--obligation", "source-correspondence",
                "--reviewer", "synthetic-test", "--rationale", "Explicit test-only review assertion.", "--evidence", Path.Combine("src", "Increment", "Program.cs")]).Code);
            var acceptance = TestCli.Run(["accept", "--project", path, "--run", output]);
            if (acceptance.Code != 0) throw new InvalidOperationException(acceptance.Json);
            string accepted = Path.Combine(work, "evidence-accepted.md");
            CoreTests.Equal(0, TestCli.Run(["report", "--project", path, "--run", output, "--output", accepted]).Code);
            string retention = Path.Combine(work, "new-stages-retention.json");
            Files.WriteNew(retention, new EvidenceRetention(Schema.Version, "evidence-retention",
                [new(output, "Synthetic retained stage evidence")], []));
            GcPlan plan = EvidenceGarbageCollection.Plan(store, retention, 0, DateTimeOffset.UtcNow.AddHours(1));
            if (plan.Status != "ready") throw new InvalidOperationException(JsonSerializer.Serialize(plan.Diagnostics, Files.Json));
            CoreTests.True(plan.Entries.Single().Status != "candidate");
        });
        test("trace matching receives explicit resources and accepts its observed build provenance", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "resource-trace")).FullName;
            string path = Path.Combine(directory, "project.json"), policyPath = Path.Combine(directory, "requirements.json");
            string examplePath = Path.Combine(root, "examples", "Workflow", "tlalink.project.json");
            string example = Path.GetDirectoryName(examplePath)!;
            AnalysisProject original = ProjectComposition.Read(examplePath);
            AnalysisScenario lanes = original.Scenarios.Single(s => s.Id == "lanes");
            Save(path, original with {
                Tools = new(Jar: new(jar, Files.Hash(jar))), Requirements = policyPath, Obligations = [],
                Scenarios = [lanes with { Stages = lanes.Stages.Select(s => s with {
                    Sources = s.Sources?.Select(p => Path.Combine(example, p)).ToList(),
                    Models = s.Models?.Select(p => Path.Combine(example, p)).ToList(),
                    Scope = s.Scope == null ? null : Path.Combine(example, s.Scope),
                    Config = Path.Combine(example, s.Config!), Resources = new(2, 512) }).ToList() }]
            });
            AcceptancePolicy policy = ProjectInputs.Read<AcceptancePolicy>(ProjectInputs.Resolve(examplePath, original.Requirements));
            Save(policyPath, policy with { Requirements = policy.Requirements.Where(r => r.Scenario == "lanes").ToList() });
            string output = Path.Combine(work, "resource-trace-run");
            var run = TestCli.Run(["run", "--project", path, "--scenario", "lanes", "--output", output]);
            if (run.Code != 0) throw new InvalidOperationException(run.Json);
            var acceptance = TestCli.Run(["accept", "--project", path, "--run", output]);
            if (acceptance.Code != 0) throw new InvalidOperationException(acceptance.Json);
            WorkflowRun receipt = ProjectInputs.Read<WorkflowRun>(Path.Combine(output, "run.json"));
            CoreTests.Equal(new TlcResources(2, 512), receipt.Stages.Single(s => s.Kind == "trace").Resources);
            CoreTests.True(receipt.Fingerprints.Keys.Any(p => Path.GetFileName(p) == "Evidence.targets"));
        });
    }

    internal static void Copy(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string copy = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
    }

    private static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Files.Json));
}
