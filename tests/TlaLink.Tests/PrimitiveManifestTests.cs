using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class PrimitiveManifestTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Primitives");

    public static void RegisterCore(Action<string, Action> test, string work)
    {
        test("primitive manifests validate their structure and closed vocabularies", () =>
        {
            string directory = Latch(work, "primitive-structure");
            Dictionary<string, string> pins = new(FilePaths.Comparer);
            PrimitiveManifest valid = Primitives.Load(Path.Combine(directory, "primitive.json"), pins);
            CoreTests.Equal("latch", valid.PrimitiveKind);
            CoreTests.True(pins.ContainsKey(Path.Combine(directory, "primitive.json")));
            _ = Primitives.Load(Mutant(directory, "conditional-step"), new(FilePaths.Comparer));
            _ = Primitives.Load(Mutant(directory, "custom-module"), new(FilePaths.Comparer));
            foreach (string name in new[] { "duplicate-member", "wait-without-predicate", "predicate-without-wait",
                "custom-without-module", "module-without-custom", "critical-section-kind", "unknown-role",
                "unknown-guarantee", "state-mapping", "empty-limits" })
                CoreTests.ThrowsAny<InputException>(() => Primitives.Load(Mutant(directory, name), new(FilePaths.Comparer)));
        });
        test("Roslyn resolves every declared member exactly once and accepts asynchronous waits", () =>
        {
            string directory = Latch(work, "primitive-members");
            CSharpScope source = CSharpScope.Parse([Path.Combine(directory, "SignalLatch.cs")], annotations: false);
            PrimitiveManifest manifest = Primitives.Load(Path.Combine(directory, "primitive.json"), new(FilePaths.Comparer));
            List<string> members = Primitives.Members(source, manifest);
            CoreTests.True(members.Contains("M:PrimitiveFixture.SignalLatch.WaitAsync"));
            CoreTests.True(!members.Any(m => m.Contains("Unused", StringComparison.Ordinal) || m.Contains("Nested", StringComparison.Ordinal)));
            foreach (var (name, message) in new[] { ("missing-member", "Undeclared"), ("unknown-member", "Unknown") })
            {
                PrimitiveManifest mutant = Primitives.Load(Mutant(directory, name), new(FilePaths.Comparer));
                InputException error = Catch<InputException>(() => Primitives.Members(source, mutant));
                CoreTests.True(error.Message.Contains(message, StringComparison.Ordinal));
            }
            CoreTests.ThrowsAny<InputException>(() => Primitives.Members(source, manifest with { Type = "T:PrimitiveFixture.Missing" }));
            File.WriteAllText(Path.Combine(directory, "SignalLatch.cs"), File.ReadAllText(Path.Combine(directory, "SignalLatch.cs"))
                .Replace("private void Unused()", "public int Exposed; private void Unused()", StringComparison.Ordinal));
            CoreTests.ThrowsAny<InputException>(() => Primitives.Members(
                CSharpScope.Parse([Path.Combine(directory, "SignalLatch.cs")], annotations: false), manifest));
        });
        test("stage uses select declared primitives with existing scenarios, inspections and acyclic dependencies", () =>
        {
            string directory = Latch(work, "primitive-selection");
            (string path, AnalysisProject project) = Project(directory);
            SelectedPrimitive selected = Primitives.Select(path, project, project.Scenarios.SelectMany(s => s.Stages)).Single();
            CoreTests.Equal("signal-latch", selected.Primitive.Id);
            CoreTests.True(File.Exists(selected.Module) && Path.GetFileName(selected.Module) == "TlaLinkLatch.tla");
            CoreTests.Equal(0, Primitives.Required(path, project, "latch", project.Scenarios[0].Stages).Count);
            CoreTests.Equal(1, Primitives.Required(path, project, "consumer", project.Scenarios[0].Stages).Count);
            AnalysisStage stage = project.Scenarios[0].Stages[0];
            foreach (AnalysisStage invalid in new[] {
                stage with { Kind = "inputs" },
                stage with { Primitives = [new("scope.json", ["signal-latch", "signal-latch"])] },
                stage with { Primitives = [new("scope.json", ["missing"])] },
                stage with { Primitives = [new("scope.json", [])] }
            })
                CoreTests.ThrowsAny<InputException>(() => Primitives.Select(path, project, [invalid]));
            CoreTests.ThrowsAny<InputException>(() => Primitives.Select(path, project with { Obligations = [] }, [stage]));
            File.Copy(Mutant(directory, "unknown-scenario"), Path.Combine(directory, "primitive.json"), overwrite: true);
            InputException scenario = Catch<InputException>(() => Primitives.Select(path, project, [stage]));
            CoreTests.True(scenario.Message.Contains("verification scenario", StringComparison.Ordinal));
            File.Copy(Mutant(directory, "unknown-dependency"), Path.Combine(directory, "primitive.json"), overwrite: true);
            CoreTests.ThrowsAny<InputException>(() => Primitives.Select(path, project, [stage]));
            File.Copy(Mutant(directory, "cycle-a"), Path.Combine(directory, "cycle-a.json"));
            File.Copy(Mutant(directory, "cycle-b"), Path.Combine(directory, "cycle-b.json"));
            WriteScope(directory, ["cycle-a.json", "cycle-b.json"]);
            InputException cycle = Catch<InputException>(() =>
                Primitives.Select(path, project, [stage with { Primitives = [new("scope.json", ["cycle-a"])] }]));
            CoreTests.True(cycle.Message.Contains("Cyclic", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(directory, "scope.json"), File.ReadAllText(Path.Combine(directory, "scope.json"))
                .Replace("\"primitives\":", "\"region\": [\"SignalLatch.cs\"], \"primitives\":", StringComparison.Ordinal));
            CoreTests.ThrowsAny<JsonException>(() => CorrespondenceInputs.Load(Path.Combine(directory, "scope.json"), new(FilePaths.Comparer)));
        });
        test("component primitive uses retain declaring-file scope paths", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "primitive-component")).FullName;
            string child = Directory.CreateDirectory(Path.Combine(directory, "child")).FullName;
            Files.WriteNew(Path.Combine(child, "component.json"), new ProjectComponent(Schema.Version, "project-component", "caller",
                [new("caller", "tlc", TimeoutSeconds: 30, Models: ["Caller.tla"], Config: "Caller.cfg",
                    Primitives: [new("scope.json", ["signal-latch"])])],
                [new("latch-inspection", "Inspect latch.", [], ["SignalLatch.cs"], Origin: ReviewOrigin.Agent)]));
            string path = Path.Combine(directory, "project.json");
            Files.WriteNew(path, new ProjectAuthoring(Schema.Version, "analysis-project", "composed-primitive",
                new(), new("net10.0", "14", "enable", false, true, []), "requirements.json",
                [new("main", Components: ["caller"])], "reviews",
                Components: [Path.Combine("child", "component.json")], AnnotationVersion: Markup.Version));
            AnalysisProject expanded = ProjectComposition.Read(path);
            PrimitiveUse use = expanded.Scenarios.Single().Stages.Single().Primitives!.Single();
            CoreTests.Equal(Path.Combine(child, "scope.json"), ProjectInputs.Resolve(path, use.Scope));
            CoreTests.Equal(ReviewOrigin.Agent, expanded.Obligations.Single().Origin);
        });
        test("inspection origins are exact and an agent decision cannot satisfy a human obligation", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "primitive-review-origin")).FullName;
            string input = Path.Combine(directory, "input.cs");
            File.WriteAllText(input, "class Input {}");
            AnalysisProject project = new(Schema.Version, "analysis-project", "origin", new(),
                new("net10.0", "14", "enable", false, true, []), "requirements.json", [],
                [new("human", "Synthetic human review.", [], ["input.cs"]),
                 new("agent", "Synthetic agent inspection.", [], ["input.cs"], ReviewOrigin.Agent)], "reviews");
            AcceptancePolicy policy = new(Schema.Version, "acceptance-policy", project.Id, []);
            string path = Path.Combine(directory, "project.json");
            CliOptions Options(string id, string origin) => new(["record-review", "--obligation", id, "--origin", origin,
                "--reviewer", "synthetic-test", "--rationale", "Role isolation control, not a real approval.",
                "--evidence", input]);
            CoreTests.ThrowsAny<InputException>(() => ProjectAcceptance.RecordReview(path, project, policy, Options("human", "agent")));
            CoreTests.ThrowsAny<InputException>(() => ProjectAcceptance.RecordReview(path, project, policy, Options("agent", "human")));
            CoreTests.Equal(0, ProjectAcceptance.RecordReview(path, project, policy, Options("agent", "agent")));
            CoreTests.Equal("accepted", ProjectAcceptance.ReviewStatus(path, project, "agent"));
            CoreTests.Equal("pending", ProjectAcceptance.ReviewStatus(path, project, "human"));
        });
        test("primitive support tracks source bytes and compiler context without a backend", () =>
        {
            string directory = Latch(work, "primitive-support");
            (string path, AnalysisProject project) = Project(directory);
            Dictionary<string, string> pins = new(FilePaths.Comparer);
            CurrentPrimitiveSupport before = Primitives.Support(path, project, "latch-inspection", pins, false)!;
            CoreTests.True(pins.ContainsKey("SignalLatch.cs") && pins.ContainsKey("primitive.json") && pins.ContainsKey("scope.json"));
            CoreTests.True(pins.Keys.Any(k => Path.GetFileName(k) == "TlaLinkLatch.tla"));
            CoreTests.Equal("latch", before.Scenarios.Single());
            CurrentPrimitiveSupport defines = Primitives.Support(path, project with {
                Compiler = project.Compiler with { Defines = ["SYNTHETIC"] } }, "latch-inspection", new(FilePaths.Comparer), false)!;
            CoreTests.True(before.SourceContextSha256 != defines.SourceContextSha256);
            File.AppendAllText(Path.Combine(directory, "SignalLatch.cs"), "\n");
            CurrentPrimitiveSupport changed = Primitives.Support(path, project, "latch-inspection", new(FilePaths.Comparer), false)!;
            CoreTests.True(before.SourceContextSha256 != changed.SourceContextSha256);
            CoreTests.Equal(before.SelectionSha256, changed.SelectionSha256);
            CoreTests.True(Primitives.Support(path, project, "other", new(FilePaths.Comparer), false) == null);
        });
    }

    public static void RegisterNative(Action<string, Action> test, string root, string work)
    {
        test("kind-module operators resolve through SANY for shipped and custom modules", () =>
        {
            string directory = Latch(work, "primitive-operators");
            (string path, AnalysisProject project) = Project(directory);
            string jar = TestTools.Jar(root);
            List<PrimitiveResolution> Inspect(string? mutant = null)
            {
                if (mutant != null) File.Copy(Mutant(directory, mutant), Path.Combine(directory, "primitive.json"), overwrite: true);
                return Primitives.Inspect(path, project, Primitives.Select(path, project, project.Scenarios.SelectMany(s => s.Stages)),
                    new(FilePaths.Comparer), (model, modulePath) => TlaBackend.Parse(model, jar, 60, modulePath));
            }
            PrimitiveResolution resolved = Inspect().Single();
            CoreTests.Equal(4, resolved.Members.Count);
            CoreTests.Equal("TlaLinkLatch.tla", Path.GetFileName(resolved.Module));
            _ = Inspect("conditional-step");
            CoreTests.Equal("CustomLatch.tla", Path.GetFileName(Inspect("custom-module").Single().Module));
            foreach (string mutant in new[] { "unknown-action", "unknown-fails", "unknown-wait", "unknown-precondition" })
            {
                InputException error = Catch<InputException>(() => Inspect(mutant));
                CoreTests.True(error.Message.Contains("is not a parameterized operator", StringComparison.Ordinal));
            }
        });
        test("a primitive is verified once, inspected with its accepted receipt and reused by a caller component", () =>
        {
            Example f = new(root, work);
            using (JsonDocument plan = JsonDocument.Parse(f.Cli("plan", "--format", "summary").Json))
            {
                JsonElement primitive = plan.RootElement.GetProperty("primitives").EnumerateArray().Single();
                CoreTests.Equal("pending", primitive.GetProperty("inspection_status").GetString());
                CoreTests.Equal("counting-resource", primitive.GetProperty("primitive_kind").GetString());
                CoreTests.Equal(2, primitive.GetProperty("outstanding_guarantees").GetArrayLength());
            }
            string verification = f.Run("permit-gate", "verification");
            WorkflowRun verified = ProjectInputs.Read<WorkflowRun>(Path.Combine(verification, "run.json"));
            CoreTests.Equal("permit-gate", verified.Primitives.Single().Primitive.Id);
            CoreTests.True(verified.Fingerprints.Keys.Any(k => Path.GetFileName(k) == "TlaLinkCountingResource.tla"));
            string receipt = Path.Combine(f.Directory, "permit-gate.acceptance.json");
            CoreTests.Equal(0, f.Cli("accept", "--run", verification, "--output", receipt).Code);
            string caller = f.Run("uploader", "caller");
            CoreTests.Equal(1, f.Cli("accept", "--run", caller).Code);
            CoreTests.Equal(2, f.Review(receipt: null).Code);
            CoreTests.Equal(0, f.Review(receipt).Code);
            CoreTests.Equal("accepted", f.Status());
            using (JsonDocument accepted = JsonDocument.Parse(f.Cli("accept", "--run", caller).Json))
            {
                CoreTests.Equal("accepted", accepted.RootElement.GetProperty("acceptance_status").GetString());
                JsonElement primitive = accepted.RootElement.GetProperty("primitives").EnumerateArray().Single();
                CoreTests.Equal("current", primitive.GetProperty("support_status").GetString());
                CoreTests.Equal("accepted", primitive.GetProperty("verification")[0].GetProperty("status").GetString());
            }
            string report = Path.Combine(f.Directory, "report.md");
            CoreTests.Equal(0, f.Cli("report", "--run", caller, "--output", report).Code);
            EvidenceReportManifest rendered = ProjectInputs.Read<EvidenceReportManifest>(report + ".json");
            EvidenceReportNode node = rendered.Nodes.Single(n => n.Kind == "code" && n.Title == "permit-gate");
            CoreTests.Equal("current", node.RecordedStatus);
            CoreTests.True(node.Gaps.Any(g => g.Contains("Outstanding guarantee obligation: fifo-waiters", StringComparison.Ordinal)));
            File.AppendAllText(f.Source, "\n");
            CoreTests.Equal("stale", f.Status());
            CoreTests.Equal(2, f.Cli("accept", "--run", caller).Code);
        });
        test("changed verification evidence and harness inputs withdraw caller support", () =>
        {
            Example f = new(root, work);
            string verification = f.Run("permit-gate", "verification");
            string receipt = Path.Combine(f.Directory, "permit-gate.acceptance.json");
            CoreTests.Equal(0, f.Cli("accept", "--run", verification, "--output", receipt).Code);
            CoreTests.Equal(0, f.Review(receipt).Code);
            string caller = f.Run("uploader", "caller");
            CoreTests.Equal(0, f.Cli("accept", "--run", caller).Code);
            string harness = Path.Combine(f.Directory, "primitives", "permit-gate", "PermitGateHarness.tla");
            File.AppendAllText(harness, "\n");
            using (JsonDocument document = JsonDocument.Parse(f.Cli("accept", "--run", caller).Json))
            {
                CoreTests.Equal("not-accepted", document.RootElement.GetProperty("acceptance_status").GetString());
                JsonElement primitive = document.RootElement.GetProperty("primitives").EnumerateArray().Single();
                CoreTests.Equal("not-current", primitive.GetProperty("support_status").GetString());
            }
        });
        test("primitive stage uses require the kind module in the consumer's import closure; scope models stay link-checkable", () =>
        {
            Example f = new(root, work);
            CoreTests.Equal(0, f.Cli("doctor", "--scenario", "uploader").Code);
            CoreTests.Equal(0, TestCli.Run(["links", "check", "--scope", Path.Combine(f.Directory, "verification", "scope.json"),
                "--target", "uploader", "--source", Path.Combine(f.Directory, "src", "Uploader.cs"), "--source", f.Source,
                "--jar", TestTools.Jar(root), "--annotation-version", Markup.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)]).Code);
            File.Copy(Path.Combine(Fixtures, "permit-gate", "UploaderWithoutKind.tla"),
                Path.Combine(f.Directory, "verification", "Uploader.tla"), overwrite: true);
            var doctor = f.Cli("doctor", "--scenario", "uploader");
            CoreTests.Equal(2, doctor.Code);
            CoreTests.True(doctor.Json.Contains("must import the kind module", StringComparison.Ordinal));
            var human = f.Cli("record-review", "--obligation", "permit-gate-inspection", "--origin", "human",
                "--reviewer", "synthetic-human", "--rationale", "Negative role control.", "--evidence", f.Source);
            CoreTests.Equal(2, human.Code);
        });
    }

    private static string Latch(string work, string name)
    {
        string directory = Path.Combine(work, name + "-" + Guid.NewGuid().ToString("N"));
        StageEvidenceTests.Copy(Path.Combine(Fixtures, "latch"), directory);
        return directory;
    }

    private static T Catch<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static string Mutant(string directory, string name) => Path.Combine(directory, "mutants", name + ".primitive.json");

    private static void WriteScope(string directory, List<string> primitives) =>
        File.WriteAllText(Path.Combine(directory, "scope.json"), JsonSerializer.Serialize(new CorrespondenceConfiguration(
            Schema.Version, "correspondence-scope", Markup.Version,
            [new("latch", "tla", ["Latch.tla"], RequiredBindings: ["latch::ready"])], [], new(primitives)), Files.Json));

    private static (string Path, AnalysisProject Project) Project(string directory)
    {
        AnalysisStage Stage(string id) => new(id, "tlc", [], 30, Models: ["Latch.tla"], Config: "Latch.cfg",
            Primitives: [new("scope.json", ["signal-latch"])]);
        AnalysisProject project = new(Schema.Version, "analysis-project", "primitive-core", new(),
            new("net10.0", "14", "enable", false, true, []), "requirements.json",
            [new("latch", [Stage("harness")]), new("consumer", [Stage("caller")])],
            [new("latch-inspection", "Inspect the synthetic latch.", [], ["SignalLatch.cs"], ReviewOrigin.Agent)], "reviews");
        return (Path.Combine(directory, "project.json"), project);
    }

    private sealed class Example
    {
        public string Directory { get; }
        public string Path => System.IO.Path.Combine(Directory, "tlalink.project.json");
        public string Source => System.IO.Path.Combine(Directory, "src", "PermitGate.cs");

        public Example(string root, string work)
        {
            Directory = System.IO.Path.Combine(work, "primitive-example-" + Guid.NewGuid().ToString("N"));
            StageEvidenceTests.Copy(System.IO.Path.Combine(root, "examples", "Primitives"), Directory);
            string jar = TestTools.Jar(root);
            string text = File.ReadAllText(Path)
                .Replace("..\\\\..\\\\.tools\\\\tla\\\\v1.8.0\\\\tla2tools.jar", JsonEncodedText.Encode(jar).ToString(), StringComparison.Ordinal)
                .Replace("D15655EEAFF4B3F7B3D65C46B838BC0F35D131B2EF4B1E473689B62C98D4726F", Files.Hash(jar), StringComparison.Ordinal)
                .Replace("..\\\\..\\\\Results\\\\primitives\\\\reviews", "reviews", StringComparison.Ordinal)
                .Replace("..\\\\..\\\\Results\\\\primitives\\\\store", "store", StringComparison.Ordinal);
            File.WriteAllText(Path, text);
        }

        public (int Code, string Json) Cli(string command, params string[] arguments) =>
            TestCli.Run([command, "--project", Path, .. arguments]);

        public string Run(string scenario, string name)
        {
            string output = System.IO.Path.Combine(Directory, "runs", name);
            var response = Cli("run", "--scenario", scenario, "--output", output);
            if (response.Code != 0) throw new InvalidOperationException(response.Json);
            return output;
        }

        public (int Code, string Json) Review(string? receipt)
        {
            List<string> arguments = ["--obligation", "permit-gate-inspection", "--origin", "agent",
                "--reviewer", "synthetic-test-agent", "--rationale", "Synthetic inspection control; not human approval.",
                "--evidence", System.IO.Path.Combine(Directory, "primitives", "permit-gate", "inspection.md")];
            if (receipt != null) arguments.AddRange(["--evidence", receipt]);
            return Cli("record-review", [.. arguments]);
        }

        public string Status() => ProjectAcceptance.ReviewStatus(Path, ProjectInputs.Load(Path).Project, "permit-gate-inspection");
    }
}
