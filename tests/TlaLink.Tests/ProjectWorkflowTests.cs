using System.Text.Json;
using System.Text.Json.Nodes;
using TlaLink;

namespace TlaLink.Tests;

public static class ProjectWorkflowTests
{
    public static void Register(Action<string, Action> test, string root, string work)
    {
        ProjectCompositionTests.RegisterNative(test, root, work);
        string fixture = Path.Combine(work, "workflow-project");
        string example = Path.Combine(fixture, "Workflow");
        foreach (string area in new[] { "Workflow", "Integrated" })
            StageEvidenceTests.Copy(Path.Combine(root, "examples", area), Path.Combine(fixture, area));
        string path = Path.Combine(example, "tlalink.project.json");
        string dafny = TestTools.Dafny(root);
        string jar = TestTools.Jar(root);
        JsonObject authoredRoot = JsonNode.Parse(Files.Read(path))!.AsObject();
        authoredRoot["tools"] = JsonSerializer.SerializeToNode(
            new ProjectTools(new(dafny, Files.Hash(dafny)), new(jar, Files.Hash(jar))), Files.Json);
        Save(path, authoredRoot);
        // Variants below are saved as flat projects; restoring these bytes returns to the composed example.
        string authored = Files.Read(path);
        void Restore() => File.WriteAllText(path, authored);
        AnalysisProject original = ProjectComposition.Read(path);
        string policyPath = ProjectInputs.Resolve(path, original.Requirements);
        AcceptancePolicy policy = ProjectInputs.Read<AcceptancePolicy>(policyPath);
        Save(policyPath, policy);
        string? creditRun = null;
        string RunScenario(string scenario, int expected = 0)
        {
            string output = Path.Combine(work, "workflow-" + scenario + "-" + Guid.NewGuid().ToString("N"));
            var response = TestCli.Run(["run", "--project", path, "--scenario", scenario, "--output", output]);
            if (response.Code != expected)
            {
                if (!File.Exists(Path.Combine(output, "run.json")))
                    throw new InvalidOperationException(response.Json);
                WorkflowRun failed = ProjectInputs.Read<WorkflowRun>(Path.Combine(output, "run.json"));
                string details = string.Join("\n", failed.Stages.Select(s => s.Id + ": " + s.Outcome +
                    (s.Evidence.Length == 0 ? "" : "\n" + Files.Read(Path.Combine(output, s.Evidence)))));
                throw new InvalidOperationException(details);
            }
            CoreTests.True(File.Exists(Path.Combine(output, "run.json")));
            return output;
        }
        string Credits() => creditRun ??= RunScenario("credits");
        int Accept(string output) => TestCli.Run(["accept", "--project", path, "--run", output]).Code;

        test("TLC diagnostics surface nested native errors and locations without reclassifying them", () =>
        {
            string log = """
                @!@!@STARTMSG 1000:1 @!@!@
                TLC threw an unexpected exception.
                @!@!@STARTMSG 2280:0 @!@!@
                In evaluation, the identifier dWire is either undefined or not an operator.
                line 10, col 11 to line 10, col 15 of module MixedDuplex
                @!@!@ENDMSG 2280 @!@!@
                @!@!@ENDMSG 1000 @!@!@
                @!@!@STARTMSG 2114:1 @!@!@
                Deadlock reached.
                @!@!@ENDMSG 2114 @!@!@
                """;
            List<Issue> issues = ProjectWorkflow.TlcDiagnostics(log);
            CoreTests.Equal(2, issues.Count);
            CoreTests.Equal("TLC1000", issues[0].Code);
            CoreTests.True(issues[0].Message.Contains("identifier dWire", StringComparison.Ordinal));
            CoreTests.True(issues[0].Message.Contains("line 10, col 11", StringComparison.Ordinal));
            CoreTests.True(!issues[0].Message.Contains("@!@!@", StringComparison.Ordinal));
            CoreTests.Equal("TLC2114", issues[1].Code);
            CoreTests.True(issues[1].Message.Contains("Deadlock reached.", StringComparison.Ordinal));
            CoreTests.True(ProjectWorkflow.TlcDiagnostics("@!@!@STARTMSG 1000:1 @!@!@")
                .Single().Message.Contains("body is unavailable", StringComparison.Ordinal));
            TlcStatistics counts = ProjectWorkflow.ReadTlcStatistics("""
                599,844 states generated, 137,075 distinct states found, 0 states left on queue.
                The depth of the complete state graph search is 61.
                calculated (optimistic):  val = 3.4E-9
                """);
            CoreTests.Equal(599844L, counts.StatesGenerated);
            CoreTests.Equal(137075L, counts.DistinctStates);
            CoreTests.Equal(0L, counts.StatesLeft);
            CoreTests.Equal(61L, counts.SearchDepth);
            CoreTests.Equal("3.4E-9", counts.OptimisticFingerprintOmissionEstimate);
            CoreTests.True(ProjectWorkflow.ReadTlcStatistics("incomplete search").DistinctStates == null);
        });
        test("project doctor and plan resolve project-relative inputs from another directory", () =>
        {
            var doctor = TestCli.Run(["doctor", "--project", path]);
            if (doctor.Code != 0) throw new InvalidOperationException(doctor.Json);
            CoreTests.True(doctor.Json.Contains("pending", StringComparison.Ordinal));
            var plan = TestCli.Run(["plan", "--project", path, "--scenario", "lanes"]);
            if (plan.Code != 0) throw new InvalidOperationException(plan.Json);
            CoreTests.True(plan.Json.Contains("explicit handoffs", StringComparison.Ordinal));
            var text = TestCli.Run(["doctor", "--project", path, "--format", "text"]);
            CoreTests.Equal(0, text.Code);
            CoreTests.True(text.Json.Length < 4000 && text.Json.Contains("project-preflight", StringComparison.Ordinal));
        });
        test("project rejects obsolete schema unknown fields invalid compiler and cyclic dependencies", () =>
        {
            try
            {
                Save(path, original with { SchemaVersion = 1 });
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path]).Code);
                Save(path, original with { Compiler = original.Compiler with { Checked = true } });
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path]).Code);
                AnalysisScenario cycle = new("credits", [
                    new("a", "oracle", ["b"], 60, @"..\Integrated\bindings.json"),
                    new("b", "oracle", ["a"], 60, @"..\Integrated\bindings.json")]);
                Save(path, original with { Scenarios = [cycle] });
                CoreTests.Equal(2, TestCli.Run(["plan", "--project", path]).Code);
                JsonObject unknown = JsonSerializer.SerializeToNode(original, Files.Json)!.AsObject();
                unknown["guess"] = true;
                Save(path, unknown);
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path]).Code);
            }
            finally { Restore(); }
        });
        test("TLC preflight pins transitive imports and rejects invalid models before creating a run", () =>
        {
            string model = Path.Combine(example, "Preflight.tla");
            string helper = Path.Combine(example, "PreflightHelper.tla");
            string config = Path.Combine(example, "Preflight.cfg");
            File.WriteAllText(model, """
                ---------------- MODULE Preflight ----------------
                EXTENDS Naturals, PreflightHelper
                VARIABLE x
                Init == x = 0
                Next == x' = 1-x
                Spec == Init /\ [][Next]_x
                Safety == x <= Limit
                ==================================================
                """);
            File.WriteAllText(helper, "---- MODULE PreflightHelper ----\nLimit == 1\n====");
            File.WriteAllText(config, "SPECIFICATION Spec\nINVARIANT Safety\n");
            try
            {
                AnalysisStage stage = new("model", "tlc", [], 60, Models: ["Preflight.tla"], Config: "Preflight.cfg");
                Save(path, original with { Scenarios = [new("parse", [stage])], Obligations = [], CheckObligations = null });
                Save(policyPath, policy with { Requirements = [new("safety", "parse", "model", "passed", ConfigSha256: Files.Hash(config))] });
                var valid = TestCli.Run(["plan", "--project", path, "--scenario", "parse"]);
                if (valid.Code != 0) throw new InvalidOperationException(valid.Json);
                using JsonDocument report = JsonDocument.Parse(valid.Json);
                CoreTests.Equal(Files.Hash(helper), report.RootElement.GetProperty("fingerprints").GetProperty(FilePaths.Full(helper)).GetString());
                File.WriteAllText(model, "This is not a TLA+ module.");
                var invalid = TestCli.Run(["plan", "--project", path, "--scenario", "parse", "--format", "summary"]);
                CoreTests.Equal(2, invalid.Code);
                CoreTests.True(invalid.Json.Contains("successfully parsed, fresh model", StringComparison.Ordinal));
                string output = Path.Combine(work, "invalid-tlc-preflight-run");
                CoreTests.Equal(2, TestCli.Run(["run", "--project", path, "--scenario", "parse", "--output", output]).Code);
                CoreTests.True(!Directory.Exists(output));
            }
            finally { Restore(); Save(policyPath, policy); }
        });
        test("declared check coverage reports mappings without inventing obligations or discharging missing checks", () =>
        {
            try
            {
                CoreTests.Equal(0, ProjectInputs.Coverage(original, policy, ["lanes"]).Count);
                AnalysisProject mapped = original with { CheckObligations = [
                    new("lane-safety", "safety", "lanes", ["lane-model"]),
                    new("lane-deadlock", "deadlock", "lanes", ["lane-model"])] };
                Save(path, mapped);
                var ready = TestCli.Run(["plan", "--project", path, "--scenario", "lanes"]);
                CoreTests.Equal(0, ready.Code);
                using (JsonDocument report = JsonDocument.Parse(ready.Json))
                    CoreTests.True(report.RootElement.GetProperty("check_coverage").EnumerateArray()
                        .All(o => o.GetProperty("status").GetString() == "mapped"));
                Save(path, mapped with { CheckObligations = [new("completion", "progress", "lanes", [])] });
                foreach (string command in new[] { "doctor", "plan" })
                {
                    var missing = TestCli.Run([command, "--project", path, "--scenario", "lanes"]);
                    CoreTests.Equal(2, missing.Code);
                    CoreTests.True(missing.Json.Contains("No required check is mapped.", StringComparison.Ordinal));
                }
                string output = Path.Combine(work, "unmapped-check-run");
                CoreTests.Equal(2, TestCli.Run(["run", "--project", path, "--scenario", "lanes", "--output", output]).Code);
                CoreTests.True(!Directory.Exists(output));
                CoreTests.Equal(0, TestCli.Run(["doctor", "--project", path, "--scenario", "credits"]).Code);
                foreach (CheckObligation invalid in new[] {
                    new CheckObligation("unknown", "liveness", "lanes", ["lane-model"]),
                    new CheckObligation("unknown", "safety", "missing", ["lane-model"]),
                    new CheckObligation("unknown", "safety", "lanes", ["missing"]),
                    new CheckObligation("unknown", "safety", "credits", ["lane-model"]) })
                {
                    Save(path, mapped with { CheckObligations = [invalid] });
                    CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path]).Code);
                }
            }
            finally { Restore(); }
        });
        test("unused tool pins and optional defines can be omitted without weakening required inputs", () =>
        {
            try
            {
                AnalysisProject tla = original with {
                    Tools = new(Jar: original.Tools.Jar),
                    Scenarios = original.Scenarios.Where(s => s.Id == "lanes").ToList(),
                    Obligations = [] };
                AcceptancePolicy requirements = policy with {
                    Requirements = policy.Requirements.Where(r => r.Scenario == "lanes").ToList() };
                Save(policyPath, requirements);
                JsonObject document = JsonSerializer.SerializeToNode(tla, Files.Json)!.AsObject();
                document["tools"]!.AsObject().Remove("dafny");
                document["tools"]!.AsObject().Remove("p");
                document["tools"]!.AsObject().Remove("apalache");
                document["compiler"]!.AsObject().Remove("defines");
                Save(path, document);
                CoreTests.Equal(0, TestCli.Run(["doctor", "--project", path]).Code);
                CoreTests.True(ProjectInputs.Read<AnalysisProject>(path).Compiler.Defines == null);
                document["compiler"]!["defines"] = new JsonArray("FEATURE");
                Save(path, document);
                CoreTests.Equal("FEATURE", ProjectInputs.Read<AnalysisProject>(path).Compiler.Defines!.Single());
                document["compiler"]!.AsObject().Remove("references");
                Save(path, document);
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path]).Code);
                document["compiler"]!["references"] = new JsonArray();
                document["tools"]!.AsObject().Remove("jar");
                Save(path, document);
                var missing = TestCli.Run(["doctor", "--project", path]);
                CoreTests.Equal(2, missing.Code);
                CoreTests.True(missing.Json.Contains("pinned JAR", StringComparison.Ordinal));
            }
            finally { Restore(); Save(policyPath, policy); }
        });
        test("project tool version pins cannot silently follow changed executables", () =>
        {
            try
            {
                Save(path, original with { Tools = original.Tools with { Dafny = original.Tools.Dafny! with { Sha256 = new string('0', 64) } } });
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path]).Code);
            }
            finally { Restore(); }
        });
        test("binding and case IDs cannot alias requirement selector delimiters", () =>
        {
            string manifestPath = Path.Combine(fixture, "Integrated", "bindings.json");
            string before = Files.Read(manifestPath);
            IntegratedManifest manifest = ProjectInputs.Read<IntegratedManifest>(manifestPath);
            try
            {
                Save(manifestPath, manifest with { Bindings = [
                    manifest.Bindings[0] with { Id = "credit/return" }, .. manifest.Bindings.Skip(1)] });
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path, "--scenario", "credits"]).Code);
                Save(manifestPath, manifest with { Bindings = [
                    manifest.Bindings[0] with { Cases = [
                        manifest.Bindings[0].Cases[0] with { Id = "return/one" }] }, .. manifest.Bindings.Skip(1)] });
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path, "--scenario", "credits"]).Code);
            }
            finally { File.WriteAllText(manifestPath, before); }
        });
        test("credit workflow produces complete accepted evidence with all required cases", () =>
        {
            string output = Credits();
            CoreTests.Equal(0, Accept(output));
            var explanation = TestCli.Run(["explain", "--project", path, "--run", output]);
            CoreTests.Equal(0, explanation.Code);
            CoreTests.True(explanation.Json.Contains("\"accepted\"", StringComparison.Ordinal));
            var summary = TestCli.Run(["accept", "--project", path, "--run", output, "--format", "summary"]);
            CoreTests.Equal(0, summary.Code);
            using (JsonDocument report = JsonDocument.Parse(summary.Json))
            {
                CoreTests.Equal("command-summary", report.RootElement.GetProperty("kind").GetString());
                CoreTests.Equal("accepted", report.RootElement.GetProperty("acceptance_status").GetString());
                CoreTests.True(!report.RootElement.TryGetProperty("fingerprints", out _));
            }
            CoreTests.Equal(2, TestCli.Run(["run", "--project", path, "--scenario", "credits", "--output", output]).Code);
        });
        test("workflow acceptance reports undeclared and duplicate run stages without crashing", () =>
        {
            string output = Credits();
            string variantPath = Path.Combine(example, "other-project.json");
            string variantPolicy = Path.Combine(example, "other-requirements.json");
            AnalysisScenario credits = original.Scenarios.Single(s => s.Id == "credits");
            AnalysisProject variant = original with {
                Id = original.Id + "-other", Requirements = Path.GetFileName(variantPolicy),
                Scenarios = original.Scenarios.Select(s => s.Id != credits.Id ? s : s with {
                    Stages = s.Stages.Select(stage => stage with {
                        Id = "other-" + stage.Id,
                        DependsOn = stage.DependsOn.Select(id => "other-" + id).ToList()
                    }).ToList()
                }).ToList()
            };
            Save(variantPath, variant);
            Save(variantPolicy, policy with {
                Project = variant.Id,
                Requirements = policy.Requirements.Select(r => r.Scenario == credits.Id
                    ? r with { Stage = "other-" + r.Stage } : r).ToList()
            });
            var mismatched = TestCli.Run(["accept", "--project", variantPath, "--run", output]);
            CoreTests.Equal(2, mismatched.Code);
            CoreTests.True(mismatched.Json.Contains("WF014", StringComparison.Ordinal));
            CoreTests.True(mismatched.Json.Contains("undeclared stage", StringComparison.Ordinal));

            string runPath = Path.Combine(output, "run.json");
            string before = Files.Read(runPath);
            try
            {
                JsonObject document = JsonNode.Parse(before)!.AsObject();
                JsonArray stages = document["stages"]!.AsArray();
                stages.Add(stages[0]!.DeepClone());
                Save(runPath, document);
                var duplicated = TestCli.Run(["accept", "--project", path, "--run", output]);
                CoreTests.Equal(2, duplicated.Code);
                CoreTests.True(duplicated.Json.Contains("WF011", StringComparison.Ordinal));
                document = JsonNode.Parse(before)!.AsObject();
                document["scenario"] = "undeclared";
                Save(runPath, document);
                var unknownScenario = TestCli.Run(["accept", "--project", path, "--run", output]);
                CoreTests.Equal(2, unknownScenario.Code);
                using JsonDocument failure = JsonDocument.Parse(unknownScenario.Json);
                CoreTests.Equal("WF010", failure.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
                var summaryFailure = TestCli.Run(["accept", "--project", path, "--run", output, "--format", "summary"]);
                CoreTests.Equal(2, summaryFailure.Code);
                using JsonDocument summaryReport = JsonDocument.Parse(summaryFailure.Json);
                CoreTests.Equal("WF010", summaryReport.RootElement.GetProperty("diagnostics")[0].GetProperty("code").GetString());
            }
            finally { File.WriteAllText(runPath, before); }
        });
        test("workflow acceptance rejects missing required cases even with a successful oracle run", () =>
        {
            string output = Credits();
            try
            {
                AnalysisRequirement requirement = policy.Requirements[0];
                Save(policyPath, policy with { Requirements = [requirement with { Cases = [.. requirement.Cases!, "credit-return/missing"] }, .. policy.Requirements.Skip(1)] });
                var rejected = TestCli.Run(["accept", "--project", path, "--run", output]);
                CoreTests.True(rejected.Code != 0);
                CoreTests.True(rejected.Json.Contains("credit-return/missing", StringComparison.Ordinal));
                var summary = TestCli.Run(["accept", "--project", path, "--run", output, "--format", "summary"]);
                CoreTests.Equal(rejected.Code, summary.Code);
                using JsonDocument grouped = JsonDocument.Parse(summary.Json);
                CoreTests.True(grouped.RootElement.GetProperty("worklist").GetProperty("groups").EnumerateArray()
                    .Any(g => g.GetProperty("kind").GetString() == "requirements" &&
                        g.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("missing").EnumerateArray()
                            .Any(reason => reason.GetString() == "case credit-return/missing"))));
            }
            finally { Save(policyPath, policy); }
        });
        test("workflow acceptance rejects changed stage artifacts and stale source inputs separately", () =>
        {
            string output = Credits();
            string artifact = Path.Combine(output, "decisions", "result.json");
            string before = Files.Read(artifact);
            try
            {
                File.AppendAllText(artifact, "\n");
                var bad = TestCli.Run(["accept", "--project", path, "--run", output]);
                CoreTests.Equal(1, bad.Code);
                CoreTests.True(bad.Json.Contains("WF012", StringComparison.Ordinal));
            }
            finally { File.WriteAllText(artifact, before); }
            string source = Path.Combine(fixture, "Integrated", "Decisions.cs");
            before = Files.Read(source);
            try
            {
                File.AppendAllText(source, "\n");
                var stale = TestCli.Run(["accept", "--project", path, "--run", output]);
                CoreTests.Equal(2, stale.Code);
                CoreTests.True(stale.Json.Contains("WF013", StringComparison.Ordinal));
            }
            finally { File.WriteAllText(source, before); }
            CoreTests.Equal(0, Accept(output));
        });
        test("workflow acceptance cannot turn incomplete execution into a passed control", () =>
        {
            string output = Credits();
            string manifest = Path.Combine(output, "run.json");
            string before = Files.Read(manifest);
            try
            {
                JsonObject interrupted = JsonNode.Parse(before)!.AsObject();
                interrupted["execution_status"] = "incomplete";
                Save(manifest, interrupted);
                var rejected = TestCli.Run(["accept", "--project", path, "--run", output]);
                CoreTests.Equal(2, rejected.Code);
                CoreTests.True(rejected.Json.Contains("cannot satisfy a negative control", StringComparison.Ordinal));
            }
            finally { File.WriteAllText(manifest, before); }
        });
        test("workflow lane model and entity-aware compiled execution both satisfy their requirements", () =>
        {
            string output = RunScenario("lanes");
            var accepted = TestCli.Run(["accept", "--project", path, "--run", output]);
            if (accepted.Code != 0) throw new InvalidOperationException(accepted.Json);
        });
        test("workflow cancellation cleanup and unsafe proof need explicit current adapter review", () =>
        {
            string output = RunScenario("cancellation");
            CoreTests.Equal(1, Accept(output));
            string reviewEvidence = Path.Combine(example, "cleanup-review-evidence.json");
            File.Copy(Path.Combine(output, "cleanup", "result.json"), reviewEvidence);
            var reviewed = TestCli.Run(["record-review", "--project", path, "--obligation", "cleanup-adapter",
                "--reviewer", "test-control", "--rationale", "Synthetic review gate regression; not a product approval.",
                "--evidence", Path.GetRelativePath(example, reviewEvidence)]);
            CoreTests.Equal(0, reviewed.Code);
            var accepted = TestCli.Run(["accept", "--project", path, "--run", output]);
            if (accepted.Code != 0) throw new InvalidOperationException(accepted.Json);
            CoreTests.Equal("accepted", ProjectAcceptance.ReviewStatus(path, original, "cleanup-adapter"));
            var summary = TestCli.Run(["accept", "--project", path, "--run", output, "--format", "summary"]);
            using (JsonDocument report = JsonDocument.Parse(summary.Json))
            {
                JsonElement review = report.RootElement.GetProperty("review_status")[0];
                CoreTests.Equal("cleanup-adapter", review.GetProperty("id").GetString());
                CoreTests.Equal("accepted", review.GetProperty("status").GetString());
                CoreTests.Equal("test-control", review.GetProperty("reviewer").GetString());
            }
            string evidenceBefore = Files.Read(reviewEvidence);
            try
            {
                File.AppendAllText(reviewEvidence, "\n");
                var staleReview = TestCli.Run(["accept", "--project", path, "--run", output]);
                CoreTests.Equal(1, staleReview.Code);
                using JsonDocument report = JsonDocument.Parse(staleReview.Json);
                CoreTests.Equal("current", report.RootElement.GetProperty("current_applicability").GetString());
                CoreTests.Equal("stale", report.RootElement.GetProperty("reviews")[0].GetProperty("status").GetString());
                CoreTests.True(staleReview.Json.Contains("WF015", StringComparison.Ordinal));
            }
            finally { File.WriteAllText(reviewEvidence, evidenceBefore); }
            AnalysisProject changedClaim = original with { Obligations = [
                original.Obligations[0] with { Claim = "A different abstraction claim." }] };
            CoreTests.Equal("stale", ProjectAcceptance.ReviewStatus(path, changedClaim, "cleanup-adapter"));
            string source = Path.Combine(example, "src", "Cancellation", "Cancellation.cs");
            string before = Files.Read(source);
            try
            {
                File.AppendAllText(source, "\n");
                CoreTests.Equal("stale", ProjectAcceptance.ReviewStatus(path, original, "cleanup-adapter"));
                CoreTests.Equal(2, Accept(output));
            }
            finally { File.WriteAllText(source, before); }
        });
        test("native TLC rejection satisfies only its specifically required diagnostic", () =>
        {
            string nativeProject = Path.Combine(example, "native-project.json");
            File.WriteAllText(Path.Combine(example, "Broken.tla"), """
                ---- MODULE Broken ----
                EXTENDS Naturals
                VARIABLE x
                Init == x = 0
                Next == x' = x + 1
                Bounded == x < 1
                ====
                """);
            File.WriteAllText(Path.Combine(example, "Broken.cfg"), "INIT Init\nNEXT Next\nINVARIANT Bounded\nCHECK_DEADLOCK FALSE\n");
            AnalysisScenario scenario = new("negative", [new("counterexample", "tlc", [], 30,
                Models: ["Broken.tla"], Config: "Broken.cfg")]);
            AnalysisProject project = original with {
                Id = "native-controls", Requirements = "native-policy.json", Scenarios = [scenario], Obligations = []
            };
            Save(nativeProject, project);
            AcceptancePolicy acceptance = new(Schema.Version, "acceptance-policy", project.Id, [
                new("required-counterexample", "negative", "counterexample", "rejected", "TLC2110",
                    ConfigSha256: Files.Hash(Path.Combine(example, "Broken.cfg")), DiagnosticText: "Invariant Bounded is violated.")
            ]);
            Save(Path.Combine(example, project.Requirements), acceptance);
            string output = Path.Combine(work, "native-counterexample");
            var run = TestCli.Run(["run", "--project", nativeProject, "--scenario", "negative", "--output", output]);
            if (run.Code != 0) throw new InvalidOperationException(run.Json);
            var accepted = TestCli.Run(["accept", "--project", nativeProject, "--run", output]);
            if (accepted.Code != 0) throw new InvalidOperationException(accepted.Json);
            string nativePolicy = Path.Combine(example, project.Requirements);
            Save(nativePolicy, acceptance with { Requirements = [acceptance.Requirements[0] with {
                DiagnosticText = "Invariant OtherBound is violated." }] });
            string wrongOutput = Path.Combine(work, "native-wrong-invariant");
            CoreTests.Equal(0, TestCli.Run(["run", "--project", nativeProject, "--scenario", "negative", "--output", wrongOutput]).Code);
            var wrongInvariant = TestCli.Run(["accept", "--project", nativeProject, "--run", wrongOutput]);
            CoreTests.Equal(1, wrongInvariant.Code);
            CoreTests.True(wrongInvariant.Json.Contains("Invariant OtherBound", StringComparison.Ordinal));
            foreach (AnalysisRequirement invalid in new[] {
                acceptance.Requirements[0] with { DiagnosticText = " " },
                acceptance.Requirements[0] with { Expected = "passed" },
                acceptance.Requirements[0] with { Diagnostic = null } })
            {
                Save(nativePolicy, acceptance with { Requirements = [invalid] });
                CoreTests.Equal(2, TestCli.Run(["plan", "--project", nativeProject]).Code);
            }
            Save(nativePolicy, acceptance);
            File.WriteAllText(Path.Combine(example, "Broken.cfg"), "INIT Init\nNEXT Next\nCHECK_DEADLOCK FALSE\n");
            var weakened = TestCli.Run(["doctor", "--project", nativeProject]);
            CoreTests.Equal(2, weakened.Code);
            CoreTests.True(weakened.Json.Contains("independently approved", StringComparison.Ordinal));
        });
        test("native TLC deadlock is a completed rejection only for the required deadlock diagnostic", () =>
        {
            string nativeProject = Path.Combine(example, "deadlock-project.json");
            File.WriteAllText(Path.Combine(example, "Deadlock.tla"),
                "---- MODULE Deadlock ----\nVARIABLE x\nInit == x = 0\nNext == FALSE /\\ UNCHANGED x\n====\n");
            string config = Path.Combine(example, "Deadlock.cfg");
            File.WriteAllText(config, "INIT Init\nNEXT Next\n");
            AnalysisProject project = original with {
                Id = "deadlock-control", Requirements = "deadlock-policy.json",
                Scenarios = [new("negative", [new("deadlock", "tlc", [], 30, Models: ["Deadlock.tla"], Config: "Deadlock.cfg")])],
                Obligations = []
            };
            Save(nativeProject, project);
            AcceptancePolicy deadlockPolicy = new(Schema.Version, "acceptance-policy", project.Id,
                [new("required-deadlock", "negative", "deadlock", "rejected", "TLC2114", ConfigSha256: Files.Hash(config))]);
            string deadlockPolicyPath = Path.Combine(example, project.Requirements);
            Save(deadlockPolicyPath, deadlockPolicy);
            string output = Path.Combine(work, "native-deadlock");
            var run = TestCli.Run(["run", "--project", nativeProject, "--output", output]);
            if (run.Code != 0) throw new InvalidOperationException(run.Json);
            var accepted = TestCli.Run(["accept", "--project", nativeProject, "--run", output]);
            if (accepted.Code != 0) throw new InvalidOperationException(accepted.Json);
            Save(deadlockPolicyPath, deadlockPolicy with { Requirements = [deadlockPolicy.Requirements[0] with { Diagnostic = "TLC2110" }] });
            string wrongOutput = Path.Combine(work, "native-deadlock-wrong-requirement");
            CoreTests.Equal(0, TestCli.Run(["run", "--project", nativeProject, "--output", wrongOutput]).Code);
            var wrongDiagnostic = TestCli.Run(["accept", "--project", nativeProject, "--run", wrongOutput]);
            CoreTests.Equal(1, wrongDiagnostic.Code);
            CoreTests.True(wrongDiagnostic.Json.Contains("diagnostic TLC2110", StringComparison.Ordinal));
        });
    }

    private static void Save<T>(string path, T value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Files.Json));
}
