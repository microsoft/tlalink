using System.Text;
using TlaLink;

namespace TlaLink.Tests;

public static class CoreTests
{
    public static void Register(Action<string, Action> test, string work)
    {
        test("streamed fingerprints preserve exact bytes without whole-file allocations", () =>
        {
            string path = Path.Combine(work, "streamed-hash.bin");
            byte[] bytes = new byte[1024 * 1024];
            new Random(42).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
            string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            _ = Files.Hash(path);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            Equal(expected, Files.Hash(path));
            True(GC.GetAllocatedBytesForCurrentThread() - allocated < bytes.Length / 4);
            File.WriteAllBytes(path, []);
            Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Files.Hash(path));
        });
        test("path inputs normalize either separator and remove dot segments and trailing separators", () =>
        {
            Equal(Path.Combine(work, "target"), FilePaths.Full(@"child\..\target\", work));
            Equal(Path.Combine(work, "target"), FilePaths.Full("child/../target/", work));
        });
        test("file identity follows host case sensitivity without rewriting spelling", () =>
        {
            Equal(OperatingSystem.IsWindows(), FilePaths.Comparer.Equals("Case.cs", "case.cs"));
            Equal(Path.Combine(work, "Case.cs"), FilePaths.Full("Case.cs", work));
            string upper = Path.Combine(work, "Case.cs"), lower = Path.Combine(work, "case.cs");
            File.WriteAllText(upper, "class Upper {}");
            File.WriteAllText(lower, "class Lower {}");
            Equal(OperatingSystem.IsWindows() ? 1 : 2, Files.Discover([upper, lower], ".cs").Count);
        });
        test("foreign absolute evidence paths remain identifiable without local reinterpretation", () =>
        {
            True(FilePaths.IsAbsolute(@"C:\evidence\input.cs"));
            True(FilePaths.IsAbsolute(@"\\server\share\input.cs"));
            True(FilePaths.IsAbsolute("/evidence/input.cs"));
            Equal(OperatingSystem.IsWindows(), FilePaths.IsNativeAbsolute(@"C:\evidence\input.cs"));
            Equal(OperatingSystem.IsWindows(), FilePaths.IsNativeAbsolute(@"\\server\share\input.cs"));
            Equal(!OperatingSystem.IsWindows(), FilePaths.IsNativeAbsolute("/evidence/input.cs"));
            if (!OperatingSystem.IsWindows())
            {
                Throws(() => FilePaths.Full(@"C:\evidence\input.cs"));
                Throws(() => FilePaths.Full(@"\\server\share\input.cs"));
            }
        });
        foreach (string text in new[] {
            "@tla 1 model A;", "@tla 3 model A;", "@tla 2 model A",
            "@tla 2 state A typo=B;", "@tla 2 step A point=commit point=complete;",
            "@tla 2 step A point=done;", "@tla 2 step A classification=maybe;",
            "@tla 2 state A type=object;", "@tla 2 step A hook=inside;",
            "@tla 2 state A guard=\"\";", "@tla 2 state A guard=\"\\q\";",
            "@tla 2;", "@tla 2 nonsense A;", "@tla 2 state \"not an ID\";"
        }) test("grammar rejects " + text, () => Throws(() => Markup.Parse(text, 0, text.Length)));
        test("schema2 multiple directives", () => Equal(3, Markup.Parse("@tla 2 model M; state Active guard=\"this.gate\" type=int units=frames; step Admit point=commit classification=transition actor=\"1\" operation=\"2\" observe=Active;", 0, 1).Count));
        test("comment-looking nonannotation ignored", () => Equal(0, Markup.Parse("hello @tla 2 model A;", 0, 1).Count));
        test("TLA nested comments and strings", () => Equal(2, Markup.TlaComments("\"(* not a comment *)\"\n(* outer (* nested *) *)\n\\* line").Count));
        test("TLA unterminated comment", () => Throws(() => Markup.TlaComments("(* broken")));
        test("UTF16LE original byte hash", () =>
        {
            string path = Path.Combine(work, "utf16le.cs");
            File.WriteAllText(path, "class A { }\r\n", Encoding.Unicode);
            Dictionary<string, string> pins = [];
            Equal("class A { }\r\n", Files.ReadPinned(path, pins));
            Equal(Files.Hash(path), pins[path]);
        });
        test("UTF16BE", () =>
        {
            string path = Path.Combine(work, "utf16be.cs");
            File.WriteAllText(path, "class A { }", Encoding.BigEndianUnicode);
            Equal("class A { }", Files.Read(path));
        });
        test("invalid UTF8 explicit failure", () =>
        {
            string path = Path.Combine(work, "invalid.cs"); File.WriteAllBytes(path, [0xff, 0xff, 0xff]);
            ThrowsAny<DecoderFallbackException>(() => Files.Read(path));
        });
        test("fingerprint merge rejects conflict", () =>
        {
            Dictionary<string, string> pins = []; Files.Pin(pins, "same", "first");
            Throws(() => Files.Pin(pins, "same", "different"));
        });
        test("new-file writes never overwrite", () =>
        {
            string path = Path.Combine(work, "exclusive.json");
            Files.WriteNew(path, new { good = true });
            ThrowsAny<IOException>(() => Files.WriteNew(path, new { good = false }));
        });
        test("missing explicit scope fails", () => Throws(() => Files.Discover([Path.Combine(work, "absent")], ".cs")));
        test("wrong extension fails", () => Throws(() => Files.Discover([Path.Combine(work, "exclusive.json")], ".cs")));

        string source = """
            /* @tla 2 model M; */
            class C {
              readonly object gate = new();
              /* @tla 2 state Active guard=gate type=int; */
              int active;
              void Admit() { lock (gate) {
                /* @tla 2 step Admit actor="1" operation="1" point=commit classification=transition observe=Active; */
                active++;
              }}
            }
            """;
        (CSharpScope, List<ModelBinding>, Packet) Scope(string text, string name)
        {
            string path = Path.Combine(work, name + ".cs"); File.WriteAllText(path, text);
            CSharpScope scope = CSharpScope.Parse([path]);
            List<ModelBinding> model = scope.Bindings.Select(b => new ModelBinding(b.Model, b.Tag,
                new(b.Tag.Identity, "", b.Tag.Kind, new("synthetic.tla", 1, 1), ""), 0)).ToList();
            return (scope, model, Review.Build("check", scope, model, [], []));
        }
        test("Roslyn resolved scalar state and lock", () =>
        {
            var (scope, _, packet) = Scope(source, "resolved");
            Equal("passed", packet.LinksStatus); Equal(2, packet.Links.Count);
            Equal("gate", packet.Links.Single(l => l.Kind == "step").Locks.Single());
            InstrumentManifest manifest = Instrumentation.Generate(scope, ScopeModels(scope), packet, Path.Combine(work, "instrumented"));
            Equal(1, manifest.Events.Count); Equal("int", manifest.Events[0].Observations.Single().Type);
        });
        test("unrelated same-named field is not mapped", () =>
        {
            var (_, _, packet) = Scope(source + "\nclass Other { int active; void F() { active++; } }", "unrelated");
            Equal("passed", packet.LinksStatus);
            Equal(1, packet.Links.Single(l => l.Kind == "state").CandidateAccesses.Count);
        });
        test("same-line write outside linked span is detected", () =>
        {
            var (_, _, packet) = Scope(source.Replace("active++;", "active++; active++;"), "adjacent");
            True(packet.Diagnostics.Any(d => d.Code == "TL200"));
        });
        test("deconstruction writes detected", () =>
        {
            var (_, _, packet) = Scope(source.Replace("active++;", "active++; (active, active) = (1, 2);"), "deconstruct");
            True(packet.Diagnostics.Any(d => d.Code == "TL200"));
        });
        test("ref escape detected", () =>
        {
            var (_, _, packet) = Scope(source.Replace("active++;", "active++; ref int escaped = ref active;"), "refescape");
            True(packet.Diagnostics.Any(d => d.Code == "TL200"));
        });
        test("unresolved compilation is incomplete, never instrumentation success", () =>
        {
            var (scope, models, packet) = Scope(source.Replace("active++;", "Missing.Call();"), "unresolved");
            Equal("incomplete", packet.LinksStatus);
            Throws(() => Instrumentation.Generate(scope, models, packet, Path.Combine(work, "unresolved-out")));
        });
        test("CSharp syntax errors are structural failures", () =>
        {
            var (_, _, packet) = Scope(source.Replace("active++;", "active++; ???;"), "syntax");
            Equal("failed", packet.LinksStatus); True(packet.Diagnostics.Any(d => d.Code == "TL010"));
        });
        test("model namespace uses nearest annotated type", () =>
        {
            string text = source.Replace("void Admit()", "/* @tla 2 model Inner; */ class Nested { /* @tla 2 state Count; */ int count; }\nvoid Admit()");
            var (scope, _, _) = Scope(text, "nested");
            Equal("Inner", scope.Bindings.Single(b => b.Tag.Identity == "Count").Model);
        });
        test("multiple model namespaces on one CSharp type are not silently selected", () =>
        {
            var (_, _, packet) = Scope(source.Replace("model M;", "model M; model Other;"), "ambiguous-namespace");
            Equal("failed", packet.LinksStatus); True(packet.Diagnostics.Any(d => d.Code == "TL102"));
        });
        test("string cannot create CSharp annotations", () =>
        {
            var (scope, _, _) = Scope(source.Replace("int active;", "int active; string ignored = \"/* @tla 2 step Fake; */\";"), "string");
            True(scope.Bindings.All(b => b.Tag.Identity != "Fake"));
        });
        test("unsupported CSharp annotation version rejected explicitly", () =>
        {
            var (_, _, packet) = Scope(source.Replace("@tla 2", "@tla 1"), "unsupported-version");
            Equal("failed", packet.LinksStatus); True(packet.Diagnostics.Any(d => d.Message.Contains("Use @tla 2")));
        });
        test("conditional compilation uses supplied symbols", () =>
        {
            string path = Path.Combine(work, "conditional.cs");
            File.WriteAllText(path, "#if PILOT\n" + source + "\n#endif");
            Equal(0, CSharpScope.Parse([path], defines: []).Bindings.Count);
            Equal(2, CSharpScope.Parse([path], defines: ["PILOT"]).Bindings.Count);
        });
        test("selection requires one linked step", () =>
        {
            var (scope, models, _) = Scope(source, "selection");
            Equal(1, Review.Build("review", scope, models, [], [], "Admit").Links.Count);
            Throws(() => Review.Build("review", scope, models, [], [], "Missing"));
        });
        test("duplicate model stable IDs rejected", () =>
        {
            var (scope, models, _) = Scope(source, "duplicates");
            models.Add(models[0]);
            True(Review.Build("check", scope, models, [], []).Diagnostics.Any(d => d.Code == "TL102"));
        });
        foreach ((string name, string candidate) in new[] {
            ("wrong guard", source.Replace("guard=gate", "guard=missing")),
            ("mutable guard", source.Replace("readonly object gate", "object gate")),
            ("wrong observation type", source.Replace("type=int", "type=bool")),
            ("unresolved classification", source.Replace("classification=transition", "classification=unresolved")),
            ("unknown observation", source.Replace("observe=Active", "observe=Unknown")),
            ("missing actor", source.Replace("actor=\"1\" ", "")),
            ("missing operation", source.Replace("operation=\"1\" ", "")),
            ("getter identity", source.Replace("class C {", "class C { int Identity => 1;").Replace("actor=\"1\"", "actor=Identity")),
            ("getter receiver chain", source.Replace("class C {", "class C { C Owner => this; readonly int identity = 1;").Replace("actor=\"1\"", "actor=\"Owner.identity\"")),
            ("another receiver lock", source.Replace("void Admit()", "void Admit(C other)").Replace("lock (gate)", "lock (other.gate)")),
            ("getter guard chain", source.Replace("class C {", "class C { C Owner => this;").Replace("guard=gate", "guard=\"Owner.gate\"").Replace("lock (gate)", "lock (Owner.gate)")),
            ("prefix increment identity", source.Replace("actor=\"1\"", "actor=\"++active\"")),
            ("call identity", source.Replace("actor=\"1\"", "actor=\"GetHashCode()\"")),
            ("hook inside lock", source.Replace("point=commit", "hook=before point=commit")),
            ("fault capture", source.Replace("point=commit", "point=fault")),
            ("duplicate observation", source.Replace("observe=Active", "observe=\"Active,Active\""))
        }) test("instrumentation rejects " + name, () =>
        {
            var (scope, models, packet) = Scope(candidate, "reject-" + name.Replace(' ', '-'));
            Throws(() => Instrumentation.Generate(scope, models, packet, Path.Combine(work, "out-" + name.Replace(' ', '-'))));
        });
        test("comments cannot jump over declarations", () =>
        {
            var (_, _, packet) = Scope(source.Replace("int active;", "void Intervening() {} int active;"), "attachment");
            True(packet.Diagnostics.Any(d => d.Code == "TL001"));
        });
        test("calls callbacks await and finally remain explicit review boundaries", () =>
        {
            string text = """
                /* @tla 2 model M; */
                class C {
                  System.Threading.Tasks.Task Work() => System.Threading.Tasks.Task.CompletedTask;
                  void Run(System.Action action) => action();
                  void Cleanup() {}
                  async System.Threading.Tasks.Task F() {
                    /* @tla 2 step Block; */
                    { try { Run(() => Cleanup()); await Work(); } finally { Cleanup(); } }
                  }
                }
                """;
            var (_, _, packet) = Scope(text, "boundaries");
            var kinds = packet.Links.Single().Boundaries.Select(b => b.Kind).ToHashSet();
            True(kinds.Contains("InvocationExpression") && kinds.Any(k => k.EndsWith("LambdaExpression", StringComparison.Ordinal)) &&
                kinds.Contains("AwaitExpression") && kinds.Contains("FinallyClause"));
            Equal("not-run", packet.ConformanceStatus);
        });
        test("Unicode prefixes and intervening ordinary comments preserve UTF16 source spans", () =>
        {
            string text = "/* @tla 2 model M; */ class C { void F() { string x = \"\\u03bb\"; /* @tla 2 step X; */ // comment\n x = \"a\"; } }";
            text = text.Replace("\\u03bb", "\u03bb", StringComparison.Ordinal);
            var (scope, _, _) = Scope(text, "unicode");
            Equal("x = \"a\";", scope.Bindings.Single().Endpoint.Source);
            Equal(2, scope.Bindings.Single().Endpoint.Location.Line);
        });
        test("duplicate CSharp stable IDs fail structural review", () =>
        {
            var (_, _, packet) = Scope(source.Replace("active++;", "active++; /* @tla 2 step Admit; */ active--;"), "duplicate-source");
            True(packet.Diagnostics.Any(d => d.Code == "TL102"));
            Equal("failed", packet.LinksStatus);
        });
    }

    private static List<ModelBinding> ScopeModels(CSharpScope scope) => scope.Bindings.Select(b => new ModelBinding(b.Model, b.Tag,
        new(b.Tag.Identity, "", b.Tag.Kind, new("synthetic.tla", 1, 1), ""), 0)).ToList();
    public static void True(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    public static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}."); }
    public static void Throws(Action action) => ThrowsAny<InputException>(action);
    public static void ThrowsAny<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
