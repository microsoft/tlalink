using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class PairedCorrespondenceTests
{
    public static void Register(Action<string, Action> test, string work)
    {
        test("paired endpoints use the same qualified ID without a native declaration name", () =>
        {
            const string annotation = """@link {"bind":[{"id":"lamp::change","reason":"Authored endpoint."}]}""";
            BindAnnotation endpoint = (BindAnnotation)Markup.Parse(annotation, 0, annotation.Length).Single().Declaration;
            CoreTests.Equal("lamp::change", endpoint.Identity);
            string json = JsonSerializer.Serialize(endpoint, Markup.AnnotationJson);
            CoreTests.True(!json.Contains("\"target\"", StringComparison.Ordinal));
        });
        test("paired CSharp declarations resolve independently before formal matching", () =>
        {
            string path = Path.Combine(work, "paired-source.cs");
            File.WriteAllText(path, """
                class Lamp {
                  bool enabled;
                  void Set() {
                    /* @link {"bind":[{"id":"lamp::change"}]} */
                    enabled = true;
                  }
                }
                """);
            CSharpScope source = CSharpScope.Parse([path]);
            CoreTests.True(source.Diagnostics.All(d => d.Severity != "error"));
            CoreTests.Equal("lamp::change", source.Bindings.Single().BindingId);
            CoreTests.Equal("lamp", source.Bindings.Single().Model);
        });
        test("paired endpoint authoring rejects the obsolete source-side target property", () =>
            CoreTests.ThrowsAny<InputException>(() => Markup.Parse(
                """@link {"bind":[{"id":"lamp::change","target":"lamp::Set"}]}""", 0, 0)));
        test("paired endpoint IDs require an explicit correspondence scope", () =>
            CoreTests.ThrowsAny<InputException>(() => Markup.Parse(
                """@link {"bind":[{"id":"change"}]}""", 0, 0)));
    }

    public static void RegisterNative(Action<string, Action> test, string root, string work)
    {
        test("TLA state endpoints attach to SANY variable declarations without an observation wrapper", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "paired-variable")).FullName;
            string source = Path.Combine(directory, "State.cs"), model = Path.Combine(directory, "State.tla");
            File.WriteAllText(source, """
                class State {
                  /* @link {"bind":[{"id":"state::value"}]} */
                  bool enabled;
                }
                """);
            File.WriteAllText(model, """
                ---- MODULE State ----
                VARIABLE
                  (* @link {"bind":[{"id":"state::value"}]} *)
                  differentlyNamed
                Init == differentlyNamed = FALSE
                Next == differentlyNamed' = ~differentlyNamed
                ====
                """);
            string scope = Path.Combine(directory, "scope.json");
            Files.WriteNew(scope, new CorrespondenceConfiguration(Schema.Version, "correspondence-scope", Markup.Version,
                [new("state", "tla", [model], RequiredBindings: ["state::value"])], []));
            var response = TestCli.Run(["inspect", "--annotation-version", "4", "--source", source,
                "--scope", scope, "--target", "state", "--jar", TestTools.Jar(root)]);
            if (response.Code != 0) throw new InvalidOperationException(response.Json);
            using JsonDocument packet = JsonDocument.Parse(response.Json);
            JsonElement endpoint = packet.RootElement.GetProperty("links")[0].GetProperty("specification");
            CoreTests.Equal("variable", endpoint.GetProperty("kind").GetString());
            CoreTests.Equal("differentlyNamed", endpoint.GetProperty("name").GetString());
        });
        test("TLA endpoints match shared IDs rather than operator spelling and reject either orphan", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "paired-tla")).FullName;
            string source = Path.Combine(directory, "Lamp.cs"), model = Path.Combine(directory, "Lamp.tla");
            File.WriteAllText(source, """
                class Lamp {
                  bool enabled;
                  void Set() {
                    /* @link {"bind":[{"id":"lamp::change"}]} */
                    enabled = true;
                  }
                }
                """);
            const string text = """
                ---- MODULE Lamp ----
                VARIABLE enabled
                Init == enabled = FALSE
                (* @link {"bind":[{"id":"lamp::change"}]} *)
                DeliberatelyDifferentName == enabled' = TRUE
                Next == DeliberatelyDifferentName
                ====
                """;
            File.WriteAllText(model, text);
            string scope = Path.Combine(directory, "scope.json");
            Files.WriteNew(scope, new CorrespondenceConfiguration(Schema.Version, "correspondence-scope", Markup.Version,
                [new("lamp", "tla", [model], RequiredBindings: ["lamp::change"])], []));
            (int Code, string Json) Inspect() => TestCli.Run(["inspect", "--annotation-version", Markup.Version.ToString(),
                "--source", source, "--scope", scope, "--target", "lamp", "--jar", TestTools.Jar(root)]);
            var good = Inspect();
            if (good.Code != 0) throw new InvalidOperationException(good.Json);
            using (JsonDocument packet = JsonDocument.Parse(good.Json))
                CoreTests.Equal("DeliberatelyDifferentName", packet.RootElement.GetProperty("links")[0]
                    .GetProperty("specification").GetProperty("name").GetString());
            File.WriteAllText(model, text.Replace("\"lamp::change\"", "\"lamp::orphan\"", StringComparison.Ordinal));
            var bad = Inspect();
            CoreTests.True(bad.Code != 0);
            CoreTests.True(bad.Json.Contains("lamp::change", StringComparison.Ordinal) &&
                bad.Json.Contains("lamp::orphan", StringComparison.Ordinal));
        });
    }
}
