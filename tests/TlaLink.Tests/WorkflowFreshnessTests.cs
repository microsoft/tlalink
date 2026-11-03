using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class WorkflowFreshnessTests
{
    private sealed record Fixture(string Directory, string ProjectPath, string RunDirectory,
        AnalysisProject Project, AcceptancePolicy Policy, WorkflowRun Run)
    {
        internal (AcceptanceResult Report, int ExitCode, AcceptanceWorklist Worklist) Evaluate() =>
            ProjectAcceptance.Evaluate(ProjectPath, Project, Policy, RunDirectory);
    }

    private static Fixture Create(string work, string name, bool executedReview = false, bool emptyClosure = false,
        bool multipleReviews = false, bool additionalScenario = false)
    {
        string directory = Directory.CreateDirectory(Path.Combine(work, "freshness-" + name)).FullName;
        string samples = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WorkflowFreshness");
        foreach (string file in new[] { "Model.tla", "review.md", "tool.txt", "unclassified.txt" })
            File.Copy(Path.Combine(samples, file), Path.Combine(directory, file));
        string projectPath = Path.Combine(directory, "project.json"), policyPath = Path.Combine(directory, "requirements.json");
        List<ReviewObligation> obligations = [new("inspection", "Synthetic review context.", [], ["review.md"])];
        if (multipleReviews) obligations.Add(new("second-inspection", "A second synthetic review area.", [], ["review.md"]));
        AnalysisProject authored = new(Schema.Version, "analysis-project", "freshness", new(Jar:
            new("tool.txt", Files.Hash(Path.Combine(directory, "tool.txt")))),
            new("net10.0", "14", "enable", false, true, []), "requirements.json",
            [new("scope", [new("model-inputs", "inputs", [], 30, Models: ["Model.tla"])])],
            obligations, "reviews");
        AcceptancePolicy policy = new(Schema.Version, "acceptance-policy", "freshness",
            [new("scope-inputs", "scope", "model-inputs", "passed",
                Reviews: multipleReviews ? ["inspection", "second-inspection"] : ["inspection"])]);
        if (additionalScenario)
        {
            authored = authored with {
                Scenarios = [.. authored.Scenarios,
                    new("independent", [new("independent-inputs", "inputs", [], 30, Models: ["Model.tla"])])]
            };
            policy = policy with {
                Requirements = [.. policy.Requirements,
                    new("independent-check", "independent", "independent-inputs", "passed")]
            };
        }
        Files.WriteNew(projectPath, authored);
        Files.WriteNew(policyPath, policy);
        AnalysisProject project = ProjectInputs.Load(projectPath).Project;
        string runDirectory = Directory.CreateDirectory(Path.Combine(directory, "run")).FullName;
        Directory.CreateDirectory(Path.Combine(runDirectory, "inputs"));
        string stageDirectory = Directory.CreateDirectory(Path.Combine(runDirectory, "model-inputs")).FullName;
        string resultPath = Path.Combine(stageDirectory, "result.json");
        Dictionary<string, string> captured = new(FilePaths.Comparer);
        if (!emptyClosure)
        {
            string model = Path.Combine(directory, "Model.tla");
            captured.Add(model, Files.Hash(model));
            if (executedReview)
            {
                string review = Path.Combine(directory, "review.md");
                captured.Add(review, Files.Hash(review));
            }
        }
        Files.WriteNew(resultPath, new ModelInputsResult(Schema.Version, "model-inputs", "inputs", "ready",
            captured, [], new(["inputs"], directory, "unexecuted synthetic producer")));
        string evidence = Path.Combine("model-inputs", "result.json");
        WorkflowRun run = new() {
            Project = project.Id, ProjectDirectory = directory, Scenario = "scope", ExecutionStatus = "completed",
            ProjectSha256 = Files.Hash(projectPath), RequirementsSha256 = Files.Hash(policyPath),
            Stages = [new("model-inputs", "inputs", "completed", "passed", evidence, Files.Hash(resultPath), [], 1)]
        };
        foreach (string file in new[] { projectPath, policyPath, "Model.tla", "review.md", "tool.txt", "unclassified.txt" })
        {
            string input = FilePaths.IsAbsolute(file) ? file : Path.Combine(directory, file);
            string hash = Files.Hash(input);
            run.Fingerprints.Add(input, hash);
            string copy = Path.Combine(runDirectory, "inputs", hash);
            if (!File.Exists(copy)) File.Copy(input, copy);
        }
        run.Artifacts.Add(evidence, Files.Hash(resultPath));
        string expansion = Path.Combine(runDirectory, "expanded-project.json");
        Files.WriteNew(expansion, ProjectComposition.Expansion(projectPath, project));
        run.Artifacts.Add("expanded-project.json", Files.Hash(expansion));
        Files.WriteNew(Path.Combine(runDirectory, "run.json"), run);
        return new(directory, projectPath, runDirectory, project, policy, run);
    }

    private static void Save<T>(string file, T value) => File.WriteAllText(file, JsonSerializer.Serialize(value, Files.Json));

    private static Fixture Reload(Fixture fixture)
    {
        var current = ProjectInputs.Load(fixture.ProjectPath);
        return fixture with { Project = current.Project, Policy = current.Policy };
    }

    private static Fixture ReplaceReview(Fixture fixture)
    {
        Save(fixture.ProjectPath, fixture.Project with {
            Obligations = [new("adequacy", "One scoped synthetic mapping review.", ["Cooperative synthetic callers."], ["review.md"])]
        });
        Save(Path.Combine(fixture.Directory, "requirements.json"), fixture.Policy with {
            Requirements = fixture.Policy.Requirements.Select(r => r with { Reviews = ["adequacy"] }).ToList()
        });
        return Reload(fixture);
    }

    private static void Decision(Fixture fixture, string id, ReviewOrigin origin = ReviewOrigin.Human)
    {
        ReviewObligation obligation = fixture.Project.Obligations.Single(o => o.Id == id);
        string directory = Directory.CreateDirectory(Path.Combine(fixture.Directory, "reviews", id)).FullName;
        Files.WriteNew(Path.Combine(directory, "synthetic.json"), new ReviewDecision(Schema.Version, "review-decision",
            id, Schema.Hash(obligation), "accepted", "unit-test-data-only",
            "Synthetic acceptance data; not a real human approval.", DateTimeOffset.UtcNow,
            new(FilePaths.Comparer) { ["review.md"] = Files.Hash(Path.Combine(fixture.Directory, "review.md")) },
            ["review.md"], origin));
    }

    private static Fixture AddScenario(Fixture fixture)
    {
        Save(fixture.ProjectPath, fixture.Project with {
            Scenarios = [.. fixture.Project.Scenarios,
                new("independent", [new("independent-inputs", "inputs", [], 30, Models: ["Model.tla"])])]
        });
        Save(Path.Combine(fixture.Directory, "requirements.json"), fixture.Policy with {
            Requirements = [.. fixture.Policy.Requirements,
                new("independent-check", "independent", "independent-inputs", "passed", Reviews: ["inspection"])]
        });
        return Reload(fixture);
    }

    public static void Register(Action<string, Action> test, string work)
    {
        test("unrelated scenario additions preserve selected execution and pending reviews without replay", () =>
        {
            Fixture f = Create(work, "scenario-addition");
            byte[] recorded = File.ReadAllBytes(Path.Combine(f.RunDirectory, "run.json"));
            f = AddScenario(f);
            var result = f.Evaluate();
            CoreTests.Equal("consistent", result.Report.ArchiveStatus);
            CoreTests.Equal("current", result.Report.CurrentApplicability);
            CoreTests.Equal("scenario-equivalent", result.Report.PolicyReassessment!.Status);
            CoreTests.True(result.Report.PolicyReassessment.ExecutionDefinitionUnchanged == true);
            CoreTests.True(result.Report.PolicyReassessment.CheckerExpectationsUnchanged == true);
            CoreTests.True(result.Report.PolicyReassessment.ChangedAreas.Contains("other-scenarios"));
            CoreTests.Equal(1, result.Report.Requirements.Count);
            CoreTests.Equal("pending", result.Report.Reviews.Single().Status);
            CoreTests.Equal("not-accepted", result.Report.AcceptanceStatus);
            CoreTests.True(result.Report.NextSteps.All(s => s.Kind is not ("run" or "check-stage")));
            CoreTests.True(recorded.SequenceEqual(File.ReadAllBytes(Path.Combine(f.RunDirectory, "run.json"))));
        });
        test("unselected stages coverage and requirements can change but shared source bytes remain current inputs", () =>
        {
            Fixture f = AddScenario(Create(work, "scenario-edits"));
            Save(f.ProjectPath, f.Project with {
                Scenarios = f.Project.Scenarios.Select(s => s.Id == "independent"
                    ? s with { Stages = [s.Stages.Single() with { TimeoutSeconds = 31 }] } : s).ToList(),
                CheckObligations = [new("independent-safety", "safety", "independent", ["independent-check"])]
            });
            Save(Path.Combine(f.Directory, "requirements.json"), f.Policy with {
                Requirements = f.Policy.Requirements.Select(r => r.Scenario == "independent"
                    ? r with { Expected = "rejected", Diagnostic = "SYN001" } : r).ToList()
            });
            f = Reload(f);
            CoreTests.Equal("current", f.Evaluate().Report.CurrentApplicability);
            File.AppendAllText(Path.Combine(f.Directory, "Model.tla"), "\nChanged shared model bytes.\n");
            var result = f.Evaluate();
            CoreTests.Equal("scenario-equivalent", result.Report.PolicyReassessment!.Status);
            CoreTests.Equal("stale", result.Report.CurrentApplicability);
            CoreTests.True(result.Report.Diagnostics.Any(d => d.Is(ErrorCode.WorkflowEvidenceStale)));
        });
        test("archived unselected scenarios can change disappear or reorder without changing selected evidence", () =>
        {
            foreach (string kind in new[] { "edit", "remove", "reorder" })
            {
                Fixture f = Create(work, "scenario-archived-" + kind, additionalScenario: true);
                Save(f.ProjectPath, f.Project with {
                    Scenarios = kind switch {
                        "remove" => f.Project.Scenarios.Where(s => s.Id == "scope").ToList(),
                        "reorder" => f.Project.Scenarios.AsEnumerable().Reverse().ToList(),
                        _ => f.Project.Scenarios.Select(s => s.Id == "independent"
                            ? s with { Stages = [s.Stages.Single() with { TimeoutSeconds = 31 }] } : s).ToList()
                    }
                });
                Save(Path.Combine(f.Directory, "requirements.json"), f.Policy with {
                    Requirements = kind switch {
                        "remove" => f.Policy.Requirements.Where(r => r.Scenario == "scope").ToList(),
                        "reorder" => f.Policy.Requirements.AsEnumerable().Reverse().ToList(),
                        _ => f.Policy.Requirements.Select(r => r.Scenario == "independent"
                            ? r with { Expected = "rejected", Diagnostic = "SYN001" } : r).ToList()
                    }
                });
                var result = Reload(f).Evaluate();
                CoreTests.Equal("scenario-equivalent", result.Report.PolicyReassessment!.Status);
                CoreTests.Equal("consistent", result.Report.ArchiveStatus);
                CoreTests.Equal("current", result.Report.CurrentApplicability);
                CoreTests.Equal("not-accepted", result.Report.AcceptanceStatus);
            }
        });
        test("scenario isolation still rejects added selected requirements and shared execution changes", () =>
        {
            foreach (string kind in new[] { "requirement", "compiler", "tool", "store", "coverage", "stage" })
            {
                Fixture f = AddScenario(Create(work, "scenario-selected-" + kind));
                if (kind == "requirement")
                    Save(Path.Combine(f.Directory, "requirements.json"), f.Policy with {
                        Requirements = [.. f.Policy.Requirements,
                            new("extra-check", "scope", "model-inputs", "passed", Reviews: ["inspection"])]
                    });
                else
                    Save(f.ProjectPath, kind switch {
                        "compiler" => f.Project with { Compiler = f.Project.Compiler with { Defines = ["SYNTHETIC_DEFINE"] } },
                        "tool" => f.Project with { Tools = f.Project.Tools with { Jar = new("tool.txt", new string('A', 64)) } },
                        "store" => f.Project with { ResultStore = "new-store" },
                        "coverage" => f.Project with { CheckObligations = [new("scope-safety", "safety", "scope", ["scope-inputs"])] },
                        _ => f.Project with { Scenarios = f.Project.Scenarios.Select(s => s.Id == "scope"
                            ? s with { Stages = [s.Stages.Single() with { TimeoutSeconds = 31 }] } : s).ToList() }
                    });
                var result = Reload(f).Evaluate();
                CoreTests.Equal("incompatible", result.Report.PolicyReassessment!.Status);
                CoreTests.Equal("stale", result.Report.CurrentApplicability);
            }
        });
        test("review-only byte changes preserve execution applicability without satisfying human review", () =>
        {
            Fixture f = Create(work, "review-only");
            string review = Path.Combine(f.Directory, "review.md");
            File.AppendAllText(review, "\nAdditional reviewed assumptions.\n");
            var result = f.Evaluate();
            CoreTests.Equal("consistent", result.Report.ArchiveStatus);
            CoreTests.Equal("current", result.Report.CurrentApplicability);
            CoreTests.Equal("not-accepted", result.Report.AcceptanceStatus);
            CoreTests.Equal(1, result.ExitCode);
            CoreTests.Equal(review, result.Report.ChangedReviewInputs.Single());
            CoreTests.True(result.Report.Diagnostics.All(d => d.Is(ErrorCode.WorkflowRequirementUnsatisfied)));
            AcceptanceSummary summary = (AcceptanceSummary)CommandSummary.Create(result.Report,
                new CliOptions(["accept", "--project", f.ProjectPath, "--run", f.RunDirectory]), result.Worklist);
            CoreTests.Equal(review, summary.ChangedReviewInputs.Single());
            CoreTests.Equal(1, summary.Counts!["changed_review_inputs"]);
            CoreTests.True(summary.NextSteps.Any(s => s.Kind == "review"));
            CoreTests.True(summary.NextSteps.All(s => s.Kind is not ("run" or "check-stage")));
            CoreTests.True(summary.NextSteps.Single(s => s.Kind == "accept").Arguments.Contains(f.RunDirectory));
        });
        test("execution dependencies win when a declared review input is also consumed by the stage", () =>
        {
            Fixture f = Create(work, "dual-role", executedReview: true);
            File.AppendAllText(Path.Combine(f.Directory, "review.md"), "\nChanged execution bytes.\n");
            var result = f.Evaluate();
            CoreTests.Equal("stale", result.Report.CurrentApplicability);
            CoreTests.Equal(0, result.Report.ChangedReviewInputs.Count);
            CoreTests.True(result.Report.Diagnostics.Any(d => d.Is(ErrorCode.WorkflowEvidenceStale)));
            CoreTests.True(result.Report.NextSteps.Select(s => s.Kind).SequenceEqual(["doctor", "run"]));
            WorkflowNextStep rerun = result.Report.NextSteps.Single(s => s.Kind == "run");
            CoreTests.Equal("scope", new CliOptions(rerun.Arguments.ToArray()).Required("scenario"));
            CoreTests.True(!Directory.Exists(new CliOptions(rerun.Arguments.ToArray()).Required("output")));
        });
        test("review-only changes invalidate a recorded review without invalidating the native execution", () =>
        {
            Fixture f = Create(work, "review-decision");
            string directory = Directory.CreateDirectory(Path.Combine(f.Directory, "reviews", "inspection")).FullName;
            string review = Path.Combine(f.Directory, "review.md");
            Files.WriteNew(Path.Combine(directory, "decision.json"), new ReviewDecision(Schema.Version, "review-decision",
                "inspection", Schema.Hash(f.Project.Obligations.Single()), "accepted", "synthetic-test-reviewer",
                "Unit test data, not a real approval.", DateTimeOffset.UtcNow,
                new(StringComparer.Ordinal) { ["review.md"] = Files.Hash(review) }, ["review.md"]));
            CoreTests.Equal("accepted", f.Evaluate().Report.AcceptanceStatus);
            File.AppendAllText(review, "\nChanged review premises.\n");
            var result = f.Evaluate();
            CoreTests.Equal("current", result.Report.CurrentApplicability);
            CoreTests.Equal("not-accepted", result.Report.AcceptanceStatus);
            CoreTests.Equal("stale", result.Report.Reviews.Single().Status);
        });
        test("model and unclassified input changes still invalidate execution applicability", () =>
        {
            foreach (string input in new[] { "Model.tla", "unclassified.txt" })
            {
                Fixture f = Create(work, "changed-" + Path.GetFileNameWithoutExtension(input));
                File.AppendAllText(Path.Combine(f.Directory, input), "\nChanged bytes.\n");
                CoreTests.Equal("stale", f.Evaluate().Report.CurrentApplicability);
            }
        });
        test("unknown stage closures do not exempt review inputs from current-byte checks", () =>
        {
            Fixture f = Create(work, "unknown", emptyClosure: true);
            File.AppendAllText(Path.Combine(f.Directory, "review.md"), "\nChanged bytes.\n");
            CoreTests.Equal("stale", f.Evaluate().Report.CurrentApplicability);
        });
        test("review input archives remain byte-checked independently of applicability", () =>
        {
            Fixture f = Create(work, "archive");
            string review = Path.Combine(f.Directory, "review.md");
            File.AppendAllText(Path.Combine(f.RunDirectory, "inputs", f.Run.Fingerprints[review]), "tampered");
            File.AppendAllText(review, "\nChanged review context.\n");
            var result = f.Evaluate();
            CoreTests.Equal("inconsistent", result.Report.ArchiveStatus);
            CoreTests.Equal("stale", result.Report.CurrentApplicability);
            CoreTests.Equal(0, result.Report.ChangedReviewInputs.Count);
        });
        test("review-only removal is explicit and cannot silently approve its review", () =>
        {
            Fixture f = Create(work, "missing-review");
            string review = Path.Combine(f.Directory, "review.md");
            File.Delete(review);
            var result = f.Evaluate();
            CoreTests.Equal("current", result.Report.CurrentApplicability);
            CoreTests.Equal(review, result.Report.ChangedReviewInputs.Single());
            CoreTests.Equal("not-accepted", result.Report.AcceptanceStatus);
        });
        test("review policy reassessment keeps execution current and requires the replacement decision", () =>
        {
            Fixture original = Create(work, "policy-replacement", multipleReviews: true);
            Decision(original, "inspection");
            Decision(original, "second-inspection");
            CoreTests.Equal("accepted", original.Evaluate().Report.AcceptanceStatus);
            byte[] runBytes = File.ReadAllBytes(Path.Combine(original.RunDirectory, "run.json"));
            Fixture current = ReplaceReview(original);
            var result = current.Evaluate();
            CoreTests.Equal("consistent", result.Report.ArchiveStatus);
            CoreTests.Equal("current", result.Report.CurrentApplicability);
            CoreTests.Equal("review-only", result.Report.PolicyReassessment!.Status);
            CoreTests.True(result.Report.PolicyReassessment.ExecutionDefinitionUnchanged == true);
            CoreTests.True(result.Report.PolicyReassessment.CheckerExpectationsUnchanged == true);
            CoreTests.Equal("pending", result.Report.Reviews.Single().Status);
            CoreTests.Equal("adequacy", result.Report.Reviews.Single().Obligation);
            CoreTests.Equal(1, result.ExitCode);
            CoreTests.True(result.Report.Diagnostics.All(d => d.Is(ErrorCode.WorkflowRequirementUnsatisfied)));
            CoreTests.True(runBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(current.RunDirectory, "run.json"))));
            AcceptanceSummary summary = (AcceptanceSummary)CommandSummary.Create(result.Report,
                new CliOptions(["accept", "--project", current.ProjectPath, "--run", current.RunDirectory]), result.Worklist);
            CoreTests.Equal("review-only", summary.PolicyReassessment!.Status);
            CoreTests.True(summary.PolicyReassessment.Recorded.RequirementsSha256 !=
                summary.PolicyReassessment.Current.RequirementsSha256);
            Decision(current, "adequacy");
            CoreTests.Equal("accepted", current.Evaluate().Report.AcceptanceStatus);
            CoreTests.True(runBytes.SequenceEqual(File.ReadAllBytes(Path.Combine(current.RunDirectory, "run.json"))));
        });
        test("review input-set edits and added context are reassessed without weakening execution inputs", () =>
        {
            Fixture f = Create(work, "policy-input-set");
            File.Copy(Path.Combine(f.Directory, "review.md"), Path.Combine(f.Directory, "new-review.md"));
            Save(Path.Combine(f.Directory, "review-inputs.json"), new ProjectInputSets(Schema.Version,
                "project-input-sets", [new("context", ["new-review.md"])]));
            Save(f.ProjectPath, new ProjectAuthoring(Schema.Version, "analysis-project", f.Project.Id,
                f.Project.Tools, f.Project.Compiler, "requirements.json",
                [new("scope", Stages: [new("model-inputs", "inputs", TimeoutSeconds: 30, Models: ["Model.tla"])])],
                "reviews", Obligations: [new("inspection", "Changed review input set.", [], InputSets: ["context"])],
                InputSets: ["review-inputs.json"], AnnotationVersion: 4));
            File.Delete(Path.Combine(f.Directory, "review.md"));
            var result = Reload(f).Evaluate();
            CoreTests.Equal("review-only", result.Report.PolicyReassessment!.Status);
            CoreTests.Equal("current", result.Report.CurrentApplicability);
            CoreTests.Equal("pending", result.Report.Reviews.Single().Status);
            CoreTests.True(result.Report.ChangedReviewInputs.Contains(Path.Combine(f.Directory, "review.md")));
        });
        test("component review consolidation reconstructs archived authoring instead of trusting edited snapshots", () =>
        {
            Fixture f = Create(work, "component-policy");
            string component = Path.Combine(f.Directory, "component.json");
            Save(component, new ProjectComponent(Schema.Version, "project-component", "layer",
                [new("model-inputs", "inputs", TimeoutSeconds: 30, Models: ["Model.tla"])],
                [new("inspection", "Synthetic review context.", [], Inputs: ["review.md"])]));
            ProjectAuthoring root = new(Schema.Version, "analysis-project", f.Project.Id, f.Project.Tools,
                f.Project.Compiler, "requirements.json", [new("scope", Components: ["layer"])], "reviews",
                Components: ["component.json"], AnnotationVersion: 4);
            Save(f.ProjectPath, root);
            f = Reload(f);
            foreach (string input in new[] { f.ProjectPath, component })
            {
                string hash = Files.Hash(input);
                f.Run.Fingerprints[input] = hash;
                File.Copy(input, Path.Combine(f.RunDirectory, "inputs", hash), overwrite: true);
            }
            WorkflowRun composed = new() {
                Project = f.Run.Project, ProjectDirectory = f.Run.ProjectDirectory, Scenario = f.Run.Scenario,
                ProjectSha256 = Files.Hash(f.ProjectPath), RequirementsSha256 = f.Run.RequirementsSha256,
                ExecutionStatus = f.Run.ExecutionStatus, Stages = f.Run.Stages, Fingerprints = f.Run.Fingerprints,
                Artifacts = new(f.Run.Artifacts, StringComparer.Ordinal)
            };
            string expansion = Path.Combine(f.RunDirectory, "expanded-project.json");
            Save(expansion, ProjectComposition.Expansion(f.ProjectPath, f.Project));
            composed.Artifacts["expanded-project.json"] = Files.Hash(expansion);
            Save(Path.Combine(f.RunDirectory, "run.json"), composed);
            CoreTests.Equal("current", f.Evaluate().Report.CurrentApplicability);
            ProjectComponent layer = ProjectInputs.Read<ProjectComponent>(component);
            Save(component, layer with { Obligations = [] });
            Save(f.ProjectPath, root with { Obligations =
                [new("adequacy", "Consolidated synthetic review.", [], Inputs: ["review.md"])] });
            Save(Path.Combine(f.Directory, "requirements.json"), f.Policy with {
                Requirements = [f.Policy.Requirements.Single() with { Reviews = ["adequacy"] }]
            });
            var result = Reload(f).Evaluate();
            CoreTests.Equal("review-only", result.Report.PolicyReassessment!.Status);
            CoreTests.Equal("current", result.Report.CurrentApplicability);
            CoreTests.Equal("pending", result.Report.Reviews.Single().Status);
            File.AppendAllText(expansion, "damaged");
            result = Reload(f).Evaluate();
            CoreTests.Equal("inconsistent", result.Report.ArchiveStatus);
            CoreTests.Equal(0, result.Report.ChangedReviewInputs.Count);
        });
        test("execution configuration compiler tools and check coverage are never review-only policy edits", () =>
        {
            foreach (string kind in new[] { "deadline", "compiler", "tool", "coverage", "model" })
            {
                Fixture f = Create(work, "execution-policy-" + kind);
                AnalysisProject changed = kind switch {
                    "deadline" => f.Project with { Scenarios = [new("scope",
                        [f.Project.Scenarios.Single().Stages.Single() with { TimeoutSeconds = 31 }])] },
                    "compiler" => f.Project with { Compiler = f.Project.Compiler with { Defines = ["SYNTHETIC_DEFINE"] } },
                    "tool" => f.Project with { Tools = f.Project.Tools with { Jar = new("tool.txt", new string('A', 64)) } },
                    "coverage" => f.Project with { CheckObligations = [new("new-check", "safety", "scope", ["scope-inputs"])] },
                    _ => f.Project with { Scenarios = [new("scope",
                        [f.Project.Scenarios.Single().Stages.Single() with { Models = ["review.md"] }])] }
                };
                Save(f.ProjectPath, changed);
                var result = Reload(f).Evaluate();
                CoreTests.Equal("incompatible", result.Report.PolicyReassessment!.Status);
                CoreTests.Equal("stale", result.Report.CurrentApplicability);
                CoreTests.True(result.Report.Diagnostics.Any(d => d.Is(ErrorCode.WorkflowProjectChanged)));
            }
        });
        test("every non-review checker expectation remains part of the compatibility comparison", () =>
        {
            Fixture f = Create(work, "native-expectations");
            AnalysisRequirement r = f.Policy.Requirements.Single();
            AnalysisRequirement[] changes = [
                r with { Expected = "rejected", Diagnostic = "SYN001" },
                r with { ConfigSha256 = new string('A', 64) },
                r with { Claim = "different-native-claim" },
                r with { Diagnostic = "SYN001" },
                r with { DiagnosticText = "different" },
                r with { DiagnosticCount = 2 },
                r with { Bindings = ["synthetic::different"] },
                r with { Cases = ["synthetic::case/different"] },
                r with { Tests = ["different-test"] },
                r with { FingerprintFunctions = 2 }
            ];
            foreach (AnalysisRequirement changed in changes)
            {
                AcceptancePolicy current = f.Policy with { Requirements = [changed] };
                Save(Path.Combine(f.Directory, "requirements.json"), current);
                WorkflowPolicyComparison.Result comparison = WorkflowPolicyComparison.Compare(f.ProjectPath,
                    f.Project, current, f.Run, f.RunDirectory, f.Project);
                CoreTests.Equal("incompatible", comparison.Report.Status);
                CoreTests.True(comparison.Report.CheckerExpectationsUnchanged == false);
            }
        });
        test("unknown authoring fields cannot enter automatic policy reassessment", () =>
        {
            foreach (string kind in new[] { "project", "requirements" })
            {
                Fixture f = Create(work, "unknown-policy-field-" + kind);
                string path = kind == "project" ? f.ProjectPath : Path.Combine(f.Directory, "requirements.json");
                string content = File.ReadAllText(path);
                File.WriteAllText(path, content.Insert(content.IndexOf('{') + 1, "\"unknown_execution_setting\":true,"));
                if (kind == "project") CoreTests.ThrowsAny<InputException>(() => Reload(f));
                else CoreTests.ThrowsAny<JsonException>(() => Reload(f));
            }
        });
        test("authoring changes after loading cannot masquerade as an unchanged policy snapshot", () =>
        {
            Fixture f = Create(work, "changed-after-loading-project");
            Save(f.ProjectPath, f.Project with {
                Compiler = f.Project.Compiler with { Defines = ["LATER_EXECUTION_CHANGE"] }
            });
            var result = f.Evaluate();
            CoreTests.Equal("stale", result.Report.CurrentApplicability);
            CoreTests.True(result.Report.Diagnostics.Any(d => d.Is(ErrorCode.WorkflowEvidenceStale)));
            f = Create(work, "changed-after-loading-policy");
            Save(Path.Combine(f.Directory, "requirements.json"), f.Policy with {
                Requirements = [f.Policy.Requirements.Single() with { Expected = "rejected", Diagnostic = "SYN001" }]
            });
            result = f.Evaluate();
            CoreTests.Equal("stale", result.Report.CurrentApplicability);
            CoreTests.True(result.Report.Diagnostics.Any(d => d.Is(ErrorCode.WorkflowEvidenceStale)));
        });
        test("a stage-consumed project remains an execution dependency even during policy reassessment", () =>
        {
            foreach (string kind in new[] { "review", "scenario" })
            {
                Fixture f = Create(work, "project-dual-role-" + kind);
                string resultPath = Path.Combine(f.RunDirectory, "model-inputs", "result.json");
                ModelInputsResult result = ProjectInputs.Read<ModelInputsResult>(resultPath);
                result.Fingerprints.Add(f.ProjectPath, f.Run.ProjectSha256);
                Save(resultPath, result);
                string hash = Files.Hash(resultPath);
                f.Run.Artifacts[Path.Combine("model-inputs", "result.json")] = hash;
                f.Run.Stages[0] = f.Run.Stages[0] with { Sha256 = hash };
                Save(Path.Combine(f.RunDirectory, "run.json"), f.Run);
                var evaluation = (kind == "review" ? ReplaceReview(f) : AddScenario(f)).Evaluate();
                CoreTests.Equal(kind == "review" ? "review-only" : "scenario-equivalent",
                    evaluation.Report.PolicyReassessment!.Status);
                CoreTests.Equal("stale", evaluation.Report.CurrentApplicability);
                CoreTests.True(evaluation.Report.Diagnostics.Any(d => d.Is(ErrorCode.WorkflowEvidenceStale)));
            }
        });
        test("missing policy snapshots unknown closures and damaged policy archives fail closed", () =>
        {
            foreach (string kind in new[] { "snapshot", "closure", "policy" })
            {
                Fixture f = Create(work, "policy-fail-closed-" + kind, emptyClosure: kind == "closure");
                if (kind == "snapshot") File.Delete(Path.Combine(f.RunDirectory, "expanded-project.json"));
                if (kind == "policy")
                    File.AppendAllText(Path.Combine(f.RunDirectory, "inputs", f.Run.RequirementsSha256), "damaged");
                var result = ReplaceReview(f).Evaluate();
                CoreTests.Equal("not-accepted", result.Report.AcceptanceStatus);
                CoreTests.Equal("stale", result.Report.CurrentApplicability);
                CoreTests.True(result.Report.Diagnostics.Any(d => d.Is(
                    ErrorCode.WorkflowProvenanceInconsistent, ErrorCode.WorkflowEvidenceStale)));
            }
        });
        test("agent decisions cannot satisfy a human replacement policy", () =>
        {
            Fixture f = ReplaceReview(Create(work, "policy-origin"));
            Decision(f, "adequacy", ReviewOrigin.Agent);
            var result = f.Evaluate();
            CoreTests.Equal("origin-mismatch", result.Report.Reviews.Single().Status);
            CoreTests.Equal("current", result.Report.CurrentApplicability);
            CoreTests.Equal("not-accepted", result.Report.AcceptanceStatus);
        });
        test("project reports preserve reassessment and pending reviews without replaying the run", () =>
        {
            Fixture f = ReplaceReview(Create(work, "policy-report"));
            string output = Path.Combine(f.Directory, "report.md");
            byte[] run = File.ReadAllBytes(Path.Combine(f.RunDirectory, "run.json"));
            CoreTests.Equal(0, TlaLink.Program.Main(["report", "--project", f.ProjectPath, "--run", f.RunDirectory,
                "--output", output, "--format", "summary"]));
            AcceptanceResult acceptance = ProjectInputs.Read<AcceptanceResult>(output + ".run-1.acceptance.json");
            CoreTests.Equal("review-only", acceptance.PolicyReassessment!.Status);
            CoreTests.Equal("current", acceptance.CurrentApplicability);
            CoreTests.Equal("pending", acceptance.Reviews.Single().Status);
            CoreTests.True(File.ReadAllText(output).Contains("policy reassessment: review-only", StringComparison.Ordinal));
            CoreTests.True(File.ReadAllText(output).Contains("## Campaign comparison", StringComparison.Ordinal));
            EvidenceReportManifest manifest = ProjectInputs.Read<EvidenceReportManifest>(output + ".json");
            ReportCampaignStage stage = manifest.Nodes.Single(n => n.Stage != null).Stage!;
            CoreTests.Equal("inputs", stage.Backend);
            CoreTests.Equal("passed", stage.Requirements.Single().Expected);
            CoreTests.Equal("unsatisfied", stage.Requirements.Single().Status);
            CoreTests.Equal("pending", stage.Reviews.Single().Status);
            CoreTests.Equal("current", stage.CurrentApplicability);
            CoreTests.Equal("not-accepted", stage.AcceptanceStatus);
            CoreTests.True(stage.NextSteps.All(s => s.Kind is not ("run" or "check-stage")));
            CoreTests.True(run.SequenceEqual(File.ReadAllBytes(Path.Combine(f.RunDirectory, "run.json"))));
        });
        test("rerun guidance fails closed on damaged archives and changed independent expectations", () =>
        {
            Fixture f = Create(work, "guidance-damaged");
            File.AppendAllText(Path.Combine(f.RunDirectory, "inputs", f.Run.RequirementsSha256), "damaged");
            CoreTests.True(f.Evaluate().Report.NextSteps.All(s => s.Kind == "investigate" && s.Arguments.Count == 0));
            f = Create(work, "guidance-expectations");
            Save(Path.Combine(f.Directory, "requirements.json"), f.Policy with {
                Requirements = [f.Policy.Requirements.Single() with { Expected = "rejected", Diagnostic = "SYN001" }]
            });
            CoreTests.True(Reload(f).Evaluate().Report.NextSteps.All(s => s.Kind == "investigate"));
        });
        test("current accepted runs do not create an endless reassessment work item", () =>
        {
            Fixture f = Create(work, "guidance-accepted");
            Decision(f, "inspection");
            var result = f.Evaluate();
            CoreTests.Equal("accepted", result.Report.AcceptanceStatus);
            CoreTests.Equal(0, result.Report.NextSteps.Count);
        });
        test("isolated TLC guidance requires a declared store and never reuses other backend evidence", () =>
        {
            Fixture f = Create(work, "guidance-tlc");
            AcceptanceResult report = f.Evaluate().Report;
            AnalysisStage tlc = new("search", "tlc", [], 120, Config: "Model.cfg", Models: ["Model.tla"]);
            AnalysisStage proof = new("proof", "dafny-verify", [], 120, Config: "proof.json");
            AnalysisScenario scenario = new("scope", [tlc, proof]);
            WorkflowRun run = new() {
                Project = f.Project.Id, ProjectDirectory = f.Directory, Scenario = "scope",
                ProjectSha256 = f.Run.ProjectSha256, RequirementsSha256 = f.Run.RequirementsSha256,
                Stages = [
                    new("search", "tlc", "incomplete", "incomplete", "", "", [], 120),
                    new("proof", "dafny-verify", "incomplete", "incomplete", "", "", [], 120)
                ]
            };
            List<WorkflowNextStep> guidance = WorkflowRerunGuidance.Create(f.ProjectPath,
                f.Project with { ResultStore = "store" }, scenario, run, report);
            WorkflowNextStep isolated = guidance.Single(s => s.Kind == "check-stage");
            CoreTests.Equal("search", isolated.Stage);
            CliOptions command = new(isolated.Arguments.ToArray());
            CoreTests.Equal("scope", command.Required("scenario"));
            CoreTests.Equal("search", command.Required("stage"));
            CoreTests.True(!command.Has("timeout") && !command.Has("fingerprint"));
            CoreTests.True(guidance.Any(s => s.Kind == "run"));
            string output = new CliOptions(guidance.Single(s => s.Kind == "run").Arguments.ToArray()).Required("output");
            CoreTests.Equal(output, new CliOptions(WorkflowRerunGuidance.Create(f.ProjectPath,
                f.Project with { ResultStore = "store" }, scenario, run, report)
                .Single(s => s.Kind == "run").Arguments.ToArray()).Required("output"));
            Directory.CreateDirectory(output);
            string fresh = new CliOptions(WorkflowRerunGuidance.Create(f.ProjectPath,
                f.Project with { ResultStore = "store" }, scenario, run, report)
                .Single(s => s.Kind == "run").Arguments.ToArray()).Required("output");
            CoreTests.True(fresh != output && !Directory.Exists(fresh));
            CoreTests.True(WorkflowRerunGuidance.Create(f.ProjectPath, f.Project, scenario, run, report)
                .All(s => s.Kind != "check-stage"));
            CoreTests.True(WorkflowRerunGuidance.Create(f.ProjectPath, f.Project with { ResultStore = "store" },
                scenario, run, report with { CurrentApplicability = "stale" }).All(s => s.Kind != "check-stage"));
        });
    }
}
