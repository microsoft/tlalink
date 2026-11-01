using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class FormalOwnedLinksTests
{
    public static void RegisterCore(Action<string, Action> test, string work)
    {
        LinkInspectionTests.Register(test, work);
        test("archived correspondence reconstruction retains the explicit selected ID scope", () =>
        {
            string path = Path.Combine(work, "ArchiveSelection.cs");
            File.WriteAllText(path, "public class ArchiveSelection { public void Commit() {} }");
            CSharpScope source = CSharpScope.Parse([path]);
            List<FormalLinkDeclaration> formal = [
                new(new("archive::chosen", Source: new("M:ArchiveSelection.Commit")),
                    new("Chosen", "Archive", "lemma", new("Archive.dfy", 1, 1), ""), 0),
                new(new("archive::unselected", Source: new("M:UnavailableDependency.Reset")),
                    new("Unselected", "Archive", "lemma", new("Dependency.dfy", 1, 1), ""), 0)
            ];
            Dictionary<string, ArchivedSourceBinding> Reconstruct(params string[] ids) =>
                CorrespondenceEvidence.Bindings([path], ids, source.Fingerprints, Files.Read, formal,
                    hash => source.Fingerprints.First(p => p.Value == hash).Key);
            Dictionary<string, ArchivedSourceBinding> selected = Reconstruct("archive::chosen");
            CoreTests.Equal(1, selected.Count);
            CoreTests.True(selected.ContainsKey("archive::chosen"));
            CoreTests.True(!selected.ContainsKey("archive::unselected"));
            CoreTests.Equal(0, Reconstruct().Count);
            CoreTests.ThrowsAny<InputException>(() => Reconstruct("archive::unselected"));
        });
        test("formal comments resolve unannotated source declarations and repeated syntax explicitly", () =>
        {
            string path = Path.Combine(work, "FormalSource.cs");
            File.WriteAllText(path, "class FormalSource { int count; public void Step() { count++; count++; } }");
            CSharpScope source = CSharpScope.Parse([path]);
            FormalLinkDeclaration Endpoint(PreparationTarget selector) => new(new("formal::step", Source: selector),
                new("Step", "Formal", "operator", new("Formal.tla", 1, 1), ""), 0);
            List<Issue> issues = [];
            CoreTests.ThrowsAny<InputException>(() => FormalCorrespondence.Match(source,
                [Endpoint(new("M:FormalSource.Step", "ExpressionStatement", "count++;"))], "formal", ["formal::step"], issues));
            var matches = FormalCorrespondence.Match(source, [
                Endpoint(new("M:FormalSource.Step", "ExpressionStatement", "count++;", Match: 2, ExpectedMatches: 2))
            ], "formal", ["formal::step"], issues);
            CoreTests.Equal(0, issues.Count);
            CoreTests.Equal(1, matches.Count);
            CoreTests.Equal(SourceBindingKind.Step, matches.Single().Implementation.Kind);
            CoreTests.True(source.Bindings.Single().Node.SpanStart > File.ReadAllText(path).IndexOf("count++;", StringComparison.Ordinal));
        });
        test("formal capture payloads attach to resolved CSharp statements and retain actual locks", () =>
        {
            string path = Path.Combine(work, "FormalCapture.cs");
            File.WriteAllText(path, "class FormalCapture { readonly object gate = new(); int value; void Step() { lock(gate) { value++; } } }");
            CSharpScope source = CSharpScope.Parse([path]);
            FormalLinkDeclaration link = new(new("capture::step", Source:
                new("M:FormalCapture.Step", "ExpressionStatement", "value++;")),
                new("Step", "Capture", "operator", new("Capture.tla", 1, 1), ""), 0,
                [new("capture::step", CapturePoint.Commit, CaptureClassification.Transition, Actor: "1", Operation: "1")]);
            FormalCorrespondence.Match(source, [link], "capture", ["capture::step"], []);
            CoreTests.Equal(CapturePoint.Commit, source.Bindings.Single().Capture!.Point);
            CoreTests.Equal("gate", source.Bindings.Single().Locks.Single());
            CoreTests.True(source.Diagnostics.All(d => d.Severity != "error"));
        });
        test("connections reject competing inline ownership and dangling formal captures", () =>
        {
            string path = Path.Combine(work, "ConflictingSource.cs");
            File.WriteAllText(path, """class ConflictingSource { /* @link {"bind":[{"id":"conflict::step"}]} */ void Step() {} }""");
            CSharpScope source = CSharpScope.Parse([path]);
            FormalLinkDeclaration formal = new(new("conflict::step", Source: new("M:ConflictingSource.Step")),
                new("Step", "Conflict", "operator", new("Conflict.tla", 1, 1), ""), 0);
            CoreTests.ThrowsAny<InputException>(() => FormalCorrespondence.Match(source, [formal], "conflict", ["conflict::step"], []));
            string text = """(* @link {"capture":[{"binding":"conflict::missing","point":"commit"}]} *) Step == TRUE""";
            CoreTests.ThrowsAny<InputException>(() => FormalCorrespondence.Attach("Conflict.tla", text,
                [new(new("Step", "Conflict", "operator", new("Conflict.tla", 1, 1), "Step == TRUE"),
                    text.IndexOf("Step ==", StringComparison.Ordinal), text.Length)], Markup.TlaComments(text)));
        });
    }

    public static void RegisterNative(Action<string, Action> test, string root, string work)
    {
        test("bulk link migration plans first preserves tokens and captures and is idempotent", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "link-migration")).FullName;
            string cs = Path.Combine(directory, "Gate.cs"), tla = Path.Combine(directory, "Gate.tla"), scope = Path.Combine(directory, "scope.json");
            string originalSource = """
                class Gate { int count; public void Step() {
                  /* @link {"bind":[{"id":"gate::step"}],"capture":[{"binding":"gate::step","point":"commit","classification":"transition","actor":"1","operation":"1"}]} */
                  count++;
                } }
                /* @link {"exclude":[{"id":"timer","from":["gate"],"reason":"Excluded helper implementation."}]} */
                class Timer {}
                """;
            File.WriteAllText(cs, originalSource);
            File.WriteAllText(tla, "---- MODULE Gate ----\nEXTENDS Naturals\nVARIABLE count\n(* @link {\"bind\":[{\"id\":\"gate::step\"}]} *)\nStep == count' = count + 1\n====\n");
            Files.WriteNew(scope, new CorrespondenceConfiguration(Schema.Version, "correspondence-scope", Markup.Version,
                [new("gate", "tla", [tla], RequiredBindings: ["gate::step"])], []));
            (int Code, string Json) Run(string action, params string[] args) =>
                TestCli.Run(["links", action, "--annotation-version", "4", "--scope", scope,
                    "--jar", TestTools.Jar(root), "--source", cs, .. args]);
            string before = Files.Read(tla);
            var plan = Run("migrate");
            if (plan.Code != 0) throw new InvalidOperationException(plan.Json);
            CoreTests.Equal(before, Files.Read(tla));
            CoreTests.Equal(originalSource, Files.Read(cs));
            using (JsonDocument report = JsonDocument.Parse(plan.Json))
            {
                CoreTests.True(report.RootElement.GetProperty("read_only").GetBoolean());
                CoreTests.Equal(1, report.RootElement.GetProperty("binding_count").GetInt32());
                CoreTests.Equal(1, report.RootElement.GetProperty("exclusion_count").GetInt32());
            }
            var applied = Run("migrate", "--apply", "true");
            if (applied.Code != 0) throw new InvalidOperationException(applied.Json);
            CoreTests.True(!Files.Read(cs).Contains("@link", StringComparison.Ordinal));
            CoreTests.True(Files.Read(tla).Contains("\"capture\"", StringComparison.Ordinal));
            CoreTests.Equal(0, Run("check", "--target", "gate").Code);
            CSharpScope current = CSharpScope.Parse([cs]);
            ModelScope model = ModelScope.Load([tla], TestTools.Jar(root), 30);
            CorrespondenceConfiguration configuration = CorrespondenceInputs.Load(scope, new(FilePaths.Comparer));
            model.BindSources(current, configuration, "gate");
            current.ApplyCorrespondence(configuration);
            int dispositions = current.Dispositions.Count, warnings = current.Diagnostics.Count;
            current.ApplyCorrespondence(configuration);
            CoreTests.Equal(dispositions, current.Dispositions.Count);
            CoreTests.Equal(warnings, current.Diagnostics.Count);
            using JsonDocument unchanged = JsonDocument.Parse(Run("migrate").Json);
            CoreTests.Equal(0, unchanged.RootElement.GetProperty("binding_count").GetInt32());
            CoreTests.Equal(0, unchanged.RootElement.GetProperty("files").GetArrayLength());
        });
        test("links CLI sets lists checks updates and removes native-owned source selectors", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "link-cli")).FullName;
            string cs = Path.Combine(directory, "Gate.cs"), tla = Path.Combine(directory, "Gate.tla"), scope = Path.Combine(directory, "scope.json");
            File.WriteAllText(cs, "class Gate { int count; public void Step() { count++; } }");
            File.WriteAllText(tla, "---- MODULE Gate ----\nEXTENDS Naturals\nVARIABLE count\nInit == count = 0\nStep == count' = count + 1\n====\n");
            Files.WriteNew(scope, new CorrespondenceConfiguration(Schema.Version, "correspondence-scope", Markup.Version,
                [new("gate", "tla", [tla], RequiredBindings: ["gate::step"])], []));
            (int Code, string Json) Run(string action, params string[] args) =>
                TestCli.Run(["links", action, "--annotation-version", "4", "--scope", scope, "--target", "gate",
                    "--jar", TestTools.Jar(root), "--source", cs, .. args]);
            var set = Run("set", "--binding", "gate::step", "--endpoint", "Step",
                "--symbol", "M:Gate.Step", "--syntax", "ExpressionStatement", "--tokens", "count++;");
            if (set.Code != 0) throw new InvalidOperationException(set.Json);
            CoreTests.True(!File.ReadAllText(cs).Contains("@link", StringComparison.Ordinal));
            CoreTests.Equal(0, Run("check").Code);
            var inspection = Run("inspect", "--binding", "gate::step");
            if (inspection.Code != 0) throw new InvalidOperationException(inspection.Json);
            using (JsonDocument inspected = JsonDocument.Parse(inspection.Json))
            {
                JsonElement endpoint = inspected.RootElement.GetProperty("endpoints")[0];
                CoreTests.True(endpoint.GetProperty("model").GetProperty("source").GetString()!.Contains("count' = count + 1", StringComparison.Ordinal));
                CoreTests.Equal("count++;", endpoint.GetProperty("links")[0].GetProperty("source").GetProperty("selected_text").GetString());
            }
            string markdown = Path.Combine(directory, "inspection.md");
            CoreTests.Equal(0, Run("inspect", "--endpoint", "Step", "--format", "markdown", "--output", markdown).Code);
            CoreTests.True(Files.Read(markdown).Contains("```csharp", StringComparison.Ordinal));
            CoreTests.Equal(0, Run("inspect", "--format", "text").Code);
            var focused = Run("inspect", "--symbol", "M:Gate.Step", "--capture-point", "none",
                "--section", "source", "--limit", "1");
            if (focused.Code != 0) throw new InvalidOperationException(focused.Json);
            using (JsonDocument projected = JsonDocument.Parse(focused.Json))
            {
                CoreTests.Equal(1, projected.RootElement.GetProperty("matched_endpoints").GetInt32());
                JsonElement endpoint = projected.RootElement.GetProperty("endpoints")[0];
                CoreTests.True(!endpoint.GetProperty("model").TryGetProperty("source", out _));
                CoreTests.Equal("count++;", endpoint.GetProperty("links")[0].GetProperty("source").GetProperty("selected_text").GetString());
            }
            CoreTests.Equal(2, Run("inspect", "--output", markdown, "--format", "markdown").Code);
            using (JsonDocument listed = JsonDocument.Parse(Run("list").Json))
                CoreTests.Equal("M:Gate.Step", listed.RootElement.GetProperty("links")[0].GetProperty("binding")
                    .GetProperty("source").GetProperty("symbol").GetString());
            CoreTests.Equal(0, Run("set", "--binding", "gate::step", "--symbol", "M:Gate.Step",
                "--syntax", "ExpressionStatement", "--tokens", "count++;", "--reason", "Updated inspection context.").Code);
            string before = File.ReadAllText(tla);
            CoreTests.Equal(2, Run("set", "--binding", "gate::step", "--symbol", "M:Gate.Missing").Code);
            CoreTests.Equal(before, File.ReadAllText(tla));
            CoreTests.Equal(0, Run("remove", "--binding", "gate::step").Code);
            CoreTests.Equal(1, Run("check").Code);
        });
    }
}
