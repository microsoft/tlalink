using System.Text;
using System.Text.Json;
using TlaLink;
using Microsoft.CodeAnalysis;

namespace TlaLink.Tests;

public static class SourcePreparationTests
{
    private static CompilerProfile Compiler => new("net10.0", "14", "enable", false, true, []);
    private static AnnotationPayload Payload(string id) => new() { Bind = [new(id, new("sample", id))] };

    public static void Register(Action<string, Action> test, string work)
    {
        string directory = Directory.CreateDirectory(Path.Combine(work, "source-preparation")).FullName;
        const string program = "public sealed class Gadget { int total; public void Apply() { total = 1; total = 1; } }\n";
        string source = Path.Combine(directory, "Gadget.cs");
        File.WriteAllText(source, program);
        SourcePreparationManifest Manifest(List<PreparationAnnotation>? annotations = null,
            List<PreparationMutation>? mutations = null) => new(Schema.Version, "source-preparation", Markup.Version, Compiler,
                [new(source, Files.Hash(source), Path.Combine("scope", "Gadget.cs"), annotations, mutations)]);
        (int Code, string Json, string Output, string ManifestPath) Run(SourcePreparationManifest manifest)
        {
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json");
            string output = path + ".prepared";
            Files.WriteNew(path, manifest);
            var result = TestCli.Run(["prepare-sources", "--manifest", path, "--output", output, "--format", "summary"]);
            return (result.Code, result.Json, output, path);
        }
        PreparationTarget repeated = new("M:Gadget.Apply", "ExpressionStatement", "total = 1 ;", 2, 2);
        test("source preparation uses symbols and syntax tokens rather than layout or surrounding text", () =>
        {
            var result = Run(Manifest([
                new(new("T:Gadget"), Payload("Gadget")),
                new(new("F:Gadget.total"), Payload("Total")),
                new(repeated, Payload("Update"))
            ]));
            if (result.Code != 0) throw new InvalidOperationException(result.Json);
            string copy = Path.Combine(result.Output, "scope", "Gadget.cs");
            CoreTests.Equal(program, Files.Read(source));
            CoreTests.True(Files.Read(copy).Contains("total = 1; /* @link " + Markup.Encode(Payload("Update")) + " */ total = 1;", StringComparison.Ordinal));
            CSharpScope parsed = CSharpScope.Parse([copy]);
            CoreTests.Equal(3, parsed.Bindings.Count);
            CoreTests.True(parsed.Diagnostics.All(d => d.Severity != "error"));
            PreparedSources ledger = ProjectInputs.Read<PreparedSources>(Path.Combine(result.Output, "prepared-sources.json"));
            CoreTests.True(ledger.Files.Single().CommentOnly);
            CoreTests.Equal(Files.Hash(source), ledger.Files.Single().SourceSha256);
            CoreTests.Equal(Files.Hash(copy), ledger.Files.Single().Sha256);
            string receipt = Path.Combine(result.Output, "prepared-sources.json");
            CoreTests.Equal(0, TestCli.Run(["check-prepared-sources", "--result", receipt]).Code);
            File.WriteAllText(Path.Combine(result.Output, "unexpected.cs"), "class Extra {}");
            CoreTests.Equal(2, TestCli.Run(["check-prepared-sources", "--result", receipt]).Code);
            CoreTests.Equal(2, TestCli.Run(["prepare-sources", "--manifest", result.ManifestPath,
                "--output", result.Output]).Code);
        });
        test("preparation retains original encoding BOM and line endings for comment-only copies", () =>
        {
            foreach (Encoding encoding in new Encoding[] { new UTF8Encoding(false), new UTF8Encoding(true),
                new UnicodeEncoding(false, true), new UnicodeEncoding(true, true) })
            foreach (string newline in new[] { "\n", "\r\n" })
            {
                string text = "public class Encoded" + newline + "{" + newline + "}" + newline;
                string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".cs");
                File.WriteAllText(path, text, encoding);
                byte[] original = File.ReadAllBytes(path);
                var result = Run(new(Schema.Version, "source-preparation", Markup.Version, Compiler,
                    [new(path, Files.Hash(path), "Encoded.cs", [new(new("T:Encoded"), Payload("Encoded"))])]));
                if (result.Code != 0) throw new InvalidOperationException(result.Json);
                byte[] actual = File.ReadAllBytes(Path.Combine(result.Output, "Encoded.cs"));
                byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes("/* @link " + Markup.Encode(Payload("Encoded")) + " */ " + text)];
                CoreTests.True(expected.SequenceEqual(actual));
                CoreTests.True(original.SequenceEqual(File.ReadAllBytes(path)));
            }
        });
        test("control mutations are explicit compilable and separate from comment-only output", () =>
        {
            var result = Run(Manifest([new(repeated, Payload("DeliberateControl"))],
                [new(repeated, "total = 7;", "Synthetic wrong-value control.")]));
            if (result.Code != 0) throw new InvalidOperationException(result.Json);
            PreparedSources ledger = ProjectInputs.Read<PreparedSources>(Path.Combine(result.Output, "prepared-sources.json"));
            CoreTests.True(!ledger.Files.Single().CommentOnly);
            CoreTests.Equal(1, ledger.Files.Single().Mutations.Count);
            CoreTests.True(Files.Read(Path.Combine(result.Output, "scope", "Gadget.cs"))
                .Contains("/* @link " + Markup.Encode(Payload("DeliberateControl")) + " */ total = 7;", StringComparison.Ordinal));
            CoreTests.Equal(program, Files.Read(source));
        });
        test("unedited preparation assets are byte copies without a text-decoding requirement", () =>
        {
            string path = Path.Combine(directory, "asset.bin");
            byte[] bytes = [0xff, 0xff, 0, 0xef];
            File.WriteAllBytes(path, bytes);
            var result = Run(new(Schema.Version, "source-preparation", Markup.Version, Compiler,
                [new(path, Files.Hash(path), "asset.bin")]));
            if (result.Code != 0) throw new InvalidOperationException(result.Json);
            CoreTests.True(bytes.SequenceEqual(File.ReadAllBytes(Path.Combine(result.Output, "asset.bin"))));
        });
        test("preparation pins compiler-only sources without copying them into the selected output", () =>
        {
            string context = Path.Combine(directory, "Context.cs"), selected = Path.Combine(directory, "Selected.cs");
            File.WriteAllText(context, "public class Context {}");
            File.WriteAllText(selected, "public class Selected : Context {}");
            SourcePreparationManifest manifest = new(Schema.Version, "source-preparation", Markup.Version, Compiler,
                [new(selected, Files.Hash(selected), "Selected.cs", [new(new("T:Selected"), Payload("Selected"))])],
                ContextSources: [new(context, Files.Hash(context))]);
            var result = Run(manifest);
            if (result.Code != 0) throw new InvalidOperationException(result.Json);
            CoreTests.True(!File.Exists(Path.Combine(result.Output, "Context.cs")));
            string receipt = Path.Combine(result.Output, "prepared-sources.json");
            File.AppendAllText(context, " ");
            CoreTests.Equal(2, TestCli.Run(["check-prepared-sources", "--result", receipt]).Code);
        });
        test("shared compiler context imports resolve their own relative pins and reject cycles", () =>
        {
            string shared = Directory.CreateDirectory(Path.Combine(directory, "shared-context")).FullName;
            string helper = Path.Combine(shared, "Helper.cs"), selected = Path.Combine(directory, "UsesHelper.cs");
            File.WriteAllText(helper, "public class Helper {}");
            File.WriteAllText(selected, "public class UsesHelper : Helper {}");
            string import = Path.Combine(shared, "context.json");
            SourcePreparationManifest context = new(Schema.Version, "source-preparation", Markup.Version, Compiler, [],
                ContextSources: [new("Helper.cs", Files.Hash(helper))]);
            Files.WriteNew(import, context);
            SourcePreparationManifest manifest = new(Schema.Version, "source-preparation", Markup.Version, Compiler,
                [new(selected, Files.Hash(selected), "UsesHelper.cs")], ContextImports: [import]);
            var good = Run(manifest);
            if (good.Code != 0) throw new InvalidOperationException(good.Json);
            PreparedSources saved = ProjectInputs.Read<PreparedSources>(Path.Combine(good.Output, "prepared-sources.json"));
            CoreTests.Equal(Files.Hash(import), saved.Fingerprints[import]);
            CoreTests.True(!File.Exists(Path.Combine(good.Output, "Helper.cs")));
            File.WriteAllText(import, JsonSerializer.Serialize(context with { ContextImports = ["context.json"] }, Files.Json));
            CoreTests.Equal(2, Run(manifest).Code);
            CoreTests.Equal(2, TestCli.Run(["check-prepared-sources", "--result",
                Path.Combine(good.Output, "prepared-sources.json")]).Code);
        });
        test("preparation resolves generic explicit interface implementations by their declared symbol identity", () =>
        {
            string path = Path.Combine(directory, "Explicit.cs");
            File.WriteAllText(path, "interface IThing<T> { T Finish(int ticket); } class Box<T> : IThing<T> { T IThing<T>.Finish(int ticket) { return default!; } }");
            CSharpScope scope = CSharpScope.Parse([path]);
            IMethodSymbol method = scope.Compilation.GetTypeByMetadataName("Box`1")!.GetMembers().OfType<IMethodSymbol>()
                .Single(m => m.ExplicitInterfaceImplementations.Length == 1);
            var result = Run(new(Schema.Version, "source-preparation", Markup.Version, Compiler,
                [new(path, Files.Hash(path), "Explicit.cs", [new(new(method.GetDocumentationCommentId()!,
                    "ReturnStatement", "return default!;"), Payload("Finish"))])]));
            if (result.Code != 0) throw new InvalidOperationException(result.Json);
            CoreTests.True(Files.Read(Path.Combine(result.Output, "Explicit.cs")).Contains("@link " + Markup.Encode(Payload("Finish")), StringComparison.Ordinal));
        });
        test("ambiguous missing stale and escaping preparation inputs fail before publication", () =>
        {
            void Reject(SourcePreparationManifest manifest)
            {
                var result = Run(manifest);
                CoreTests.Equal(2, result.Code);
                CoreTests.True(!Directory.Exists(result.Output));
                using JsonDocument document = JsonDocument.Parse(result.Json);
                CoreTests.True(document.RootElement.GetProperty("diagnostics").GetArrayLength() > 0);
            }
            Reject(Manifest([new(new("M:Gadget.Apply", "ExpressionStatement", "total = 1;"), Payload("Ambiguous"))]));
            Reject(Manifest([new(new("M:Gadget.Absent"), Payload("Missing"))]));
            SourcePreparationManifest basic = Manifest();
            Reject(basic with { Files = [basic.Files[0] with { Sha256 = new string('0', 64) }] });
            Reject(basic with { Files = [basic.Files[0] with { Destination = Path.Combine("..", "escaped.cs") }] });
            Reject(basic with { Files = [basic.Files[0] with { Destination = source }] });
            Reject(basic with { Files = [basic.Files[0], basic.Files[0]] });
            Reject(basic with { Files = [basic.Files[0] with { Destination = "parent" },
                basic.Files[0] with { Destination = Path.Combine("parent", "Child.cs") }] });
            Reject(Manifest([new(new("T:Gadget"), Payload("bad*/ public class Injected {}"))]));
            Reject(Manifest(null, [new(repeated, "total = missing;", "Ill-typed control must not publish.")]));
            Reject(Manifest(null, [new(repeated, "total = 2;", "")]));
            Reject(Manifest(null, [new(repeated, "total = 2;", "First."), new(repeated, "total = 3;", "Overlapping.")]));
        });
        test("preparation reports null list entries instead of crashing", () =>
        {
            string path = Path.Combine(directory, "null-file.json");
            System.Text.Json.Nodes.JsonNode node = JsonSerializer.SerializeToNode(Manifest(), Files.Json)!;
            node["files"]![0] = null;
            Files.WriteNew(path, node);
            var result = TestCli.Run(["prepare-sources", "--manifest", path, "--output", path + ".output"]);
            CoreTests.Equal(2, result.Code);
            CoreTests.True(result.Json.Contains("SP001", StringComparison.Ordinal));
            CoreTests.True(!Directory.Exists(path + ".output"));
        });
    }

    public static void RegisterNative(Action<string, Action> test, string work, string jar)
    {
        test("formal preparation preserves native endpoint identities without inserting source bindings", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "prepare-native")).FullName;
            string model = Path.Combine(directory, "Sample.tla");
            File.WriteAllText(model, """
                ---- MODULE Sample ----
                EXTENDS Naturals
                VARIABLE n
                Init == n = 0
                Advance == n' = n + 1
                ====
                """);
            SourcePreparationManifest manifest = new(Schema.Version, "source-preparation", Markup.Version, Compiler,
                [new(model, Files.Hash(model), "Sample.tla", [
                    new(new("Init", "operator"), Payload("Sample")),
                    new(new("Advance", "operator"), Payload("Advance"))
                ])], new(jar, Files.Hash(jar)));
            string path = Path.Combine(directory, "prepare.json"), output = Path.Combine(directory, "prepared");
            Files.WriteNew(path, manifest);
            var result = TestCli.Run(["prepare-sources", "--manifest", path, "--output", output]);
            CoreTests.Equal(2, result.Code);
            CoreTests.True(!Directory.Exists(output));
            Files.WriteNew(path + ".copy", manifest with { Files = [manifest.Files[0] with { Annotations = [] }] });
            result = TestCli.Run(["prepare-sources", "--manifest", path + ".copy", "--output", output]);
            if (result.Code != 0) throw new InvalidOperationException(result.Json);
            ModelScope parsed = ModelScope.Load([Path.Combine(output, "Sample.tla")], jar, 30);
            CoreTests.True(parsed.Diagnostics.All(d => d.Severity != "error"));
            CoreTests.True(parsed.Inventory.Any(e => e.Name == "Advance"));
            CoreTests.Equal(0, parsed.Bindings.Count);
        });
    }
}
