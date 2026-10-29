using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;

namespace TlaLink;

public static class ProjectEditorSchema
{
    public static int Run(CliOptions options)
    {
        string output = FilePaths.Full(options.Required("output"));
        Files.WriteNew(output, Create());
        ProjectWorkflow.Render(new ProjectSchemaResult(Schema.Version, "project-schema-result", "ready", output), options);
        return 0;
    }

    public static JsonObject Create()
    {
        JsonSerializerOptions options = new(ProjectInputs.Json) {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        JsonObject definitions = new();
        (string Name, string Kind, Type Type)[] documents = [
            ("project", "analysis-project", typeof(ProjectAuthoring)),
            ("component", "project-component", typeof(ProjectComponent)),
            ("inputSets", "project-input-sets", typeof(ProjectInputSets)),
            ("executionProfiles", "project-execution-profiles", typeof(ProjectExecutionProfiles)),
            ("correspondenceScope", "correspondence-scope", typeof(CorrespondenceConfiguration))
        ];
        foreach (var document in documents)
        {
            JsonNode schema = options.GetJsonSchemaAsNode(document.Type);
            Rewrite(schema, "#/$defs/" + document.Name);
            JsonObject properties = schema["properties"]!.AsObject();
            properties["schema_version"]!["const"] = Schema.Version;
            properties["kind"]!["const"] = document.Kind;
            if (document.Kind is "analysis-project" or "correspondence-scope")
            {
                properties["annotation_version"]!["const"] = Markup.Version;
                properties["annotation_version"]!.AsObject().Remove("default");
            }
            if (document.Kind == "correspondence-scope")
            {
                properties["targets"]!["minItems"] = 1;
                properties["targets"]!["maxItems"] = 1000;
                properties["omissions"]!["maxItems"] = 1000;
            }
            definitions[document.Name] = schema;
        }
        return new JsonObject {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "TlaLink project authoring",
            ["description"] = "Explicit component composition; paths are relative to their declaring document. Expansion is structural, not checker readiness or acceptance.",
            ["oneOf"] = new JsonArray(documents.Select(d => (JsonNode)new JsonObject {
                ["$ref"] = "#/$defs/" + d.Name
            }).ToArray()),
            ["$defs"] = definitions
        };
    }

    private static void Rewrite(JsonNode node, string prefix)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue reference && reference.TryGetValue(out string? value) && value.StartsWith('#'))
                obj["$ref"] = prefix + value[1..];
            if (obj["properties"] is JsonObject properties)
            {
                if (properties.ContainsKey("language_version") && properties.ContainsKey("target_framework"))
                    properties["language_version"]!["enum"] = new JsonArray("8", "14");
                if (properties.ContainsKey("language") && properties.ContainsKey("inputs") &&
                    properties.ContainsKey("required_bindings"))
                {
                    properties["language"]!["enum"] = new JsonArray("tla", "dafny", "p");
                    properties["inputs"]!["minItems"] = 1;
                    properties["inputs"]!["maxItems"] = 1000;
                    properties["inputs"]!["uniqueItems"] = true;
                    obj["allOf"] = new JsonArray(new JsonObject {
                        ["if"] = new JsonObject {
                            ["properties"] = new JsonObject { ["language"] = new JsonObject { ["const"] = "tla" } },
                            ["required"] = new JsonArray("language")
                        },
                        ["then"] = new JsonObject {
                            ["required"] = new JsonArray("required_bindings"),
                            ["properties"] = new JsonObject {
                                ["inputs"] = new JsonObject { ["maxItems"] = 1 },
                                ["required_bindings"] = new JsonObject {
                                    ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 1000, ["uniqueItems"] = true
                                }
                            }
                        },
                        ["else"] = new JsonObject {
                            ["properties"] = new JsonObject {
                                ["required_bindings"] = new JsonObject { ["type"] = "null" },
                                ["hidden_actions"] = new JsonObject { ["type"] = "null" }
                            }
                        }
                    });
                }
                if (properties["timeout_seconds"] is JsonObject timeout)
                {
                    timeout["minimum"] = 1;
                    timeout["maximum"] = 7200;
                }
                if (properties["method_summaries"] is JsonObject summaries)
                    summaries["maxItems"] = 1000;
                if (properties.ContainsKey("abstraction") && properties.ContainsKey("inspection"))
                {
                    properties["abstraction"]!["enum"] = new JsonArray("pure", "atomic");
                    properties["sources"]!["minItems"] = 1;
                    properties["sources"]!["maxItems"] = 1000;
                    properties["sources"]!["uniqueItems"] = true;
                    properties["limits"]!["minItems"] = 1;
                    properties["depends_on"]!["uniqueItems"] = true;
                }
                if (properties.ContainsKey("scope") && properties.ContainsKey("bindings") && !properties.ContainsKey("kind"))
                {
                    properties["bindings"]!["minItems"] = 1;
                    properties["bindings"]!["maxItems"] = 1000;
                    properties["bindings"]!["uniqueItems"] = true;
                }
                if (properties.ContainsKey("timeout_seconds") && properties["kind"] is JsonObject kind)
                    kind["enum"] = new JsonArray(ProjectInputs.StageKinds.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
                if (properties["workers"] is JsonObject workers)
                {
                    workers["minimum"] = 1;
                    workers["maximum"] = 64;
                }
                if (properties["max_heap_mib"] is JsonObject heap)
                {
                    heap["minimum"] = 128;
                    heap["maximum"] = 65536;
                }
                if (properties["id"] is JsonObject id)
                {
                    id["pattern"] = "^[A-Za-z0-9_.-]{1,100}$";
                    id["not"] = new JsonObject { ["enum"] = new JsonArray(".", "..") };
                }
                if (properties.ContainsKey("bindings") && properties.ContainsKey("profile"))
                {
                    obj["oneOf"] = new JsonArray(
                        new JsonObject {
                            ["required"] = new JsonArray("profile"),
                            ["properties"] = new JsonObject {
                                ["profile"] = new JsonObject { ["type"] = "string" },
                                ["timeout_seconds"] = new JsonObject { ["type"] = "null" },
                                ["resources"] = new JsonObject { ["type"] = "null" }
                            }
                        },
                        new JsonObject {
                            ["required"] = new JsonArray("timeout_seconds"),
                            ["properties"] = new JsonObject {
                                ["timeout_seconds"] = new JsonObject { ["type"] = "integer" },
                                ["profile"] = new JsonObject { ["type"] = "null" }
                            }
                        });
                }
            }
            foreach (var entry in obj.ToArray())
                if (entry.Value != null) Rewrite(entry.Value, prefix);
        }
        else if (node is JsonArray array)
            foreach (JsonNode? item in array)
                if (item != null) Rewrite(item, prefix);
    }
}
