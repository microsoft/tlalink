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
        ProjectWorkflow.Render(new { SchemaVersion = Schema.Version, Kind = "project-schema-result",
            Status = "ready", Path = output }, options);
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
            ("executionProfiles", "project-execution-profiles", typeof(ProjectExecutionProfiles))
        ];
        foreach (var document in documents)
        {
            JsonNode schema = options.GetJsonSchemaAsNode(document.Type);
            Rewrite(schema, "#/$defs/" + document.Name);
            JsonObject properties = schema["properties"]!.AsObject();
            properties["schema_version"]!["const"] = Schema.Version;
            properties["kind"]!["const"] = document.Kind;
            if (document.Kind == "analysis-project")
            {
                properties["annotation_version"]!["const"] = Markup.Version;
                properties["annotation_version"]!.AsObject().Remove("default");
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
                if (properties["timeout_seconds"] is JsonObject timeout)
                {
                    timeout["minimum"] = 1;
                    timeout["maximum"] = 7200;
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
