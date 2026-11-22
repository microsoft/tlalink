using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class SynchronizationInstanceTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SynchronizationInstances");

    private static readonly (string Mutant, string File, string[] Codes, string Code, string Text)[] Mutants = [
        ("unbound-site.cs", "Pump.cs", ["SR108"], "SR108",
            "M:InstanceFixture.Pump.Abort: unbound site: M:InstanceFixture.Gate.Close (close) of primitive gate on F:InstanceFixture.Pump.admission"),
        ("local-receiver.cs", "Pump.cs", ["SR111"], "SR111", "unresolved instance receiver: M:InstanceFixture.Gate.Exit (release) of primitive gate is applied to local entered"),
        ("missing-instance.tla", "Pump.tla", ["SR107", "SR108"], "SR107", "undeclared instance: field F:InstanceFixture.Pump.stopped of primitive latch"),
        ("wrong-operator.tla", "Pump.tla", ["SR109"], "SR109", "bind pump::exit (operator Leave) covers M:InstanceFixture.Gate.Exit (release) of primitive gate on F:InstanceFixture.Pump.admission but does not reference Gate!Exit."),
        ("wait-predicate.tla", "Pump.tla", ["SR109"], "SR109", "bind pump::resume (operator Resume) covers M:InstanceFixture.Latch.WaitAsync (wait) of primitive latch on F:InstanceFixture.Pump.stopped but does not reference Latch!CanWait."),
        ("stale-instance.tla", "Pump.tla", ["SR110"], "SR110", "instance pump::limit (Pump.tla): F:InstanceFixture.Pump.limit is not a region field whose type is a declared primitive."),
        ("wrong-kind.tla", "Pump.tla", ["SR110"], "SR110", "instance pump::stopped (Pump.tla): Gate instantiates TlaLinkAdmissionGate, not TlaLinkLatch.tla of primitive latch."),
        ("bad-expression.tla", "Pump.tla", ["SR110"], "SR110", "instance pump::admission: model expression <<\"admission\" does not parse in Pump with index names bound")
    ];

    public static void RegisterCore(Action<string, Action> test, string work)
    {
        test("instance annotations validate their schema and attach only before INSTANCE definitions", () =>
        {
            const string valid = "@link {\"instance\":[{\"id\":\"pump::admission\",\"field\":\"F:InstanceFixture.Pump.admission\"," +
                "\"kind_instance\":\"Gate\",\"model\":\"<<\\\"admission\\\", s>>\",\"index\":[\"s\"]}]}";
            InstanceAnnotation instance = (InstanceAnnotation)Markup.Parse(valid, 0, 0).Single().Declaration;
            CoreTests.True(instance.KindInstance == "Gate" && instance.Index!.Single() == "s" && instance.Model == "<<\"admission\", s>>");
            foreach (string invalid in new[] {
                valid.Replace("pump::admission", "admission", StringComparison.Ordinal),
                valid.Replace("F:InstanceFixture", "T:InstanceFixture", StringComparison.Ordinal),
                valid.Replace("\"Gate\"", "\"Gate!X\"", StringComparison.Ordinal),
                valid.Replace("[\"s\"]", "[\"s\",\"s\"]", StringComparison.Ordinal),
                valid.Replace("[\"s\"]", "[]", StringComparison.Ordinal),
                valid.Replace(", s>>", ",\\n s>>", StringComparison.Ordinal) })
                CoreTests.ThrowsAny<InputException>(() => Markup.Parse(invalid, 0, 0));
            string text = "(* " + valid + " *)\nGate == INSTANCE Kind\nOp == TRUE\n";
            List<(int Start, int End, string Content)> comments = Markup.TlaComments(text);
            int op = text.IndexOf("Op ==", StringComparison.Ordinal), gate = text.IndexOf("Gate ==", StringComparison.Ordinal);
            NativeDeclarationSpan span = new(new("Op", "M", "operator", new("M.tla", 3, 3, 1), "Op == TRUE"), op, op + 10);
            CoreTests.Equal(0, FormalCorrespondence.Attach("M.tla", text, [span], comments, instances: true).Count);
            CoreTests.ThrowsAny<InputException>(() => FormalCorrespondence.Attach("M.dfy", text, [span], comments));
            string mixed = text.Replace("{\"instance\":", "{\"bind\":[{\"id\":\"pump::x\"}],\"instance\":", StringComparison.Ordinal);
            CoreTests.ThrowsAny<InputException>(() => FormalCorrespondence.Attach("M.tla", mixed, [span with {
                Start = mixed.IndexOf("Op ==", StringComparison.Ordinal), End = mixed.IndexOf("Op ==", StringComparison.Ordinal) + 10 }],
                Markup.TlaComments(mixed), instances: true));
            TlaInstance definition = new("Gate", "Kind", false, gate, gate + 21, 2, 1, 2, 21);
            FormalInstanceDeclaration attached = FormalCorrespondence.Instances("M.tla", text, [definition], comments).Single();
            CoreTests.True(attached.Definition == "Gate" && attached.Module == "Kind" && attached.Location.Line == 2);
            CoreTests.ThrowsAny<InputException>(() => FormalCorrespondence.Instances("M.tla", text, [], comments));
            CSharpScope csharp = CSharpScope.Parse([WriteCSharp(work, "/* " + valid + " */\nclass C { }\n")]);
            CoreTests.True(csharp.Diagnostics.Any(d => d.Code == "TL001" && d.Message.Contains("formal comments", StringComparison.Ordinal)));
        });
        test("a region scope with TLA+ targets needs SANY for its instance and site checks", () =>
        {
            string directory = Copy(work, "instances-no-jar");
            InputException error = Catch<InputException>(() => Analyze(directory, new(FilePaths.Comparer), null));
            CoreTests.True(error.Code == ErrorCode.WorkflowPrerequisiteMissing && error.Message.Contains("TLA+ targets", StringComparison.Ordinal));
            CoreTests.True(SynchronizationWorkflow.NeedsJar(Path.Combine(directory, "scope.json"), new(FilePaths.Comparer)));
        });
    }

    public static void RegisterNative(Action<string, Action> test, string root, string work)
    {
        string jar = TestTools.Jar(root);
        TlaDocument Parse(string model, IReadOnlyList<string> path) => TlaBackend.Parse(model, jar, 60, path);
        test("SANY reports INSTANCE definitions and the kind operators each definition references", () =>
        {
            string directory = Copy(work, "instances-index");
            TlaDocument document = Parse(Path.Combine(directory, "Pump.tla"), [Primitives.LibraryDirectory]);
            CoreTests.True(document.ParserSucceeded);
            CoreTests.True(document.Instances.Select(i => i.Name + ":" + i.Module).SequenceEqual(["Gate:TlaLinkAdmissionGate", "Latch:TlaLinkLatch"]));
            CoreTests.True(document.Text[document.Instances[0].Start..document.Instances[0].End].StartsWith("Gate == INSTANCE", StringComparison.Ordinal));
            CoreTests.True(document.References.Contains(new("Leave", "Exit", "Gate", "TlaLinkAdmissionGate")));
            CoreTests.True(document.References.Contains(new("Next", "Leave", null, "Pump")));
            CoreTests.True(!document.References.Any(r => r.From.Contains('!', StringComparison.Ordinal)));
        });
        test("the clean instance fixture declares every instance, binds every site and matches every contract", () =>
        {
            string directory = Copy(work, "instances-clean");
            Dictionary<string, string> pins = new(FilePaths.Comparer);
            SynchronizationRegionReport report = Analyze(directory, pins, Parse);
            if (report.Status != "passed") throw new InvalidOperationException(JsonSerializer.Serialize(report.Diagnostics, Files.Json));
            RegionCoverage coverage = report.Coverage!;
            CoreTests.True(coverage.Instances.Select(i => i.Field + ":" + i.Status + ":" + i.Bound).SequenceEqual([
                "F:InstanceFixture.Pump.admission:declared:4", "F:InstanceFixture.Pump.stopped:declared:2"]));
            CoreTests.True(coverage.Sites.Count == 6 && coverage.Sites.All(s => s.Status == "bound" && s.Bindings.Count == 1));
            CoreTests.True(coverage.Contracts.Count == 6 && coverage.Contracts.All(c => c.Status == "matched"));
            CoreTests.True(pins.ContainsKey(Path.Combine(directory, "Pump.tla")) &&
                pins.Keys.Any(k => Path.GetFileName(k) == "TlaLinkAdmissionGate.tla"));
            CoreTests.True(!Directory.EnumerateDirectories(AppContext.BaseDirectory, ".tlalink-instances-*").Any());
        });
        test("named instance and site mutants fail with their exact diagnostic", () =>
        {
            foreach (var (mutant, file, codes, code, text) in Mutants)
            {
                string directory = Copy(work, "instances-" + Path.GetFileNameWithoutExtension(mutant));
                File.Copy(Path.Combine(directory, "mutants", mutant), Path.Combine(directory, file), overwrite: true);
                SynchronizationRegionReport report = Analyze(directory, new(FilePaths.Comparer), Parse);
                if (report.Status != "rejected" || report.Diagnostics.Any(d => !codes.Contains(d.Code)) ||
                    report.Diagnostics.Count(d => d.Code == code && d.Message.Contains(text, StringComparison.Ordinal)) != 1)
                    throw new InvalidOperationException(mutant + ": " + JsonSerializer.Serialize(report.Diagnostics, Files.Json));
            }
        });
        test("binds of targets without an instance for a field neither cover its sites nor face its contract checks", () =>
        {
            string directory = Copy(work, "instances-coarse");
            File.Copy(Path.Combine(directory, "mutants", "coarse-scope.json"), Path.Combine(directory, "scope.json"), overwrite: true);
            SynchronizationRegionReport report = Analyze(directory, new(FilePaths.Comparer), Parse);
            if (report.Status != "passed") throw new InvalidOperationException(JsonSerializer.Serialize(report.Diagnostics, Files.Json));
            RegionCoverage coverage = report.Coverage!;
            CoreTests.True(coverage.Sites.Count == 6 && coverage.Sites.All(s => s.Status == "bound" &&
                s.Bindings.All(b => b.StartsWith("pump::", StringComparison.Ordinal))));
            CoreTests.True(coverage.Contracts.Count == 6 && !coverage.Contracts.Any(c => c.Binding == "coarse::stop"));
            File.Copy(Path.Combine(directory, "mutants", "close-unbound.tla"), Path.Combine(directory, "Pump.tla"), overwrite: true);
            report = Analyze(directory, new(FilePaths.Comparer), Parse);
            if (report.Status != "rejected" || report.Diagnostics.Count != 1 || report.Diagnostics[0].Code != "SR108" ||
                !report.Diagnostics[0].Message.Contains("unbound site: M:InstanceFixture.Gate.Close (close) of primitive gate on " +
                    "F:InstanceFixture.Pump.admission is not selected by any bind source selector of a TLA+ target that declares an instance",
                    StringComparison.Ordinal))
                throw new InvalidOperationException(JsonSerializer.Serialize(report.Diagnostics, Files.Json));
        });
        test("exclusions cover instance fields and their sites, and individual sites", () =>
        {
            string directory = Copy(work, "instances-excluded");
            File.Copy(Path.Combine(directory, "mutants", "unbound-site.cs"), Path.Combine(directory, "Pump.cs"), overwrite: true);
            File.Copy(Path.Combine(directory, "mutants", "excluded.tla"), Path.Combine(directory, "Pump.tla"), overwrite: true);
            SynchronizationRegionReport report = Analyze(directory, new(FilePaths.Comparer), Parse);
            if (report.Status != "passed") throw new InvalidOperationException(JsonSerializer.Serialize(report.Diagnostics, Files.Json));
            RegionCoverage coverage = report.Coverage!;
            CoreTests.True(coverage.Instances.Select(i => i.Status + ":" + i.Bound + ":" + i.Excluded).SequenceEqual(["declared:4:1", "excluded:0:2"]));
            CoreTests.Equal(3, coverage.Sites.Count(s => s.Status == "excluded"));
        });
        test("check-synchronization renders instance coverage and contract status with pinned formal inputs", () =>
        {
            string directory = Copy(work, "instances-cli");
            string scope = Path.Combine(directory, "scope.json");
            var passed = TestCli.Run(["check-synchronization", "--scope", scope, "--jar", jar, "--format", "summary"]);
            CoreTests.Equal(0, passed.Code);
            using (JsonDocument summary = JsonDocument.Parse(passed.Json))
            {
                JsonElement coverage = summary.RootElement.GetProperty("region").GetProperty("coverage");
                CoreTests.True(coverage.GetProperty("bound").GetInt32() == 6 && coverage.GetProperty("unbound").GetInt32() == 0);
                CoreTests.Equal(6, coverage.GetProperty("contracts").EnumerateArray().Count(c => c.GetProperty("status").GetString() == "matched"));
            }
            var full = TestCli.Run(["check-synchronization", "--scope", scope, "--jar", jar]);
            SynchronizationResult result = (SynchronizationResult)AnalysisDocuments.Parse(full.Json);
            CoreTests.True(result.Fingerprints.ContainsKey(Path.Combine(directory, "Pump.tla")) && result.Region.Coverage!.Sites.Count == 6);
            File.Copy(Path.Combine(directory, "mutants", "wrong-operator.tla"), Path.Combine(directory, "Pump.tla"), overwrite: true);
            CoreTests.Equal(1, TestCli.Run(["check-synchronization", "--scope", scope, "--jar", jar]).Code);
            CoreTests.Equal(2, TestCli.Run(["check-synchronization", "--scope", scope]).Code);
        });
    }

    private static string Copy(string work, string name)
    {
        string directory = Path.Combine(work, name + "-" + Guid.NewGuid().ToString("N"));
        StageEvidenceTests.Copy(Fixtures, directory);
        return directory;
    }

    private static string WriteCSharp(string work, string text)
    {
        Directory.CreateDirectory(work);
        string path = Path.Combine(work, "instance-" + Guid.NewGuid().ToString("N") + ".cs");
        File.WriteAllText(path, text);
        return path;
    }

    private static SynchronizationRegionReport Analyze(string directory, Dictionary<string, string> pins,
        Func<string, IReadOnlyList<string>, TlaDocument>? parse) =>
        SynchronizationRegion.Analyze(Path.Combine(directory, "scope.json"), [], null, "14", pins, parse);

    private static T Catch<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
