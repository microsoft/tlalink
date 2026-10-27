using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class ActionArgumentTests
{
    public static void Register(Action<string, Action> test, string work)
    {
        string directory = Directory.CreateDirectory(Path.Combine(work, "action-arguments")).FullName;
        const string template = """
            using System;

            class Sample {
                int field = 1;
                int Property => field;
                public void Run(int amount, bool enabled, string label) {
                    int delta = amount;
                    /* @link {"bind":[{"id":"Apply","target":"ArgumentModel::Apply"}],"capture":[{"binding":"Apply","actor":"this","operation":"$call","arguments":["ARGUMENTS"],"point":"commit","classification":"transition"}]} */
                    GC.KeepAlive(0);
                }
            }
            """;
        InstrumentManifest Generate(string arguments, string output, int arity = 5)
        {
            string source = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".cs");
            string[] expressions = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseArgumentList("(" + arguments + ")")
                .Arguments.Select(a => a.ToString()).ToArray();
            File.WriteAllText(source, template.Replace("[\"ARGUMENTS\"]", JsonSerializer.Serialize(expressions), StringComparison.Ordinal));
            CSharpScope scope = CSharpScope.Parse([source]);
            List<ModelBinding> models = scope.Bindings.Select(b => new ModelBinding(b.Model, b.Tag,
                new(b.Tag.Identity, "", b.Tag.Kind, new("Arguments.tla", 1, 1), ""), arity)).ToList();
            Packet packet = Review.Build("instrument", scope, models, [], []);
            return Instrumentation.Generate(scope, models, packet, output);
        }
        test("action arguments capture typed scalar data independently of identities and guarded observations", () =>
        {
            string output = Path.Combine(directory, "valid");
            InstrumentManifest manifest = Generate("delta,enabled,label", output);
            CoreTests.True(manifest.Events.Single().ArgumentTypes!.SequenceEqual(new[] { "int", "bool", "string" }));
            CoreTests.Equal(0, manifest.Events.Single().Observations.Count);
            CoreTests.True(!manifest.Events.Single().Entity);
            InstrumentManifest saved = Instrumentation.ReadManifest(Path.Combine(output, "bindings.json"));
            CoreTests.Equal(manifest.BindingId, saved.BindingId);
            string literals = Path.Combine(directory, "literals");
            InstrumentManifest literal = Generate("-1,true,\"x,y\"", literals);
            CoreTests.Equal(3, literal.Events.Single().ArgumentTypes!.Count);
        });
        test("action arguments reject fields getters references calls malformed lists and arity mismatches", () =>
        {
            foreach (string value in new[] { "field,enabled,label", "Property,enabled,label", "this,enabled,label",
                "Math.Abs(amount),enabled,label", "amount,,label", "ref amount,enabled,label", "" })
                CoreTests.ThrowsAny<InputException>(() => Generate(value, Path.Combine(directory, Guid.NewGuid().ToString("N"))));
            CoreTests.ThrowsAny<InputException>(() => Generate("delta,enabled,label",
                Path.Combine(directory, "wrong-arity"), 7));
        });
    }

    public static void RegisterNative(Action<string, Action> test, string root, string work)
    {
        test("native action arguments preserve integer boolean string data and detect an incorrect model requirement", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "argument-native")).FullName;
            string source = Path.Combine(directory, "Subject.cs"), model = Path.Combine(directory, "Arguments.tla");
            File.WriteAllText(source, """

                class Subject {
                    public void Apply(int amount, bool enabled, string label) {
                        /* @link {"bind":[{"id":"Apply","target":"Arguments::Apply"}],"capture":[{"binding":"Apply","actor":"this","operation":"$call","arguments":["amount","enabled","label"],"point":"commit","classification":"transition"}]} */
                        System.GC.KeepAlive(0);
                    }
                }
                """);
            File.WriteAllText(model, """
                ---- MODULE Arguments ----
                EXTENDS Integers

                CONSTANT Expected
                VARIABLE total
                vars == <<total>>
                Init == total = 0

                Apply(actor, op, amount, enabled, label) ==
                    /\ amount = Expected /\ enabled = TRUE /\ label = "x,y"
                    /\ total' = total + amount
                Next == Apply("object:1", "call:1", Expected, TRUE, "x,y")
                Spec == Init /\ [][Next]_vars
                ====
                """);
            string generated = Path.Combine(directory, "generated"), jar = TestTools.Jar(root);
            var instrument = TestCli.Tla(["instrument", "--annotation-version", "3", "--source", source,
                "--model", model, "--jar", jar, "--output", generated]);
            if (instrument.Code != 0) throw new InvalidOperationException(instrument.Json);
            File.WriteAllText(Path.Combine(generated, "Program.cs"), """
                class Program {
                    static void Main(string[] args) {
                        new Subject().Apply(3, true, "x,y");
                        TlaLink.Runtime.TraceRuntime.Complete(args[0]);
                    }
                }
                """);
            string project = Path.Combine(generated, "Fixture.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><LangVersion>14</LangVersion><Nullable>enable</Nullable><AllowUnsafeBlocks>true</AllowUnsafeBlocks><CheckForOverflowUnderflow>false</CheckForOverflowUnderflow></PropertyGroup></Project>");
            TestCli.Restore(project, directory, Path.Combine(root, "NuGet.Config"));
            string binding = Path.Combine(generated, "bindings.json"), trace = Path.Combine(directory, "trace.json"), execution = Path.Combine(directory, "execution");
            var run = TestCli.Run(["execute", "--manifest", binding, "--project", project, "--trace", trace, "--output", execution]);
            if (run.Code != 0) throw new InvalidOperationException(run.Json);
            using (JsonDocument captured = JsonDocument.Parse(Files.Read(trace)))
            {
                JsonElement values = captured.RootElement.GetProperty("events")[0].GetProperty("arguments");
                CoreTests.Equal(3, values[0].GetInt32());
                CoreTests.True(values[1].GetBoolean());
                CoreTests.Equal("x,y", values[2].GetString());
            }
            foreach (int expected in new[] { 3, 4 })
            {
                string config = Path.Combine(directory, "expected-" + expected + ".cfg");
                File.WriteAllText(config, "SPECIFICATION Spec\nCONSTANT Expected = " + expected + "\n");
                var check = TestCli.Tla(["validate-trace", "--annotation-version", "3", "--model", model, "--jar", jar,
                    "--manifest", binding, "--trace", trace, "--execution", Path.Combine(execution, "execution.json"),
                    "--config", config, "--output", Path.Combine(directory, "validation-" + expected)]);
                if (check.Code != (expected == 3 ? 0 : 1)) throw new InvalidOperationException(check.Json);
                if (expected == 4) CoreTests.True(check.Json.Contains("TL511", StringComparison.Ordinal));
            }
        });
    }
}
