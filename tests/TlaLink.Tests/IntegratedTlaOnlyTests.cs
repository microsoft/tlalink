using System.Text.Json;
using System.Collections.Immutable;
using TlaLink;

namespace TlaLink.Tests;

public static class IntegratedTlaOnlyTests
{
    public static void Register(Action<string, Action> test, string root, string work)
    {
        string jar = TestTools.Jar(root);
        string Fixture(string name)
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, name)).FullName;
            foreach (string file in new[] { "Counter.cs", "CounterModel.tla", "scope.json", "bindings.json" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "TlaOnlyInspection", file), Path.Combine(directory, file));
            return directory;
        }
        test("TLA-only integrated inspection resolves and archives attachments without a Dafny tool or stages", () =>
        {
            string directory = Fixture("tla-only");
            string output = Path.Combine(directory, "result");
            var response = TestCli.Run(["inspect-integrated", "--annotation-version", "4", "--bindings",
                Path.Combine(directory, "bindings.json"), "--jar", jar, "--output", output]);
            if (response.Code != 0) throw new InvalidOperationException(response.Json);
            string resultPath = Path.Combine(output, "result.json");
            IntegratedResult result = ProjectInputs.Read<IntegratedResult>(resultPath);
            CoreTests.Equal("ready", result.Status);
            CoreTests.Equal("not-run", result.ProofStatus);
            CoreTests.Equal("not-run", result.ConformanceStatus);
            CoreTests.Equal(0, result.Links.Length);
            CoreTests.Equal(0, result.Stages.Count);
            CoreTests.Equal("counter-only::increment", result.Tla!.Links.Single().Key);
            CoreTests.Equal(0, IntegratedEvidence.Validate(result, Path.Combine(output, "inputs")).Count);
            if (OperatingSystem.IsWindows())
            {
                var recased = result.Tla with {
                    Inventory = result.Tla.Inventory.Select(e => e with {
                        Location = e.Location with { Path = char.ToLowerInvariant(e.Location.Path[0]) + e.Location.Path[1..] }
                    }).ToImmutableArray()
                };
                CoreTests.Equal(0, IntegratedEvidence.Validate(result with { Tla = recased },
                    Path.Combine(output, "inputs")).Count);
            }
            CoreTests.Equal(0, TestCli.Run(["read-integrated", "--result", resultPath, "--current", "true"]).Code);
            CoreTests.True(IntegratedEvidence.Validate(result with {
                Tla = result.Tla with { Links = [] }
            }, Path.Combine(output, "inputs")).Count > 0);
            CoreTests.True(IntegratedEvidence.Validate(result with {
                Tla = result.Tla with { Inventory = [] }
            }, Path.Combine(output, "inputs")).Count > 0);
            Link original = result.Tla.Links.Single();
            CoreTests.True(IntegratedEvidence.Validate(result with {
                Tla = result.Tla with { Links = [original with {
                    Implementation = original.Implementation with {
                        Location = original.Implementation.Location with { Line = 1 }
                    }
                }] }
            }, Path.Combine(output, "inputs")).Count > 0);
            File.AppendAllText(Path.Combine(directory, "CounterModel.tla"), "\n");
            CoreTests.Equal(0, TestCli.Run(["read-integrated", "--result", resultPath]).Code);
            CoreTests.Equal(2, TestCli.Run(["read-integrated", "--result", resultPath, "--current", "true"]).Code);
        });
        test("TLA-only project inspection requires only the selected JAR and cannot become a proof stage", () =>
        {
            string directory = Fixture("tla-only-project");
            string path = Path.Combine(directory, "project.json");
            AnalysisProject project = new(Schema.Version, "analysis-project", "tla-only",
                new(Jar: new(jar, Files.Hash(jar))), new("net10.0", "14", "enable", false, true, []),
                "requirements.json", [new("scope", [new("links", "inspect", [], 30, Bindings: "bindings.json")])],
                [], "reviews");
            Files.WriteNew(path, project);
            Files.WriteNew(Path.Combine(directory, "requirements.json"),
                new AcceptancePolicy(Schema.Version, "acceptance-policy", project.Id,
                    [new("links", "scope", "links", "passed")]));
            string output = Path.Combine(directory, "run");
            var run = TestCli.Run(["run", "--project", path, "--scenario", "scope", "--output", output]);
            if (run.Code != 0) throw new InvalidOperationException(run.Json);
            CoreTests.Equal(0, TestCli.Run(["accept", "--project", path, "--run", output]).Code);
            foreach (string kind in new[] { "verify", "oracle" })
            {
                AnalysisProject invalid = project with {
                    Scenarios = [new("scope", [new("links", kind, [], 30, Bindings: "bindings.json")])]
                };
                File.WriteAllText(path, JsonSerializer.Serialize(invalid, Files.Json));
                CoreTests.Equal(2, TestCli.Run(["doctor", "--project", path, "--scenario", "scope"]).Code);
            }
        });
        test("TLA-only manifests reject proof and oracle commands rather than reporting vacuous success", () =>
        {
            string directory = Fixture("tla-only-commands");
            foreach (string command in new[] { "verify-integrated", "test-integrated" })
            {
                var response = TestCli.Run([command, "--annotation-version", "4", "--bindings",
                    Path.Combine(directory, "bindings.json"), "--jar", jar, "--output", Path.Combine(directory, command)]);
                CoreTests.Equal(2, response.Code);
                CoreTests.True(response.Json.Contains("require selected Dafny bindings", StringComparison.Ordinal));
            }
        });
    }
}
