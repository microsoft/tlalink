using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class IntegrationTests
{
    public static void Register(Action<string, Action> test, string root, string work)
    {
        string area = Directory.CreateDirectory(Path.Combine(work, "cli")).FullName;
        string source = Path.Combine(area, "Demo.cs"), model = Path.Combine(area, "Demo.tla");
        File.Copy(Path.Combine(root, @"examples\Demo.cs"), source);
        File.Copy(Path.Combine(root, @"examples\Demo.tla"), model);
        string jar = TestTools.Jar(root);
        string[] Common(string command) => [command, "--source", source, "--model", model, "--jar", jar];
        (int Code, string Json) Call(string[] args, int expected)
        {
            var result = TestCli.Run(args);
            if (result.Code != expected) throw new InvalidOperationException(result.Json);
            return result;
        }
        test("Demo resolves all three bindings without modifying inputs", () =>
        {
            string before = Files.Hash(model);
            var result = Call(Common("inspect"), 0);
            using JsonDocument packet = JsonDocument.Parse(result.Json);
            CoreTests.Equal(3, packet.RootElement.GetProperty("links").GetArrayLength());
            CoreTests.Equal("not-run", packet.RootElement.GetProperty("conformance_status").GetString());
            CoreTests.Equal(before, Files.Hash(model));
        });
        test("review selection resolves explicit PlusCal label", () => Call([.. Common("review"), "--step", "Admit"], 0));
        test("review ambiguous or absent selection is incomplete", () => Call([.. Common("review"), "--step", "Missing"], 2));
        string baseline = Path.Combine(area, "baseline.json");
        test("snapshot baseline compares identical fingerprints", () =>
        {
            Call([.. Common("check"), "--snapshot", baseline], 0);
            Call([.. Common("check"), "--baseline", baseline], 0);
        });
        test("snapshot cannot overwrite existing evidence", () => Call([.. Common("check"), "--snapshot", baseline], 2));
        test("source drift invalidates prior review", () =>
        {
            string text = Files.Read(source);
            try
            {
                File.WriteAllText(source, text + "\n");
                var result = Call([.. Common("check"), "--baseline", baseline], 2);
                CoreTests.True(result.Json.Contains("TL300", StringComparison.Ordinal));
            }
            finally { File.WriteAllText(source, text); }
        });
        test("malformed baseline fingerprint is rejected", () =>
        {
            string path = Path.Combine(area, "bad-baseline.json");
            File.WriteAllText(path, """{"schema_version":4,"kind":"source-review","fingerprints":{"bad":123}}""");
            Call([.. Common("check"), "--baseline", path], 2);
        });
        test("obsolete baseline schema is rejected rather than upgraded", () =>
        {
            string path = Path.Combine(area, "schema1-baseline.json");
            File.WriteAllText(path, """{"schema_version":1,"fingerprints":{"runtime:unrecognized":"different"}}""");
            Call([.. Common("check"), "--baseline", path], 2);
        });
        test("model-only readiness pins authoritative parser and closure", () =>
        {
            var result = Call(["inputs", "--model", model, "--jar", jar], 0);
            using JsonDocument packet = JsonDocument.Parse(result.Json);
            JsonElement fingerprints = packet.RootElement.GetProperty("fingerprints");
            CoreTests.Equal("ready", packet.RootElement.GetProperty("status").GetString());
            CoreTests.Equal(Schema.Version, packet.RootElement.GetProperty("schema_version").GetInt32());
            CoreTests.Equal(Files.Hash(jar), fingerprints.GetProperty(jar).GetString());
            CoreTests.True(fingerprints.TryGetProperty("runtime:dotnet", out _));
            CoreTests.True(fingerprints.EnumerateObject().Any(p => p.Name.EndsWith("TlaIndex.java", StringComparison.Ordinal)));
        });
        test("unsupported TLA annotation version explicitly fails review", () =>
        {
            string original = Files.Read(model);
            try { File.WriteAllText(model, original.Replace("@tla 2", "@tla 1", StringComparison.Ordinal)); Call(Common("check"), 1); }
            finally { File.WriteAllText(model, original); }
        });
        test("missing parser input is incomplete", () => Call(["inputs", "--model", Path.Combine(area, "absent.tla"), "--jar", jar], 2));
        test("unannotated inspection retains the full inventory and remains incomplete", () =>
        {
            string plainSource = Path.Combine(area, "Plain.cs"), plainModel = Path.Combine(area, "Plain.tla");
            File.WriteAllText(plainSource, "class Plain {}");
            File.WriteAllText(plainModel, "---- MODULE Plain ----\nValue == 1\n====\n");
            var result = Call(["inspect", "--source", plainSource, "--model", plainModel, "--jar", jar], 2);
            using JsonDocument packet = JsonDocument.Parse(result.Json);
            CoreTests.Equal("incomplete", packet.RootElement.GetProperty("links_status").GetString());
            CoreTests.True(packet.RootElement.GetProperty("inventory").EnumerateArray().Any(e => e.GetProperty("name").GetString() == "Value"));
        });
        test("unknown option fails closed", () => Call([.. Common("check"), "--guess", "true"], 2));
        test("inputs cannot accidentally accept source/snapshot options", () => Call(["inputs", "--model", model, "--jar", jar, "--source", source], 2));
        test("semantically invalid TLA is incomplete, not source/model mismatch acceptance", () =>
        {
            string invalid = Path.Combine(area, "Invalid.tla");
            File.WriteAllText(invalid, "---- MODULE Invalid ----\nMissing == UndefinedOperator\n====\n");
            Call(["check", "--source", source, "--model", invalid, "--jar", jar], 2);
        });
        test("stale PlusCal review remains incomplete", () =>
        {
            string original = Files.Read(model);
            try
            {
                File.WriteAllText(model, original.Replace("active := active + 1;", "active := active + 2;", StringComparison.Ordinal));
                var result = Call(Common("check"), 2);
                CoreTests.True(result.Json.Contains("TL400", StringComparison.Ordinal));
            }
            finally { File.WriteAllText(model, original); }
        });
    }
}
