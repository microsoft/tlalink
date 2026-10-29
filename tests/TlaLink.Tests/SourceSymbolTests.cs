using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class SourceSymbolTests
{
    public static void Register(Action<string, Action> test, string work)
    {
        SourceQueryTests.Register(test, work);
        test("symbol discovery retains overloads and groups partial types without choosing a declaration", () =>
        {
            Fixture f = new(work);
            SourceSymbolsResult methods = f.Query("find", 0, "--name", "Add");
            CoreTests.Equal("found", methods.Status);
            CoreTests.Equal(2, methods.MatchCount);
            CoreTests.True(methods.Symbols.Select(s => s.Id).ToHashSet().SetEquals([
                "M:SymbolsProbe.Box`1.Add(System.Int32)", "M:SymbolsProbe.Box`1.Add(System.String)"
            ]));
            SourceSymbolsResult constructors = f.Query("find", 0, "--name", "Box");
            CoreTests.Equal(3, constructors.MatchCount);
            CoreTests.Equal(2, constructors.Symbols.Count(s => s.Kind == "constructor"));
            CoreTests.Equal(2, constructors.Symbols.Single(s => s.Kind == "type").Declarations.Count);
            CoreTests.Equal(1, f.Query("find", 0, "--name", "SymbolsProbe.Box<T>.Value").MatchCount);
            CoreTests.Equal(1, f.Query("find", 0, "--name", "SymbolsProbe.Box<T>.Add(int)").MatchCount);
        });
        test("Roslyn constructor generic and ref IDs round trip through show and exact source positions", () =>
        {
            Fixture f = new(work);
            foreach (string id in new[] {
                "T:SymbolsProbe.Box`1", "M:SymbolsProbe.Box`1.#ctor",
                "M:SymbolsProbe.Box`1.#ctor(System.Int32)",
                "M:SymbolsProbe.Box`1.Map``1(`0,``0@)"
            })
            {
                SourceSymbolsResult result = f.Query("show", 0, "--id", id);
                CoreTests.Equal("resolved", result.Status);
                SourceSymbol symbol = result.Symbols.Single();
                CoreTests.Equal(id, symbol.Id);
                SymbolDeclaration location = symbol.Declarations[0];
                SourceSymbolsResult at = f.Query("resolve", 0, "--file", location.Location.Path,
                    "--line", location.Location.Line.ToString(), "--column", location.Location.Column.ToString());
                CoreTests.Equal(id, at.Symbols.Single().Id);
                string text = File.ReadAllText(location.Location.Path);
                CoreTests.True(text.Substring(location.Start, location.Length).Length > 0);
            }
        });
        test("symbol lookup exposes field property event and indexer declaration IDs", () =>
        {
            Fixture f = new(work);
            foreach (var item in new[] {
                (Name: "first", Id: "F:SymbolsProbe.Box`1.first", Kind: "field"),
                (Name: "Value", Id: "P:SymbolsProbe.Box`1.Value", Kind: "property"),
                (Name: "Changed", Id: "E:SymbolsProbe.Box`1.Changed", Kind: "event"),
                (Name: "this[]", Id: "P:SymbolsProbe.Box`1.Item(System.Int32)", Kind: "indexer")
            })
            {
                SourceSymbol symbol = f.Query("find", 0, "--name", item.Name).Symbols.Single();
                CoreTests.Equal(item.Id, symbol.Id);
                CoreTests.Equal(item.Kind, symbol.Kind);
                CoreTests.Equal(item.Id, f.Query("show", 0, "--id", symbol.Id).Symbols.Single().Id);
            }
        });
        test("same-line fields and sibling methods require a column rather than shortest-span guessing", () =>
        {
            Fixture f = new(work);
            string[] lines = File.ReadAllLines(f.First);
            int fieldLine = Array.FindIndex(lines, line => line.Contains("first, second", StringComparison.Ordinal));
            SourceSymbolsResult fields = f.Query("resolve", 1, "--file", f.First, "--line", (fieldLine + 1).ToString());
            CoreTests.Equal("ambiguous", fields.Status);
            CoreTests.Equal(2, fields.MatchCount);
            CoreTests.True(fields.Diagnostics.Any(d => d.Is(ErrorCode.SourceSymbolAmbiguous)));
            int column = lines[fieldLine].IndexOf("second", StringComparison.Ordinal) + 1;
            CoreTests.Equal("F:SymbolsProbe.Box`1.second", f.Query("resolve", 0, "--file", f.First,
                "--line", (fieldLine + 1).ToString(), "--column", column.ToString()).Symbols.Single().Id);
            CoreTests.Equal(1, f.Query("resolve", 1, "--file", f.First, "--line", (fieldLine + 1).ToString(),
                "--column", (lines[fieldLine].IndexOf("private", StringComparison.Ordinal) + 1).ToString()).Diagnostics.Count);
            File.WriteAllText(f.First, "class SameLine { void Left() {} void Right() {} }");
            SourceSymbolsResult methods = f.Query("resolve", 1, "--file", f.First, "--line", "1");
            CoreTests.Equal(2, methods.MatchCount);
            CoreTests.True(methods.Symbols.All(s => s.Kind == "method"));
        });
        test("symbol identity survives line movement while physical source spans and fingerprints change", () =>
        {
            Fixture f = new(work);
            const string id = "M:SymbolsProbe.Box`1.Add(System.Int32)";
            SourceSymbolsResult before = f.Query("show", 0, "--id", id);
            File.WriteAllText(f.First, "// Added unrelated comment.\n\n" + File.ReadAllText(f.First));
            SourceSymbolsResult after = f.Query("show", 0, "--id", id);
            CoreTests.Equal(id, after.Symbols.Single().Id);
            CoreTests.Equal(before.Symbols.Single().Declarations.Single().Location.Line + 2,
                after.Symbols.Single().Declarations.Single().Location.Line);
            CoreTests.True(before.Fingerprints[f.First] != after.Fingerprints[f.First]);
            CoreTests.Equal(Files.Hash(f.First), after.Fingerprints[f.First]);
        });
        test("partial method definition and implementation share one lookup ID and retain both locations", () =>
        {
            Fixture f = new(work);
            File.WriteAllText(f.First, "partial class Partial { partial void Hook(); }");
            File.WriteAllText(f.Second, "partial class Partial { partial void Hook() {} }");
            SourceSymbolsResult found = f.Query("find", 0, "--name", "Hook");
            CoreTests.Equal(1, found.MatchCount);
            CoreTests.Equal(2, found.Symbols.Single().Declarations.Count);
            CoreTests.Equal(2, f.Query("show", 0, "--id", "M:Partial.Hook").Symbols.Single().Declarations.Count);
        });
        test("explicit interface and operator IDs come from Roslyn and round trip without spelling inference", () =>
        {
            Fixture f = new(work);
            File.WriteAllText(f.First, """
                interface ICall { void Send(int value); }
                class Explicit : ICall {
                    void ICall.Send(int value) {}
                    public static Explicit operator +(Explicit left, Explicit right) => left;
                }
                """);
            SourceSymbolsResult methods = f.Query("find", 0, "--name", "Send");
            CoreTests.Equal(2, methods.MatchCount);
            SourceSymbol implementation = methods.Symbols.Single(s => s.ContainingType == "Explicit");
            CoreTests.Equal(implementation.Id, f.Query("show", 0, "--id", implementation.Id).Symbols.Single().Id);
            SourceSymbol addition = f.Query("find", 0, "--name", "op_Addition").Symbols.Single();
            CoreTests.Equal("operator", addition.Kind);
            CoreTests.Equal(addition.Id, f.Query("show", 0, "--id", addition.Id).Symbols.Single().Id);
        });
        test("symbol positions use UTF16 columns and find the smallest supported declaration", () =>
        {
            Fixture f = new(work);
            string text = "class Unicode { string marker = \"\U0001F600\"; public void Left() {} public void Right() {} }";
            File.WriteAllText(f.First, text);
            int column = text.IndexOf("public void Right", StringComparison.Ordinal) + 1;
            CoreTests.Equal("M:Unicode.Right", f.Query("resolve", 0, "--file", f.First, "--line", "1",
                "--column", column.ToString()).Symbols.Single().Id);
        });
        test("position lookup identifies the containing declaration rather than a referenced field or local function", () =>
        {
            Fixture f = new(work);
            File.WriteAllText(f.First, """
                class Enclosing {
                    int field;
                    void Outer() {
                        void Local() { field = 1; }
                        Local();
                    }
                }
                """);
            CoreTests.Equal("M:Enclosing.Outer", f.Query("resolve", 0, "--file", f.First,
                "--line", "4").Symbols.Single().Id);
            CoreTests.Equal("M:Enclosing.Outer", f.Query("resolve", 0, "--file", f.First,
                "--line", "5").Symbols.Single().Id);
        });
        test("symbol queries ignore annotation payloads without weakening annotated source inspection", () =>
        {
            Fixture f = new(work);
            File.WriteAllText(f.First, """class Plain { /* @link not-json */ public void Run() {} }""");
            CoreTests.Equal("M:Plain.Run", f.Query("find", 0, "--name", "Run").Symbols.Single().Id);
            CoreTests.Equal(2, TestCli.Run(["inspect-source", "--annotation-version", "4", "--source", f.First]).Code);
            CoreTests.Equal(2, TestCli.Run(["symbols", "find", "--source", f.First, "--name", "Run",
                "--annotation-version", "4"]).Code);
        });
        test("missing compilation context never yields success-shaped partial symbol results", () =>
        {
            Fixture f = new(work);
            File.WriteAllText(f.First, "class Broken { public MissingType Run() => null; }");
            SourceSymbolsResult result = f.Query("find", 2, "--name", "Run");
            CoreTests.Equal("incomplete", result.Status);
            CoreTests.Equal(0, result.MatchCount);
            CoreTests.True(result.Diagnostics.Any(d => d.Is(ErrorCode.UnresolvedCompilation)));
        });
        test("symbol lookup honors language and defines and emits useful summary and saved JSON", () =>
        {
            Fixture f = new(work);
            File.WriteAllText(f.First, """
                class Dialect {
                #if PICK
                    public void Selected() {}
                #else
                    public void Other() {}
                #endif
                }
                """);
            CoreTests.Equal("M:Dialect.Selected", f.Query("find", 0, "--name", "Selected", "--define", "PICK",
                "--language-version", "8").Symbols.Single().Id);
            CoreTests.Equal("not-found", f.Query("find", 1, "--name", "Selected").Status);
            string output = Path.Combine(f.Directory, "symbols.json");
            var response = f.Cli("find", "--name", "Other", "--output", output, "--format", "summary");
            CoreTests.Equal(0, response.Code);
            using JsonDocument document = JsonDocument.Parse(response.Json);
            CoreTests.Equal("source-symbols", document.RootElement.GetProperty("result_kind").GetString());
            CoreTests.Equal("M:Dialect.Other", document.RootElement.GetProperty("symbols")[0].GetProperty("id").GetString());
            CoreTests.Equal("Other", ProjectInputs.Read<SourceSymbolsResult>(output).Query.Name);
            CoreTests.Equal(2, f.Cli("find", "--name", "Other", "--output", output).Code);
            File.WriteAllText(f.First, "class Dialect { object field = new(); public void Run() {} }");
            CoreTests.Equal("incomplete", f.Query("find", 2, "--name", "Run", "--language-version", "8").Status);
        });
        test("symbol queries distinguish not found and invalid selectors without metadata or nearest-line fallback", () =>
        {
            Fixture f = new(work);
            CoreTests.Equal("not-found", f.Query("show", 1, "--id", "T:System.String").Status);
            CoreTests.Equal("not-found", f.Query("find", 1, "--name", "add").Status);
            CoreTests.Equal("not-found", f.Query("show", 1, "--id", "M:SymbolsProbe.Box`1.Missing").Status);
            foreach (string malformed in new[] { "nonsense", "M:", "T:Bad{", "M:SymbolsProbe.Box`x.Add(" })
                CoreTests.Equal(1, f.Cli("show", "--id", malformed).Code);
            foreach (string[] args in new[] {
                new[] { "find" }, new[] { "show" },
                new[] { "resolve", "--line", "1" },
                new[] { "resolve", "--file", f.First, "--line", "0" },
                new[] { "resolve", "--file", f.First, "--line", "9999" },
                new[] { "resolve", "--file", f.First, "--line", "1", "--column", "9999" },
                new[] { "find", "--name", "Add", "--line", "1" },
                new[] { "show", "--id", "T:SymbolsProbe.Box`1", "--name", "Box" },
                new[] { "invent", "--name", "Box" }
            })
                CoreTests.Equal(2, TestCli.Run(["symbols", .. args, "--source", f.First, "--source", f.Second]).Code);
        });
    }

    private sealed class Fixture
    {
        public string Directory { get; }
        public string First => Path.Combine(Directory, "Box.cs");
        public string Second => Path.Combine(Directory, "Other.cs");

        public Fixture(string work)
        {
            Directory = System.IO.Directory.CreateDirectory(Path.Combine(work, "symbols-" + Guid.NewGuid().ToString("N"))).FullName;
            File.WriteAllText(First, """
                namespace SymbolsProbe {
                    public partial class Box<T> {
                        private int first, second;
                        public Box() {}
                        public Box(int size) { first = size; }
                        public void Add(int amount) {}
                        public void Add(string amount) {}
                        public U Map<U>(T value, ref U other) => other;
                        public int Value { get; set; }
                        public event System.Action Changed { add {} remove {} }
                        public int this[int index] => index;
                    }
                }
                """);
            File.WriteAllText(Second, """
                namespace SymbolsProbe {
                    public partial class Box<T> { public bool Ready() => true; }
                }
                """);
        }

        public (int Code, string Json) Cli(string action, params string[] args) =>
            TestCli.Run(["symbols", action, "--source", First, "--source", Second, .. args]);

        public SourceSymbolsResult Query(string action, int expected, params string[] args)
        {
            var response = Cli(action, args);
            if (response.Code != expected) throw new InvalidOperationException(response.Json);
            return JsonSerializer.Deserialize<SourceSymbolsResult>(response.Json, ProjectInputs.Json)
                ?? throw new InvalidOperationException("Missing symbol result.");
        }
    }
}
