using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class IntegratedTests
{
    public static void Register(Action<string, Action> test, string root, string work)
    {
        string dafny = TestTools.Dafny(root);
        string jar = TestTools.Jar(root);
        string example = Path.Combine(root, "examples", "Integrated");
        string NewDirectory(string name) => Directory.CreateDirectory(Path.Combine(work, "integrated-" + name)).FullName;
        IntegratedManifest Example(string directory, bool models = false)
        {
            foreach (string name in new[] { "Decisions.cs", "Decisions.dfy", "Credits.tla" })
                File.Copy(Path.Combine(example, name), Path.Combine(directory, name));
            IntegratedManifest manifest = IntegratedInputs.Load(Path.Combine(example, "bindings.json"), []);
            manifest = IntegratedInputs.ResolveBindings(manifest, CSharpScope.Parse(manifest.Sources));
            return manifest with {
                Sources = ["Decisions.cs"], Proofs = ["Decisions.dfy"], Models = models ? ["Credits.tla"] : [],
                Correspondences = models ? manifest.Correspondences : [], Scope = "scope.json"
            };
        }
        (int Code, JsonElement Result) Run(string directory, IntegratedManifest manifest, string command = "test-integrated", string? baseline = null)
        {
            string path = Path.Combine(directory, "bindings.json");
            BindingFixtures.WriteIntegrated(path, manifest);
            List<string> args = [command, "--annotation-version", "3", "--bindings", path, "--dafny", dafny, "--output", Path.Combine(directory, "result"),
                "--jar", jar, "--timeout", "60"];
            if (baseline != null) args.AddRange(["--baseline", baseline]);
            var result = TestCli.Run(args.ToArray());
            using JsonDocument document = JsonDocument.Parse(result.Json);
            JsonElement packet = document.RootElement.Clone();
            CoreTests.True(File.Exists(Path.Combine(directory, "result", "result.json")));
            return (result.Code, packet);
        }
        void Status(JsonElement packet, string field, string expected) => CoreTests.Equal(expected, packet.GetProperty(field).GetString());
        void Diagnostic(JsonElement packet, string text) => CoreTests.True(packet.GetProperty("diagnostics").ToString().Contains(text, StringComparison.Ordinal));

        test("Dafny callback-expression bindings remain review-only and retain their actual source span", () =>
        {
            string directory = NewDirectory("callback-binding");
            File.WriteAllText(Path.Combine(directory, "Callback.cs"), """
                public static class Callback {
                  public static void Register() {
                    System.Action run = () =>
                      /* @link {"bind":[{"id":"callback","target":"proof::Claim"}]} */
                      System.GC.KeepAlive(1);
                    run();
                  }
                }
                """);
            File.WriteAllText(Path.Combine(directory, "Claim.dfy"), "lemma Claim() ensures 1 == 1 {}");
            Files.WriteNew(Path.Combine(directory, "scope.json"),
                new CorrespondenceConfiguration(Schema.Version, "correspondence-scope", Markup.Version,
                    [new("proof", "dafny", ["Claim.dfy"])], []));
            Files.WriteNew(Path.Combine(directory, "bindings.json"),
                new IntegratedManifest(Schema.Version, ["Callback.cs"], [],
                    [new("callback", "", "", null, [])], [], []) { Scope = "scope.json" });
            string output = Path.Combine(directory, "result");
            var response = TestCli.Run(["inspect-integrated", "--annotation-version", "3",
                "--bindings", Path.Combine(directory, "bindings.json"), "--dafny", dafny, "--output", output]);
            if (response.Code != 0) throw new InvalidOperationException(response.Json);
            JsonElement packet = ProjectInputs.Read<JsonElement>(Path.Combine(output, "result.json"));
            DafnyLink link = packet.GetProperty("links")[0].Deserialize<DafnyLink>(Files.Json)!;
            CoreTests.Equal("InvocationExpression", link.Implementation.Name);
            CoreTests.True(link.Specification.Ghost);
            CoreTests.Equal("not-run", packet.GetProperty("conformance_status").GetString());
            CoreTests.Equal(0, TestCli.Run(["read-integrated", "--result", Path.Combine(output, "result.json")]).Code);
        });

        test("integrated source obligations retain structural evidence without approving correspondence", () =>
        {
            string directory = NewDirectory("source-premises");
            File.WriteAllText(Path.Combine(directory, "Notification.cs"), SourceObligationTests.Fixture);
            File.WriteAllText(Path.Combine(directory, "Claim.dfy"), "lemma Claim() ensures 1 == 1 {}");
            var (code, packet) = Run(directory, SourceObligationTests.Manifest("Notification.cs"), "verify-integrated");
            CoreTests.Equal(0, code);
            Status(packet, "source_obligations_status", "passed");
            Status(packet, "proof_status", "verified"); Status(packet, "audit_status", "passed");
            Status(packet, "coverage_status", "requires-review"); Status(packet, "conformance_status", "not-run");
            CoreTests.Equal(4, packet.GetProperty("source_obligations").GetArrayLength());
            CoreTests.Equal(0, IntegratedEvidence.Validate(packet).Count);
            CoreTests.Equal(0, TestCli.Run(["read-integrated", "--result",
                Path.Combine(directory, "result", "result.json"), "--current", "true"]).Code);
            var forged = System.Text.Json.Nodes.JsonNode.Parse(packet.GetRawText())!;
            forged["source_obligations"]![0]!["status"] = "rejected";
            using JsonDocument changed = JsonDocument.Parse(forged.ToJsonString());
            CoreTests.True(IntegratedEvidence.Validate(changed.RootElement).Count > 0);
            forged = System.Text.Json.Nodes.JsonNode.Parse(packet.GetRawText())!;
            forged["source_obligations"] = new System.Text.Json.Nodes.JsonArray();
            forged["source_obligations_status"] = "not-specified";
            using JsonDocument omitted = JsonDocument.Parse(forged.ToJsonString());
            CoreTests.True(IntegratedEvidence.Validate(omitted.RootElement,
                Path.Combine(directory, "result", "inputs")).Any(i => i.Message.Contains("pinned binding manifest", StringComparison.Ordinal)));
        });
        test("integrated generic reference premises retain getter and declaration identities in archived evidence", () =>
        {
            string directory = NewDirectory("reference-premises");
            File.WriteAllText(Path.Combine(directory, "Memo.cs"), ReferenceObligationTests.Fixture);
            File.WriteAllText(Path.Combine(directory, "Claim.dfy"), "lemma Claim() ensures 1 == 1 {}");
            var (code, packet) = Run(directory, ReferenceObligationTests.Manifest("Memo.cs"), "verify-integrated");
            CoreTests.Equal(0, code);
            Status(packet, "source_obligations_status", "passed");
            Status(packet, "proof_status", "verified"); Status(packet, "coverage_status", "requires-review");
            CoreTests.Equal(5, packet.GetProperty("source_obligations").GetArrayLength());
            CoreTests.Equal(0, TestCli.Run(["read-integrated", "--result",
                Path.Combine(directory, "result", "result.json"), "--current", "true"]).Code);
        });
        test("project acceptance checks source premises against the run-level input archive", () =>
        {
            string directory = NewDirectory("source-project");
            File.WriteAllText(Path.Combine(directory, "Notification.cs"), SourceObligationTests.Fixture);
            File.WriteAllText(Path.Combine(directory, "Claim.dfy"), "lemma Claim() ensures 1 == 1 {}");
            BindingFixtures.WriteIntegrated(Path.Combine(directory, "bindings.json"), SourceObligationTests.Manifest("Notification.cs"));
            string project = Path.Combine(directory, "project.json");
            Files.WriteNew(project, new AnalysisProject(Schema.Version, "analysis-project", "notification",
                new(Dafny: new(dafny, Files.Hash(dafny))), new("net10.0", "14", "enable", false, true, []),
                "requirements.json", [new("premises", [new("proof", "verify", [], 60, "bindings.json")])],
                [], "reviews"));
            Files.WriteNew(Path.Combine(directory, "requirements.json"), new AcceptancePolicy(Schema.Version,
                "acceptance-policy", "notification", [new("proof", "premises", "proof", "passed")]));
            string output = Path.Combine(directory, "run");
            var execution = TestCli.Run(["run", "--project", project, "--scenario", "premises", "--output", output]);
            if (execution.Code != 0) throw new InvalidOperationException(execution.Json);
            var acceptance = TestCli.Run(["accept", "--project", project, "--run", output]);
            if (acceptance.Code != 0) throw new InvalidOperationException(acceptance.Json);
            using JsonDocument document = JsonDocument.Parse(acceptance.Json);
            Status(document.RootElement, "archive_status", "consistent");
            Status(document.RootElement, "current_applicability", "current");
        });
        test("integrated false source premises reject and unsupported cleanup stays incomplete before proof", () =>
        {
            foreach (bool unsupported in new[] { false, true })
            {
                string directory = NewDirectory(unsupported ? "source-unsupported" : "source-rejected");
                string body = unsupported ? "try { if (this.flag.Raise()) OnRaised(); } finally {}" :
                    "if (!this.flag.Raise()) OnRaised();";
                File.WriteAllText(Path.Combine(directory, "Notification.cs"), SourceObligationTests.Fixture.Replace(
                    "if (this.flag.Raise()) { OnRaised(); }", body, StringComparison.Ordinal));
                File.WriteAllText(Path.Combine(directory, "Claim.dfy"), "lemma Claim() ensures 1 == 1 {}");
                var (code, packet) = Run(directory, SourceObligationTests.Manifest("Notification.cs"), "inspect-integrated");
                CoreTests.Equal(unsupported ? 2 : 1, code);
                Status(packet, "source_obligations_status", unsupported ? "incomplete" : "rejected");
                Status(packet, "proof_status", "not-run"); Status(packet, "audit_status", "not-run");
                Diagnostic(packet, unsupported ? "SO200" : "SO100");
                CoreTests.Equal(0, IntegratedEvidence.Validate(packet).Count);
            }
        });
        test("integrated two backends retain separate evidence and unproved correspondence", () =>
        {
            string directory = NewDirectory("both");
            var (code, packet) = Run(directory, Example(directory, models: true));
            CoreTests.Equal(0, code);
            foreach (var field in new[] { "links_status", "audit_status" }) Status(packet, field, "passed");
            Status(packet, "proof_status", "verified"); Status(packet, "conformance_status", "conforms");
            Status(packet, "coverage_status", "requires-review"); Status(packet, "correspondence_status", "requires-review");
            Status(packet.GetProperty("correspondences")[0], "composition_status", "not-proved");
            Status(packet.GetProperty("tla"), "conformance_status", "not-run");
            CoreTests.Equal(10, packet.GetProperty("execution").GetProperty("cases").GetArrayLength());
            CoreTests.Equal(3, packet.GetProperty("execution").GetProperty("compile_inputs").EnumerateObject().Count());
            CoreTests.True(packet.GetProperty("execution").GetProperty("compile_inputs").EnumerateObject().All(p => Path.GetExtension(p.Name) == ".cs"));
            CoreTests.Equal(JsonValueKind.Object, packet.GetProperty("execution").GetProperty("references").ValueKind);
            foreach (var pin in packet.GetProperty("fingerprints").EnumerateObject().Where(p => Path.IsPathFullyQualified(p.Name)))
                CoreTests.Equal(pin.Value.GetString(), Files.Hash(Path.Combine(directory, "result", "inputs", pin.Value.GetString()!)));
            string result = Path.Combine(directory, "result", "result.json");
            CoreTests.Equal(0, TestCli.Run(["read-integrated", "--result", result, "--current", "true"]).Code);
            File.AppendAllText(Path.Combine(directory, "Decisions.cs"), "\n// changed after capture\n");
            var stale = TestCli.Run(["read-integrated", "--result", result, "--current", "true"]);
            CoreTests.Equal(2, stale.Code); CoreTests.True(stale.Json.Contains("DF302", StringComparison.Ordinal));
            CoreTests.Equal(0, TestCli.Run(["read-integrated", "--result", result]).Code);
            string hash = packet.GetProperty("fingerprints").GetProperty(Path.Combine(directory, "Decisions.cs")).GetString()!;
            File.AppendAllText(Path.Combine(directory, "result", "inputs", hash), "tampered");
            var corrupt = TestCli.Run(["read-integrated", "--result", result]);
            CoreTests.Equal(1, corrupt.Code); CoreTests.True(corrupt.Json.Contains("DF301", StringComparison.Ordinal));
        });
        test("integrated mutation is rejected for oracle mismatch, not a build failure", () =>
        {
            string directory = NewDirectory("mutation"); var manifest = Example(directory);
            string source = Path.Combine(directory, "Decisions.cs");
            File.WriteAllText(source, Files.Read(source).Replace("available + count", "available + count + 1", StringComparison.Ordinal));
            var (code, packet) = Run(directory, manifest);
            CoreTests.Equal(1, code); Status(packet, "proof_status", "verified");
            Status(packet, "conformance_status", "rejected"); Diagnostic(packet, "DF204");
        });
        test("integrated false proof is rejected before oracle execution", () =>
        {
            string directory = NewDirectory("false-proof"); var manifest = Example(directory);
            string proof = Path.Combine(directory, "Decisions.dfy");
            File.WriteAllText(proof, Files.Read(proof).Replace("ensures 0 <= next <= window", "ensures next < 0", StringComparison.Ordinal));
            var (code, packet) = Run(directory, manifest);
            CoreTests.Equal(1, code); Status(packet, "proof_status", "rejected");
            Status(packet, "conformance_status", "not-run"); Diagnostic(packet, "DF100");
        });
        test("integrated assumption audit blocks a vacuously verified model", () =>
        {
            string directory = NewDirectory("assumption"); var manifest = Example(directory);
            File.AppendAllText(Path.Combine(directory, "Decisions.dfy"), "\nlemma Assumed() ensures false { assume {:axiom} false; }\n");
            var (code, packet) = Run(directory, manifest);
            CoreTests.Equal(1, code); Status(packet, "proof_status", "verified"); Status(packet, "audit_status", "findings");
            Status(packet, "conformance_status", "not-run");
        });
        test("integrated invalid case preconditions are incomplete, never conformance", () =>
        {
            string directory = NewDirectory("preconditions"); var manifest = Example(directory);
            using JsonDocument inputs = JsonDocument.Parse("[1,2,1]");
            DafnyBinding binding = manifest.Bindings[0] with { Cases = [new("invalid", inputs.RootElement.EnumerateArray().Select(e => e.Clone()).ToList())] };
            var (code, packet) = Run(directory, manifest with { Bindings = [binding] });
            CoreTests.Equal(2, code); Status(packet, "proof_status", "verified");
            Status(packet, "conformance_status", "incomplete"); Diagnostic(packet, "DF201");
        });
        test("integrated unresolved symbols are not lexical matches", () =>
        {
            string directory = NewDirectory("missing"); var manifest = Example(directory);
            var (code, packet) = Run(directory, manifest with { Bindings = [manifest.Bindings[0] with { Dafny = "Other.Return" }] });
            CoreTests.Equal(2, code); Diagnostic(packet, "DF020"); Status(packet, "proof_status", "not-run");
        });
        test("integrated adapters must match resolved signatures", () =>
        {
            string directory = NewDirectory("signature"); var manifest = Example(directory);
            var (code, packet) = Run(directory, manifest with { Bindings = [manifest.Bindings[0] with { Adapter = "Decisions.Invert" }] });
            CoreTests.Equal(2, code); Diagnostic(packet, "DF200"); Status(packet, "conformance_status", "not-run");
        });
        test("integrated dangling correspondence cannot be reported as linked", () =>
        {
            string directory = NewDirectory("correspondence"); var manifest = Example(directory, models: true);
            var (code, packet) = Run(directory, manifest with { Correspondences = [manifest.Correspondences[0] with { TlaStep = "Missing:step:Return" }] });
            CoreTests.Equal(2, code); Diagnostic(packet, "DF022");
        });
        test("integrated empty testing cannot produce conformance", () =>
        {
            string directory = NewDirectory("empty"); var manifest = Example(directory);
            var (code, packet) = Run(directory, manifest with { Bindings = manifest.Bindings.Select(b => b with { Cases = [] }).ToList() });
            CoreTests.Equal(2, code); Diagnostic(packet, "DF200"); Status(packet, "conformance_status", "not-run");
        });
        test("integrated transitive dependencies are pinned and audited", () =>
        {
            string directory = NewDirectory("include"); var manifest = Example(directory);
            File.Move(Path.Combine(directory, "Decisions.dfy"), Path.Combine(directory, "Dependency.dfy"));
            File.WriteAllText(Path.Combine(directory, "Decisions.dfy"), "include \"Dependency.dfy\"\n");
            File.AppendAllText(Path.Combine(directory, "Dependency.dfy"), "\nlemma HiddenAssumption() ensures false { assume {:axiom} false; }\n");
            var (code, packet) = Run(directory, manifest);
            CoreTests.Equal(1, code); Status(packet, "audit_status", "findings");
            CoreTests.True(packet.GetProperty("fingerprints").TryGetProperty(Path.Combine(directory, "Dependency.dfy"), out _));
        });
        test("integrated inspect resolves ghost lemmas without claiming runtime evidence", () =>
        {
            string directory = NewDirectory("ghost"); var manifest = Example(directory);
            File.AppendAllText(Path.Combine(directory, "Decisions.dfy"), "\nlemma Identity(x: int) ensures x == x { }\n");
            var (code, packet) = Run(directory, manifest with {
                Bindings = [manifest.Bindings[0] with { Dafny = "Identity", Adapter = null, Cases = [] }]
            }, "inspect-integrated");
            CoreTests.Equal(0, code); Status(packet, "proof_status", "not-run"); Status(packet, "conformance_status", "not-run");
        });
        test("integrated documentation IDs disambiguate overloaded source methods", () =>
        {
            string directory = NewDirectory("overloads"); var manifest = Example(directory);
            string source = Path.Combine(directory, "Decisions.cs");
            File.WriteAllText(source, Files.Read(source).Replace("public static class Decisions", "public static partial class Decisions", StringComparison.Ordinal) +
                "\npublic static partial class Decisions { public static void Invert(int value, out int result) { result = -value; } }\n");
            DafnyBinding binding = manifest.Bindings.Single(b => b.Id == "boolean-function") with {
                Source = "M:Decisions.Invert(System.Boolean,System.Boolean@)", Adapter = null
            };
            var (code, packet) = Run(directory, manifest with { Bindings = [binding] }, "inspect-integrated");
            CoreTests.Equal(0, code); Status(packet, "links_status", "passed");
        });
        test("integrated case type coercion and unsupported ghost execution are blocked", () =>
        {
            string directory = NewDirectory("typed-case"); var manifest = Example(directory);
            using JsonDocument input = JsonDocument.Parse("[\"1\",0,1]");
            var binding = manifest.Bindings[0] with { Cases = [new("wrong-type", input.RootElement.EnumerateArray().Select(e => e.Clone()).ToList())] };
            var (code, packet) = Run(directory, manifest with { Bindings = [binding] });
            CoreTests.Equal(2, code); Diagnostic(packet, "DF200"); Status(packet, "conformance_status", "not-run");
            string ghostDirectory = NewDirectory("ghost-execution"); var ghost = Example(ghostDirectory);
            File.AppendAllText(Path.Combine(ghostDirectory, "Decisions.dfy"), "\nlemma Identity(x: int) ensures x == x { }\n");
            var (ghostCode, ghostPacket) = Run(ghostDirectory, ghost with { Bindings = [ghost.Bindings[0] with { Dafny = "Identity" }] });
            CoreTests.Equal(2, ghostCode); Diagnostic(ghostPacket, "DF200"); Status(ghostPacket, "proof_status", "not-run");
        });
        test("integrated subprocess timeout remains incomplete, never a rejection", () =>
        {
            string directory = NewDirectory("timeout");
            IntegratedPacket packet = new("test-integrated");
            DafnyBackend backend = new(dafny, directory, 1, packet);
            StageResult stage = backend.Run("timeout", "dotnet", [typeof(Program).Assembly.Location, "--timeout-probe"]);
            CoreTests.Equal("incomplete", stage.Status);
            CoreTests.Equal("incomplete", packet.Status);
            CoreTests.Equal("not-run", packet.ProofStatus);
            CoreTests.True(packet.Diagnostics.Any(d => d.Code == "DF900" && d.Message.Contains("timed out", StringComparison.Ordinal)));
        });
        test("integrated malformed schema and duplicate JSON properties are explicit failures", () =>
        {
            string directory = NewDirectory("schema");
            string manifest = Path.Combine(directory, "bindings.json");
            File.WriteAllText(manifest, """{"schema_version":1,"schema_version":1}""");
            var result = TestCli.Run(["inspect-integrated", "--annotation-version", "3", "--bindings", manifest, "--dafny", dafny,
                "--output", Path.Combine(directory, "result")]);
            CoreTests.Equal(2, result.Code);
            CoreTests.True(result.Json.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
        });
        test("integrated archived baseline detects changed included source", () =>
        {
            string directory = NewDirectory("baseline"); var manifest = Example(directory);
            var (code, _) = Run(directory, manifest, "inspect-integrated");
            CoreTests.Equal(0, code);
            File.AppendAllText(Path.Combine(directory, "Decisions.dfy"), "\n// reviewed input changed\n");
            string baseline = Path.Combine(directory, "result", "result.json");
            string second = Path.Combine(directory, "second.json"); Files.WriteNew(second, manifest);
            var result = TestCli.Run(["inspect-integrated", "--annotation-version", "3", "--bindings", Path.Combine(directory, "bindings.json"),
                "--dafny", dafny, "--output", Path.Combine(directory, "second-result"), "--baseline", baseline]);
            CoreTests.Equal(2, result.Code); CoreTests.True(result.Json.Contains("DF030", StringComparison.Ordinal));
        });
    }
}
