using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class SynchronizationRegionTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SynchronizationRegion");

    private static readonly (string Mutant, string File, string Code, string Text)[] Mutants = [
        ("raw-interlocked", "Connection.cs", "SR100", "raw synchronization type System.Threading.Interlocked"),
        ("raw-tcs", "Connection.cs", "SR100", "raw synchronization type System.Threading.Tasks.TaskCompletionSource"),
        ("undeclared-lock", "Connection.cs", "SR100", "lock (this.pending) targets an object that is not a declared lock"),
        ("lock-order", "Connection.cs", "SR103", "acquires F:SyncFixture.Connection.stateLock inside lock F:SyncFixture.Connection.tableLock"),
        ("wait-in-lock", "Connection.cs", "SR103", "via M:SyncFixture.Connection.Watch); it is a wait operation"),
        ("delegate-in-lock", "Connection.cs", "SR103", "invokes delegate this.Sent?.Invoke inside lock F:SyncFixture.Connection.stateLock"),
        ("unsafe-environment-in-lock", "Connection.cs", "SR103", "M:SyncFixture.IFrameSink.Flush of frame-sink inside lock"),
        ("cancel-in-lock", "Connection.cs", "SR103", "M:System.Threading.CancellationTokenSource.Cancel of primitive shutdown inside lock"),
        ("unawaited-task", "Connection.cs", "SR104", "M:SyncFixture.Connection.CloseAsync: undeclared spawn (unawaited task)"),
        ("undeclared-spawn", "Connection.cs", "SR104", "undeclared spawn (ThreadPool.QueueUserWorkItem)"),
        ("async-void", "Connection.cs", "SR104", "undeclared spawn (async void call)"),
        ("stale-spawn", "Connection.cs", "SR105", "The spawn site in M:SyncFixture.Connection.Start does not resolve"),
        ("await-task-field", "Connection.cs", "SR102", "awaits field F:SyncFixture.Connection.flushed"),
        ("undeclared-operation", "Connection.cs", "SR101", "M:System.Threading.CancellationTokenSource.CancelAfter(System.Int32) of primitive shutdown"),
        ("latch-without-async", "Latch.cs", "SR106", "without a constant TaskCreationOptions.RunContinuationsAsynchronously. Primitive closed claims signals-async"),
        ("gate-blocking-result", "Gate.cs", "SR106", "Blocking read of Task.Result. Primitive sends claims non-blocking")
    ];

    public static void RegisterCore(Action<string, Action> test, string work)
    {
        test("synchronization declarations validate regions, lock order, spawns and waits", () =>
        {
            string directory = Copy(work, "region-declarations");
            CorrespondenceConfiguration scope = CorrespondenceInputs.Load(Path.Combine(directory, "scope.json"), new(FilePaths.Comparer));
            CoreTests.Equal(0, scope.Targets.Count);
            CoreTests.True(scope.Synchronization!.Locks!.Count == 3 && scope.Omissions!.Count == 0);
            CoreTests.Equal(0, scope.Synchronization.Locks.Single(l => l.Field == "F:SyncFixture.Ledger.sync").MayHold!.Count);
            File.Copy(Declaration(directory, "empty-lists"), Path.Combine(directory, "scope.json"), overwrite: true);
            CorrespondenceConfiguration empty = CorrespondenceInputs.Load(Path.Combine(directory, "scope.json"), new(FilePaths.Comparer));
            CoreTests.True(empty.Synchronization!.Spawns!.Count == 0 && empty.Synchronization.Waits!.Count == 0 &&
                empty.Synchronization.Environments!.Count == 0);
            foreach (string name in new[] { "cyclic-locks", "unknown-may-hold", "bad-disposition", "empty-reason", "waits-without-region" })
            {
                File.Copy(Declaration(directory, name), Path.Combine(directory, "scope.json"), overwrite: true);
                InputException error = Catch<InputException>(() => CorrespondenceInputs.Load(Path.Combine(directory, "scope.json"), new(FilePaths.Comparer)));
                CoreTests.True(error.Code == ErrorCode.InvalidAnnotation);
                if (name == "cyclic-locks") CoreTests.True(error.Message.Contains("cyclic", StringComparison.Ordinal));
            }
            File.Copy(Path.Combine(Fixtures, "scope.json"), Path.Combine(directory, "scope.json"), overwrite: true);
            File.Delete(Path.Combine(directory, "Connection.cs"));
            CoreTests.ThrowsAny<InputException>(() => CorrespondenceInputs.Load(Path.Combine(directory, "scope.json"), new(FilePaths.Comparer)));
        });
        test("environment manifests validate kinds, roles, types, obligations and modules", () =>
        {
            string directory = Copy(work, "region-environments");
            string path = Path.Combine(directory, "environments", "frame-sink.environment.json");
            Dictionary<string, string> pins = new(FilePaths.Comparer);
            EnvironmentManifest valid = Environments.Load(path, pins);
            CoreTests.Equal("interface", valid.EnvironmentKind);
            CoreTests.True(pins.ContainsKey(path) && valid.Operations.Count(o => o.LockSafe) == 1);
            foreach (string name in new[] { "scheduler-kind", "unknown-role", "no-obligations", "actions-without-module", "method-type" })
                CoreTests.ThrowsAny<InputException>(() => Environments.Load(Environment(directory, name), new(FilePaths.Comparer)));
            CoreTests.Equal("../FrameSink.tla", Environments.Load(Environment(directory, "module"), new(FilePaths.Comparer)).Module);
            File.Delete(Path.Combine(directory, "FrameSink.tla"));
            CoreTests.ThrowsAny<InputException>(() => Environments.Load(Environment(directory, "module"), new(FilePaths.Comparer)));
        });
        test("library primitives declare metadata types, used members and inspection-only support", () =>
        {
            string directory = Copy(work, "region-library");
            PrimitiveManifest library = Primitives.Load(Path.Combine(directory, "primitives", "shutdown.primitive.json"), new(FilePaths.Comparer));
            CoreTests.True(Primitives.IsLibrary(library) && library.Verification.Scenarios.Count == 0);
            CSharpScope metadata = CSharpScope.Parse([], annotations: false);
            CoreTests.Equal(3, Primitives.Members(metadata, library).Count);
            CoreTests.Equal(0, SynchronizationGuarantees.Check(metadata, library).Count);
            CoreTests.Equal("outstanding", SynchronizationGuarantees.Check(metadata, library with { Guarantees = ["signals-async"] }).Single().Status);
            CoreTests.ThrowsAny<InputException>(() => Primitives.Members(metadata, library with {
                Operations = [.. library.Operations, new("M:System.Threading.CancellationToken.ThrowIfCancellationRequested", "query")] }));
            CoreTests.Equal(4, Primitives.Members(metadata, library with {
                Operations = [.. library.Operations, new("M:System.IDisposable.Dispose", "close", ["Cancel"], InlineCallbacks: true)] }).Count);
            CoreTests.ThrowsAny<InputException>(() => Primitives.Validate(library with {
                Operations = [.. library.Operations.Select(o => o with { InlineCallbacks = false })] }));
            CoreTests.ThrowsAny<InputException>(() => Primitives.Validate(library with {
                Operations = [.. library.Operations.Select(o => o.Role == "query" ? o with { InlineCallbacks = true } : o)] }));
            CoreTests.ThrowsAny<InputException>(() => Primitives.Validate(library with { Guarantees = ["inline-callbacks"] }));
            CSharpScope source = CSharpScope.Parse([Path.Combine(directory, "Gate.cs")], annotations: false);
            CoreTests.ThrowsAny<InputException>(() => Primitives.Members(source, library with { Type = "T:SyncFixture.Gate" }));
            PrimitiveManifest gate = Primitives.Load(Path.Combine(directory, "primitives", "gate.primitive.json"), new(FilePaths.Comparer));
            CoreTests.ThrowsAny<InputException>(() => Primitives.Validate(gate with { Verification = gate.Verification with { Scenarios = [] } }));
            CoreTests.ThrowsAny<InputException>(() => Primitives.Members(metadata, gate with { Sources = [] }));
            List<GuaranteeStatus> checks = SynchronizationGuarantees.Check(source, gate);
            CoreTests.True(checks.All(g => g.Status == "discharged") && checks.Count == 2);
        });
        test("the clean synthetic region passes every region check", () =>
        {
            string directory = Copy(work, "region-clean");
            Dictionary<string, string> pins = new(FilePaths.Comparer);
            SynchronizationRegionReport report = Analyze(directory, pins);
            CoreTests.Equal("passed", report.Status);
            CoreTests.Equal(0, report.Diagnostics.Count);
            CoreTests.Equal(new RegionSites(5, 5, 2, 18, 6, 1), report.Sites);
            CoreTests.True(pins.Keys.Any(k => Path.GetFileName(k) == "Connection.cs") && pins.Keys.Any(k => Path.GetFileName(k) == "Gate.cs") &&
                pins.Keys.Any(k => Path.GetFileName(k) == "frame-sink.environment.json"));
            RegionPrimitive gate = report.Primitives.Single(p => p.Id == "sends");
            CoreTests.True(gate.Origin == "source" && gate.Guarantees.All(g => g.Status == "discharged"));
            RegionPrimitive shutdown = report.Primitives.Single(p => p.Id == "shutdown");
            CoreTests.True(shutdown.Origin == "library" && shutdown.Guarantees.Count == 0);
            RegionEnvironment sink = report.Environments.Single(e => e.Id == "frame-sink");
            CoreTests.Equal("unsupported-assumption", sink.Support);
            CoreTests.Equal("M:SyncFixture.IFrameSink.Post(System.Int32)", sink.LockSafe.Single());
            CoreTests.Equal("M:SyncFixture.IJournal.Append(System.Int32)", report.Environments.Single(e => e.Id == "journal").LockSafe.Single());
            CoreTests.True(report.Coverage == null);
        });
        test("only primitives with waiters must claim signals-async to resume under a lock", () =>
        {
            string directory = Copy(work, "region-waiters");
            File.Copy(Path.Combine(directory, "mutants", "ledger-latch-in-lock.cs"), Path.Combine(directory, "Ledger.cs"), overwrite: true);
            CoreTests.Equal("passed", Analyze(directory, new(FilePaths.Comparer)).Status);
            File.Copy(Path.Combine(directory, "primitives", "mutants", "latch-unclaimed.primitive.json"),
                Path.Combine(directory, "primitives", "latch.primitive.json"), overwrite: true);
            Issue issue = Analyze(directory, new(FilePaths.Comparer)).Diagnostics.Single();
            CoreTests.True(issue.Code == "SR103" && issue.Message.Contains("M:SyncFixture.Latch.TrySet of primitive closed inside lock " +
                "F:SyncFixture.Ledger.sync", StringComparison.Ordinal) && issue.Message.Contains("role signal can resume waiters inline", StringComparison.Ordinal));
        });
        test("named region and primitive mutants fail with their exact diagnostic", () =>
        {
            foreach (var (mutant, file, code, text) in Mutants)
            {
                string directory = Copy(work, "region-" + mutant);
                File.Copy(Path.Combine(directory, "mutants", mutant + ".cs"), Path.Combine(directory, file), overwrite: true);
                SynchronizationRegionReport report = Analyze(directory, new(FilePaths.Comparer));
                if (report.Status != "rejected" || report.Diagnostics.Any(d => d.Code != code) ||
                    report.Diagnostics.Count(d => d.Message.Contains(text, StringComparison.Ordinal)) != 1)
                    throw new InvalidOperationException(mutant + ": " + JsonSerializer.Serialize(report.Diagnostics, Files.Json));
                Issue issue = report.Diagnostics.Single(d => d.Message.Contains(text, StringComparison.Ordinal));
                CoreTests.True(issue.Location != null && issue.Location.Line > 0 &&
                    issue.Message.StartsWith(code == "SR105" ? "scope.json: " : file + ":", StringComparison.Ordinal));
            }
        });
        test("stale lock and wait declarations and unresolved environment members are reported", () =>
        {
            string directory = Copy(work, "region-stale");
            File.Copy(Declaration(directory, "stale-lock"), Path.Combine(directory, "scope.json"), overwrite: true);
            SynchronizationRegionReport locks = Analyze(directory, new(FilePaths.Comparer));
            CoreTests.Equal("SR105", locks.Diagnostics.Single().Code);
            CoreTests.True(locks.Diagnostics.Single().Message.Contains("F:SyncFixture.Connection.pending is not the target of any lock", StringComparison.Ordinal));
            File.Copy(Declaration(directory, "stale-wait"), Path.Combine(directory, "scope.json"), overwrite: true);
            SynchronizationRegionReport waits = Analyze(directory, new(FilePaths.Comparer));
            CoreTests.True(waits.Diagnostics.Any(d => d.Code == "SR105" && d.Message.Contains("already a declared wait (primitive wait)", StringComparison.Ordinal)));
            CoreTests.True(waits.Diagnostics.Any(d => d.Code == "SR102" && d.Message.Contains("awaits field F:SyncFixture.Connection.pump", StringComparison.Ordinal)));
            File.Copy(Path.Combine(Fixtures, "scope.json"), Path.Combine(directory, "scope.json"), overwrite: true);
            _ = Environment(directory, "unknown-member");
            SynchronizationRegionReport members = Analyze(directory, new(FilePaths.Comparer));
            CoreTests.True(members.Diagnostics.Single().Code == "SR105" &&
                members.Diagnostics.Single().Message.Contains("M:SyncFixture.IFrameSink.Drain", StringComparison.Ordinal));
            _ = Environment(directory, "module");
            InputException jar = Catch<InputException>(() => Analyze(directory, new(FilePaths.Comparer)));
            CoreTests.True(jar.Code == ErrorCode.WorkflowPrerequisiteMissing);
            File.WriteAllText(Path.Combine(directory, "Connection.cs"), File.ReadAllText(Path.Combine(directory, "Connection.cs")) + "\nclass Broken {");
            _ = Environment(directory, "evidence");
            SynchronizationRegionReport incomplete = Analyze(directory, new(FilePaths.Comparer));
            CoreTests.True(incomplete.Status == "incomplete" && incomplete.Diagnostics.All(d => d.Code == "SR200"));
        });
        test("check-synchronization reports passed, rejected and incomplete outcomes with pinned inputs", () =>
        {
            string directory = Copy(work, "region-cli");
            var passed = TestCli.Run(["check-synchronization", "--scope", Path.Combine(directory, "scope.json")]);
            CoreTests.Equal(0, passed.Code);
            SynchronizationResult result = (SynchronizationResult)AnalysisDocuments.Parse(passed.Json);
            CoreTests.True(result.Status == "passed" && result.Fingerprints.ContainsKey(Path.Combine(directory, "scope.json")));
            File.Copy(Path.Combine(directory, "mutants", "raw-interlocked.cs"), Path.Combine(directory, "Connection.cs"), overwrite: true);
            CoreTests.Equal(1, TestCli.Run(["check-synchronization", "--scope", Path.Combine(directory, "scope.json"), "--format", "summary"]).Code);
            File.WriteAllText(Path.Combine(directory, "Connection.cs"), "class Broken {");
            CoreTests.Equal(2, TestCli.Run(["check-synchronization", "--scope", Path.Combine(directory, "scope.json")]).Code);
        });
        test("a synchronization stage runs without a model checker and its receipt is accepted", () =>
        {
            string directory = Copy(work, "region-workflow");
            string control = Path.Combine(directory, "control");
            StageEvidenceTests.Copy(Fixtures, control);
            File.Copy(Path.Combine(control, "mutants", "raw-interlocked.cs"), Path.Combine(control, "Connection.cs"), overwrite: true);
            string path = Path.Combine(directory, "project.json");
            Files.WriteNew(path, new ProjectAuthoring(Schema.Version, "analysis-project", "region-project", new(),
                new("net10.0", "14", "enable", false, true, []), "requirements.json",
                [new("region", [new("connection", "synchronization", TimeoutSeconds: 60, Scope: "scope.json"),
                    new("raw-control", "synchronization", TimeoutSeconds: 60, Scope: Path.Combine("control", "scope.json"))])],
                "reviews", AnnotationVersion: Markup.Version));
            Files.WriteNew(Path.Combine(directory, "requirements.json"), new AcceptancePolicy(Schema.Version, "acceptance-policy", "region-project", [
                new("connection-checked", "region", "connection", "passed"),
                new("raw-control-rejected", "region", "raw-control", "rejected", "SR100",
                    DiagnosticText: "raw synchronization type System.Threading.Interlocked", DiagnosticCount: 1)]));
            using (JsonDocument plan = JsonDocument.Parse(TestCli.Run(["plan", "--project", path, "--format", "summary"]).Json))
            {
                CoreTests.Equal("ready", plan.RootElement.GetProperty("status").GetString());
                JsonElement[] regions = [.. plan.RootElement.GetProperty("regions").EnumerateArray()];
                CoreTests.True(regions.Length == 2 && regions.All(r => r.GetProperty("role").GetString() == "stage"));
                CoreTests.Equal(1, regions.Count(r => r.GetProperty("status").GetString() == "rejected"));
            }
            string run = Path.Combine(directory, "runs", "region");
            CoreTests.Equal(0, TestCli.Run(["run", "--project", path, "--scenario", "region", "--output", run]).Code);
            WorkflowRun recorded = ProjectInputs.Read<WorkflowRun>(Path.Combine(run, "run.json"));
            CoreTests.Equal("passed", recorded.Stages.Single(s => s.Id == "connection").Outcome);
            CoreTests.Equal("rejected", recorded.Stages.Single(s => s.Id == "raw-control").Outcome);
            CoreTests.True(recorded.Fingerprints.Keys.Any(k => k == Path.Combine(control, "Connection.cs")));
            CoreTests.Equal(0, TestCli.Run(["accept", "--project", path, "--run", run]).Code);
            string report = Path.Combine(directory, "report.md");
            CoreTests.Equal(0, TestCli.Run(["report", "--project", path, "--run", run, "--output", report]).Code);
            EvidenceReportNode node = ProjectInputs.Read<EvidenceReportManifest>(report + ".json").Nodes.Single(n => n.Title == "region: connection");
            CoreTests.True(node.Assumptions.Any(a => a.StartsWith("Environment frame-sink (interface): Post buffers", StringComparison.Ordinal)));
            CoreTests.True(node.Gaps.Any(g => g.Contains("frame-sink obligations have no declared supporting evidence", StringComparison.Ordinal)));
            File.AppendAllText(Path.Combine(directory, "Connection.cs"), "\n");
            CoreTests.Equal(2, TestCli.Run(["accept", "--project", path, "--run", run]).Code);
            _ = Environment(directory, "evidence");
            var unknown = TestCli.Run(["doctor", "--project", path]);
            CoreTests.True(unknown.Code != 0 && unknown.Json.Contains("transport-conformance", StringComparison.Ordinal));
        });
    }

    public static void RegisterNative(Action<string, Action> test, string root, string work)
    {
        test("environment module actions and waits resolve through SANY", () =>
        {
            string directory = Copy(work, "region-module");
            string jar = TestTools.Jar(root);
            TlaDocument Parse(string model, IReadOnlyList<string> path) => TlaBackend.Parse(model, jar, 60, path);
            _ = Environment(directory, "module");
            Dictionary<string, string> pins = new(FilePaths.Comparer);
            CoreTests.Equal("passed", Analyze(directory, pins, Parse).Status);
            CoreTests.True(pins.ContainsKey(Path.Combine(directory, "FrameSink.tla")));
            _ = Environment(directory, "unknown-operator");
            Issue issue = Analyze(directory, new(FilePaths.Comparer), Parse).Diagnostics.Single();
            CoreTests.True(issue.Code == "SR105" && issue.Message.Contains("Operator CanDrain", StringComparison.Ordinal));
        });
    }

    internal static string Copy(string work, string name)
    {
        string directory = Path.Combine(work, name + "-" + Guid.NewGuid().ToString("N"));
        StageEvidenceTests.Copy(Fixtures, directory);
        return directory;
    }

    private static SynchronizationRegionReport Analyze(string directory, Dictionary<string, string> pins,
        Func<string, IReadOnlyList<string>, TlaDocument>? parse = null) =>
        SynchronizationRegion.Analyze(Path.Combine(directory, "scope.json"), [], null, "14", pins, parse);

    private static string Declaration(string directory, string name) => Path.Combine(directory, "declarations", name + ".scope.json");

    /// <summary>Installs a named environment mutant in place of the declared manifest and returns its path.</summary>
    private static string Environment(string directory, string name)
    {
        string path = Path.Combine(directory, "environments", "frame-sink.environment.json");
        File.Copy(Path.Combine(directory, "environments", "mutants", name + ".environment.json"), path, overwrite: true);
        return path;
    }

    private static T Catch<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
