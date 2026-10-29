using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using TlaLink;

namespace TlaLink.Tests;

public static class MethodSummaryTests
{
    public static void RegisterCore(Action<string, Action> test, string work)
    {
        test("method summaries validate kinds, required endpoints and acyclic local dependencies", () =>
        {
            MethodSummary definition = new("permit::take", "atomic", ["Gate.cs"], "inspect-gate", [],
                ["No progress claim."], []);
            CorrespondenceConfiguration scope = new(Schema.Version, "correspondence-scope", Markup.Version,
                [new("permit", "tla", ["Gate.tla"], RequiredBindings: ["permit::take"])], [], [definition]);
            MethodSummaries.Validate(scope);
            foreach (MethodSummary invalid in new[] {
                definition with { Abstraction = "protocol" }, definition with { Limits = [] },
                definition with { Sources = [] }, definition with { Inspection = "../review" },
                definition with { DependsOn = ["permit::missing"] }, definition with { DependsOn = ["permit::take"] },
                definition with { Binding = "permit::unrequired" }
            })
                CoreTests.ThrowsAny<InputException>(() => MethodSummaries.Validate(scope with { MethodSummaries = [invalid] }));
            CoreTests.ThrowsAny<InputException>(() => MethodSummaries.Validate(scope with { MethodSummaries = [definition, definition] }));
        });
        test("review origins are explicit and agent assertions cannot satisfy human obligations", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "summary-review-origin")).FullName;
            string input = Path.Combine(directory, "input.cs");
            File.WriteAllText(input, "class Input {}");
            AnalysisProject project = new(Schema.Version, "analysis-project", "origin", new(),
                new("net10.0", "14", "enable", false, true, []), "requirements.json", [],
                [new("human", "Synthetic human review.", [], ["input.cs"]),
                 new("agent", "Synthetic agent inspection.", [], ["input.cs"], ReviewOrigin.Agent)], "reviews");
            string path = Path.Combine(directory, "project.json");
            CliOptions Options(string id, string origin) => new(["record-review", "--obligation", id, "--origin", origin,
                "--reviewer", "synthetic-test", "--rationale", "Role isolation control, not a real approval.",
                "--evidence", input]);
            CoreTests.ThrowsAny<InputException>(() => ProjectAcceptance.RecordReview(path, project, Options("human", "agent")));
            CoreTests.Equal(0, ProjectAcceptance.RecordReview(path, project, Options("agent", "agent")));
            CoreTests.Equal("accepted", ProjectAcceptance.ReviewStatus(path, project, "agent"));
            string receipt = Directory.GetFiles(Path.Combine(directory, "reviews", "agent")).Single();
            ReviewDecision decision = ProjectInputs.Read<ReviewDecision>(receipt);
            CoreTests.Equal(ReviewOrigin.Agent, decision.Origin);
        });
        test("summary metadata resolves declaring-file inputs and tracks compiler context without a backend", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "summary-core-context")).FullName;
            string scopePath = Path.Combine(directory, "scope.json");
            File.WriteAllText(Path.Combine(directory, "Gate.cs"),
                """class Gate { /* @link {"bind":[{"id":"gate::value"}]} */ public int Value() => 1; }""");
            File.WriteAllText(Path.Combine(directory, "Gate.tla"), "---- MODULE Gate ----\nValue(x) == x\n====\n");
            MethodSummary summary = new("gate::value", "pure", ["Gate.cs"], "inspect-gate", [], ["Selected result only."], []);
            Files.WriteNew(scopePath, new CorrespondenceConfiguration(Schema.Version, "correspondence-scope", Markup.Version,
                [new("gate", "tla", ["Gate.tla"], RequiredBindings: ["gate::value"])], [], [summary]));
            AnalysisStage stage = new("caller", "tlc", [], 30, Models: ["Caller.tla"], Config: "Caller.cfg",
                MethodSummaries: [new("scope.json", ["gate::value"])]);
            AnalysisProject project = new(Schema.Version, "analysis-project", "summary-core", new(),
                new("net10.0", "14", "enable", false, true, []), "requirements.json", [new("main", [stage])],
                [new("inspect-gate", "Inspect synthetic helper.", [], ["Gate.cs"], ReviewOrigin.Agent)], "reviews");
            string path = Path.Combine(directory, "project.json");
            Dictionary<string, string> pins = new(FilePaths.Comparer);
            MethodSummarySupport before = MethodSummaries.Support(path, project, "inspect-gate", pins, false)!;
            CoreTests.True(pins.ContainsKey("Gate.cs") && pins.ContainsKey("Gate.tla") && pins.ContainsKey("scope.json"));
            MethodSummarySupport changed = MethodSummaries.Support(path, project with {
                Compiler = project.Compiler with { Defines = ["SYNTHETIC"] }
            }, "inspect-gate", new(FilePaths.Comparer), false)!;
            CoreTests.True(before.SourceContextSha256 != changed.SourceContextSha256);
            CoreTests.ThrowsAny<InputException>(() => MethodSummaries.Select(path, project,
                [stage with { MethodSummaries = [new("scope.json", ["gate::value", "gate::value"])] }]));
            CoreTests.ThrowsAny<InputException>(() => MethodSummaries.Select(path, project,
                [stage with { Kind = "inputs" }]));
            CoreTests.ThrowsAny<InputException>(() => MethodSummaries.Select(path, project,
                [stage with { MethodSummaries = [new("scope.json", ["gate::unknown"])] }]));
        });
        test("component summary uses retain declaring-file scope paths and agent inspection origins", () =>
        {
            string directory = System.IO.Directory.CreateDirectory(Path.Combine(work, "summary-component")).FullName;
            string child = System.IO.Directory.CreateDirectory(Path.Combine(directory, "child")).FullName;
            Files.WriteNew(Path.Combine(child, "component.json"), new ProjectComponent(Schema.Version,
                "project-component", "helper",
                [new("caller", "tlc", TimeoutSeconds: 30, Models: ["Caller.tla"], Config: "Caller.cfg",
                    MethodSummaries: [new("scope.json", ["helper::value"])])],
                [new("helper-inspection", "Inspect helper.", [], ["Helper.cs"], Origin: ReviewOrigin.Agent)]));
            string path = Path.Combine(directory, "project.json");
            Files.WriteNew(path, new ProjectAuthoring(Schema.Version, "analysis-project", "composed-summary",
                new(), new("net10.0", "14", "enable", false, true, []), "requirements.json",
                [new("main", Components: ["helper"])], "reviews",
                Components: [Path.Combine("child", "component.json")], AnnotationVersion: Markup.Version));
            AnalysisProject expanded = ProjectComposition.Read(path);
            MethodSummaryUse use = expanded.Scenarios.Single().Stages.Single().MethodSummaries!.Single();
            CoreTests.Equal(Path.Combine(child, "scope.json"), ProjectInputs.Resolve(path, use.Scope));
            CoreTests.Equal(ReviewOrigin.Agent, expanded.Obligations.Single().Origin);
        });
    }

    public static void RegisterNative(Action<string, Action> test, string root, string work)
    {
        test("two callers reuse a stateless summary with fresh agent support, reports and unchanged TLC reuse", () =>
        {
            Fixture f = new(root, work);
            var plan = f.Cli("plan", "--scenario", "callers", "--format", "summary");
            CoreTests.Equal(0, plan.Code);
            using (JsonDocument document = JsonDocument.Parse(plan.Json))
            {
                JsonElement summary = document.RootElement.GetProperty("method_summaries")[0];
                CoreTests.Equal("pending", summary.GetProperty("inspection_status").GetString());
                CoreTests.Equal("M:SummaryExample.PermitGate.TryTake", summary.GetProperty("method").GetString());
                CoreTests.Equal("Requires", summary.GetProperty("precondition").GetProperty("name").GetString());
            }
            string first = f.Run("first");
            WorkflowRun initial = ProjectInputs.Read<WorkflowRun>(Path.Combine(first, "run.json"));
            CoreTests.Equal(2L, initial.Stages.Single(s => s.Id == "caller-a").Statistics!.DistinctStates!.Value);
            CoreTests.Equal(4L, initial.Stages.Single(s => s.Id == "caller-b").Statistics!.DistinctStates!.Value);
            CoreTests.Equal(1, f.Cli("accept", "--run", first).Code);
            f.Review();
            CoreTests.Equal(0, f.Cli("accept", "--run", first).Code);
            string runPath = Path.Combine(first, "run.json"), originalRun = File.ReadAllText(runPath);
            try
            {
                JsonObject changed = JsonNode.Parse(originalRun)!.AsObject();
                changed["method_summaries"] = new JsonArray();
                File.WriteAllText(runPath, changed.ToJsonString(Files.Json));
                CoreTests.Equal(1, f.Cli("accept", "--run", first).Code);
                changed = JsonNode.Parse(originalRun)!.AsObject();
                changed["fingerprints"]!.AsObject().Remove(f.Source);
                File.WriteAllText(runPath, changed.ToJsonString(Files.Json));
                using JsonDocument missingPin = JsonDocument.Parse(f.Cli("accept", "--run", first).Json);
                CoreTests.Equal("inconsistent", missingPin.RootElement.GetProperty("archive_status").GetString());
                changed = JsonNode.Parse(originalRun)!.AsObject();
                changed["method_summaries"]![0]!["specification"]!["location"]!["path"] = f.Source;
                File.WriteAllText(runPath, changed.ToJsonString(Files.Json));
                CoreTests.Equal(1, f.Cli("accept", "--run", first).Code);
            }
            finally { File.WriteAllText(runPath, originalRun); }
            ReviewDecision receipt = f.Latest();
            CoreTests.True(receipt.SummarySupport is { Inputs.Count: > 5 });
            CoreTests.True(receipt.Fingerprints.ContainsKey(Path.Combine("verification", "PermitSummary.tla")));
            string report = Path.Combine(f.Directory, "report.md");
            CoreTests.Equal(0, f.Cli("report", "--run", first, "--output", report).Code);
            EvidenceReportManifest rendered = ProjectInputs.Read<EvidenceReportManifest>(report + ".json");
            CoreTests.True(rendered.Nodes.Count(n => n.Kind == "review") == 1);
            CoreTests.True(rendered.Nodes.Where(n => n.Kind == "requirement").All(n => n.DependsOn.Count == 2));
            CoreTests.True(rendered.Nodes.Any(n => n.Kind == "code" && n.Claim.Contains("atomic", StringComparison.Ordinal)));
            File.AppendAllText(f.Source, "\n");
            CoreTests.Equal(2, f.Cli("accept", "--run", first).Code);
            CoreTests.Equal("stale", ProjectAcceptance.ReviewStatus(f.Path, ProjectInputs.Load(f.Path).Project, "permit-summary"));
            string second = f.Run("source-changed");
            WorkflowRun current = ProjectInputs.Read<WorkflowRun>(Path.Combine(second, "run.json"));
            CoreTests.Equal(1, current.MethodSummaries.Count);
            CoreTests.True(current.Stages.All(s => s.StoredResults?.Any(r => r.Primary && r.Origin == "reused") == true));
            CoreTests.Equal(1, f.Cli("accept", "--run", second).Code);
            f.Review();
            CoreTests.Equal(0, f.Cli("accept", "--run", second).Code);
            string summaryPath = Path.Combine(f.Directory, "verification", "PermitSummary.tla");
            File.AppendAllText(summaryPath, "\n");
            CoreTests.Equal("stale", ProjectAcceptance.ReviewStatus(f.Path, ProjectInputs.Load(f.Path).Project, "permit-summary"));
            CoreTests.Equal(2, f.Cli("accept", "--run", second).Code);
        });
        test("provider defects require new inspection and an agent cannot discharge a separate human review", () =>
        {
            Fixture f = new(root, work, humanReview: true);
            CoreTests.Equal(false, InvokeGate(f.Source, 0));
            File.WriteAllText(f.Source, File.ReadAllText(f.Source).Replace("return false;", "return true;", StringComparison.Ordinal));
            CoreTests.Equal(true, InvokeGate(f.Source, 0));
            string output = f.Run("defective");
            f.Review("rejected");
            CoreTests.Equal(1, f.Cli("accept", "--run", output).Code);
            var forged = f.Cli("record-review", "--obligation", "human-boundary", "--origin", "agent",
                "--reviewer", "synthetic-agent", "--rationale", "Negative role control.", "--evidence", f.Source);
            CoreTests.Equal(2, forged.Code);
            using JsonDocument document = JsonDocument.Parse(f.Cli("accept", "--run", output).Json);
            CoreTests.True(document.RootElement.GetProperty("reviews").EnumerateArray().Any(r =>
                r.GetProperty("obligation").GetString() == "human-boundary" && r.GetProperty("status").GetString() == "pending"));
        });
        test("caller precondition misuse is a completed native invariant rejection, not a disabled action", () =>
        {
            Fixture f = new(root, work);
            string caller = Path.Combine(f.Directory, "verification", "CallerA.tla");
            File.WriteAllText(caller, File.ReadAllText(caller)
                .Replace("EXTENDS PermitSummary", "EXTENDS PermitSummary, Integers", StringComparison.Ordinal)
                .Replace("Next ==", """
                    Next ==
                        \/ /\ pending
                           /\ available' = -1
                           /\ UNCHANGED <<acquired, pending>>
                    """, StringComparison.Ordinal));
            string output = f.Run("misuse");
            WorkflowRun run = ProjectInputs.Read<WorkflowRun>(Path.Combine(output, "run.json"));
            CoreTests.Equal("rejected", run.Stages.Single(s => s.Id == "caller-a").Outcome);
            string log = File.ReadAllText(Path.Combine(output, "caller-a", "tlc.log"));
            CoreTests.True(log.Contains("CallerPrecondition", StringComparison.Ordinal));
            CoreTests.Equal(1, f.Cli("accept", "--run", output).Code);
        });
        test("unresolved preconditions and asynchronous providers fail summary preflight explicitly", () =>
        {
            Fixture f = new(root, work);
            string scope = Path.Combine(f.Directory, "verification", "scope.json");
            string original = File.ReadAllText(scope);
            File.WriteAllText(scope, original.Replace("\"Requires\"", "\"MissingPredicate\"", StringComparison.Ordinal));
            CoreTests.Equal(2, f.Cli("doctor", "--scenario", "callers").Code);
            File.WriteAllText(scope, original);
            string source = File.ReadAllText(f.Source);
            File.WriteAllText(f.Source, source
                .Replace("public bool TryTake()", "public async System.Threading.Tasks.Task<bool> TryTake()", StringComparison.Ordinal));
            CoreTests.Equal(2, f.Cli("doctor", "--scenario", "callers").Code);
            File.WriteAllText(f.Source, source
                .Replace("public bool TryTake()", "public System.Threading.Tasks.Task<bool> TryTake()", StringComparison.Ordinal)
                .Replace("return true;", "return System.Threading.Tasks.Task.FromResult(true);", StringComparison.Ordinal)
                .Replace("return false;", "return System.Threading.Tasks.Task.FromResult(false);", StringComparison.Ordinal));
            CoreTests.Equal(2, f.Cli("doctor", "--scenario", "callers").Code);
            File.WriteAllText(f.Source, source);
            string model = Path.Combine(f.Directory, "verification", "PermitSummary.tla");
            File.WriteAllText(model, File.ReadAllText(model).Replace("EXTENDS Naturals",
                "EXTENDS Naturals\nVARIABLE hiddenState", StringComparison.Ordinal));
            var stateful = f.Cli("doctor", "--scenario", "callers");
            CoreTests.Equal(2, stateful.Code);
            CoreTests.True(stateful.Json.Contains("stateless", StringComparison.Ordinal));
        });
    }

    private static bool InvokeGate(string path, int permits)
    {
        CSharpScope source = CSharpScope.Parse([path]);
        using MemoryStream binary = new();
        var emitted = source.Compilation.Emit(binary);
        if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
        binary.Position = 0;
        AssemblyLoadContext context = new(null, isCollectible: true);
        try
        {
            Type gate = context.LoadFromStream(binary).GetType("SummaryExample.PermitGate", throwOnError: true)!;
            object instance = Activator.CreateInstance(gate, permits)!;
            return (bool)gate.GetMethod("TryTake")!.Invoke(instance, null)!;
        }
        finally { context.Unload(); }
    }

    private sealed class Fixture
    {
        public string Directory { get; }
        public string Path => System.IO.Path.Combine(Directory, "tlalink.project.json");
        public string Source => System.IO.Path.Combine(Directory, "src", "PermitGate.cs");

        public Fixture(string root, string work, bool humanReview = false)
        {
            Directory = System.IO.Path.Combine(work, "summary-" + Guid.NewGuid().ToString("N"));
            StageEvidenceTests.Copy(System.IO.Path.Combine(root, "examples", "MethodSummaries"), Directory);
            string jar = TestTools.Jar(root);
            List<ReviewObligation> obligations = [
                new("permit-summary", "The selected C# method supports the atomic permit relation.",
                    ["The declared method-summary assumptions hold."],
                    [System.IO.Path.Combine("src", "PermitGate.cs")], ReviewOrigin.Agent)
            ];
            if (humanReview) obligations.Add(new("human-boundary", "Synthetic separate human gate.", [], ["README.md"]));
            List<AnalysisStage> stages = new[] { "A", "B" }.Select(c => new AnalysisStage("caller-" + c.ToLowerInvariant(),
                "tlc", [], 60, Models: [System.IO.Path.Combine("verification", "Caller" + c + ".tla")],
                Config: System.IO.Path.Combine("verification", "Caller" + c + ".cfg"), Resources: new(1, 256),
                MethodSummaries: [new(System.IO.Path.Combine("verification", "scope.json"), ["permits::try-take"])])).ToList();
            AnalysisProject project = new(Schema.Version, "analysis-project", "summary-example",
                new(Jar: new(jar, Files.Hash(jar))), new("net10.0", "14", "enable", false, true, []),
                "requirements.json", [new("callers", stages)], obligations, "reviews", ResultStore: "store");
            File.WriteAllText(Path, JsonSerializer.Serialize(project, Files.Json));
            Files.WriteNew(System.IO.Path.Combine(Directory, "requirements.json"), new AcceptancePolicy(Schema.Version,
                "acceptance-policy", project.Id, stages.Select(s => new AnalysisRequirement(s.Id + "-safe", "callers",
                    s.Id, "passed", ConfigSha256: Files.Hash(ProjectInputs.Resolve(Path, s.Config!)),
                    Reviews: humanReview ? ["human-boundary"] : null)).ToList()));
        }

        public (int Code, string Json) Cli(string command, params string[] arguments) =>
            TestCli.Run([command, "--project", Path, .. arguments]);

        public string Run(string name)
        {
            string output = System.IO.Path.Combine(Directory, "runs", name);
            var response = Cli("run", "--scenario", "callers", "--output", output);
            if (response.Code != 0) throw new InvalidOperationException(response.Json);
            return output;
        }

        public void Review(string disposition = "accepted")
        {
            var response = Cli("record-review", "--obligation", "permit-summary", "--origin", "agent",
                "--reviewer", "synthetic-test-agent", "--rationale", "Synthetic inspection control; not human approval.",
                "--disposition", disposition, "--evidence", System.IO.Path.Combine(Directory, "verification", "inspection.md"));
            if (response.Code != 0) throw new InvalidOperationException(response.Json);
        }

        public ReviewDecision Latest() => System.IO.Directory.GetFiles(System.IO.Path.Combine(Directory, "reviews", "permit-summary"))
            .Select(ProjectInputs.Read<ReviewDecision>).OrderByDescending(r => r.RecordedAt).First();
    }
}
