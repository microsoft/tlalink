using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TlaLink;

namespace TlaLink.Tests;

public static class CleanupInstrumentationTests
{
    private const string Source = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;

        public class Subject {
            public static List<string> Log = new();
            sealed class Tracked : IDisposable {
                readonly string name;
                readonly bool fails;
                public Tracked(string name, bool fails = false) { this.name = name; this.fails = fails; }
                public void Dispose() { Log.Add(name); if (fails) throw new InvalidOperationException(name); }
            }
            static Tracked Create(string mode) {
                if (mode == "initializer-fault") throw new InvalidOperationException("initializer");
                return mode == "null" ? null : new Tracked("second");
            }
            static async Task Pause(string mode) {
                await Task.Yield();
                if (mode == "await-fault") throw new InvalidOperationException("await");
            }
            public static async Task<string> Run(string mode) {
                try {
                    /* @link {"bind":[{"id":"Lifetime::FirstCleanup"}],"capture":[{"binding":"Lifetime::FirstCleanup","actor":"1","operation":"$call","point":"cleanup","classification":"transition"}]} */
                    using var first = new Tracked("first");
                    /* @link {"bind":[{"id":"Lifetime::SecondCleanup"},{"id":"Lifetime::SecondFault"}],"capture":[{"binding":"Lifetime::SecondCleanup","actor":"1","operation":"$call","point":"cleanup","classification":"transition"},{"binding":"Lifetime::SecondFault","actor":"1","operation":"$call","point":"fault","classification":"transition"}]} */
                    using var second = Create(mode);
                    /* @link {"bind":[{"id":"Lifetime::PauseBegin"},{"id":"Lifetime::PauseEnd"},{"id":"Lifetime::PauseFault"}],"capture":[{"binding":"Lifetime::PauseBegin","actor":"1","operation":"$call","point":"invoke","classification":"transition"},{"binding":"Lifetime::PauseEnd","actor":"1","operation":"$call","point":"complete","classification":"transition"},{"binding":"Lifetime::PauseFault","actor":"1","operation":"$call","point":"fault","classification":"transition"}]} */
                    await Pause(mode);
                    /* @link {"bind":[{"id":"Lifetime::ThirdCleanup"},{"id":"Lifetime::ThirdFault"}],"capture":[{"binding":"Lifetime::ThirdCleanup","actor":"1","operation":"$call","point":"cleanup","classification":"transition"},{"binding":"Lifetime::ThirdFault","actor":"1","operation":"$call","point":"fault","classification":"transition"}]} */
                    using (var third = new Tracked("third", mode == "disposer-fault")) {
                        if (mode == "body-fault") throw new InvalidOperationException("body");
                        if (mode == "return") return "returned";
                    }
                    return "completed";
                } catch (InvalidOperationException) { return "caught"; }
            }
        }
        """;

    public static void Register(Action<string, Action> test, string work)
    {
        string directory = Directory.CreateDirectory(Path.Combine(work, "cleanup-capture")).FullName;
        (CSharpScope Scope, string Output) Generate(string text)
        {
            string input = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".cs");
            File.WriteAllText(input, text);
            CSharpScope scope = CSharpScope.Parse([input]);
            if (scope.Diagnostics.Any(d => d.Is(ErrorCode.InvalidAnnotation)))
                throw new InvalidOperationException(string.Join("\n", scope.Diagnostics));
            List<ModelBinding> models = scope.Bindings.Select(b => new ModelBinding(b.Model, b.Tag,
                new(b.Tag.Identity, "", b.Tag.Kind, new("Lifetime.tla", 1, 1), ""), 2)).ToList();
            Packet packet = Review.Build("instrument", scope, models, [], []);
            string output = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            Instrumentation.Generate(scope, models, packet, output);
            return (scope, output);
        }
        foreach (string mode in new[] { "normal", "return", "body-fault", "initializer-fault", "await-fault", "disposer-fault", "null", "expression", "explicit-interface" })
            test("cleanup capture preserves acquisition disposal and async exception semantics: " + mode, () =>
            {
                string text = mode == "explicit-interface" ? Source.Replace("public void Dispose()", "void IDisposable.Dispose()", StringComparison.Ordinal) : Source;
                if (mode == "expression")
                    text = text.Replace("static Tracked Create(string mode)",
                        "public static int Evaluations; static Tracked Third(string mode) { Evaluations++; return new Tracked(\"third\"); } static Tracked Create(string mode)",
                        StringComparison.Ordinal).Replace("using (var third = new Tracked(\"third\", mode == \"disposer-fault\"))",
                        "using (Third(mode))", StringComparison.Ordinal);
                var (scope, output) = Generate(text);
                var trees = Directory.GetFiles(output, "*.cs").Select(p => CSharpSyntaxTree.ParseText(Files.Read(p),
                    (CSharpParseOptions)scope.Compilation.SyntaxTrees.First().Options, p));
                using MemoryStream bytes = new();
                var emitted = scope.Compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(trees).Emit(bytes);
                if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
                Assembly assembly = Assembly.Load(bytes.ToArray());
                Type subject = assembly.GetType("Subject")!;
                string result = ((Task<string>)subject.GetMethod("Run")!.Invoke(null, [mode])!).GetAwaiter().GetResult();
                CoreTests.Equal(mode == "return" ? "returned" : mode is "normal" or "null" or "expression" or "explicit-interface" ? "completed" : "caught", result);
                if (mode == "expression") CoreTests.Equal(1, (int)subject.GetField("Evaluations")!.GetValue(null)!);
                string trace = Path.Combine(output, "trace.json");
                assembly.GetType("TlaLink.Runtime.TraceRuntime")!.GetMethod("Complete")!.Invoke(null, [trace]);
                using JsonDocument document = JsonDocument.Parse(Files.Read(trace));
                var events = document.RootElement.GetProperty("events").EnumerateArray().ToList();
                string[] steps = events.Select(e => e.GetProperty("step").GetString()!).ToArray();
                string[] expected = mode switch {
                    "initializer-fault" => ["FirstCleanup"],
                    "await-fault" => ["PauseBegin", "PauseFault", "SecondCleanup", "FirstCleanup"],
                    "disposer-fault" => ["PauseBegin", "PauseEnd", "ThirdFault", "SecondCleanup", "FirstCleanup"],
                    "null" => ["PauseBegin", "PauseEnd", "ThirdCleanup", "FirstCleanup"],
                    _ => ["PauseBegin", "PauseEnd", "ThirdCleanup", "SecondCleanup", "FirstCleanup"]
                };
                CoreTests.True(steps.SequenceEqual(expected.Select(id => "Lifetime::" + id)));
                CoreTests.Equal(1, events.Select(e => e.GetProperty("operation").GetString()).Distinct().Count());
                string[] disposed = ((IEnumerable<string>)subject.GetField("Log")!.GetValue(null)!).ToArray();
                CoreTests.True(disposed.SequenceEqual(mode switch {
                    "initializer-fault" => ["first"],
                    "await-fault" => ["second", "first"],
                    "null" => ["third", "first"],
                    _ => new[] { "third", "second", "first" }
                }));
            });
        test("cleanup capture rejects value resources await using multiple declarations and source locks", () =>
        {
            foreach (string resource in new[] {
                "using var first = default(System.Threading.CancellationTokenRegistration);",
                "using (IDisposable first = new Tracked(\"first\"), extra = new Tracked(\"extra\")) {}",
                "await using var first = new System.IO.MemoryStream();",
                "using dynamic first = new Tracked(\"first\");" })
                CoreTests.ThrowsAny<InputException>(() => Generate(Source.Replace(
                    "using var first = new Tracked(\"first\");", resource, StringComparison.Ordinal)));
            CoreTests.ThrowsAny<InputException>(() => Generate(Source.Replace(
                "using var first = new Tracked(\"first\");",
                "using var first = new Tracked(\"first\");", StringComparison.Ordinal).Replace(
                "\"point\":\"cleanup\"", "\"point\":\"cleanup\",\"hook\":\"after\"", StringComparison.Ordinal)));
            CoreTests.ThrowsAny<InputException>(() => Generate("""

                class Locked {
                    readonly object gate = new();
                    void Run() {
                        lock (gate) {
                            /* @link {"bind":[{"id":"Lifetime::Cleanup"}],"capture":[{"binding":"Lifetime::Cleanup","actor":"1","operation":"$call","point":"cleanup","classification":"transition"}]} */
                            using var resource = new System.IO.MemoryStream();
                        }
                    }
                }
                """));
        });
    }
}
