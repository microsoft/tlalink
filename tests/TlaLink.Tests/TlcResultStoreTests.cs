using System.Text.Json;
using System.Text.Json.Nodes;
using TlaLink;

namespace TlaLink.Tests;

public static class TlcResultStoreTests
{
    public static void RegisterCore(Action<string, Action> test, string work)
    {
        string root = Directory.CreateDirectory(Path.Combine(work, "tlc-store-core")).FullName;
        string model = Path.Combine(root, "M.tla"), helper = Path.Combine(root, "H.tla");
        string config = Path.Combine(root, "M.cfg"), jar = Path.Combine(root, "tools.jar");
        File.WriteAllText(model, "---- MODULE M ----\n====\n");
        File.WriteAllText(helper, "---- MODULE H ----\n====\n");
        File.WriteAllText(config, "INIT Init\nNEXT Next\n");
        File.WriteAllText(jar, "synthetic jar bytes");
        Dictionary<string, string> Dependencies() => new(FilePaths.Comparer)
        {
            [helper] = Files.Hash(helper), ["library:Naturals.tla"] = Files.HashText("naturals")
        };
        TlcResultKey Key(int fingerprint) => TlcResultStore.Key(model, config, jar, Dependencies(), fingerprint);

        test("TLC result keys cover answer-determining inputs and the fingerprint function, not the budget", () =>
        {
            TlcResultKey first = Key(0), other = Key(7);
            CoreTests.Equal(TlcResultStore.Family(first), TlcResultStore.Family(other));
            CoreTests.True(TlcResultStore.Hash(first) != TlcResultStore.Hash(other));
            CoreTests.True(TlcResultStore.Hash(first with { AdapterVersion = first.AdapterVersion + 1 }) != TlcResultStore.Hash(first));
            foreach (string file in new[] { model, helper, config, jar })
            {
                string before = File.ReadAllText(file);
                try
                {
                    File.AppendAllText(file, "\\* changed\n");
                    CoreTests.True(TlcResultStore.Family(Key(0)) != TlcResultStore.Family(first));
                }
                finally { File.WriteAllText(file, before); }
            }
            CoreTests.Equal(TlcResultStore.Hash(first), TlcResultStore.Hash(Key(0)));
        });

        test("stored TLC outcomes are kept once and reused only while intact and completed", () =>
        {
            string store = Path.Combine(root, "store");
            string Stage(int fingerprint, bool recordFingerprint = true)
            {
                string directory = Directory.CreateDirectory(Path.Combine(root, "stage-" + Guid.NewGuid().ToString("N"))).FullName;
                File.WriteAllText(Path.Combine(directory, "tlc.log"), "Model checking completed. No error has been found.");
                List<string> arguments = recordFingerprint ? ["-tool", "-fp", fingerprint.ToString(), "M.tla"] : ["-tool", "M.tla"];
                File.WriteAllText(Path.Combine(directory, "tlc.invocation.json"),
                    JsonSerializer.Serialize(new { SchemaVersion = Schema.Version, Kind = "process-invocation", Arguments = arguments }, Files.Json));
                return directory;
            }
            JsonElement Result(string status, int fingerprint) => JsonSerializer.SerializeToElement(new {
                SchemaVersion = Schema.Version, Kind = "tlc-result", Status = status, Fingerprint = fingerprint }, Files.Json);
            string? Save(int fingerprint, string status = "passed", bool recordFingerprint = true) =>
                TlcResultStore.Store(store, Key(fingerprint), "stage", "test", Stage(fingerprint, recordFingerprint),
                    Result(status, fingerprint), null, new(1, 512), 60, 1.5);

            CoreTests.True(Save(0, "incomplete") == null);
            string entry = Save(0) ?? throw new InvalidOperationException("Completed outcome was not stored.");
            CoreTests.True(Save(0) == null);
            CoreTests.True(Save(1, "rejected") != null);
            CoreTests.Equal(2, TlcResultStore.Find(store, Key(0)).Count);
            CoreTests.Equal(2, TlcResultStore.Find(store, Key(5)).Count);
            File.WriteAllText(helper, "---- MODULE H ----\nX == 1\n====\n");
            try { CoreTests.Equal(0, TlcResultStore.Find(store, Key(0)).Count); }
            finally { File.WriteAllText(helper, "---- MODULE H ----\n====\n"); }

            string log = Path.Combine(entry, "tlc.log"), before = File.ReadAllText(log);
            File.AppendAllText(log, "\nforged");
            CoreTests.Equal(1, TlcResultStore.Find(store, Key(0)).Count);
            File.WriteAllText(log, before);
            File.WriteAllText(Path.Combine(entry, "extra.txt"), "unexpected");
            CoreTests.Equal(1, TlcResultStore.Find(store, Key(0)).Count);
            File.Delete(Path.Combine(entry, "extra.txt"));
            CoreTests.Equal(2, TlcResultStore.Find(store, Key(0)).Count);

            CoreTests.True(Save(3, recordFingerprint: false) != null);
            CoreTests.True(TlcResultStore.Find(store, Key(3)).All(e => e.Entry.Material.Fingerprint != 3));
        });
    }

    public static void RegisterNative(Action<string, Action> test, string root, string work)
    {
        string jar = TestTools.Jar(root);
        string fixture = Path.Combine(work, "tlc-store-project");
        StageEvidenceTests.Copy(Path.Combine(root, "examples", "Components"), fixture);
        string path = Path.Combine(fixture, "tlalink.project.json");
        JsonObject authored = JsonNode.Parse(Files.Read(path))!.AsObject();
        authored["tools"] = JsonSerializer.SerializeToNode(new ProjectTools(Jar: new(jar, Files.Hash(jar))), Files.Json);
        authored["result_store"] = "store";
        File.WriteAllText(path, authored.ToJsonString(Files.Json));
        string policyPath = Path.Combine(fixture, "verification", "requirements.json");
        string policy = Files.Read(path: policyPath);
        string model = Path.Combine(fixture, "src", "Counter", "verification", "Counter.tla");

        WorkflowRun Run(string name, params string[] extra)
        {
            string output = Path.Combine(work, "tlc-store-" + name);
            var response = TestCli.Run(["run", "--project", path, "--scenario", "complete", "--output", output, .. extra]);
            if (response.Code != 0) throw new InvalidOperationException(response.Json);
            return ProjectInputs.Read<WorkflowRun>(Path.Combine(output, "run.json"));
        }
        static int Reused(WorkflowRun run) => run.Stages.Count(s => s.StoredResults?.Any(r => r.Primary && r.Origin == "reused") == true);
        int Accept(string name) => TestCli.Run(["accept", "--project", path, "--run", Path.Combine(work, "tlc-store-" + name)]).Code;
        void RequireFingerprints(int? count)
        {
            JsonObject document = JsonNode.Parse(policy)!.AsObject();
            foreach (JsonNode? requirement in document["requirements"]!.AsArray())
                if (requirement!["id"]!.GetValue<string>() == "counter-safe") requirement["fingerprint_functions"] = count;
            File.WriteAllText(policyPath, document.ToJsonString(Files.Json));
        }
        void Review() => CoreTests.Equal(0, TestCli.Run(["record-review", "--project", path, "--obligation", "counter-correspondence",
            "--reviewer", "test-control", "--rationale", "Synthetic reuse regression; not a product approval.",
            "--evidence", Path.Combine("src", "Counter", "Counter.cs")]).Code);

        test("unchanged TLC stages reuse stored exhaustive outcomes with identical acceptance", () =>
        {
            WorkflowRun first = Run("first");
            CoreTests.Equal(0, Reused(first));
            CoreTests.True(first.Stages.All(s => s.StoredResults?.Single().Origin == "executed"));
            WorkflowRun second = Run("second");
            CoreTests.Equal(2, Reused(second));
            CoreTests.True(first.Stages.Select(s => s.Outcome).SequenceEqual(second.Stages.Select(s => s.Outcome)));
            CoreTests.Equal(Accept("first"), Accept("second"));
            Review();
            CoreTests.Equal(0, Accept("second"));
            var plan = TestCli.Run(["plan", "--project", path, "--scenario", "complete"]);
            CoreTests.Equal(0, plan.Code);
            CoreTests.True(plan.Json.Contains("\"stored\"", StringComparison.Ordinal));

            string forged = Path.Combine(work, "tlc-store-forged");
            StageEvidenceTests.Copy(Path.Combine(work, "tlc-store-second"), forged);
            File.AppendAllText(Path.Combine(forged, "counter-safety", "stored", "fp-0", "tlc.log"), "\nforged");
            CoreTests.True(TestCli.Run(["accept", "--project", path, "--run", forged]).Code != 0);
        });
        test("separately stored fingerprint functions satisfy only their required count", () =>
        {
            var check = TestCli.Run(["check-stage", "--project", path, "--stage", "counter-safety", "--fingerprint", "5",
                "--workers", "2", "--max-heap-mib", "1024", "--timeout", "120"]);
            if (check.Code != 0) throw new InvalidOperationException(check.Json);
            CoreTests.Equal(0, TestCli.Run(["check-stage", "--project", path, "--stage", "counter-safety", "--fingerprint", "5"]).Code);
            CoreTests.Equal(2, TestCli.Run(["check-stage", "--project", path, "--stage", "counter-safety", "--fingerprint", "131"]).Code);
            try
            {
                Review();
                RequireFingerprints(2);
                WorkflowRun run = Run("fingerprints");
                CoreTests.Equal(2, run.Stages.Single(s => s.Id == "counter-safety").StoredResults!.Count);
                CoreTests.Equal(0, Accept("fingerprints"));
                RequireFingerprints(3);
                _ = Run("more-fingerprints");
                var rejected = TestCli.Run(["accept", "--project", path, "--run", Path.Combine(work, "tlc-store-more-fingerprints")]);
                CoreTests.Equal(1, rejected.Code);
                CoreTests.True(rejected.Json.Contains("3 distinct fingerprint functions (found 2)", StringComparison.Ordinal));
            }
            finally { File.WriteAllText(policyPath, policy); }
        });
        test("fresh runs and changed models execute TLC again", () =>
        {
            CoreTests.Equal(0, Reused(Run("fresh", "--fresh", "true")));
            string before = Files.Read(model);
            try
            {
                File.AppendAllText(model, "\\* comment-only change still requires a new search\n");
                CoreTests.Equal(0, Reused(Run("edited")));
            }
            finally { File.WriteAllText(model, before); }
            CoreTests.Equal(2, Reused(Run("restored")));
        });
    }
}
