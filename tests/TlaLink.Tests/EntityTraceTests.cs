using System.Text.Json;
using System.Text.Json.Nodes;
using TlaLink;

namespace TlaLink.Tests;

public static class EntityTraceTests
{
    public static void Register(Action<string, Action> test, string root, string work)
    {
        test("entity observations distinguish independently guarded lanes with explicit handoff", () => Run(root, Path.Combine(work, "entity-handoff"), "healthy"));
        test("entity observation rejects corruption confined to the second lane", () => Run(root, Path.Combine(work, "entity-corrupt"), "corrupt"));
        test("reversed explicit handoff rejects the required model ordering", () => Run(root, Path.Combine(work, "entity-reverse"), "reverse"));
        test("independent lanes without handoff are not ordered by logging sequence or join", () => Run(root, Path.Combine(work, "entity-unordered"), "unordered"));
        test("one entity cannot switch physical observation guards", () => Run(root, Path.Combine(work, "entity-guard"), "guard"));
        test("capture document versions and kinds are explicit", () => DocumentControls(Path.Combine(work, "entity-documents")));
        foreach (string variant in new[] { "getter", "call", "boolean", "mutable", "unheld", "missing-entity", "arity" })
        {
            string selected = variant;
            test("entity capture rejects " + selected, () => RejectCapture(Path.Combine(work, "entity-reject-" + selected), selected));
        }
    }

    private static void RejectCapture(string output, string variant)
    {
        Directory.CreateDirectory(output);
        string text = """
            /* @link model Fixture; */
            class Lane {
                readonly object gate = new();
                readonly int id = 1;
                int Identity => id;
                /* @link state Value guard=gate type=int; */
                int value;
                void Move() {
                    lock (gate) {
                        /* @link step Move actor="1" operation="1" entity=id point=commit classification=transition observe=Value; */
                        value = 1;
                    }
                }
            }
            """;
        text = variant switch
        {
            "getter" => text.Replace("entity=id", "entity=Identity", StringComparison.Ordinal),
            "call" => text.Replace("entity=id", "entity=\"GetHashCode()\"", StringComparison.Ordinal),
            "boolean" => text.Replace("entity=id", "entity=\"true\"", StringComparison.Ordinal),
            "mutable" => text.Replace("readonly int id", "int id", StringComparison.Ordinal),
            "unheld" => text.Replace("lock (gate)", "if (true)", StringComparison.Ordinal),
            "missing-entity" => text.Replace("entity=id ", "", StringComparison.Ordinal),
            _ => text
        };
        string source = Path.Combine(output, "Lane.cs");
        File.WriteAllText(source, text);
        CSharpScope scope = CSharpScope.Parse([source]);
        List<ModelBinding> models = scope.Bindings.Select(b => new ModelBinding(b.Model, b.Tag,
            new(b.Tag.Identity, "", b.Tag.Kind, new("synthetic.tla", 1, 1), ""),
            b.Tag.Kind == "state" ? variant == "arity" ? 2 : 1 : 0)).ToList();
        Packet packet = Review.Build("instrument", scope, models, [], []);
        CoreTests.Throws(() => Instrumentation.Generate(scope, models, packet, Path.Combine(output, "generated")));
    }

    private static void DocumentControls(string output)
    {
        Directory.CreateDirectory(output);
        InstrumentManifest manifest = new(Schema.Version, "", [], [], [
            new("Fixture", "Move", "Move", 1, "commit", "transition", [new("Value", "Value", "int", 1)], true)
        ], "synthetic decoding control");
        manifest = manifest with { BindingId = Files.HashText(JsonSerializer.Serialize(new { manifest.Fingerprints, manifest.Events }, Files.Json)) };
        string path = Path.Combine(output, "bindings.json");
        Files.WriteNew(path, manifest);
        CoreTests.Equal(Schema.Version, Instrumentation.ReadManifest(path).SchemaVersion);
        JsonObject document = JsonNode.Parse(Files.Read(path))!.AsObject();
        foreach (int version in new[] { 1, 2, 3, 5 })
        {
            document["schema_version"] = version;
            Reject(document, path);
        }
        document["schema_version"] = Schema.Version;
        document.Remove("kind");
        Reject(document, path);
        CoreTests.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<InstrumentManifest>(document.ToJsonString(), Files.Json));
        document["kind"] = "trace";
        Reject(document, path);
        document["kind"] = "instrumentation-manifest";
        document["unexpected"] = true;
        Reject(document, path);
        document.Remove("unexpected");
        document["events"]![0]!.AsObject().Remove("entity");
        Reject(document, path);
        ExecutionReceipt receipt = new(Schema.Version, "", "", "", [], [], "", "", "", [], [], [], "decoding control");
        JsonObject receiptDocument = JsonSerializer.SerializeToNode(receipt, Files.Json)!.AsObject();
        receiptDocument.Remove("kind");
        CoreTests.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<ExecutionReceipt>(receiptDocument.ToJsonString(), Files.Json));
    }

    private static void Reject(JsonObject document, string path)
    {
        File.WriteAllText(path, document.ToJsonString());
        try { Instrumentation.ReadManifest(path); }
        catch (Exception error) when (error is InputException or JsonException) { return; }
        throw new InvalidOperationException("Unsupported or malformed capture document was accepted.");
    }

    private static void Run(string root, string output, string mode)
    {
        Directory.CreateDirectory(output);
        string source = Path.Combine(output, "Lane.cs"), model = Path.Combine(output, "EntityFixture.tla");
        File.WriteAllText(source, """
            /* @link model EntityFixture; */
            class Lane
            {
                readonly object gate = new();
                readonly int id;
                /* @link state Value guard=gate type=int; */
                int value;
                public Lane(int id) { this.id = id; }
                public void Move()
                {
                    lock (gate)
                    {
                        /* @link step Move actor="1" operation="1" entity=id point=commit classification=transition observe=Value; */
                        value = MUTATION;
                    }
                }
            }
            """.Replace("MUTATION", mode == "corrupt" ? "id == 2 ? 2 : 1" : "1", StringComparison.Ordinal));
        File.WriteAllText(model, """
            ---- MODULE EntityFixture ----
            EXTENDS Integers
            (* @link model EntityFixture; *)
            VARIABLE x
            vars == <<x>>
            Init == x = [i \in 1..2 |-> 0]
            (* @link state Value type=int; *)
            Value(entity) == x[entity]
            (* @link step Move; *)
            Move == \E lane \in 1..2:
                      /\ x[lane] = 0
                      /\ (lane = 1 \/ x[1] = 1)
                      /\ x' = [x EXCEPT ![lane] = 1]
            Next == Move
            Spec == Init /\ [][Next]_vars
            ====
            """);
        string config = Path.Combine(output, "fixture.cfg");
        File.WriteAllText(config, "SPECIFICATION Spec\n");
        string generated = Path.Combine(output, "generated");
        string jar = TestTools.Jar(root);
        var instrument = TestCli.Run(["instrument", "--annotation-version", "2", "--source", source, "--model", model, "--jar", jar, "--output", generated]);
        if (instrument.Code != 0) throw new InvalidOperationException(instrument.Json);
        string first = mode is "reverse" or "unordered" ? "b" : "a";
        string second = mode is "reverse" or "unordered" ? "a" : "b";
        bool handoff = mode != "unordered";
        File.WriteAllText(Path.Combine(generated, "Program.cs"), $$"""
            using System;
            using System.Threading;
            using TlaLink.Runtime;
            class Program
            {
                static void Main(string[] args)
                {
                    var a = new Lane(1);
                    var b = new Lane({{(mode == "guard" ? 1 : 2)}});
                    TraceHandoff? handoff = null;
                    Exception? failure = null;
                    var first = new Thread(() => {
                        try { {{first}}.Move(); {{(handoff ? "handoff = TraceRuntime.CaptureHandoff();" : "")}} }
                        catch (Exception error) { failure = error; }
                    });
                    var second = new Thread(() => {
                        try {
                            {{(handoff ? "TraceRuntime.AcceptHandoff(handoff!);" : "")}}
                            {{second}}.Move();
                        }
                        catch (Exception error) { failure = error; }
                    });
                    first.Start(); first.Join();
                    second.Start(); second.Join();
                    if (failure is not null) throw failure;
                    TraceRuntime.Complete(args[0]);
                }
            }
            """);
        string project = Path.Combine(generated, "Fixture.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>");
        TestCli.Restore(project, output, Path.Combine(root, "NuGet.Config"));
        string manifest = Path.Combine(generated, "bindings.json"), trace = Path.Combine(output, "trace.json"), execution = Path.Combine(output, "execution");
        var run = TestCli.Run(["execute", "--manifest", manifest, "--project", project, "--trace", trace, "--output", execution]);
        if (run.Code != 0) throw new InvalidOperationException(run.Json);
        using JsonDocument captured = JsonDocument.Parse(Files.Read(trace));
        Files.RequireSchema(captured.RootElement, "trace");
        JsonElement[] events = captured.RootElement.GetProperty("events").EnumerateArray().ToArray();
        CoreTests.Equal(2, events.Length);
        CoreTests.Equal(mode is "reverse" or "unordered" ? 2 : 1, events[0].GetProperty("entity").GetInt32());
        if (events[0].GetProperty("guard").GetInt64() == events[1].GetProperty("guard").GetInt64())
            throw new InvalidOperationException("Independent lane instances did not retain independent physical guards.");
        long[] causes = events[1].GetProperty("causes").EnumerateArray().Select(e => e.GetInt64()).ToArray();
        CoreTests.Equal(handoff ? 1 : 0, causes.Length);
        if (handoff) CoreTests.Equal(1L, causes[0]);
        else CoreTests.Equal(0, events[1].GetProperty("predecessors").GetArrayLength());
        var check = TestCli.Run(["validate-trace", "--annotation-version", "2", "--model", model, "--jar", jar, "--manifest", manifest, "--trace", trace,
            "--execution", Path.Combine(execution, "execution.json"), "--config", config, "--output", Path.Combine(output, "validation")]);
        if (mode == "guard")
        {
            if (check.Code == 0 || !check.Json.Contains("different source guards", StringComparison.Ordinal))
                throw new InvalidOperationException("Changing one entity's physical guard was not rejected: " + check.Json);
            return;
        }
        using JsonDocument result = JsonDocument.Parse(check.Json);
        Files.RequireSchema(result.RootElement, "trace-validation");
        string expected = mode is "corrupt" or "reverse" ? "rejected" : "conforms";
        if (result.RootElement.GetProperty("status").GetString() != expected) throw new InvalidOperationException(check.Json);
    }
}
