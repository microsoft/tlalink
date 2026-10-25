using System.Text.Json;
using System.Text.Json.Nodes;
using TlaLink;

namespace TlaLink.Tests;

public static class EvidenceHardeningTests
{
    public static void Register(Action<string, Action> test, string root, string work)
    {
        string dafny = TestTools.Dafny(root);
        string Summary(string text) => JsonSerializer.Serialize(new { type = "status", value = "\n" + text + "\n" });
        string Diagnostic(string message, string source = "Verifier") => JsonSerializer.Serialize(new {
            type = "diagnostic", value = new { severity = 1, source, defaultFormatMessage = message,
                relatedInformation = new[] { new { defaultFormatMessage = "ensures result == !canceled // resource limit" } } }
        });
        string Classify(string log, int? exit = 4, string state = "failed") =>
            DafnyBackend.ClassifyVerification(new(state, "native.log", exit), log);
        test("proof classifier uses native JSON outcomes, not source snippets or identifiers", () =>
        {
            string failed = Diagnostic("a postcondition could not be proved on this return path") + "\n" +
                Summary("Dafny program verifier finished with 0 verified, 1 error");
            CoreTests.Equal("rejected", Classify(failed));
            CoreTests.Equal("rejected", Classify(failed.Replace("canceled", "inconclusive out of memory", StringComparison.Ordinal)));
            CoreTests.Equal("verified", Classify(Summary("Dafny program verifier finished with 1 verified, 0 errors"), 0, "passed"));
            CoreTests.Equal("incomplete", Classify(failed, null, "incomplete"));
            CoreTests.Equal("incomplete", Classify(failed, 1));
            CoreTests.Equal("incomplete", Classify("ensures result == !canceled\n" + failed));
            CoreTests.Equal("incomplete", Classify(failed + "\n" + Summary("Dafny program verifier finished with 0 verified, 1 error")));
            CoreTests.Equal("incomplete", Classify("{broken json"));
            CoreTests.Equal("incomplete", Classify(Summary("Dafny program verifier finished with 1 verified, 0 errors")));
            CoreTests.Equal("incomplete", Classify(Diagnostic("invalid input", "Parser") + "\n" +
                Summary("Dafny program verifier finished with 0 verified, 1 error")));
        });
        test("proof classifier keeps timeout resource and solver failures incomplete", () =>
        {
            foreach (string outcome in new[] { "1 time out", "1 out of resource", "1 inconclusive" })
                CoreTests.Equal("incomplete", Classify(Diagnostic("a postcondition could not be proved") + "\n" +
                    Summary("Dafny program verifier finished with 0 verified, 1 error, " + outcome)));
            foreach (string message in new[] { "Verification timed out (canceled)", "Verification out of resource (Bad)",
                "Verification inconclusive (Bad)", "Prover died", "Solver exception", "Out of memory" })
                CoreTests.Equal("incomplete", Classify(Diagnostic(message) + "\n" +
                    Summary("Dafny program verifier finished with 0 verified, 1 error")));
        });
        test("native bad postcondition using canceled is rejected, not incomplete", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "hardening-canceled")).FullName;
            string source = Path.Combine(directory, "Decisions.cs"), proof = Path.Combine(directory, "Decisions.dfy");
            File.WriteAllText(source, "public static class Decisions { public static void Bad(bool canceled, out bool result) { result = canceled; } }");
            File.WriteAllText(proof, "method Bad(canceled: bool) returns (result: bool)\n ensures result == !canceled\n{ result := canceled; }\n");
            string bindings = Path.Combine(directory, "bindings.json");
            Files.WriteNew(bindings, new IntegratedManifest(Schema.Version, [source], [proof],
                [new("bad", "Decisions.Bad", "Bad", null, [])], [], []));
            var run = TestCli.Run(["verify-integrated", "--bindings", bindings, "--dafny", dafny,
                "--output", Path.Combine(directory, "result"), "--timeout", "60"]);
            if (run.Code != 1) throw new InvalidOperationException(run.Json);
            using JsonDocument document = JsonDocument.Parse(run.Json);
            CoreTests.Equal("rejected", document.RootElement.GetProperty("proof_status").GetString());
            CoreTests.Equal(4, document.RootElement.GetProperty("stages").GetProperty("dafny-verify").GetProperty("exit_code").GetInt32());
            using JsonDocument invocation = JsonDocument.Parse(Files.Read(Path.Combine(directory, "result", "dafny-verify.invocation.json")));
            CoreTests.Equal(Schema.Version, invocation.RootElement.GetProperty("schema_version").GetInt32());
            CoreTests.Equal("process-invocation", invocation.RootElement.GetProperty("kind").GetString());
            CoreTests.Equal(0, IntegratedEvidence.Validate(document.RootElement).Count);
            var inspection = IntegratedEvidence.Inspect(Path.Combine(directory, "result", "result.json"), true);
            CoreTests.Equal(0, inspection.ExitCode);
            CoreTests.Equal("consistent", inspection.SemanticStatus);
            CoreTests.Equal("rejected", inspection.RecordedStatus);
            CoreTests.Equal("not-evaluated", inspection.AcceptanceStatus);
        });

        JsonObject Packet() => JsonSerializer.SerializeToNode(new IntegratedPacket("test-integrated"), Files.Json)!.AsObject();
        List<Issue> Validate(JsonObject packet)
        {
            using JsonDocument document = JsonDocument.Parse(packet.ToJsonString());
            return IntegratedEvidence.Validate(document.RootElement);
        }
        JsonObject Complete()
        {
            JsonObject packet = Packet();
            packet["status"] = "passed"; packet["links_status"] = "passed"; packet["proof_status"] = "verified";
            packet["audit_status"] = "passed"; packet["conformance_status"] = "conforms"; packet["provenance_status"] = "consistent";
            packet["links"] = JsonNode.Parse("""[{"id":"tested"},{"id":"untested"}]""");
            JsonObject generated = packet["generated_files"]!.AsObject(), stages = packet["stages"]!.AsObject();
            string P(string name) => Path.Combine(work, "semantic", name);
            string hash = new('a', 64);
            foreach (string name in new[] { "dafny-version", "dafny-bridge", "dafny-discover", "dafny-resolve", "dafny-verify",
                "dafny-audit", "case-preconditions", "oracle-translate", "oracle-compare" })
            {
                string log = P(name == "dafny-bridge" ? "DafnyIndex.dll" : name + ".log");
                stages[name] = JsonSerializer.SerializeToNode(new StageResult("passed", log, 0), Files.Json);
                generated[log] = hash;
                if (name != "dafny-bridge") generated[P(name + ".invocation.json")] = hash;
            }
            foreach (string name in new[] { "dafny-discover.json", "dafny-resolve.json", "dafny-audit.txt", "Cases.dfy",
                "Oracle.cs", "Conformance.cs", "Conformance.dll", "Conformance.runtimeconfig.json", "case-results.json" })
                generated[P(name)] = hash;
            packet["fingerprints"]![P("Source.cs")] = hash;
            packet["fingerprints"]![P("Framework.dll")] = hash;
            packet["execution"] = new JsonObject {
                ["binary"] = P("Conformance.dll"), ["binary_sha256"] = hash, ["comparison_sha256"] = hash,
                ["compile_inputs"] = new JsonObject { [P("Source.cs")] = hash, [P("Oracle.cs")] = hash, [P("Conformance.cs")] = hash },
                ["references"] = new JsonObject { [P("Framework.dll")] = hash },
                ["compiler_context"] = new JsonObject { ["framework"] = "net10.0", ["language_version"] = "CSharp14",
                    ["nullable"] = "Enable", ["compiler"] = P("Framework.dll"), ["defines"] = new JsonArray() },
                ["tested_bindings"] = new JsonArray("tested"),
                ["untested_bindings"] = new JsonArray("untested"),
                ["cases"] = JsonNode.Parse("""[{"binding":"tested","id":"one","expected":["true"],"actual":["true"],"matches":true}]""")
            };
            return packet;
        }
        test("semantic validation retains empty incomplete packets without invented acceptance", () =>
        {
            CoreTests.Equal(0, Validate(Packet()).Count);
            CoreTests.Equal(0, Validate(Complete()).Count);
        });
        test("semantic validation rejects root stage command and composition contradictions", () =>
        {
            Action<JsonObject>[] mutations = [
                p => p["proof_status"] = "rejected",
                p => p["audit_status"] = "findings",
                p => p["command"] = "inspect-integrated",
                p => p["status"] = "ready",
                p => p["stages"]!["dafny-verify"]!["exit_code"] = 4,
                p => p["generated_files"]!.AsObject().Clear(),
                p => p["correspondence_status"] = "proved",
                p => p["correspondences"] = JsonNode.Parse("""[{"status":"passed","composition_status":"proved","claim":{"id":"claim","dafny_binding":"tested"}}]"""),
                p => p["schema_version"] = 3,
                p => p["kind"] = "trace-validation"
            ];
            foreach (Action<JsonObject> mutate in mutations)
            {
                JsonObject packet = Complete(); mutate(packet);
                CoreTests.True(Validate(packet).Count > 0);
            }
        });
        test("semantic validation reconciles actual case results and tested untested identifiers", () =>
        {
            Action<JsonObject>[] mutations = [
                p => p["execution"]!["cases"]![0]!["matches"] = false,
                p => p["execution"]!["cases"]![0]!["actual"]![0] = "false",
                p => p["execution"]!["cases"]![0]!["binding"] = "unknown",
                p => p["execution"]!["cases"]!.AsArray().Add(p["execution"]!["cases"]![0]!.DeepClone()),
                p => p["execution"]!["cases"] = new JsonArray(),
                p => p["execution"]!["tested_bindings"] = new JsonArray("tested", "untested"),
                p => p["execution"]!["untested_bindings"] = new JsonArray("tested"),
                p => p["execution"]!["binary_sha256"] = new string('b', 64),
                p => p["conformance_status"] = "rejected",
                p => p["execution"] = null
            ];
            foreach (Action<JsonObject> mutate in mutations)
            {
                JsonObject packet = Complete(); mutate(packet);
                CoreTests.True(Validate(packet).Count > 0);
            }
            JsonObject rejected = Complete();
            rejected["status"] = "rejected"; rejected["conformance_status"] = "rejected";
            rejected["execution"]!["cases"]![0]!["actual"]![0] = "false";
            rejected["execution"]!["cases"]![0]!["matches"] = false;
            CoreTests.Equal(0, Validate(rejected).Count);
        });
        test("semantic contradictions fail read even when archived bytes are consistent", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "hardening-reader")).FullName;
            string input = Path.Combine(directory, "input.txt"); File.WriteAllText(input, "pinned");
            string hash = Files.Hash(input);
            string archive = Directory.CreateDirectory(Path.Combine(directory, "inputs")).FullName;
            File.Copy(input, Path.Combine(archive, hash));
            JsonObject packet = Packet(); packet["fingerprints"]![input] = hash;
            packet["status"] = "passed";
            string path = Path.Combine(directory, "result.json"); File.WriteAllText(path, packet.ToJsonString());
            var read = IntegratedEvidence.Inspect(path, true);
            CoreTests.Equal("consistent", read.ArchiveStatus); CoreTests.Equal("inconsistent", read.SemanticStatus);
            CoreTests.Equal(1, read.ExitCode); CoreTests.Equal("not-evaluated", read.AcceptanceStatus);
            packet["status"] = "incomplete"; File.WriteAllText(path, packet.ToJsonString());
            CoreTests.Equal(0, IntegratedEvidence.Inspect(path, true).ExitCode);
            File.AppendAllText(input, "changed");
            CoreTests.Equal(2, IntegratedEvidence.Inspect(path, true).ExitCode);
            CoreTests.Equal(0, IntegratedEvidence.Inspect(path, false).ExitCode);
            File.AppendAllText(Path.Combine(archive, hash), "corrupt");
            CoreTests.Equal(1, IntegratedEvidence.Inspect(path, false).ExitCode);
            packet["schema_version"] = 3; File.WriteAllText(path, packet.ToJsonString());
            bool rejected = false;
            try { IntegratedEvidence.Inspect(path, false); } catch (InputException) { rejected = true; }
            CoreTests.True(rejected);
        });
    }
}
