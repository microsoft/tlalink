using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class SourceQueryTests
{
    public static void Register(Action<string, Action> test, string work)
    {
        test("references distinguish overloads method groups nameof candidates and unrelated spelling", () =>
        {
            Fixture f = new(work, """
                using System;
                class Target {
                    public void Ping(int value) {}
                    public void Ping(string value) {}
                }
                class Other { public void Ping(int value) {} }
                class Use {
                    public void Go(Target target, Other other) {
                        target.Ping(1);
                        target.Ping("text");
                        other.Ping(2);
                        Action<int> callback = target.Ping;
                        var name = nameof(target.Ping);
                        var text = "Ping"; // Ping is not a semantic reference here.
                        callback(3);
                    }
                }
                """);
            SourceSymbolsResult result = f.Query("references", "M:Target.Ping(System.Int32)");
            CoreTests.Equal(3, result.ReferenceCount!.Value);
            List<SourceReferenceSite> references = result.Details?.References ?? throw new InvalidOperationException("Missing references.");
            CoreTests.True(references.Any(r => r.Kind == "method-group" && r.Resolution == "resolved"));
            CoreTests.True(references.Any(r => r.Kind == "nameof" && r.Resolution == "candidate"));
            SourceSymbolsResult callers = f.Query("callers", "M:Target.Ping(System.Int32)");
            CoreTests.Equal(1, callers.CallCount!.Value);
            CoreTests.Equal("M:Use.Go(Target,Other)", callers.Details!.Calls!.Single().Caller);
            CoreTests.Equal(4, f.Query("callees", "M:Use.Go(Target,Other)").CallCount!.Value);
        });
        test("generic and reduced extension calls retain canonical IDs and constructed targets", () =>
        {
            Fixture f = new(work, """
                class Box<T> { public void Put(T value) {} }
                static class Extensions { public static void Touch<T>(this Box<T> box) {} }
                class Use {
                    public void Go() {
                        var first = new Box<int>();
                        var second = new Box<string>();
                        first.Put(1); second.Put("two");
                        first.Touch(); second.Touch();
                    }
                }
                """);
            SourceSymbolsResult calls = f.Query("callers", "M:Box`1.Put(`0)");
            CoreTests.Equal(2, calls.CallCount!.Value);
            CoreTests.Equal(2, calls.Details!.Calls!.Select(c => c.ConstructedTarget).Distinct().Count());
            string extension = f.Find("Touch");
            CoreTests.Equal(2, f.Query("callers", extension).CallCount!.Value);
            CoreTests.Equal(2, f.Query("references", extension).ReferenceCount!.Value);
            SourceSymbolsResult outgoing = f.Query("callees", "M:Use.Go");
            CoreTests.Equal(6, outgoing.CallCount!.Value);
            CoreTests.Equal(2, outgoing.Details!.Calls!.Count(c => c.Target == extension));
        });
        test("constructor queries include base this explicit and target-typed creation sites", () =>
        {
            Fixture f = new(work, """
                class Base { public Base(int value) {} }
                class Child : Base {
                    public Child() : this(1) {}
                    public Child(int value) : base(value) {}
                }
                class Use { public Child Create() { Child first = new(); return new Child(2); } }
                """);
            SourceSymbolsResult parent = f.Query("callers", "M:Base.#ctor(System.Int32)");
            CoreTests.Equal(1, parent.CallCount!.Value);
            SourceCallSite site = parent.Details?.Calls?.Single() ?? throw new InvalidOperationException("Missing constructor call.");
            CoreTests.Equal("base", site.Dispatch);
            CoreTests.Equal("M:Child.#ctor(System.Int32)", site.Caller);
            CoreTests.Equal(2, f.Query("callers", "M:Child.#ctor(System.Int32)").CallCount!.Value);
            CoreTests.Equal(1, f.Query("references", "M:Child.#ctor").ReferenceCount!.Value);
            CoreTests.Equal("M:Child.#ctor(System.Int32)",
                f.Query("callees", "M:Child.#ctor").Details!.Calls!.Single().Target);
        });
        test("callers preserve declared interface virtual override and base distinctions", () =>
        {
            Fixture f = new(work, """
                interface IWorker { void Work(); }
                class Worker : IWorker { public virtual void Work() {} }
                class Derived : Worker { public override void Work() { base.Work(); } }
                class Use {
                    public void Go(IWorker first, Worker second, Derived third) {
                        first.Work(); second.Work(); third.Work();
                    }
                }
                """);
            CoreTests.Equal("interface", f.Query("callers", "M:IWorker.Work").Details!.Calls!.Single().Dispatch);
            SourceSymbolsResult declared = f.Query("callers", "M:Worker.Work");
            CoreTests.Equal(2, declared.CallCount!.Value);
            CoreTests.True(declared.Details!.Calls!.Select(c => c.Dispatch).ToHashSet().SetEquals(["virtual", "base"]));
            CoreTests.Equal(1, f.Query("callers", "M:Derived.Work").CallCount!.Value);
        });
        test("nested call sites retain containing members without asserting callback execution", () =>
        {
            Fixture f = new(work, """
                using System;
                class Nested {
                    void Effect() {}
                    public void Outer() {
                        Action callback = () => Effect();
                        void Local() { Effect(); }
                    }
                }
                """);
            SourceSymbolsResult result = f.Query("callers", "M:Nested.Effect");
            CoreTests.Equal(2, result.CallCount!.Value);
            CoreTests.True(result.Details!.Calls!.All(c => c.Caller == "M:Nested.Outer" && c.InNestedFunction));
        });
        test("describe bundles source and member facts without constructing a CFG", () =>
        {
            Fixture f = new(work, """
                using System;
                using System.IO;
                using System.Threading.Tasks;
                class Subject {
                    readonly object gate = new object();
                    int count;
                    public async Task<int> Run() {
                        lock (gate) { count++; }
                        using var stream = new MemoryStream();
                        await Task.Yield();
                        if (count < 0) throw new InvalidOperationException();
                        return count;
                    }
                }
                """);
            SourceSymbolsResult result = f.Query("describe", "M:Subject.Run");
            SourceMemberDescription member = result.Details!.Members!.Single();
            CoreTests.True(member.Source.Contains("public async", StringComparison.Ordinal));
            CoreTests.Equal(1, member.Facts.Locks.Count);
            CoreTests.Equal(1, member.Facts.Awaits.Count);
            CoreTests.Equal(1, member.Facts.Throws.Count);
            CoreTests.Equal(1, member.Facts.Using.Count);
            CoreTests.Equal(3, result.CallCount!.Value);
            CoreTests.True(member.Facts.Flow == null);
            CoreTests.True(member.Facts.FieldAccesses.Any(a => a.Name == "count"));
        });
        test("field initializer calls have field owners and member access facts use exact spans", () =>
        {
            Fixture f = new(work, """
                class Subject {
                    static object Make() => new object();
                    object item = Make();
                    int first, second;
                    int A() => first; int B() => second;
                }
                """);
            CoreTests.Equal("F:Subject.item", f.Query("callers", "M:Subject.Make").Details!.Calls!.Single().Caller);
            CoreTests.Equal("M:Subject.Make", f.Query("callees", "F:Subject.item").Details!.Calls!.Single().Target);
            SourceMember facts = f.Query("describe", "M:Subject.A").Details!.Members!.Single().Facts;
            CoreTests.True(facts.FieldAccesses.All(a => a.Name == "first"));
            CoreTests.Equal(1, facts.FieldAccesses.Count);
        });
        test("metadata method IDs from callees can be queried without scanning metadata bodies", () =>
        {
            Fixture f = new(work, "class Use { public void Run() { System.GC.KeepAlive(this); } }");
            string id = f.Query("callees", "M:Use.Run").Details!.Calls!.Single().Target!;
            SourceSymbolsResult callers = f.Query("callers", id);
            CoreTests.Equal(1, callers.CallCount!.Value);
            CoreTests.Equal(0, callers.Symbols.Single().Declarations.Count);
            CoreTests.True(callers.Symbols.Single().Assembly != null);
            CoreTests.Equal(1, f.Query("references", id).ReferenceCount!.Value);
            CoreTests.Equal(0, f.Query("show", id, 1).MatchCount);
        });
        test("type references include aliases and predefined keyword types", () =>
        {
            Fixture f = new(work, """
                using Alias = System.String;
                class Use { string first; Alias second; System.String third; }
                """);
            CoreTests.Equal(4, f.Query("references", "T:System.String").ReferenceCount!.Value);
        });
        test("reference queries distinguish conditional invocations documentation and implicit indexer calls", () =>
        {
            Fixture f = new(work, """
                class Target {
                    public void Ping() {}
                    public int this[int index] => index;
                }
                /// <summary>See <see cref="Target.Ping"/>.</summary>
                class Use {
                    public int Run(Target target) { target?.Ping(); return target[0]; }
                }
                """);
            SourceSymbolsResult references = f.Query("references", "M:Target.Ping");
            CoreTests.Equal(2, references.ReferenceCount!.Value);
            CoreTests.True(references.Details!.References!.Select(r => r.Kind).ToHashSet().SetEquals(["documentation", "invocation"]));
            CoreTests.Equal(1, f.Query("callers", "M:Target.Ping").CallCount!.Value);
            CoreTests.Equal("indexer", f.Query("references", "P:Target.Item(System.Int32)").Details!.References!.Single().Kind);
            CoreTests.Equal(1, f.Query("callees", "M:Use.Run(Target)").CallCount!.Value);
            var summary = f.Cli("callees", "M:Use.Run(Target)", "--format", "summary");
            CoreTests.Equal(0, summary.Code);
            using JsonDocument document = JsonDocument.Parse(summary.Json);
            CoreTests.Equal(1, document.RootElement.GetProperty("call_count").GetInt32());
            CoreTests.Equal(1, document.RootElement.GetProperty("details").GetProperty("calls").GetArrayLength());
        });
        test("dynamic outgoing calls are explicitly incomplete and not invented targets", () =>
        {
            Fixture f = new(work, "class Use { public void Run(dynamic value) { value.Unknown(); } }");
            foreach (string action in new[] { "callees", "describe" })
            {
                SourceSymbolsResult result = f.Query(action, "M:Use.Run(System.Object)", 2);
                CoreTests.Equal("incomplete", result.Status);
                CoreTests.True(result.Diagnostics.Any(d => d.Is(ErrorCode.UnresolvedCompilation)));
                CoreTests.True(result.Details!.Calls!.Single().Target == null);
            }
        });
        test("query actions reject unsupported targets and preserve successful empty results", () =>
        {
            Fixture f = new(work, "class Use { int field; public void Run() {} }");
            CoreTests.Equal(0, f.Query("callers", "M:Use.Run").CallCount!.Value);
            CoreTests.Equal(0, f.Query("references", "M:Use.Run").ReferenceCount!.Value);
            CoreTests.Equal(0, f.Query("callees", "M:Use.Run").CallCount!.Value);
            CoreTests.Equal(2, f.Cli("callers", "F:Use.field").Code);
            CoreTests.Equal(2, f.Cli("describe", "T:Use").Code);
            CoreTests.Equal("not-found", f.Query("references", "M:Use.Missing", 1).Status);
        });
    }

    private sealed class Fixture
    {
        private readonly string path;
        public Fixture(string work, string source)
        {
            path = Path.Combine(work, "query-" + Guid.NewGuid().ToString("N") + ".cs");
            File.WriteAllText(path, source);
        }

        public (int Code, string Json) Cli(string action, string id, params string[] arguments) =>
            TestCli.Run(["symbols", action, "--source", path, "--id", id, .. arguments]);

        public SourceSymbolsResult Query(string action, string id, int expected = 0)
        {
            var response = Cli(action, id);
            if (response.Code != expected) throw new InvalidOperationException(response.Json);
            return JsonSerializer.Deserialize<SourceSymbolsResult>(response.Json, ProjectInputs.Json)!;
        }

        public string Find(string name)
        {
            var result = TestCli.Run(["symbols", "find", "--source", path, "--name", name]);
            if (result.Code != 0) throw new InvalidOperationException(result.Json);
            return JsonSerializer.Deserialize<SourceSymbolsResult>(result.Json, ProjectInputs.Json)!.Symbols.Single().Id;
        }
    }
}
