using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class CorrespondenceTests
{
    public static void Register(Action<string, Action> test, string work)
    {
        test("JSON annotations deserialize into typed bindings captures exclusions and native references", () =>
        {
            List<LocatedAnnotation> parsed = Markup.Parse("""
                @link {
                  "bind": [{"id":"lamp::change"}],
                  "capture": [{"binding":"lamp::change","point":"commit","classification":"transition",
                    "actor":"this","operation":"$call","arguments":["requested"],"observe":["lamp::enabled"]}],
                  "exclude": [{"id":"diagnostics","from":["lamp"],"reason":"Diagnostic output only."}]
                }
                """, 4, 8);
            BindAnnotation binding = (BindAnnotation)parsed[0].Declaration;
            CaptureAnnotation capture = (CaptureAnnotation)parsed[1].Declaration;
            ExcludeAnnotation exclusion = (ExcludeAnnotation)parsed[2].Declaration;
            CoreTests.Equal(new NativeReference("lamp", "change"), Markup.Name(binding.Id));
            CoreTests.Equal(CapturePoint.Commit, capture.Point);
            CoreTests.Equal(CaptureClassification.Transition, capture.Classification);
            CoreTests.True(capture.Arguments!.SequenceEqual(["requested"]));
            CoreTests.True(exclusion.From.SequenceEqual(["lamp"]));
            CoreTests.Equal(4, parsed[0].Start);
            AnnotationPayload payload = new() { Bind = [binding], Capture = [capture], Exclude = [exclusion] };
            CoreTests.Equal(3, Markup.Parse("@link " + Markup.Encode(payload), 0, 0).Count);
        });
        foreach (string payload in new[] {
            """{"bind":[null]}""", """{"bind":null}""", """{"bind":[{"id":"x"}]}""",
            """{"bind":[{"id":"x","target":"lamp::Set","typo":1}]}""",
            """{"bind":[],"bind":[]}""", """{"bind":[{"id":"x","id":"y","target":"lamp::Set"}]}""",
            """{"capture":[{"binding":"x","point":"afterwards"}]}""",
            """{"capture":[{"binding":"x","point":1}]}""",
            """{"capture":[{"binding":"x","observe":"enabled"}]}""",
            """{"capture":[{"binding":"x","arguments":[1]}]}""",
            """{"exclude":[{"id":"x","from":["*"],"reason":"Not explicit"}]}""",
            """{"exclude":[{"id":"x","from":["lamp"],"reason":""}]}""",
            """{"unknown":[]}""", "[]", "null", "{}" })
            test("typed annotation validation rejects " + payload, () =>
                CoreTests.ThrowsAny<InputException>(() => Markup.Parse("@link " + payload, 0, 0)));
        test("generated JSON escapes comment terminators without changing binding reasons", () =>
        {
            AnnotationPayload payload = new() { Bind = [new("lamp::change", "contains */ text")] };
            string json = Markup.Encode(payload);
            CoreTests.True(!json.Contains("*/", StringComparison.Ordinal));
            CoreTests.Equal("contains */ text", ((BindAnnotation)Markup.Parse("@link " + json, 0, 0)[0].Declaration).Reason);
        });
        test("native authoring rejects duplicate endpoint registries rather than silently ignoring them", () =>
        {
            CoreTests.ThrowsAny<JsonException>(() => IntegratedInputs.Parse("""
                {"schema_version":4,"kind":"integrated-bindings","sources":["Lamp.cs"],"scope":"scope.json",
                 "bindings":[{"id":"desired","source":"Lamp.Desired","dafny":"Math.Desired","adapter":null,"cases":[]}],
                 "correspondences":[]}
                """));
            CoreTests.ThrowsAny<JsonException>(() => PInputs.Parse("""
                {"schema_version":4,"kind":"p-analysis","name":"Lamp","engine":"inspect","models":["Lamp.p"],
                 "sources":["Lamp.cs"],"scope":"scope.json","dependencies":[],
                 "bindings":[{"id":"desired","declaration":"Lamp.Desired","relation":"Structural only.","assumptions":["Explicit scope."]}]}
                """));
        });
        test("required TLA binding IDs cannot disappear from a selected source scope", () =>
        {
            string path = Path.Combine(work, "required-binding.cs");
            File.WriteAllText(path, "class Lamp {}");
            CSharpScope source = CSharpScope.Parse([path]);
            CorrespondenceConfiguration config = new(Schema.Version, "correspondence-scope", Markup.Version,
                [new("lamp", "tla", ["not-opened-before-source-presence-check.tla"], RequiredBindings: ["lamp::change"])], []);
            ModelScope model = new();
            model.BindSources(source, config, "lamp");
            CoreTests.True(model.Diagnostics.Any(d => d.Is(ErrorCode.MissingModelEndpoint) &&
                d.Message.Contains("lamp::change", StringComparison.Ordinal)));
        });
        test("one CSharp point can own independent native correspondences without capture dictionaries", () =>
        {
            string path = Path.Combine(work, "multiple-correspondences.cs");
            File.WriteAllText(path, """
                class Lamp {
                  /* @link {"bind":[{"id":"proof::arithmetic"},{"id":"protocol::protocol"}]} */
                  static bool Desired(bool value) => value;
                }
                """);
            CSharpScope source = CSharpScope.Parse([path]);
            CoreTests.Equal(2, source.Bindings.Count);
            CoreTests.True(source.Bindings.All(b => b.Kind == SourceBindingKind.Declaration && b.Capture == null));
            CoreTests.True(source.Diagnostics.All(d => d.Severity != "error"));
            CoreTests.Equal(new NativeReference("proof", "arithmetic"), Markup.Name(source.Bindings[0].Declaration.Id));
        });
        test("one JSON argument entry cannot expand into several CSharp arguments", () =>
        {
            string path = Path.Combine(work, "argument-entry.cs");
            File.WriteAllText(path, """
                class Lamp {
                  void Apply() {
                    /* @link {"bind":[{"id":"lamp::change"}],"capture":[{"binding":"lamp::change","point":"commit","classification":"transition","actor":"1","operation":"1","arguments":["1,true"]}]} */
                    System.GC.KeepAlive(0);
                  }
                }
                """);
            CSharpScope source = CSharpScope.Parse([path]);
            List<ModelBinding> models = source.Bindings.Select(b => new ModelBinding(b.Model, b.Tag,
                new("Set", "Lamp", "operator", new("synthetic.tla", 1, 1), ""), 4)).ToList();
            Packet packet = Review.Build("instrument", source, models, [], []);
            CoreTests.ThrowsAny<InputException>(() =>
                Instrumentation.Generate(source, models, packet, Path.Combine(work, "argument-entry-output")));
        });
        test("logging omissions retain argument effects and exclusion conflicts", () =>
        {
            string path = Path.Combine(work, "omission.cs");
            const string text = """
                class Lamp {
                  readonly object gate = new();
                  /* @link {"bind":[{"id":"lamp::state"}]} */
                  bool enabled;
                  static void Log(int marker) {}
                  void Set() { Log(ARGUMENT); }
                }
                """;
            CorrespondenceConfiguration configuration = new(Schema.Version, "correspondence-scope", Markup.Version,
                [new("lamp", "tla", ["not-opened-for-source-test.tla"])],
                [new("logs", ["lamp"], ["M:Lamp.Log(System.Int32)"], "Diagnostic output.",
                    ["Logging internals and failures are outside the claim."])]);
            File.WriteAllText(path, text.Replace("ARGUMENT", "1"));
            CSharpScope clean = CSharpScope.Parse([path]);
            clean.ApplyCorrespondence(configuration);
            CoreTests.Equal("rule-omitted", clean.Dispositions.Single().Status);
            CoreTests.Equal(1, clean.OmissionMatches.Single().MatchedSites);
            CoreTests.True(clean.Diagnostics.All(d => d.Severity != "error"));
            File.WriteAllText(path, text.Replace("ARGUMENT", "enabled ? 1 : 0"));
            CSharpScope reads = CSharpScope.Parse([path]);
            reads.ApplyCorrespondence(configuration);
            CoreTests.True(reads.Diagnostics.All(d => !d.Is(ErrorCode.OmissionBoundaryConflict)));
            CoreTests.True(reads.Accesses.Any(a => a.Name == "enabled" && a.Mode == "read"));
            File.WriteAllText(path, text.Replace("ARGUMENT", "(enabled = true) ? 1 : 0"));
            CSharpScope mutation = CSharpScope.Parse([path]);
            mutation.ApplyCorrespondence(configuration);
            CoreTests.True(mutation.Diagnostics.Any(d => d.Is(ErrorCode.OmissionBoundaryConflict)));
            File.WriteAllText(path, text.Replace("ARGUMENT", "1").Replace("static void Log(int marker) {}",
                "void Change() { enabled = true; } void Log(int marker) { Change(); }"));
            CSharpScope transitive = CSharpScope.Parse([path]);
            transitive.ApplyCorrespondence(configuration);
            CoreTests.True(transitive.Diagnostics.Any(d => d.Is(ErrorCode.OmissionBoundaryConflict)));
            File.WriteAllText(path, text.Replace("ARGUMENT", "1").Replace("Log(1);", """
                /* @link {"exclude":[{"id":"excluded","from":["lamp"],"reason":"Diagnostic block."}]} */
                {
                  /* @link {"bind":[{"id":"lamp::conflict"}]} */
                  Log(1);
                }
                """));
            CSharpScope conflict = CSharpScope.Parse([path]);
            conflict.ApplyCorrespondence(configuration);
            CoreTests.True(conflict.Diagnostics.Any(d => d.Severity == "error"));
            File.WriteAllText(path, text.Replace("ARGUMENT", "\"message\"").Replace("static void Log(int marker) {}",
                "static void Log(int marker) {} static void Log(string marker) {}"));
            CSharpScope overload = CSharpScope.Parse([path]);
            overload.ApplyCorrespondence(configuration);
            CoreTests.Equal(0, overload.OmissionMatches.Single().MatchedSites);
            CoreTests.Equal(0, overload.Dispositions.Count);
        });
        test("logging rules reject ambiguous declaration IDs across metadata assemblies", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "ambiguous-logging")).FullName;
            string sourcePath = Path.Combine(directory, "Caller.cs");
            File.WriteAllText(sourcePath, """
                class Caller {
                  void Run() {
                    Logging.FirstFactory.Create().Write(1);
                    Logging.SecondFactory.Create().Write(2);
                  }
                }
                """);
            var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator).Select(p => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(p));
            List<string> references = [];
            foreach (string factory in new[] { "FirstFactory", "SecondFactory" })
            {
                string path = Path.Combine(directory, factory + ".dll");
                var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(factory,
                    [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                        "namespace Logging { public class Logger { public void Write(int value) {} } " +
                        "public static class " + factory + " { public static Logger Create() => new Logger(); } }")],
                    framework, new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                        Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
                using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write);
                var result = compilation.Emit(output);
                CoreTests.True(result.Success);
                references.Add(path);
            }
            CSharpScope source = CSharpScope.Parse([sourcePath], references);
            source.RequireCompilable();
            source.ApplyCorrespondence(new(Schema.Version, "correspondence-scope", Markup.Version,
                [new("lamp", "tla", ["not-opened-for-symbol-test.tla"], RequiredBindings: ["change"])],
                [new("logs", ["lamp"], ["M:Logging.Logger.Write(System.Int32)"],
                    "Diagnostic output.", ["Explicit logger boundary."])]));
            CoreTests.True(source.Diagnostics.Any(d => d.Is(ErrorCode.InvalidAnnotation) &&
                d.Message.Contains("resolve uniquely", StringComparison.Ordinal)));
            CoreTests.Equal(0, source.Dispositions.Count);
        });
    }
}
