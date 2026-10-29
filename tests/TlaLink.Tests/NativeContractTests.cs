using System.Text.Json;
using System.Text.Json.Nodes;
using TlaLink;

namespace TlaLink.Tests;

public static class NativeContractTests
{
    public static void Register(Action<string, Action> test, string work)
    {
        ApalachePacket packet = new()
        {
            Config = Path.Combine(work, "analysis.json"), ConfigSha256 = new('0', 64),
            Mode = "bounded", Directory = Path.Combine(work, "native-contracts"),
            Java = Path.Combine(work, "java"), Jar = Path.Combine(work, "apalache.jar"),
            Model = Path.Combine(work, "Demo.tla"), TimeoutSeconds = 37
        };
        ApalacheAnalysis analysis = new(Schema.Version, "apalache-analysis", "native-contracts",
            "bounded", packet.Model, "Init", "Next", ["Safe"], [], Length: 2);
        ApalacheQuery query = new("safety", "check", "Init", ["Safe"], 2);
        test("native Apalache configuration preserves every approved checker field and hyphenated name", () =>
        {
            ApalacheNativeConfig config = ApalacheBackend.NativeConfig(packet, analysis, query);
            string directory = JsonSerializer.Serialize(Path.Combine(packet.Directory, query.Id));
            using JsonDocument expected = JsonDocument.Parse($$"""
                {
                  "out-dir": {{directory}}, "run-dir": {{directory}},
                  "write-intermediate": false, "typechecker": {"infer-poly": true},
                  "checker": {
                    "init": "Init", "next": "Next", "inv": ["Safe"], "length": 2,
                    "algo": "incremental", "smt-solver": "z3", "smt-encoding": "oopsla19",
                    "discard-disabled": false, "no-deadlock": true, "timeout-smt": 37,
                    "max-error": 1
                  }
                }
                """);
            JsonElement actual = JsonSerializer.SerializeToElement(config, Files.Json);
            CoreTests.True(JsonElement.DeepEquals(expected.RootElement, actual));
            CoreTests.True(ApalacheEvidence.NativeConfigMatches(expected.RootElement.GetRawText(), config));
            CoreTests.True(!actual.GetProperty("checker").TryGetProperty("cinit", out _));
            ApalacheNativeConfig initialized = ApalacheBackend.NativeConfig(packet,
                analysis with { ConstInit = "Constants" }, query);
            JsonElement checker = JsonSerializer.SerializeToElement(initialized, Files.Json).GetProperty("checker");
            CoreTests.Equal("Constants", checker.GetProperty("cinit").GetString());
            CoreTests.Equal(12, checker.EnumerateObject().Count());
        });
        test("native Apalache typechecking omits checker rather than emitting null or checker defaults", () =>
        {
            ApalacheNativeConfig config = ApalacheBackend.NativeConfig(packet, analysis,
                query with { Mode = "typecheck" });
            JsonElement actual = JsonSerializer.SerializeToElement(config, Files.Json);
            CoreTests.Equal(4, actual.EnumerateObject().Count());
            CoreTests.True(!actual.TryGetProperty("checker", out _));
            CoreTests.True(actual.GetProperty("typechecker").GetProperty("infer-poly").GetBoolean());
            CoreTests.True(!actual.GetProperty("write-intermediate").GetBoolean());
            JsonObject explicitNull = JsonNode.Parse(actual.GetRawText())!.AsObject();
            explicitNull["checker"] = null;
            CoreTests.True(!ApalacheEvidence.NativeConfigMatches(explicitNull.ToJsonString(), config));
        });
        test("native Apalache configuration comparison retains unknown missing duplicate and type checks", () =>
        {
            ApalacheNativeConfig config = ApalacheBackend.NativeConfig(packet, analysis, query);
            string json = JsonSerializer.Serialize(config, Files.Json);
            CoreTests.True(ApalacheEvidence.NativeConfigMatches(json, config));
            foreach (string container in new[] { "", "checker", "typechecker" })
            {
                JsonObject changed = JsonNode.Parse(json)!.AsObject();
                JsonObject target = container.Length == 0 ? changed : changed[container]!.AsObject();
                target["unexpected"] = true;
                CoreTests.True(!ApalacheEvidence.NativeConfigMatches(changed.ToJsonString(), config));
            }
            foreach (string field in new[] { "infer-poly", "write-intermediate", "timeout-smt" })
            {
                string value = field == "timeout-smt" ? "37" : field == "infer-poly" ? "true" : "false";
                string duplicate = json.Replace($"\"{field}\":", $"\"{field}\": {value}, \"{field}\":",
                    StringComparison.Ordinal);
                CoreTests.ThrowsAny<JsonException>(() => ApalacheEvidence.NativeConfigMatches(duplicate, config));
            }
            JsonObject missing = JsonNode.Parse(json)!.AsObject();
            missing["checker"]!.AsObject().Remove("no-deadlock");
            CoreTests.True(!ApalacheEvidence.NativeConfigMatches(missing.ToJsonString(), config));
            JsonObject wrongType = JsonNode.Parse(json)!.AsObject();
            wrongType["checker"]!["length"] = "2";
            CoreTests.True(!ApalacheEvidence.NativeConfigMatches(wrongType.ToJsonString(), config));
            JsonObject explicitNull = JsonNode.Parse(json)!.AsObject();
            explicitNull["checker"]!["cinit"] = null;
            CoreTests.True(!ApalacheEvidence.NativeConfigMatches(explicitNull.ToJsonString(), config));
        });
        test("native TLA row discriminators roundtrip all variants without a conflicting kind property", () =>
        {
            const string json = """
                [
                  {"kind":"operator","name":"Init","scope":"Demo","line":2,"column":1,"endLine":2,"endColumn":12},
                  {"name":"x","kind":"variable","scope":"Demo","line":1,"column":1,"endLine":1,"endColumn":1},
                  {"kind":"label","name":"Step","scope":"Demo","line":3,"column":1,"endLine":3,"endColumn":4},
                  {"kind":"dependency","path":"library:Naturals","sha256":"abc"},
                  {"kind":"translation","state":"current","detail":"","line":1},
                  {"kind":"assumptions","module":"Demo","count":1},
                  {"kind":"operator-level","name":"Init","level":1}
                ]
                """;
            List<TlaNativeRow> rows = TlaNativeJson.ReadIndex(json);
            CoreTests.Equal(7, rows.Count);
            CoreTests.True(rows[0] is TlaNativeOperator { Kind: "operator", EndColumn: 12 });
            CoreTests.True(rows[1] is TlaNativeVariable { Kind: "variable" });
            CoreTests.True(rows[2] is TlaNativeLabel { Kind: "label" });
            CoreTests.True(rows[3] is TlaNativeDependency { Path: "library:Naturals", Sha256: "abc" });
            CoreTests.True(rows[4] is TlaNativeTranslation { State: "current", Detail: "", Line: 1 });
            CoreTests.True(rows[5] is TlaNativeAssumptions { Module: "Demo", Count: 1 });
            CoreTests.True(rows[6] is TlaNativeOperatorLevel { Name: "Init", Level: 1 });
            JsonElement serialized = JsonSerializer.SerializeToElement(rows, TlaNativeJson.Options);
            using JsonDocument expected = JsonDocument.Parse(json);
            CoreTests.True(JsonElement.DeepEquals(expected.RootElement, serialized));
            foreach (JsonElement row in serialized.EnumerateArray())
                CoreTests.Equal(1, row.EnumerateObject().Count(property => property.Name == "kind"));
        });
        test("native TLA documents retain typed rows for Apalache without a raw JSON payload", () =>
        {
            IReadOnlyList<TlaNativeRow> index = Array.AsReadOnly<TlaNativeRow>(
                [new TlaNativeOperatorLevel("Init", 1), new TlaNativeOperatorLevel("Next", 2),
                    new TlaNativeOperatorLevel("Safe", 1)]);
            TlaDocument model = new(packet.Model, "", new('0', 64),
                new[] { "Init", "Next", "Safe" }.Select(name =>
                    new TlaEndpoint(name, "operator", "Demo", 0, 0, 1, 1, 1, 1)).ToArray(),
                [], new Dictionary<string, string>())
                { NativeIndex = index };
            CoreTests.True(ReferenceEquals(index, model.NativeIndex));
            ApalacheBackend.RequireSupportedModel(model, analysis);
            CoreTests.ThrowsAny<InputException>(() =>
                ApalacheBackend.RequireSupportedModel(model with { NativeIndex = null }, analysis));
            CoreTests.ThrowsAny<InputException>(() =>
                ApalacheBackend.RequireSupportedModel(model with
                {
                    NativeIndex = index.Append(new TlaNativeAssumptions("Demo", 1)).ToArray()
                }, analysis));
            string archive = JsonSerializer.Serialize(model.NativeIndex, TlaNativeJson.Options);
            List<TlaNativeRow> archived = TlaNativeJson.ReadIndex(archive);
            ApalacheBackend.RequireSupportedIndex(archived, analysis);
            CoreTests.Equal(3, archived.Count);
        });
        test("native TLA bridge rejects malformed envelopes row variants and metadata shapes", () =>
        {
            foreach (string json in new[]
            {
                "null", "{}", "[null]", "[{}]", """[{"kind":"unknown"}]""",
                """[{"kind":"operator","name":"Init"}]""",
                """[{"kind":"dependency","path":null,"sha256":"abc"}]""",
                """[{"kind":"operator-level","name":"Init","level":"1"}]"""
            })
                CoreTests.ThrowsAny<JsonException>(() => TlaNativeJson.ReadIndex(json));
            TlaNativeMetadata metadata = JsonSerializer.Deserialize<TlaNativeMetadata>(
                """{"module":"Demo","variables":["x"],"arities":{"Init":0}}""", TlaNativeJson.Options)!;
            CoreTests.Equal("Demo", metadata.Module);
            CoreTests.Equal("x", metadata.Variables.Single());
            CoreTests.Equal(0, metadata.Arities["Init"]);
            foreach (string json in new[]
            {
                """{"module":"Demo","arities":{}}""",
                """{"module":null,"variables":[],"arities":{}}""",
                """{"module":"Demo","variables":null,"arities":{}}""",
                """{"module":"Demo","variables":[],"arities":{"Init":"0"}}"""
            })
                CoreTests.ThrowsAny<JsonException>(() =>
                    JsonSerializer.Deserialize<TlaNativeMetadata>(json, TlaNativeJson.Options));
        });
        test("native P and reused Dafny frontend contracts require typed envelope collections", () =>
        {
            string path = JsonSerializer.Serialize(Path.Combine(work, "Demo.p"));
            string json = $$"""
                {"schema_version":{{Schema.Version}},"kind":"p-source-index","compiler_version":"synthetic",
                 "profile":"pchecker","declarations":[{"name":"Demo","kind":"machine","path":{{path}},"line":1,"end_line":2}]}
                """;
            PFrontendIndex index = JsonSerializer.Deserialize<PFrontendIndex>(json, Files.Json)!;
            CoreTests.Equal(Schema.Version, index.SchemaVersion);
            CoreTests.Equal("p-source-index", index.Kind);
            CoreTests.Equal("Demo", index.Declarations.Single().Name);
            foreach (string value in new[] { "null", "{}", "\"declarations\"" })
            {
                JsonObject malformed = JsonNode.Parse(json)!.AsObject();
                malformed["declarations"] = JsonNode.Parse(value);
                CoreTests.ThrowsAny<JsonException>(() =>
                    JsonSerializer.Deserialize<PFrontendIndex>(malformed.ToJsonString(), Files.Json));
            }
            JsonObject missing = JsonNode.Parse(json)!.AsObject();
            missing.Remove("declarations");
            CoreTests.ThrowsAny<JsonException>(() =>
                JsonSerializer.Deserialize<PFrontendIndex>(missing.ToJsonString(), Files.Json));
            DafnyIndexResult dafny = JsonSerializer.Deserialize<DafnyIndexResult>(
                """{"declarations":[],"dependencies":["Demo.dfy"]}""", Files.Json)!;
            CoreTests.Equal("Demo.dfy", dafny.Dependencies.Single());
            foreach (string malformed in new[]
            {
                """{"declarations":[]}""",
                """{"declarations":null,"dependencies":[]}""",
                """{"declarations":[],"dependencies":{}}"""
            })
                CoreTests.ThrowsAny<JsonException>(() =>
                    JsonSerializer.Deserialize<DafnyIndexResult>(malformed, Files.Json));
        });
        test("native Dafny messages retain discriminator order tolerance and diagnostic field presence", () =>
        {
            DafnyNativeMessage? Read(string json) =>
                JsonSerializer.Deserialize<DafnyNativeMessage>(json, DafnyNativeJson.Options);
            CoreTests.True(Read("""{"type":"status","value":"finished"}""")
                is DafnyNativeStatus { Value: "finished" });
            CoreTests.True(Read("""{"value":"finished","type":"status"}""")
                is DafnyNativeStatus { Value: "finished" });
            CoreTests.True(Read("""{"type":"status","value":null}""")
                is DafnyNativeStatus { Value: null });
            CoreTests.True(Read("""{"type":"diagnostic","value":{"severity":1,"source":"Verifier","defaultFormatMessage":null}}""")
                is DafnyNativeDiagnostic { Value.HasDefaultFormatMessage: true, Value.DefaultFormatMessage: null });
            CoreTests.True(Read("""{"type":"diagnostic","value":{"severity":2}}""")
                is DafnyNativeDiagnostic { Value.HasDefaultFormatMessage: false });
            CoreTests.True(Read("""{"type":"diagnostic","value":{"severity":1,"source":"Verifier","defaultFormatMessage":"failure","relatedInformation":[{}]}}""")
                is DafnyNativeDiagnostic { Value.DefaultFormatMessage: "failure" });
        });
        test("native Dafny classifier keeps malformed message shapes and absent error messages incomplete", () =>
        {
            const string passed = """
                {"type":"status","value":"Dafny program verifier finished with 1 verified, 0 errors"}
                """;
            CoreTests.Equal("verified", DafnyBackend.ClassifyVerification(new("passed", "native.log", 0), passed));
            foreach (string malformed in new[]
            {
                "null", "{}", """{"value":"finished"}""", """{"type":"unknown","value":""}""",
                """{"type":"status"}""", """{"type":"status","value":1}""",
                """{"type":"diagnostic","value":null}""",
                """{"type":"diagnostic","value":{"severity":"1"}}"""
            })
                CoreTests.Equal("incomplete", DafnyBackend.ClassifyVerification(
                    new("passed", "native.log", 0), passed + "\n" + malformed));
            const string absentMessage = """
                {"type":"diagnostic","value":{"severity":1,"source":"Verifier"}}
                {"type":"status","value":"Dafny program verifier finished with 0 verified, 1 error"}
                """;
            CoreTests.Equal("incomplete", DafnyBackend.ClassifyVerification(new("failed", "native.log", 4), absentMessage));
            CoreTests.Equal("verified", DafnyBackend.ClassifyVerification(new("passed", "native.log", 0),
                """{"type":"diagnostic","value":{"severity":2}}""" + "\n" + passed));
        });
    }
}
