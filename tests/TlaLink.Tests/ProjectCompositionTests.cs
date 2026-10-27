using System.Text.Json;
using System.Text.Json.Nodes;
using TlaLink;

namespace TlaLink.Tests;

public static class ProjectCompositionTests
{
    public static void RegisterCore(Action<string, Action> test, string work)
    {
        test("root annotation version is required and components cannot override it", () =>
        {
            Fixture f = new(work);
            string original = File.ReadAllText(f.Path);
            JsonObject root = JsonNode.Parse(original)!.AsObject();
            root.Remove("annotation_version");
            Save(f.Path, root);
            CoreTests.ThrowsAny<InputException>(() => ProjectComposition.Read(f.Path));
            root["annotation_version"] = 2;
            Save(f.Path, root);
            CoreTests.ThrowsAny<InputException>(() => ProjectComposition.Read(f.Path));
            File.WriteAllText(f.Path, original);
            CoreTests.Equal(3, ProjectComposition.Read(f.Path).AnnotationVersion);
            JsonObject component = JsonNode.Parse(File.ReadAllText(f.ComponentPath))!.AsObject();
            component["annotation_version"] = 2;
            Save(f.ComponentPath, component);
            CoreTests.ThrowsAny<InputException>(() => ProjectComposition.Read(f.Path));
        });
        test("component expansion resolves declaring-file paths and shares exact input sets and execution budgets", () => {
            Fixture f = new(work);
            ProjectExpansion expanded = ProjectComposition.Expand(f.Path);
            AnalysisStage stage = expanded.ExpandedProject.Scenarios.Single().Stages.Single();
            CoreTests.Equal(30, stage.TimeoutSeconds);
            CoreTests.Equal(new TlcResources(1, 512), stage.Resources);
            CoreTests.Equal(f.Model, ProjectInputs.Resolve(f.Path, stage.Models!.Single()));
            CoreTests.Equal(f.Source, ProjectInputs.Resolve(f.Path, expanded.ExpandedProject.Obligations.Single().Inputs.Single()));
            CoreTests.Equal(4, expanded.Fingerprints.Count);
            CoreTests.True(expanded.Fingerprints.Keys.All(FilePaths.IsAbsolute));
            var cli = TestCli.Run(["expand", "--project", f.Path]);
            CoreTests.Equal(0, cli.Code);
            CoreTests.True(cli.Json.Contains("project-expansion", StringComparison.Ordinal));
            string schema = System.IO.Path.Combine(f.Root, "project.schema.json");
            CoreTests.Equal(0, TestCli.Run(["project-schema", "--output", schema]).Code);
            JsonObject editor = JsonNode.Parse(File.ReadAllText(schema))!.AsObject();
            CoreTests.Equal(5, editor["oneOf"]!.AsArray().Count);
            CoreTests.Equal("project-component", editor["$defs"]!["component"]!["properties"]!["kind"]!["const"]!.GetValue<string>());
            void References(JsonNode node)
            {
                if (node is JsonObject obj)
                {
                    if (obj["$ref"] is JsonValue reference)
                    {
                        string pointer = reference.GetValue<string>();
                        CoreTests.True(pointer.StartsWith("#/", StringComparison.Ordinal));
                        JsonNode? target = editor;
                        foreach (string segment in pointer[2..].Split('/'))
                            target = target?[segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)];
                        CoreTests.True(target != null);
                    }
                    foreach (var property in obj)
                        if (property.Value != null) References(property.Value);
                }
                else if (node is JsonArray array)
                    foreach (JsonNode? item in array)
                        if (item != null) References(item);
            }
            References(editor);
        });
        test("prepared source ledgers expand explicit review inputs without directory discovery and remain archivable", () =>
        {
            Fixture f = new(work);
            string recipe = System.IO.Path.Combine(f.Root, "prepare.json"), output = System.IO.Path.Combine(f.Root, "prepared");
            Files.WriteNew(recipe, new SourcePreparationManifest(Schema.Version, "source-preparation", Markup.Version,
                new("net10.0", "14", "enable", false, true, []),
                [new(f.Source, Files.Hash(f.Source), System.IO.Path.Combine("scope", "Counter.cs"))]));
            SourcePreparation.Prepare(recipe, output, 30);
            string ledger = System.IO.Path.Combine(output, "prepared-sources.json");
            Save(f.SetsPath, new ProjectInputSets(Schema.Version, "project-input-sets",
                [new("code", [System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(f.SetsPath)!, f.Source)],
                    [System.IO.Path.GetRelativePath(System.IO.Path.GetDirectoryName(f.SetsPath)!, ledger)])]));
            ProjectExpansion expanded = ProjectComposition.Expand(f.Path);
            List<string> inputs = expanded.ExpandedProject.Obligations.Single().Inputs
                .Select(p => ProjectInputs.Resolve(f.Path, p)).ToList();
            CoreTests.Equal(3, inputs.Count);
            CoreTests.True(inputs.Contains(ledger));
            CoreTests.True(inputs.Contains(System.IO.Path.Combine(output, "scope", "Counter.cs")));
            CoreTests.True(expanded.Fingerprints.ContainsKey(ledger));
            Dictionary<string, ProjectComposition.Source> archived = expanded.Fingerprints
                .ToDictionary(p => p.Key, p => ProjectComposition.ReadSource(p.Key), FilePaths.Comparer);
            File.Delete(ledger);
            AnalysisProject historical = ProjectComposition.Read(f.Path, p => archived[p]);
            CoreTests.Equal(Schema.Hash(expanded.ExpandedProject), Schema.Hash(historical));
            CoreTests.ThrowsAny<InputException>(() => ProjectComposition.Read(f.Path));
        });
        test("nested component imports require explicit child selection and retain project-wide review scopes", () => {
            Fixture f = new(work);
            JsonObject component = JsonNode.Parse(File.ReadAllText(f.ComponentPath))!.AsObject();
            component["components"] = new JsonArray("child.json");
            Save(f.ComponentPath, component);
            string child = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.ComponentPath)!, "child.json");
            Save(child, new ProjectComponent(Schema.Version, "project-component", "child",
                [new("child-safety", "tlc", Models: ["Counter.tla"], Config: "Counter.cfg", Profile: "small")],
                [new("child-review", "Review the child separately.", [], InputSets: ["code"])]));
            ProjectExpansion parent = ProjectComposition.Expand(f.Path);
            CoreTests.Equal(1, parent.ExpandedProject.Scenarios.Single().Stages.Count);
            CoreTests.Equal(2, parent.ExpandedProject.Obligations.Count);
            CoreTests.Equal(5, parent.Fingerprints.Count);
            JsonObject root = JsonNode.Parse(File.ReadAllText(f.Path))!.AsObject();
            root["scenarios"]![0]!["components"]!.AsArray().Add("child");
            Save(f.Path, root);
            CoreTests.Equal(2, ProjectComposition.Expand(f.Path).ExpandedProject.Scenarios.Single().Stages.Count);
        });
        test("component expansion is relocatable and archived readers need no current authoring files", () => {
            Fixture f = new(work);
            ProjectExpansion before = ProjectComposition.Expand(f.Path);
            Dictionary<string, ProjectComposition.Source> archived = before.Fingerprints.ToDictionary(
                p => p.Key, p => ProjectComposition.ReadSource(p.Key), FilePaths.Comparer);
            string relocated = f.Root + "-moved";
            Directory.Move(f.Root, relocated);
            AnalysisProject moved = ProjectComposition.Read(System.IO.Path.Combine(relocated, "project.json"));
            CoreTests.Equal(Schema.Hash(before.ExpandedProject), Schema.Hash(moved));
            AnalysisProject original = ProjectComposition.Read(f.Path, p => archived[p]);
            CoreTests.Equal(Schema.Hash(before.ExpandedProject), Schema.Hash(original));
        });
        foreach (string defect in new[] { "unknown-field", "wrong-schema", "missing-component", "duplicate-import",
            "duplicate-component", "duplicate-stage", "cycle", "unknown-selection", "unknown-profile",
            "mixed-profile", "profile-kind", "unknown-input-set", "duplicate-input-set", "duplicate-profile" })
            test("component authoring rejects " + defect + " with an authored-file diagnostic", () => {
                Fixture f = new(work);
                JsonObject root = JsonNode.Parse(File.ReadAllText(f.Path))!.AsObject();
                JsonObject component = JsonNode.Parse(File.ReadAllText(f.ComponentPath))!.AsObject();
                JsonObject sets = JsonNode.Parse(File.ReadAllText(f.SetsPath))!.AsObject();
                JsonObject profiles = JsonNode.Parse(File.ReadAllText(f.ProfilesPath))!.AsObject();
                switch (defect)
                {
                    case "unknown-field": component["guess"] = true; break;
                    case "wrong-schema": component["schema_version"] = 3; break;
                    case "missing-component": root["components"] = new JsonArray("missing.json"); break;
                    case "duplicate-import": root["components"]!.AsArray().Add(root["components"]![0]!.GetValue<string>()); break;
                    case "duplicate-component":
                    case "duplicate-stage":
                        string second = System.IO.Path.Combine(f.Root, "second.json");
                        JsonObject copy = component.DeepClone().AsObject();
                        if (defect == "duplicate-stage") copy["id"] = "second";
                        Save(second, copy);
                        root["components"]!.AsArray().Add("second.json");
                        break;
                    case "cycle": component["components"] = new JsonArray("component.json"); break;
                    case "unknown-selection": root["scenarios"]![0]!["components"] = new JsonArray("absent"); break;
                    case "unknown-profile": component["stages"]![0]!["profile"] = "absent"; break;
                    case "mixed-profile": component["stages"]![0]!["timeout_seconds"] = 60; break;
                    case "profile-kind": profiles["profiles"]![0]!["kind"] = "trace"; break;
                    case "unknown-input-set": component["obligations"]![0]!["input_sets"] = new JsonArray("absent"); break;
                    case "duplicate-input-set": sets["sets"]!.AsArray().Add(sets["sets"]![0]!.DeepClone()); break;
                    case "duplicate-profile": profiles["profiles"]!.AsArray().Add(profiles["profiles"]![0]!.DeepClone()); break;
                }
                Save(f.Path, root);
                Save(f.ComponentPath, component);
                Save(f.SetsPath, sets);
                Save(f.ProfilesPath, profiles);
                var result = TestCli.Run(["expand", "--project", f.Path]);
                CoreTests.Equal(2, result.Code);
                CoreTests.True(result.Json.Contains("json", StringComparison.Ordinal));
            });
        test("retention review roots scan composed authoring files without requiring checker installations", () => {
            Fixture f = new(work);
            string store = System.IO.Path.Combine(f.Root, "store");
            Directory.CreateDirectory(store);
            string retention = System.IO.Path.Combine(f.Root, "retention.json");
            Save(retention, new { schema_version = Schema.Version, kind = "evidence-retention", pins = Array.Empty<object>(),
                review_projects = new[] { "project.json" } });
            var result = TestCli.Run(["gc", "--store", store, "--pins", retention]);
            CoreTests.Equal(0, result.Code);
            using JsonDocument json = JsonDocument.Parse(result.Json);
            CoreTests.True(json.RootElement.GetProperty("fingerprints").TryGetProperty(f.ComponentPath, out _));
        });
    }

    public static void RegisterNative(Action<string, Action> test, string root, string work)
    {
        test("composed native workflow saves and independently reconstructs its expanded project", () => {
            Fixture f = new(work, TestTools.Jar(root));
            string output = System.IO.Path.Combine(f.Root, "run");
            var execution = TestCli.Run(["run", "--project", f.Path, "--scenario", "complete", "--output", output]);
            if (execution.Code != 0) throw new InvalidOperationException(execution.Json);
            CoreTests.True(File.Exists(System.IO.Path.Combine(output, "expanded-project.json")));
            CoreTests.Equal(0, TestCli.Run(["accept", "--project", f.Path, "--run", output]).Code);
            string runPath = System.IO.Path.Combine(output, "run.json");
            string originalRun = File.ReadAllText(runPath);
            WorkflowRun missing = ProjectInputs.Read<WorkflowRun>(runPath);
            missing.Artifacts.Remove("expanded-project.json");
            Save(runPath, missing);
            var absent = TestCli.Run(["accept", "--project", f.Path, "--run", output]);
            CoreTests.True(absent.Code != 0 && absent.Json.Contains("missing its expanded", StringComparison.Ordinal));
            File.WriteAllText(runPath, originalRun);
            string original = File.ReadAllText(f.ComponentPath);
            File.AppendAllText(f.ComponentPath, "\n");
            var stale = TestCli.Run(["accept", "--project", f.Path, "--run", output]);
            CoreTests.Equal(2, stale.Code);
            CoreTests.True(stale.Json.Contains("composition differs", StringComparison.Ordinal));
            File.WriteAllText(f.ComponentPath, original);
            string snapshotPath = System.IO.Path.Combine(output, "expanded-project.json");
            JsonObject snapshot = JsonNode.Parse(File.ReadAllText(snapshotPath))!.AsObject();
            snapshot["expanded_project"]!["scenarios"]![0]!["stages"]![0]!["timeout_seconds"] = 31;
            Save(snapshotPath, snapshot);
            WorkflowRun run = ProjectInputs.Read<WorkflowRun>(runPath);
            run.Artifacts["expanded-project.json"] = Files.Hash(snapshotPath);
            Save(runPath, run);
            var corrupted = TestCli.Run(["accept", "--project", f.Path, "--run", output]);
            CoreTests.True(corrupted.Code != 0 && corrupted.Json.Contains("archived authoring", StringComparison.Ordinal));
        });
    }

    private static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, Files.Json));

    private sealed class Fixture
    {
        public string Root { get; }
        public string Path { get; }
        public string ComponentPath { get; }
        public string SetsPath { get; }
        public string ProfilesPath { get; }
        public string Model { get; }
        public string Source { get; }

        public Fixture(string work, string? jar = null)
        {
            Root = System.IO.Path.Combine(work, "composition-" + Guid.NewGuid().ToString("N"));
            string directory = System.IO.Path.Combine(Root, "src", "Counter", "verification");
            Directory.CreateDirectory(directory);
            Directory.CreateDirectory(System.IO.Path.Combine(Root, "verification"));
            Path = System.IO.Path.Combine(Root, "project.json");
            ComponentPath = System.IO.Path.Combine(directory, "component.json");
            SetsPath = System.IO.Path.Combine(Root, "verification", "inputs.json");
            ProfilesPath = System.IO.Path.Combine(Root, "verification", "profiles.json");
            Model = System.IO.Path.Combine(directory, "Counter.tla");
            Source = System.IO.Path.Combine(Root, "src", "Counter", "Counter.cs");
            File.WriteAllText(Source, "class Counter { public int Value; }");
            File.WriteAllText(Model, """
                ---- MODULE Counter ----
                EXTENDS Naturals
                VARIABLES value
                Init == value = 0
                Next == value' = (value + 1) % 3
                Safety == value \in 0..2
                ====
                """);
            string config = System.IO.Path.Combine(directory, "Counter.cfg");
            File.WriteAllText(config, "INIT Init\nNEXT Next\nINVARIANT Safety\n");
            Save(ComponentPath, new ProjectComponent(Schema.Version, "project-component", "counter",
                [new("counter-safety", "tlc", [], Models: ["Counter.tla"], Config: "Counter.cfg", Profile: "small")],
                [new("correspondence", "Synthetic counter correspondence requires review.", [], InputSets: ["code"])]));
            Save(SetsPath, new ProjectInputSets(Schema.Version, "project-input-sets",
                [new("code", [System.IO.Path.Combine("..", "src", "Counter", "Counter.cs")])]));
            Save(ProfilesPath, new ProjectExecutionProfiles(Schema.Version, "project-execution-profiles",
                [new("small", "tlc", 30, new(1, 512))]));
            Save(Path, new ProjectAuthoring(Schema.Version, "analysis-project", "counter-project",
                new(Jar: new(jar ?? "missing.jar", jar == null ? new('0', 64) : Files.Hash(jar))),
                new("net10.0", "14", "enable", false, true, []), "requirements.json",
                [new("complete", Components: ["counter"])], "verification/reviews",
                Components: [System.IO.Path.GetRelativePath(Root, ComponentPath)],
                InputSets: ["verification/inputs.json"], ExecutionProfiles: ["verification/profiles.json"],
                AnnotationVersion: Markup.Version));
            Save(System.IO.Path.Combine(Root, "requirements.json"), new AcceptancePolicy(Schema.Version,
                "acceptance-policy", "counter-project", [
                    new("counter-safe", "complete", "counter-safety", "passed", ConfigSha256: Files.Hash(config))
                ]));
        }
    }
}
