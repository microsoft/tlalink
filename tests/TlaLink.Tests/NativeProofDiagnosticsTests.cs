using System.Text.Json;
using System.Text.Json.Nodes;
using TlaLink;

namespace TlaLink.Tests;

public static class NativeProofDiagnosticsTests
{
    public static void RegisterCore(Action<string, Action> test, string root, string work)
    {
        string Fixture(string name) => Files.Read(Path.Combine(root, "tests", "TlaLink.Tests", "Fixtures",
            "NativeProofDiagnostics", name));
        test("PVerifier goals retain failed unknown and remaining native locations without changing classification", () =>
        {
            string log = Fixture("PVerifierGoals.stdout.txt");
            PProofDiagnostics diagnostics = PProofOutput.Read(log);
            CoreTests.Equal(2, diagnostics.VerifiedInvariants);
            CoreTests.Equal(2, diagnostics.FailedProperties);
            CoreTests.Equal(4, diagnostics.ObservedGoals);
            CoreTests.True(diagnostics.Goals.Select(g => g.Status).SequenceEqual(["remaining", "remaining", "failed", "unknown"]));
            CoreTests.Equal(new PProofLocation("synthetic.p", 15, 2), diagnostics.Goals[2].Location);
            CoreTests.Equal(new PProofLocation("synthetic.p", 18, 6), diagnostics.Goals[3].Location);
            CoreTests.Equal("incomplete", PResults.Classify("pverifier", 0, false, log, 1).Status);
            CoreTests.Equal("incomplete", PResults.Classify("pverifier", 0, true, log, 1).Status);
            CoreTests.Equal("rejected", PResults.Classify("pverifier", 0, false,
                "Failed to verify 1 property!\n❌ Failed to verify invariant Credit at synthetic.p:15:2", 1).Status);
        });
        test("proof diagnostic previews have exact omission counts and bounded messages", () =>
        {
            string p = string.Join('\n', Enumerable.Repeat("❌ Failed to verify invariant " +
                new string('x', 3000) + " at synthetic.p:15:2", 105));
            PProofDiagnostics pd = PProofOutput.Read(p);
            CoreTests.Equal(105, pd.ObservedGoals);
            CoreTests.Equal(5, pd.OmittedGoals);
            CoreTests.Equal(100, pd.Goals.Count);
            CoreTests.Equal(2048, pd.Goals[0].Message.Length);
            string error = Fixture("DafnyPostcondition.jsonl").Split('\n')[0];
            DafnyProofDiagnostics dd = DafnyProofOutput.Read(string.Join('\n', Enumerable.Repeat(error, 103)));
            CoreTests.Equal(103, dd.ObservedGoals);
            CoreTests.Equal(3, dd.OmittedGoals);
            CoreTests.Equal(100, dd.Goals.Count);
            CoreTests.True(dd.VerifiedObligations == null && dd.Errors == null);
            JsonObject row = JsonNode.Parse(error)!.AsObject();
            JsonObject value = row["value"]!.AsObject();
            JsonNode related = value["relatedInformation"]![0]!;
            value["relatedInformation"] = new JsonArray(Enumerable.Range(0, 12).Select(_ => related.DeepClone()).ToArray());
            value["defaultFormatMessage"] = new string('x', 3000);
            dd = DafnyProofOutput.Read(row.ToJsonString());
            CoreTests.Equal(10, dd.Goals[0].RelatedInformation.Count);
            CoreTests.Equal(2, dd.Goals[0].OmittedRelatedInformation);
            CoreTests.Equal(2048, dd.Goals[0].Message.Length);
        });
        test("Dafny native postconditions preserve related zero-based source ranges", () =>
        {
            string log = Fixture("DafnyPostcondition.jsonl");
            DafnyProofDiagnostics diagnostics = DafnyProofOutput.Read(log);
            CoreTests.Equal(2, diagnostics.VerifiedObligations);
            CoreTests.Equal(1, diagnostics.Errors);
            DafnyProofGoal goal = diagnostics.Goals.Single();
            CoreTests.Equal("Verifier", goal.Source);
            CoreTests.Equal(9, goal.Location!.Range!.Start.Line);
            CoreTests.Equal(8, goal.RelatedInformation.Single().Location!.Range!.Start.Line);
            CoreTests.Equal("rejected", DafnyBackend.ClassifyVerification(new("failed", "unused", 4), log));
            string parser = log.Replace("\"source\":\"Verifier\"", "\"source\":\"Parser\"");
            CoreTests.Equal("incomplete", DafnyBackend.ClassifyVerification(new("failed", "unused", 4), parser));
            CoreTests.Equal("Parser", DafnyProofOutput.Read(parser).Goals.Single().Source);
            CoreTests.Equal("incomplete", DafnyBackend.ClassifyVerification(new("incomplete", "unused", null), log));
            CoreTests.True(DafnyProofOutput.Read(log + log).VerifiedObligations == null);
        });
        test("Dafny native timing CSV retains named proof efforts without assigning times to error diagnostics", () =>
        {
            string csv = Fixture("DafnyTiming.csv");
            DafnyProofTimings timings = DafnyProofTiming.Read(csv);
            CoreTests.Equal(2, timings.ObservedEfforts);
            CoreTests.Equal(0, timings.OmittedEfforts);
            CoreTests.Equal(1.25, timings.Efforts[0].DurationSeconds);
            CoreTests.Equal(1001L, timings.Efforts[0].ResourceCount);
            CoreTests.Equal("Credit.Terminal,Completion (correctness)", timings.Efforts[1].Name);
            CoreTests.Equal("Failed", timings.Efforts[1].Outcome);
            CoreTests.Equal(7, timings.Efforts[1].RandomSeed);
            string[] rows = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            timings = DafnyProofTiming.Read(rows[0] + "\n" + string.Join('\n', Enumerable.Repeat(rows[1], 104)));
            CoreTests.Equal(104, timings.ObservedEfforts);
            CoreTests.Equal(4, timings.OmittedEfforts);
            CoreTests.ThrowsAny<InputException>(() => DafnyProofTiming.Read(csv.Replace("00:00:01.2500000", "unknown")));
            CoreTests.ThrowsAny<InputException>(() => DafnyProofTiming.Read(csv.Replace("TestResult.Duration", "Unknown")));
        });
        test("P native numeric counters preserve exact upstream values and omit missing ambiguous totals", () =>
        {
            PNativeMetrics pex = PNativeOutput.Read("pex",
                "..... Explored 1,234 distinct states over 17 timelines\n..... Explored 2,345 distinct schedules\n" +
                "..... Number of steps explored: 2 (min), 5 (avg), 9 (max).");
            CoreTests.Equal(1234L, pex.DistinctStates);
            CoreTests.Equal(17L, pex.Timelines);
            CoreTests.Equal(2345L, pex.Schedules);
            CoreTests.Equal(9L, pex.MaximumReportedDepth);
            PNativeMetrics psym = PNativeOutput.Read("psym",
                "Explored 1 symbolic execution\nExecution finished at depth 4\nExecution finished at depth 7");
            CoreTests.Equal(1L, psym.SymbolicExecutions);
            CoreTests.Equal(7L, psym.MaximumReportedDepth);
            PNativeMetrics observe = PNativeOutput.Read("pobserve", "",
                "Total Events Read Total Verified Events Total Verified Keys Total Partition Keys\n-----\n12 11 2 3\n" +
                "Total Parser Errors Total Spec Errors Total Out of Order Errors Total Unknown Errors\n----\n1 2 0 0");
            CoreTests.Equal(12L, observe.EventsRead);
            CoreTests.Equal(11L, observe.VerifiedEvents);
            CoreTests.Equal(2L, observe.VerifiedKeys);
            CoreTests.Equal(3L, observe.PartitionKeys);
            CoreTests.Equal(1L, observe.ParserErrors);
            CoreTests.Equal(2L, observe.SpecErrors);
            CoreTests.Equal(0L, observe.UnknownErrors);
            CoreTests.True(PNativeOutput.Read("pex", "").Schedules == null);
            CoreTests.True(PNativeOutput.Read("pex", "Explored 2 distinct schedules\nExplored 3 distinct schedules").Schedules == null);
            CoreTests.True(PNativeOutput.Read("pex", "Explored 99999999999999999999999999 distinct schedules").Schedules == null);
            CoreTests.True(PNativeOutput.Read("pex", "Explored 1,2 distinct schedules").Schedules == null);
        });
        test("Apalache phase timing is a measured timestamp interval including midnight and excludes unclosed phases", () =>
        {
            List<ApalachePhaseTiming> timings = ApalacheTiming.Read(Fixture("Apalache.stdout.txt"));
            CoreTests.Equal(2, timings.Count);
            CoreTests.Equal(0.6, timings[0].DurationSeconds);
            CoreTests.Equal(0.15, timings[1].DurationSeconds);
            CoreTests.Equal("native-log-timestamp-interval", timings[0].Basis);
            CoreTests.Equal(0, ApalacheTiming.Read("PASS #0: SanyParser I@12:00:00.000").Count);
            CoreTests.Equal(0, ApalacheTiming.Read("Total time: 1.23 sec").Count);
        });
        test("backend command summaries retain proof goals and measured phase intervals", () =>
        {
            PPacket p = new() { Command = "check-p", Engine = "pverifier", Config = "config.json",
                ConfigSha256 = new('0', 64), TimeoutSeconds = 10,
                ProofDiagnostics = PProofOutput.Read(Fixture("PVerifierGoals.stdout.txt")) };
            PAnalysisSummary ps = (PAnalysisSummary)CommandSummary.Create(p, new CliOptions(["check-p"]));
            CoreTests.Equal(p.ProofDiagnostics, ps.ProofDiagnostics);
            DafnyResult d = new(Schema.Version, "dafny-result", "rejected", "not-established", "config.json",
                new('0', 64), "dafny", "output", 10, [], "rejected", "not-run", [], [], [], [], [], "boundary")
                { ProofDiagnostics = DafnyProofOutput.Read(Fixture("DafnyPostcondition.jsonl")) };
            DafnySummary ds = (DafnySummary)CommandSummary.Create(d, new CliOptions(["check-dafny"]));
            CoreTests.Equal(d.ProofDiagnostics, ds.ProofDiagnostics);
            ApalachePacket a = new() { Config = "config.json", ConfigSha256 = new('0', 64), Mode = "typecheck",
                Directory = "output", Java = "java", Jar = "apalache.jar", Model = "Credit.tla", TimeoutSeconds = 10 };
            List<ApalachePhaseTiming> intervals = ApalacheTiming.Read(Fixture("Apalache.stdout.txt"));
            a.Processes.Add(new("typing", [], 0, false, 1.48) { PhaseTimings = intervals });
            ApalacheSummary summary = (ApalacheSummary)CommandSummary.Create(a, new CliOptions(["check-apalache"]));
            CoreTests.Equal(intervals, summary.PhaseTimings["typing"]);
            CoreTests.Equal(1.48, summary.Processes.Single().DurationSeconds);
        });
        test("campaign backend metrics preserve available counters phase intervals and incomplete native reasons", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "native-metrics")).FullName;
            PPacket p = new() { Command = "check-p", Engine = "pex", Config = "config.json",
                ConfigSha256 = new('0', 64), TimeoutSeconds = 10,
                NativeMetrics = PNativeOutput.Read("pex", "Explored 12 distinct states over 3 timelines\nExplored 17 distinct schedules") };
            p.Diagnostics.Add(new(ErrorCode.PExplorationIncomplete, "Native bound exhausted before completion."));
            string file = Path.Combine(directory, "result.json");
            Files.WriteNew(file, p);
            WorkflowStageResult stage = new("proof", "p-pex", "incomplete", "incomplete", "result.json",
                Files.Hash(file), [], 1.0);
            List<string> reasons = ["incomplete"];
            List<string> pins = [];
            List<ReportMetric> metrics = ProjectReport.Metrics(stage, directory, pins.Add, reasons);
            CoreTests.True(metrics.Contains(new("distinct_states", "12", "count")));
            CoreTests.True(metrics.Contains(new("schedules", "17", "count")));
            CoreTests.True(!metrics.Any(m => m.Name == "maximum_reported_depth"));
            CoreTests.True(reasons.Contains("Native bound exhausted before completion."));
            CoreTests.True(pins.Contains(file));
            File.Delete(file);
            DafnyResult d = new(Schema.Version, "dafny-result", "rejected", "not-established", "config.json",
                new('0', 64), "dafny", "output", 10, [], "rejected", "not-run", [], [], [], [], [], "boundary")
                { ProofDiagnostics = DafnyProofOutput.Read(Fixture("DafnyPostcondition.jsonl")),
                    ProofTimings = DafnyProofTiming.Read(Fixture("DafnyTiming.csv")) };
            Files.WriteNew(file, d);
            stage = stage with { Sha256 = Files.Hash(file) };
            metrics = ProjectReport.Metrics(stage, directory, pins.Add, reasons);
            CoreTests.True(metrics.Contains(new("verified_obligations", "2", "count")));
            CoreTests.True(metrics.Any(m => m.Name.StartsWith("native_proof_effort.", StringComparison.Ordinal) &&
                m.Value == "1.25" && m.Unit == "seconds"));
            File.Delete(file);
            ApalachePacket a = new() { Config = "config.json", ConfigSha256 = new('0', 64), Mode = "typecheck",
                Directory = "output", Java = "java", Jar = "apalache.jar", Model = "Credit.tla", TimeoutSeconds = 10 };
            a.Processes.Add(new("typing", [], 0, false, 1.48) {
                PhaseTimings = ApalacheTiming.Read(Fixture("Apalache.stdout.txt")) });
            Files.WriteNew(file, a);
            stage = stage with { Sha256 = Files.Hash(file) };
            metrics = ProjectReport.Metrics(stage, directory, pins.Add, reasons);
            CoreTests.True(metrics.Contains(new("native_log_interval.typing.pass-0.SanyParser", "0.6", "seconds")));
            File.AppendAllText(file, " ");
            metrics = ProjectReport.Metrics(stage, directory, pins.Add, reasons);
            CoreTests.Equal(0, metrics.Count);
            CoreTests.True(reasons.Contains("inconsistent-stage-evidence"));
        });
    }

    public static void RegisterNative(Action<string, Action> test, string root, string work)
    {
        test("LeakedCredit terminal obligation rejects while a Dafny parser control remains incomplete", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "terminal-credit")).FullName;
            foreach (string file in new[] { "Dispatcher.dfy", "LeakedCredit.dfy" })
                File.Copy(Path.Combine(root, "examples", "Dispatcher", "verification", "Dafny", file), Path.Combine(directory, file));
            string parser = Path.Combine(root, "tests", "TlaLink.Tests", "Fixtures", "NativeProofDiagnostics", "ParserFailure.dfy");
            File.Copy(parser, Path.Combine(directory, "ParserFailure.dfy"));
            foreach (var control in new[] { (File: "LeakedCredit.dfy", Code: 1), (File: "ParserFailure.dfy", Code: 2) })
            {
                string config = Path.Combine(directory, control.File + ".json");
                Files.WriteNew(config, new DafnyAnalysis(Schema.Version, "dafny-analysis", [control.File]));
                string output = Path.Combine(directory, control.File + ".evidence");
                var run = TestCli.Run(["check-dafny", "--config", config, "--dafny", TestTools.Dafny(root),
                    "--output", output, "--timeout", "30"]);
                if (run.Code != control.Code) throw new InvalidOperationException(run.Json);
                DafnyResult receipt = ProjectInputs.Read<DafnyResult>(Path.Combine(output, "result.json"));
                CoreTests.Equal(control.Code == 1 ? "rejected" : "incomplete", receipt.Status);
                CoreTests.Equal(0, DafnyAnalysisBackend.Validate(receipt, output).Count);
                if (control.Code == 1)
                {
                    DafnyProofGoal goal = receipt.ProofDiagnostics!.Goals.Single();
                    CoreTests.True(goal.Message.Contains("postcondition", StringComparison.Ordinal));
                    CoreTests.True(goal.RelatedInformation.Any(r => r.Location?.Filename == "LeakedCredit.dfy"));
                    CoreTests.Equal(1, receipt.ProofDiagnostics.Errors);
                    CoreTests.True(receipt.ProofDiagnostics.VerifiedObligations > 0);
                    DafnyProofEffort effort = receipt.ProofTimings!.Efforts.Single(e =>
                        e.Name.Contains("TerminalCompletionReturnsCredit", StringComparison.Ordinal) && e.Outcome == "Failed");
                    CoreTests.True(effort.DurationSeconds >= 0 && effort.ResourceCount > 0);
                    CoreTests.True(DafnyAnalysisBackend.Validate(receipt with {
                        ProofDiagnostics = receipt.ProofDiagnostics with { Errors = 0 }
                    }, output).Any(i => i.Is(ErrorCode.WorkflowStageInconsistent)));
                }
                else CoreTests.True(receipt.ProofDiagnostics == null);
            }
        });
    }
}
