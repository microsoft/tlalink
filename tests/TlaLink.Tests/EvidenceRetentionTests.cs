using System.Text.Json;
using System.Diagnostics;
using TlaLink;
using static TlaLink.Tests.CoreTests;

namespace TlaLink.Tests;

public static class EvidenceRetentionTests
{
    public static void Register(Action<string, Action> test, string work)
    {
        test("GC retains transitive pinned dependencies, not every accepted run, and writes nothing", () => {
            var f = new Fixture(work);
            string producer = f.Unit("producer");
            string kept = f.Unit("milestone", Path.Combine(producer, "stage", "result.json"));
            string spare = f.Unit("superseded");
            Files.WriteNew(Path.Combine(spare, "acceptance.json"),
                AcceptanceSummaryTests.Packet(acceptance: "accepted") with { Project = "synthetic" });
            f.Old();
            f.Pins([new("store\\milestone\\run.json", "accepted milestone")]);
            var before = f.Snapshot();
            GcPlan plan = f.Plan();
            Equal("ready", plan.Status);
            Equal(2, plan.Counts.Retained);
            Equal(1, plan.Counts.Candidates);
            Equal("candidate", plan.Entries.Single(e => e.Path == spare).Status);
            True(plan.Entries.Single(e => e.Path == kept).Dependencies.Contains(producer));
            True(plan.ReadOnly && !plan.DeletionAuthorized);
            Equal(before, f.Snapshot());
        });
        test("GC traces cycles without retaining unrooted cycles or losing rooted cycles", () => {
            var f = new Fixture(work);
            string a = f.Unit("a"), b = f.Unit("b", Path.Combine(a, "stage", "result.json"));
            f.RewriteInput(a, Path.Combine(b, "stage", "result.json"));
            f.Old();
            f.Pins([]);
            Equal(2, f.Plan().Counts.Candidates);
            f.Pins([new("store\\a", "cycle root")]);
            Equal(2, f.Plan().Counts.Retained);
        });
        test("GC change impact includes transitively affected runs even when producer output bytes are unchanged", () => {
            var f = new Fixture(work);
            string input = f.Source("model.tla", "synthetic input");
            string a = f.Unit("a", input);
            string b = f.Unit("b", Path.Combine(a, "stage", "result.json"));
            string c = f.Unit("c", Path.Combine(b, "stage", "result.json"));
            string unrelated = f.Unit("unrelated");
            f.Old();
            f.Pins([new("store\\c", "current composition")]);
            File.WriteAllText(input, "changed input");
            GcPlan plan = f.Plan();
            Equal("ready", plan.Status);
            Equal(1, plan.ChangedInputs.Count);
            Equal("changed", plan.ChangedInputs[0].Status);
            Equal(3, plan.Recheck.Count);
            True(plan.Recheck.Single(r => r.Path == b).AffectedBy.Contains(a));
            True(plan.Recheck.Single(r => r.Path == c).AffectedBy.Contains(b));
            True(!plan.Entries.Single(e => e.Path == unrelated).RecheckRequired);
            True(plan.Recheck.All(r => r.Scope == "whole-run-conservative"));
        });
        test("GC compares each consumer's own hash and detects missing recorded inputs", () => {
            var f = new Fixture(work);
            string input = f.Source("input.cs", "one");
            string old = f.Unit("old", input);
            File.WriteAllText(input, "two");
            string current = f.Unit("current", input);
            f.Old();
            f.Pins([]);
            GcPlan plan = f.Plan();
            Equal(1, plan.Recheck.Count);
            Equal(old, plan.Recheck.Single().Path);
            True(!plan.Entries.Single(e => e.Path == current).RecheckRequired);
            File.Delete(input);
            plan = f.Plan();
            Equal("missing", plan.ChangedInputs.Single().Status);
            Equal(2, plan.Recheck.Count);
        });
        foreach (string malformed in new[] { "unknown", "unfinished", "legacy", "invalid-json", "artifact-escape", "artifact-missing", "missing-input-copy" })
            test("GC blocks all candidates for unresolved archive scope: " + malformed, () => {
                var f = new Fixture(work);
                f.Unit("otherwise-candidate");
                string bad = f.Unit("bad");
                string record = Path.Combine(bad, "run.json");
                switch (malformed)
                {
                    case "unknown": File.Delete(record); break;
                    case "unfinished": f.EditRun(bad, run => run.ExecutionStatus = "incomplete"); break;
                    case "legacy": File.WriteAllText(record, """{"schema_version":1,"kind":"analysis-run"}"""); break;
                    case "invalid-json": File.WriteAllText(record, "{"); break;
                    case "artifact-escape": f.EditRun(bad, run => run.Artifacts.Add(@"..\outside.bin", new string('a', 64))); break;
                    case "artifact-missing": File.Delete(Path.Combine(bad, "stage", "result.json")); break;
                    case "missing-input-copy":
                        File.Delete(Directory.GetFiles(Path.Combine(bad, "inputs")).Single()); break;
                }
                f.Old();
                f.Pins([]);
                GcPlan plan = f.Plan();
                Equal("incomplete", plan.Status);
                Equal(0, plan.Counts.Candidates);
                True(plan.Diagnostics.Count > 0);
                True(plan.Entries.All(e => e.Status == "protected"));
            });
        test("GC grace protection retains dependencies and rejects future timestamps as candidates", () => {
            var f = new Fixture(work);
            string producer = f.Unit("old");
            string recent = f.Unit("recent", Path.Combine(producer, "stage", "result.json"));
            f.Old();
            File.SetLastWriteTimeUtc(Path.Combine(recent, "run.json"), f.Now.UtcDateTime.AddHours(2));
            f.Pins([]);
            GcPlan plan = f.Plan(24);
            Equal("protected", plan.Entries.Single(e => e.Path == recent).Status);
            Equal("retained", plan.Entries.Single(e => e.Path == producer).Status);
            Equal(0, plan.Counts.Candidates);
        });
        test("GC acceptance-embedded review paths use the original project directory, not the receipt directory", () => {
            var f = new Fixture(work);
            string evidence = f.Unit("evidence");
            string accepted = f.Unit("accepted");
            string relative = @"store\evidence\run.json";
            var review = f.Review(relative);
            Files.WriteNew(Path.Combine(accepted, "receipt.json"),
                AcceptanceSummaryTests.Packet(reviews: [new("review", "stale", review)]) with { Project = "synthetic" });
            f.Old();
            f.Pins([new(@"store\accepted", "retained receipt")]);
            GcPlan plan = f.Plan();
            Equal("ready", plan.Status);
            Equal("retained", plan.Entries.Single(e => e.Path == evidence).Status);
        });
        test("GC explicit review projects retain append-only rejected and accepted evidence and report stale review inputs", () => {
            var f = new Fixture(work);
            string first = f.Unit("first");
            string later = f.Unit("later");
            string project = f.Project();
            string directory = Path.Combine(f.Root, "reviews", "review");
            Directory.CreateDirectory(directory);
            string oldReview = Path.Combine(directory, "old.json");
            Files.WriteNew(oldReview, f.Review(@"store\first\run.json", "rejected"));
            Files.WriteNew(Path.Combine(directory, "latest.json"), f.Review(@"store\later\run.json"));
            f.Old();
            f.Pins([], ["project.json"]);
            GcPlan plan = f.Plan();
            Equal("ready", plan.Status);
            Equal(2, plan.Counts.Retained);
            True(plan.Entries.Single(e => e.Path == first).Reasons.Any(r => r.Contains("old.json", StringComparison.Ordinal)));
            True(plan.Entries.Single(e => e.Path == later).Reasons.Any(r => r.Contains("latest.json", StringComparison.Ordinal)));
            File.AppendAllText(Path.Combine(first, "run.json"), " ");
            plan = f.Plan();
            True(plan.Recheck.Any(r => r.Path == oldReview && r.Kind == "review"));
            True(File.Exists(project));
        });
        test("GC protects loose legacy JSON and refuses missing or duplicate explicit pins", () => {
            var f = new Fixture(work);
            f.Unit("run");
            f.Old();
            f.Pins([new(@"store\missing", "missing")]);
            Equal("incomplete", f.Plan().Status);
            f.Pins([new(@"store\run", "one"), new(@"store\run\.", "two")]);
            Throws(() => f.Plan());
            f.Pins([]);
            File.WriteAllText(Path.Combine(f.Store, "old.json"), """{"SchemaVersion":1}""");
            Equal(0, f.Plan().Counts.Candidates);
        });
        test("GC JSON retention input is strict and does not silently supply empty roots", () => {
            var f = new Fixture(work);
            f.Unit("run");
            foreach (string json in new[] {
                """{"schema_version":4,"kind":"evidence-retention","pins":[]}""",
                """{"schema_version":4,"kind":"evidence-retention","pins":[],"review_projects":[],"typo":true}""",
                """{"schema_version":4,"kind":"evidence-retention","pins":[],"pins":[],"review_projects":[]}""",
                """{"schema_version":4,"kind":"evidence-retention","pins":[null],"review_projects":[]}"""
            })
            {
                File.WriteAllText(f.Policy, json);
                var result = TestCli.Run(["gc", "--store", f.Store, "--pins", f.Policy]);
                Equal(2, result.Code);
            }
        });
        test("GC CLI emits a persistent full plan and summary without implicit deletion or apply overrides", () => {
            var f = new Fixture(work);
            f.Unit("run");
            f.Old();
            f.Pins([]);
            string output = Path.Combine(f.Root, "plan.json");
            string before = f.Snapshot();
            var result = TestCli.Run(["gc", "--store", f.Store, "--pins", f.Policy, "--output", output, "--format", "summary"]);
            Equal(0, result.Code);
            using var summary = JsonDocument.Parse(result.Json);
            Equal("gc-plan", summary.RootElement.GetProperty("result_kind").GetString());
            True(summary.RootElement.GetProperty("read_only").GetBoolean());
            True(!summary.RootElement.GetProperty("deletion_authorized").GetBoolean());
            True(!summary.RootElement.TryGetProperty("fingerprints", out _));
            using var full = JsonDocument.Parse(File.ReadAllText(output));
            Equal("gc-plan", full.RootElement.GetProperty("kind").GetString());
            Equal(1, full.RootElement.GetProperty("counts").GetProperty("candidates").GetInt32());
            True(full.RootElement.TryGetProperty("fingerprints", out _));
            Equal(before, f.Snapshot());
            Equal(2, TestCli.Run(["gc", "--store", f.Store, "--pins", f.Policy, "--output", output]).Code);
            foreach (string option in new[] { "--apply", "--delete", "--force" })
                Equal(2, TestCli.Run(["gc", "--store", f.Store, "--pins", f.Policy, option, "true"]).Code);
            Equal(2, TestCli.Run(["gc", "--store", f.Store, "--pins", f.Policy, "--output", Path.Combine(f.Store, "plan.json")]).Code);
        });
        test("GC validates grace hours and supports text presentation", () => {
            var f = new Fixture(work);
            f.Unit("run");
            f.Old();
            f.Pins([]);
            foreach (string value in new[] { "-1", "87601", "1.5", "abc" })
                Equal(2, TestCli.Run(["gc", "--store", f.Store, "--pins", f.Policy, "--grace-hours", value]).Code);
            var result = TestCli.Run(["gc", "--store", f.Store, "--pins", f.Policy, "--format", "text"]);
            Equal(0, result.Code);
            True(result.Json.Contains("candidate:", StringComparison.Ordinal));
            True(result.Json.Contains("deletion not authorized", StringComparison.Ordinal));
        });
        test("GC caller-relative CLI paths and document-relative pins use distinct declaring bases", () => {
            var f = new Fixture(work);
            f.Unit("kept");
            f.Old();
            f.Pins([new(@"store\kept", "explicit root")]);
            string previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = f.Root;
                var result = TestCli.Run(["gc", "--store", @".\store", "--pins", @".\retention.json"]);
                Equal(0, result.Code);
                using var document = JsonDocument.Parse(result.Json);
                Equal(1, document.RootElement.GetProperty("counts").GetProperty("retained").GetInt32());
                Directory.CreateDirectory(Path.Combine(f.Root, "plans"));
                Equal(0, TestCli.Run(["gc", "--store", @".\store", "--pins", @".\retention.json",
                    "--output", @".\plans\.\plan.json"]).Code);
                True(File.Exists(Path.Combine(f.Root, "plans", "plan.json")));
            }
            finally { Environment.CurrentDirectory = previous; }
        });
        test("GC does not recommend collecting the unit that owns its retention manifest", () => {
            var f = new Fixture(work);
            string unit = f.Unit("policy-owner");
            string policy = Path.Combine(unit, "retention.json");
            Files.WriteNew(policy, new EvidenceRetention(Schema.Version, "evidence-retention", [], []));
            f.Old();
            GcPlan plan = EvidenceGarbageCollection.Plan(f.Store, policy, 24, f.Now);
            Equal("ready", plan.Status);
            Equal("retained", plan.Entries.Single().Status);
        });
        test("GC unreadable dependencies block candidates and report an explicit unreadable impact", () => {
            var f = new Fixture(work);
            string input = f.Source("locked.tla", "fixture");
            f.Unit("run", input);
            f.Old();
            f.Pins([]);
            using var lease = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            GcPlan plan = f.Plan();
            Equal("incomplete", plan.Status);
            Equal(0, plan.Counts.Candidates);
            Equal("unreadable", plan.ChangedInputs.Single().Status);
            True(plan.Diagnostics.Count > 0);
        });
        test("GC snapshot verification detects file-byte changes even with restored length and timestamp", () => {
            var f = new Fixture(work);
            string input = f.Source("snapshot.json", """{"x":1}""");
            var scan = new EvidenceGarbageCollection.Scan(f.Store);
            scan.Observe(input);
            scan.Read(input);
            DateTime original = File.GetLastWriteTimeUtc(input);
            File.WriteAllText(input, """{"x":2}""");
            File.SetLastWriteTimeUtc(input, original);
            scan.Verify();
            True(scan.Diagnostics.Any(d => d.Message.Contains("File bytes changed", StringComparison.Ordinal)));
        });
        test("GC snapshot verification detects added and removed filesystem entries", () => {
            var f = new Fixture(work);
            string unit = f.Unit("run");
            var scan = new EvidenceGarbageCollection.Scan(f.Store);
            scan.Walk(unit);
            File.Delete(Path.Combine(unit, "run.json"));
            File.WriteAllText(Path.Combine(unit, "added.bin"), "new");
            Directory.SetLastWriteTimeUtc(unit, DateTime.UtcNow.AddSeconds(1));
            scan.Verify();
            True(scan.Diagnostics.Count > 0);
        });
        test("GC never follows directory links as units, store ancestors or output parents", () => {
            var f = new Fixture(work);
            f.Unit("run");
            f.Old();
            f.Pins([]);
            string outside = Directory.CreateDirectory(Path.Combine(f.Root, "outside")).FullName;
            string child = Path.Combine(f.Store, "linked");
            string alias = Path.Combine(f.Root, "alias");
            try
            {
                Link(child, outside);
                Link(alias, f.Store);
                GcPlan plan = f.Plan();
                Equal("incomplete", plan.Status);
                Equal(0, plan.Counts.Candidates);
                True(plan.Diagnostics.Any(d => d.Message.Contains("Reparse", StringComparison.Ordinal)));
                Throws(() => EvidenceGarbageCollection.Plan(alias, f.Policy, 24, f.Now));
                Equal(2, TestCli.Run(["gc", "--store", f.Store, "--pins", f.Policy, "--output", Path.Combine(alias, "plan.json")]).Code);
                True(!File.Exists(Path.Combine(f.Store, "plan.json")));
                True(Directory.Exists(outside));
            }
            finally
            {
                if (Directory.Exists(child)) Directory.Delete(child);
                if (Directory.Exists(alias)) Directory.Delete(alias);
            }
        });
        test("GC apply requires a full ready plan, a new receipt and the exclusive-writer assertion", () => {
            var f = new Fixture(work);
            f.Unit("run");
            f.Old();
            f.Pins([]);
            string plan = f.SavePlan();
            string before = f.Snapshot();
            Equal(2, TestCli.Run(["gc", "--apply", plan, "--output", Path.Combine(f.Root, "refused.json")]).Code);
            Equal(2, TestCli.Run(["gc", "--apply", plan, "--exclusive", "true"]).Code);
            Equal(2, TestCli.Run(["gc", "--apply", plan, "--exclusive", "true", "--store", f.Store,
                "--output", Path.Combine(f.Root, "refused.json")]).Code);
            Equal(2, TestCli.Run(["gc", "--exclusive", "true", "--store", f.Store, "--pins", f.Policy]).Code);
            Equal(before, f.Snapshot());
        });
        test("GC understands authoritative SANY library metadata without treating URIs as filepaths", () => {
            var f = new Fixture(work);
            string jar = f.Source("tla2tools.jar", "synthetic pinned jar");
            string unit = f.Unit("run", jar);
            f.EditRun(unit, r => r.Fingerprints.Add("library:jar:file:/synthetic/tla2tools.jar!/Naturals.tla!Naturals.tla", new('a', 64)));
            f.Old();
            f.Pins([]);
            GcPlan plan = f.Plan();
            Equal("ready", plan.Status);
            Equal(1, plan.Counts.Candidates);
            True(plan.FileInputs.ContainsKey(jar));
            f.EditRun(unit, r => r.Fingerprints.Add("unknown:possible-reference", new('b', 64)));
            Equal("incomplete", f.Plan().Status);
        });
        test("GC summary bounds previews and reports every omission without losing full saved evidence", () => {
            var f = new Fixture(work);
            string input = f.Source("shared.tla", "original");
            for (int i = 0; i < 14; i++)
            {
                f.Unit("run" + i, input);
                Directory.CreateDirectory(Path.Combine(f.Store, "unknown" + i));
            }
            f.Old();
            f.Pins([]);
            File.Delete(input);
            string output = Path.Combine(f.Root, "full-plan.json");
            var result = TestCli.Run(["gc", "--store", f.Store, "--pins", f.Policy, "--output", output, "--format", "summary"]);
            Equal(2, result.Code);
            using var summary = JsonDocument.Parse(result.Json);
            Equal(10, summary.RootElement.GetProperty("entries").GetArrayLength());
            Equal(18, summary.RootElement.GetProperty("omitted").GetProperty("entries").GetInt32());
            Equal(4, summary.RootElement.GetProperty("omitted").GetProperty("recheck").GetInt32());
            Equal(4, summary.RootElement.GetProperty("omitted").GetProperty("diagnostics").GetInt32());
            Equal(14, summary.RootElement.GetProperty("changed_inputs")[0].GetProperty("consumer_count").GetInt32());
            True(result.Json.Length < 20000);
            using var full = JsonDocument.Parse(File.ReadAllText(output));
            Equal(28, full.RootElement.GetProperty("entries").GetArrayLength());
            Equal(14, full.RootElement.GetProperty("diagnostics").GetArrayLength());
            True(full.RootElement.TryGetProperty("snapshot", out _));
        });
        test("GC protected native catalogs retain declared closure without implicitly retaining unrelated runs", () => {
            var f = new Fixture(work);
            string producer = f.Unit("producer"), kept = f.Unit("kept", Path.Combine(producer, "stage", "result.json"));
            string spare = f.Unit("spare");
            string native = Directory.CreateDirectory(Path.Combine(f.Store, "native")).FullName;
            File.WriteAllText(Path.Combine(native, "native.json"), """{"native_format":1}""");
            f.Old();
            f.Pins([]);
            f.Catalog([f.Capture(native, [], [Path.Combine(kept, "run.json")])]);
            GcPlan plan = f.Plan();
            Equal("ready", plan.Status);
            Equal(2, plan.Counts.Retained);
            Equal("protected", plan.Entries.Single(e => e.Path == native).Status);
            Equal("candidate", plan.Entries.Single(e => e.Path == spare).Status);
            if (OperatingSystem.IsWindows())
            {
                Equal(0, f.Apply(f.SavePlan()).Code);
                True(Directory.Exists(native) && Directory.Exists(kept));
                True(!Directory.Exists(spare));
            }
        });
        test("GC catalog sidecars retain their dependencies and remain permanently outside collection", () => {
            var f = new Fixture(work);
            string kept = f.Unit("kept");
            string sidecar = Path.Combine(f.Store, "old-command.json");
            File.WriteAllText(sidecar, """{"original_native_output":true}""");
            f.Old();
            f.Pins([]);
            f.Catalog([f.Capture(sidecar, [], [Path.Combine(kept, "run.json")])]);
            Equal("ready", f.Plan().Status);
            Equal(1, f.Plan().Counts.Retained);
            True(File.Exists(sidecar));
        });
        foreach (string change in new[] { "new-file", "removed-file", "changed-bytes", "producer", "missing-consumer" })
            test("GC protected catalog refuses incomplete or stale inventory: " + change, () => {
                var f = new Fixture(work);
                f.Unit("spare");
                string native = Directory.CreateDirectory(Path.Combine(f.Store, "native")).FullName;
                string file = Path.Combine(native, "data.json");
                File.WriteAllText(file, """{"x":1}""");
                f.Old();
                f.Pins([]);
                f.Catalog([f.Capture(native, [], [])]);
                switch (change)
                {
                    case "new-file": File.WriteAllText(Path.Combine(native, "new.bin"), "new"); break;
                    case "removed-file": File.Delete(file); break;
                    case "changed-bytes":
                        DateTime stamp = File.GetLastWriteTimeUtc(file);
                        File.WriteAllText(file, """{"x":2}""");
                        File.SetLastWriteTimeUtc(file, stamp);
                        break;
                    case "producer": File.WriteAllText(Path.Combine(f.Root, "adapter.cs"), "changed producer"); break;
                    case "missing-consumer": File.Delete(file); Directory.Delete(native); break;
                }
                GcPlan plan = f.Plan();
                Equal("incomplete", plan.Status);
                Equal(0, plan.Counts.Candidates);
            });
        test("GC catalog preserves expected native input hashes and propagates recheck impact", () => {
            var f = new Fixture(work);
            string input = f.Source("source.cs", "original");
            string run = f.Unit("run", input);
            string native = Directory.CreateDirectory(Path.Combine(f.Store, "native")).FullName;
            File.WriteAllText(Path.Combine(native, "result.json"), """{"native_result":"rejected"}""");
            f.Old();
            f.Pins([]);
            f.Catalog([f.Capture(native, [new(input, Files.Hash(input))], [Path.Combine(run, "run.json")])]);
            File.WriteAllText(input, "changed");
            GcPlan plan = f.Plan();
            Equal("ready", plan.Status);
            Equal(2, plan.Recheck.Count);
            True(plan.ChangedInputs.Single().Consumers.Any(c => c.Consumer == native));
        });
        if (!OperatingSystem.IsWindows())
        {
            test("GC apply explicitly refuses platforms without directory-handle guards", () => {
                var f = new Fixture(work);
                f.Unit("run");
                f.Old();
                f.Pins([]);
                Equal(2, f.Apply(f.SavePlan()).Code);
                True(Directory.Exists(Path.Combine(f.Store, "run")));
            });
            return;
        }
        test("GC guarded apply removes only planned old candidates and retains pinned closure", () => {
            var f = new Fixture(work);
            string producer = f.Unit("producer");
            string kept = f.Unit("kept", Path.Combine(producer, "stage", "result.json"));
            string spare = f.Unit("spare");
            f.Old();
            f.Pins([new(@"store\kept", "retained milestone")]);
            string saved = f.SavePlan();
            var result = f.Apply(saved);
            Equal(0, result.Code);
            True(!Directory.Exists(spare));
            True(Directory.Exists(kept) && Directory.Exists(producer));
            True(File.Exists(Path.Combine(f.Root, "spare.tla")));
            using var report = JsonDocument.Parse(result.Json);
            Equal("gc-apply-result", report.RootElement.GetProperty("result_kind").GetString());
            Equal("completed", report.RootElement.GetProperty("status").GetString());
            Equal(1, report.RootElement.GetProperty("deleted_units").GetArrayLength());
            True(!report.RootElement.GetProperty("read_only").GetBoolean());
            True(report.RootElement.GetProperty("deletion_authorized").GetBoolean());
            using var receipt = JsonDocument.Parse(File.ReadAllText(f.Receipt));
            Equal("gc-apply-result", receipt.RootElement.GetProperty("kind").GetString());
            True(receipt.RootElement.GetProperty("removed_files").GetArrayLength() > 0);
            True(!File.Exists(f.Store + ".gc.lock"));
            Equal(2, f.Apply(saved).Code);
        });
        test("GC resolves linked external input identity while leaving managed archive links prohibited", () => {
            var f = new Fixture(work);
            string kept = f.Unit("kept");
            string alias = Path.Combine(f.Root, "alias");
            try
            {
                Link(alias, kept);
                string original = Path.Combine(alias, "stage", "result.json");
                string consumer = f.Unit("consumer", original);
                f.Old();
                f.Pins([new(@"store\consumer", "current consumer")]);
                GcPlan plan = f.Plan();
                Equal("ready", plan.Status);
                Equal(2, plan.Counts.Retained);
                True(plan.Entries.Single(e => e.Path == consumer).Dependencies.Contains(kept));
                Equal(Path.Combine(kept, "stage", "result.json"), plan.ResolvedInputs[original]);
            }
            finally { if (Directory.Exists(alias)) Directory.Delete(alias); }
        });
        test("GC apply detects an input alias retargeted to identical bytes outside the store", () => {
            var f = new Fixture(work);
            string first = Directory.CreateDirectory(Path.Combine(f.Root, "first")).FullName;
            string second = Directory.CreateDirectory(Path.Combine(f.Root, "second")).FullName;
            foreach (string directory in new[] { first, second }) File.WriteAllText(Path.Combine(directory, "source.dat"), "identical bytes");
            string alias = Path.Combine(f.Root, "alias");
            try
            {
                Link(alias, first);
                f.Unit("consumer", Path.Combine(alias, "source.dat"));
                f.Unit("spare");
                f.Old();
                f.Pins([new(@"store\consumer", "retained consumer")]);
                string saved = f.SavePlan();
                Directory.Delete(alias);
                Link(alias, second);
                string before = f.Snapshot();
                Equal(2, f.Apply(saved).Code);
                Equal(before, f.Snapshot());
                using var receipt = JsonDocument.Parse(File.ReadAllText(f.Receipt));
                True(!receipt.RootElement.GetProperty("deletion_authorized").GetBoolean());
            }
            finally { if (Directory.Exists(alias)) Directory.Delete(alias); }
        });
        test("GC apply revalidates protected native bytes before deleting unrelated candidates", () => {
            var f = new Fixture(work);
            f.Unit("spare");
            string native = Directory.CreateDirectory(Path.Combine(f.Store, "native")).FullName;
            string file = Path.Combine(native, "native.json");
            File.WriteAllText(file, """{"x":1}""");
            f.Old();
            f.Pins([]);
            f.Catalog([f.Capture(native, [], [])]);
            string saved = f.SavePlan();
            DateTime stamp = File.GetLastWriteTimeUtc(file);
            File.WriteAllText(file, """{"x":2}""");
            File.SetLastWriteTimeUtc(file, stamp);
            string before = f.Snapshot();
            Equal(2, f.Apply(saved).Code);
            Equal(before, f.Snapshot());
        });
        test("GC guarded apply removes an unpinned cycle after validating every member", () => {
            var f = new Fixture(work);
            string a = f.Unit("a");
            string b = f.Unit("b", Path.Combine(a, "stage", "result.json"));
            f.RewriteInput(a, Path.Combine(b, "stage", "result.json"));
            f.Old();
            f.Pins([]);
            Equal(0, f.Apply(f.SavePlan()).Code);
            True(!Directory.Exists(a) && !Directory.Exists(b));
        });
        test("GC apply keeps today's completed runs under the default grace interval", () => {
            var f = new Fixture(work);
            string unit = f.Unit("today");
            f.Pins([]);
            string before = f.Snapshot();
            Equal(0, f.Apply(f.SavePlan()).Code);
            True(Directory.Exists(unit));
            Equal(before, f.Snapshot());
        });
        foreach (string change in new[] { "policy", "source", "new-file", "new-unit", "deleted-file", "mtime" })
            test("GC apply refuses a stale plan before deleting any files: " + change, () => {
                var f = new Fixture(work);
                string input = f.Source("model.tla", "initial model");
                string unit = f.Unit("run", input);
                f.Old();
                f.Pins([]);
                string plan = f.SavePlan();
                switch (change)
                {
                    case "policy": f.Pins([new(@"store\run", "new retention pin")]); break;
                    case "source": File.WriteAllText(input, "changed model"); break;
                    case "new-file": File.WriteAllText(Path.Combine(unit, "new.bin"), "new"); break;
                    case "new-unit": Directory.CreateDirectory(Path.Combine(f.Store, "active")); break;
                    case "deleted-file": File.Delete(Path.Combine(unit, "stage", "result.json")); break;
                    case "mtime": File.SetLastWriteTimeUtc(Path.Combine(unit, "run.json"), DateTime.UtcNow); break;
                }
                string before = f.Snapshot();
                Equal(2, f.Apply(plan).Code);
                Equal(before, f.Snapshot());
            });
        foreach (string corrupted in new[] { "archived-input", "binary-artifact" })
            test("GC apply checks candidate bytes, not just filenames and timestamps: " + corrupted, () => {
                var f = new Fixture(work);
                string unit = f.Unit("run");
                string file;
                if (corrupted == "binary-artifact")
                {
                    file = Path.Combine(unit, "data.bin");
                    File.WriteAllBytes(file, [1, 2, 3]);
                    f.EditRun(unit, r => r.Artifacts.Add("data.bin", Files.Hash(file)));
                }
                else file = Directory.GetFiles(Path.Combine(unit, "inputs")).Single();
                f.Old();
                f.Pins([]);
                string plan = f.SavePlan();
                DateTime modified = File.GetLastWriteTimeUtc(file);
                byte[] bytes = File.ReadAllBytes(file);
                bytes[0] ^= 1;
                File.WriteAllBytes(file, bytes);
                File.SetLastWriteTimeUtc(file, modified);
                string before = f.Snapshot();
                Equal(2, f.Apply(plan).Code);
                Equal(before, f.Snapshot());
                using var receipt = JsonDocument.Parse(File.ReadAllText(f.Receipt));
                True(!receipt.RootElement.GetProperty("deletion_authorized").GetBoolean());
            });
        test("GC apply preserves unknown archives and refuses an incomplete or summary plan", () => {
            var f = new Fixture(work);
            f.Unit("old");
            Directory.CreateDirectory(Path.Combine(f.Store, "unknown-native"));
            f.Old();
            f.Pins([]);
            string before = f.Snapshot();
            Equal(2, f.Apply(f.SavePlan()).Code);
            Equal(before, f.Snapshot());
            string summary = Path.Combine(f.Root, "summary.json");
            File.WriteAllText(summary, TestCli.Run(["gc", "--store", f.Store, "--pins", f.Policy, "--format", "summary"]).Json);
            Equal(2, f.Apply(summary).Code);
        });
        test("GC apply rejects forged outside-store candidates and output inside the store", () => {
            var f = new Fixture(work);
            f.Unit("run");
            f.Old();
            f.Pins([]);
            string before = f.Snapshot();
            string plan = f.SavePlan();
            Equal(2, TestCli.Run(["gc", "--apply", plan, "--exclusive", "true", "--output", Path.Combine(f.Store, "receipt.json")]).Code);
            GcPlan original = ProjectInputs.Read<GcPlan>(plan);
            string outside = Directory.CreateDirectory(Path.Combine(f.Root, "store-other")).FullName;
            File.WriteAllText(plan, JsonSerializer.Serialize(original with {
                Entries = [new(outside, "candidate", 0, [], [], false)]
            }, Files.Json));
            Equal(2, f.Apply(plan).Code);
            Equal(before, f.Snapshot());
            True(Directory.Exists(outside));
        });
        test("GC apply refuses an existing receipt or competing lease without overwriting either", () => {
            var f = new Fixture(work);
            f.Unit("run");
            f.Old();
            f.Pins([]);
            string plan = f.SavePlan();
            File.WriteAllText(f.Receipt, "existing receipt");
            Equal(2, f.Apply(plan).Code);
            Equal("existing receipt", File.ReadAllText(f.Receipt));
            File.Delete(f.Receipt);
            File.WriteAllText(f.Store + ".gc.lock", "other owner");
            Equal(2, f.Apply(plan).Code);
            Equal("other owner", File.ReadAllText(f.Store + ".gc.lock"));
            True(Directory.Exists(Path.Combine(f.Store, "run")));
        });
        test("GC directory handles deny ancestor rename and reject junction identities", () => {
            var f = new Fixture(work);
            using (var guard = new EvidenceGarbageCollection.DirectoryLeases())
            {
                guard.Pin(f.Store);
                ThrowsAny<IOException>(() => Directory.Move(f.Root, f.Root + "-moved"));
                string link = Path.Combine(f.Root, "alias");
                try
                {
                    Link(link, f.Store);
                    Throws(() => guard.Pin(link));
                }
                finally { if (Directory.Exists(link)) Directory.Delete(link); }
            }
        });
        test("GC apply surfaces partial deletion explicitly and never reports completion after a file error", () => {
            var f = new Fixture(work);
            string unit = f.Unit("run");
            f.Old();
            f.Pins([]);
            string plan = f.SavePlan();
            using var busy = new FileStream(Path.Combine(unit, "stage", "result.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            Equal(2, f.Apply(plan).Code);
            using var receipt = JsonDocument.Parse(File.ReadAllText(f.Receipt));
            Equal("incomplete", receipt.RootElement.GetProperty("status").GetString());
            Equal(unit, receipt.RootElement.GetProperty("partial_unit").GetString());
            True(receipt.RootElement.GetProperty("removed_file_count").GetInt32() > 0);
            Equal(0, receipt.RootElement.GetProperty("deleted_units").GetArrayLength());
            Equal(1, receipt.RootElement.GetProperty("pending_units").GetArrayLength());
            True(receipt.RootElement.GetProperty("diagnostics").GetArrayLength() > 0);
        });
    }

    private static void Link(string path, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(path, target); return; }
        string shell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var start = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + path.Replace("'", "''", StringComparison.Ordinal) +
            "' -Target '" + target.Replace("'", "''", StringComparison.Ordinal) + "' | Out-Null" })
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not create junction fixture.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new InvalidOperationException("Junction fixture timed out."); }
        if (process.ExitCode != 0) throw new InvalidOperationException(output.GetAwaiter().GetResult() + errors.GetAwaiter().GetResult());
    }

    private sealed class Fixture
    {
        public string Root { get; }
        public string Store => Path.Combine(Root, "store");
        public string Policy => Path.Combine(Root, "retention.json");
        public string Receipt => Path.Combine(Root, "apply-result.json");
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        public Fixture(string work)
        {
            Root = Path.Combine(work, "gc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Store);
        }
        public string Source(string name, string content)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllText(path, content);
            return path;
        }
        public string Unit(string name, string? input = null)
        {
            string directory = Path.Combine(Store, name);
            Directory.CreateDirectory(Path.Combine(directory, "stage"));
            string artifact = Path.Combine(directory, "stage", "result.json");
            Files.WriteNew(artifact, new { SchemaVersion = Schema.Version, Kind = "tlc-result", Status = "passed" });
            input ??= Source(name + ".tla", "synthetic model, not native evidence");
            string hash = Files.Hash(input);
            Directory.CreateDirectory(Path.Combine(directory, "inputs"));
            File.Copy(input, Path.Combine(directory, "inputs", hash));
            string relative = Path.Combine("stage", "result.json");
            string artifactHash = Files.Hash(artifact);
            Files.WriteNew(Path.Combine(directory, "run.json"), new WorkflowRun {
                Project = "synthetic", ProjectDirectory = Root, Scenario = "test",
                RequirementsSha256 = new('a', 64), ProjectSha256 = new('b', 64), ExecutionStatus = "completed",
                Fingerprints = new(FilePaths.Comparer) { [input] = hash },
                Artifacts = new(StringComparer.Ordinal) { [relative] = artifactHash },
                Stages = [new("stage", "tlc", "completed", "passed", relative, artifactHash, [], 0)]
            });
            return directory;
        }
        public void EditRun(string unit, Action<WorkflowRun> edit)
        {
            string path = Path.Combine(unit, "run.json");
            WorkflowRun run = ProjectInputs.Read<WorkflowRun>(path);
            edit(run);
            File.WriteAllText(path, JsonSerializer.Serialize(run, Files.Json));
        }
        public void RewriteInput(string unit, string input)
        {
            string hash = Files.Hash(input);
            File.Copy(input, Path.Combine(unit, "inputs", hash));
            EditRun(unit, r => r.Fingerprints.Add(input, hash));
        }
        public void Pins(List<RetentionPin> pins, List<string>? projects = null) =>
            File.WriteAllText(Policy, JsonSerializer.Serialize(new EvidenceRetention(Schema.Version, "evidence-retention", pins, projects ?? []), Files.Json));
        public ProtectedConsumer Capture(string path, List<CatalogInput> inputs, List<string> references)
        {
            bool directory = Directory.Exists(path);
            string root = directory ? path : Store;
            string[] files = directory ? Directory.GetFiles(path, "*", SearchOption.AllDirectories) : [path];
            return new(Path.GetRelativePath(Root, path), "Synthetic protected native consumer",
                files.ToDictionary(f => Path.GetRelativePath(root, f), Files.Hash, FilePaths.Comparer), inputs, references);
        }
        public void Catalog(List<ProtectedConsumer> consumers)
        {
            string producer = Source("adapter.cs", "synthetic native metadata producer");
            Files.WriteNew(Path.Combine(Root, "catalog.json"), new ProtectedConsumerCatalog(Schema.Version,
                "protected-consumer-catalog", Store, new() { [producer] = Files.Hash(producer) }, consumers));
            EvidenceRetention policy = ProjectInputs.Read<EvidenceRetention>(Policy);
            File.WriteAllText(Policy, JsonSerializer.Serialize(policy with { Catalog = "catalog.json" }, Files.Json));
        }
        public ReviewDecision Review(string evidence, string disposition = "accepted") =>
            new(Schema.Version, "review-decision", "review", new('c', 64), disposition, "synthetic fixture",
                "Synthetic GC metadata, not a proof assertion.", Now,
                new(StringComparer.Ordinal) { [evidence] = Files.Hash(FilePaths.Full(evidence, Root)) }, [evidence]);
        public string Project()
        {
            string path = Path.Combine(Root, "project.json");
            Files.WriteNew(path, new AnalysisProject(Schema.Version, "analysis-project", "synthetic", new(),
                new("net10.0", "14", "enable", false, true, null, []), "requirements.json", [],
                [new("review", "synthetic contract", [], [])], "reviews"));
            return path;
        }
        public void Old()
        {
            foreach (string path in Directory.GetFiles(Store, "*", SearchOption.AllDirectories))
                File.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddDays(-3));
            foreach (string path in Directory.GetDirectories(Store, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
                Directory.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddDays(-3));
        }
        public GcPlan Plan(int graceHours = 24) => EvidenceGarbageCollection.Plan(Store, Policy, graceHours, Now);
        public string SavePlan()
        {
            string path = Path.Combine(Root, "gc-plan.json");
            Files.WriteNew(path, Plan());
            return path;
        }
        public (int Code, string Json) Apply(string plan) =>
            TestCli.Run(["gc", "--apply", plan, "--exclusive", "true", "--output", Receipt, "--format", "summary"]);
        public string Snapshot() => string.Join("\n", Directory.GetFiles(Store, "*", SearchOption.AllDirectories)
            .Order(FilePaths.Comparer).Select(p => p + ":" + Files.Hash(p)));
    }
}
