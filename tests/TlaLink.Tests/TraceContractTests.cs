using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TlaLink;
using TlaLink.Runtime;

namespace TlaLink.Tests;

public static class TraceContractTests
{
    private const string Trace = """
        {
          "schema_version": 4,
          "kind": "trace",
          "binding_id": "synthetic-binding",
          "complete": true,
          "event_count": 1,
          "runtime": "synthetic-runtime",
          "os": "synthetic-os",
          "executable": "synthetic-binary",
          "binaries": { "synthetic-binary": "synthetic-hash" },
          "events": [{
            "sequence": 1,
            "model": "Synthetic",
            "step": "Apply",
            "point": "invoke",
            "classification": "transition",
            "connection": 1,
            "actor": "actor",
            "operation": "operation",
            "observations": {},
            "guard": null,
            "thread": 1,
            "causes": [],
            "predecessors": []
          }],
          "boundary": "Synthetic trace contract fixture."
        }
        """;

    public static void Register(Action<string, Action> test, string work)
    {
        string directory = Directory.CreateDirectory(Path.Combine(work, "trace-contracts")).FullName;
        string path = Path.Combine(directory, "trace.json");
        TraceEnvelope Read(JsonObject root)
        {
            File.WriteAllText(path, root.ToJsonString());
            return TraceDocuments.Read(path);
        }
        JsonObject Fixture() => JsonNode.Parse(Trace)!.AsObject();
        void Reject(Action<JsonObject> mutate)
        {
            JsonObject root = Fixture();
            mutate(root);
            CoreTests.ThrowsAny<JsonException>(() => Read(root));
        }

        test("trace runtime scalar conversion preserves supported integer boolean and string wire values", () =>
        {
            object[] values = [sbyte.MinValue, sbyte.MaxValue, byte.MaxValue, short.MinValue, short.MaxValue,
                ushort.MaxValue, int.MinValue, int.MaxValue, uint.MaxValue, long.MinValue, long.MaxValue,
                true, false, "", "quotes \" slash \\ newline \n unicode é"];
            foreach (object value in values)
            {
                TraceScalar scalar = TraceScalar.From(value);
                CoreTests.Equal(JsonSerializer.Serialize(value), JsonSerializer.Serialize(scalar));
                CoreTests.Equal(value is not bool, scalar.IsIdentity);
                TraceScalar parsed = JsonSerializer.Deserialize<TraceScalar>(JsonSerializer.Serialize(scalar))!;
                CoreTests.Equal(scalar.Kind, parsed.Kind);
                CoreTests.Equal(scalar.Integer, parsed.Integer);
                CoreTests.Equal(scalar.Boolean, parsed.Boolean);
                CoreTests.Equal(scalar.Text, parsed.Text);
            }
        });
        test("trace parsed scalar and operation identity serialization retain exact JSON element spellings", () =>
        {
            foreach (string wire in new[] { "-0", "0", "1", "-9223372036854775808", "9223372036854775807",
                "\"\\u0061\"", "\"\\u003C\"", "\"é\"", "\"quote\\\"slash\\\\\"", "true", "false" })
            {
                using JsonDocument document = JsonDocument.Parse(wire);
                TraceScalar scalar = JsonSerializer.Deserialize<TraceScalar>(wire)!;
                CoreTests.Equal(JsonSerializer.Serialize(document.RootElement), JsonSerializer.Serialize(scalar));
                CoreTests.Equal(
                    JsonSerializer.Serialize(new object?[] { document.RootElement, document.RootElement, document.RootElement, null }),
                    JsonSerializer.Serialize(new object?[] { scalar, scalar, scalar, null }));
                CoreTests.Equal(
                    JsonSerializer.Serialize(new { model = "Synthetic", Connection = document.RootElement, Id = "State", Entity = (string?)null }),
                    JsonSerializer.Serialize(new { model = "Synthetic", Connection = scalar, Id = "State", Entity = (string?)null }));
            }
        });
        test("trace runtime scalar boundary rejects null unsigned64 floating point and structured values", () =>
        {
            foreach (object? value in new object?[] { null, 1UL, ulong.MaxValue, 1.0, 1.0f, 1m, 'a', new object(), new[] { 1 } })
                CoreTests.ThrowsAny<InvalidOperationException>(() => TraceScalar.From(value!));
        });
        test("trace JSON scalars reject null fractional exponent overflow and structured values", () =>
        {
            foreach (string wire in new[] { "null", "1.5", "1.0", "1e0", "9223372036854775808",
                "-9223372036854775809", "18446744073709551615", "{}", "[]", "[1]" })
                CoreTests.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<TraceScalar>(wire));
        });
        test("trace envelope requires every fixed metadata member and nonnull reference metadata", () =>
        {
            foreach (string field in new[] { "schema_version", "kind", "binding_id", "complete", "event_count",
                "runtime", "os", "executable", "binaries", "events", "boundary" })
                Reject(root => root.Remove(field));
            foreach (string field in new[] { "schema_version", "kind", "binding_id", "complete", "event_count",
                "runtime", "os", "executable", "binaries", "events", "boundary" })
                Reject(root => root[field] = null);
            JsonObject wrongKind = Fixture();
            wrongKind["kind"] = "not-a-trace";
            CoreTests.Throws(() => Read(wrongKind));
            JsonObject wrongVersion = Fixture();
            wrongVersion["schema_version"] = Schema.Version + 1;
            CoreTests.Throws(() => Read(wrongVersion));
        });
        test("trace event requires all metadata including a present nullable guard", () =>
        {
            foreach (string field in new[] { "sequence", "model", "step", "point", "classification", "connection",
                "actor", "operation", "observations", "guard", "thread", "causes", "predecessors" })
                Reject(root => root["events"]![0]!.AsObject().Remove(field));
            foreach (string field in new[] { "sequence", "model", "step", "point", "classification", "connection",
                "actor", "operation", "observations", "thread", "causes", "predecessors" })
                Reject(root => root["events"]![0]![field] = null);
            CoreTests.True(Read(Fixture()).Events[0].Guard is null);
        });
        test("trace optional entity and arguments remain omitted on a typed roundtrip", () =>
        {
            TraceEnvelope trace = Read(Fixture());
            CoreTests.True(trace.Events[0].Entity is null && trace.Events[0].Arguments is null);
            JsonObject saved = JsonNode.Parse(JsonSerializer.Serialize(trace, Files.Json))!.AsObject();
            CoreTests.True(!saved["events"]![0]!.AsObject().ContainsKey("entity"));
            CoreTests.True(!saved["events"]![0]!.AsObject().ContainsKey("arguments"));
        });
        test("trace present null entity and arguments never become omitted captures", () =>
        {
            Reject(root => root["events"]![0]!["entity"] = null);
            Reject(root => root["events"]![0]!["arguments"] = null);
        });
        test("trace typed entity arguments and observations retain scalar values and Int64 boundaries", () =>
        {
            JsonObject root = Fixture();
            root["events"]![0]!["entity"] = "lane";
            root["events"]![0]!["arguments"] = new JsonArray(long.MinValue, true, "label", long.MaxValue);
            root["events"]![0]!["observations"] = new JsonObject { ["Count"] = long.MaxValue, ["Ready"] = false };
            TraceEvent entry = Read(root).Events[0];
            CoreTests.Equal("lane", entry.Entity!.Text);
            CoreTests.Equal(long.MinValue, entry.Arguments![0].Integer);
            CoreTests.True(entry.Arguments[1].Boolean);
            CoreTests.Equal("label", entry.Arguments[2].Text);
            CoreTests.Equal(long.MaxValue, entry.Arguments[3].Integer);
            CoreTests.Equal(long.MaxValue, entry.Observations["Count"].Integer);
            CoreTests.True(!entry.Observations["Ready"].Boolean);
        });
        test("trace collections reject wrong shapes null members and noninteger causal identities", () =>
        {
            Reject(root => root["events"]![0]!["arguments"] = new JsonArray((JsonNode?)null));
            Reject(root => root["events"]![0]!["arguments"] = new JsonObject());
            Reject(root => root["events"]![0]!["observations"] = new JsonObject { ["Value"] = null });
            Reject(root => root["events"]![0]!["observations"] = new JsonArray());
            Reject(root => root["events"]![0]!["causes"] = new JsonArray(1.5));
            Reject(root => root["events"]![0]!["predecessors"] = new JsonArray((JsonNode?)null));
            JsonObject nullEvent = Fixture();
            nullEvent["events"] = new JsonArray((JsonNode?)null);
            CoreTests.Throws(() => Read(nullEvent));
            JsonObject nullBinary = Fixture();
            nullBinary["binaries"]!["synthetic-binary"] = null;
            CoreTests.Throws(() => Read(nullBinary));
        });
        test("trace duplicate metadata binary pins and observation members cannot collapse during deserialization", () =>
        {
            foreach ((string before, string after) in new[] {
                ("\"event_count\": 1", "\"event_count\": 0, \"event_count\": 1"),
                ("\"synthetic-binary\": \"synthetic-hash\"", "\"synthetic-binary\": \"other\", \"synthetic-binary\": \"synthetic-hash\""),
                ("\"observations\": {}", "\"observations\": {\"State\": 1, \"State\": 2}")
            })
            {
                File.WriteAllText(path, Trace.Replace(before, after, StringComparison.Ordinal));
                CoreTests.ThrowsAny<JsonException>(() => TraceDocuments.Read(path));
            }
        });
        test("trace validation result exposes typed analysis and input contracts without changing invocation wire shape", () =>
        {
            TraceValidationResult result = new(Schema.Version, "trace-validation", "validate-trace", "conforms",
                1, "synthetic-binding", 12, false, null, new(1, 512), 30,
                new("synthetic-java", ["tlc2.TLC"], "synthetic-output"), new("Init", "Next", ["Hidden"]),
                [], "wrapper", "config", new() { ["synthetic-input"] = "pin" }, "log", "Synthetic scope.");
            IAnalysisResult analysis = result;
            IInputEvidence inputs = result;
            CoreTests.True(ReferenceEquals(result.Diagnostics, analysis.Diagnostics));
            CoreTests.True(ReferenceEquals(result.Fingerprints, inputs.Fingerprints));
            JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(result, Files.Json))!.AsObject();
            CoreTests.Equal("conforms", json["status"]!.GetValue<string>());
            CoreTests.Equal(3, json["invocation"]!.AsObject().Count);
            CoreTests.Equal("synthetic-java", json["invocation"]!["executable"]!.GetValue<string>());
            CoreTests.True(json.ContainsKey("requested_resources") && json["requested_resources"] is null);
            CoreTests.Equal("Hidden", json["mapping"]!["hidden"]![0]!.GetValue<string>());
        });
        test("trace deployed runtime compiles standalone with only the generated schema and framework references", () =>
        {
            string source = Path.Combine(directory, "CompilerContext.cs");
            File.WriteAllText(source, "public class CompilerContext {}");
            CSharpScope scope = CSharpScope.Parse([source]);
            CSharpParseOptions options = new(LanguageVersion.CSharp14);
            SyntaxTree runtime = CSharpSyntaxTree.ParseText(Files.Read(Path.Combine(AppContext.BaseDirectory, "TraceRuntime.source.cs")),
                options, "TlaLink.TraceRuntime.cs");
            SyntaxTree schema = CSharpSyntaxTree.ParseText(
                $"namespace TlaLink; public static class Schema {{ public const int Version = {Schema.Version}; }}",
                options, "TlaLink.Schema.cs");
            using MemoryStream binary = new();
            var emitted = scope.Compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(runtime, schema).Emit(binary);
            if (!emitted.Success) throw new InvalidOperationException(string.Join("\n", emitted.Diagnostics));
            CoreTests.True(binary.Length > 0);
        });
    }
}
