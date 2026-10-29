using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class LinkInspectionTests
{
    public static void Register(Action<string, Action> test, string work)
    {
        test("inspection inlines native and exact source syntax with lexical guards and observations", () =>
        {
            var fixture = Fixture(work, "context");
            LinkInspectionResult result = Read(fixture, "--binding", "sample::step", "--view", "full");
            CoreTests.Equal("ready", result.Status);
            InspectedEndpoint endpoint = result.Endpoints.Single();
            CoreTests.Equal("Step == value' = value + 1", endpoint.Model.Source);
            InspectedLink link = endpoint.Links.Single();
            CoreTests.Equal("value++;", link.Source!.SelectedText);
            CoreTests.Equal("gate", link.Source.Locks.Single());
            CoreTests.True(link.Source.Context.Any(c => c.Text == "then: enabled"));
            CoreTests.True(link.Source.Context.Any(c => c.Text == "try"));
            CoreTests.Equal(1, link.Source.Lines.Count(l => l.Selected));
            InspectionObservation observed = link.Observations.Single();
            CoreTests.Equal("int value;", observed.Expression);
            CoreTests.Equal("gate", observed.Guard);
            CoreTests.Equal(CapturedType.Int, observed.Type);
            string text = LinkInspection.Readable(result, false);
            CoreTests.True(text.Contains("after successful execution", StringComparison.Ordinal));
            CoreTests.True(text.Contains("declared guard: gate", StringComparison.Ordinal));
            CoreTests.True(!text.Contains("@link", StringComparison.Ordinal));
            CoreTests.True(result.Fingerprints!.ContainsKey(fixture.Source));
        });
        test("inspection distinguishes unlinked endpoints and declared hidden actions without inventing coverage", () =>
        {
            var fixture = Fixture(work, "inventory");
            LinkInspectionResult result = Read(fixture);
            CoreTests.Equal(4, result.Endpoints.Count);
            CoreTests.Equal("unlinked", result.Endpoints.Single(e => e.Model.Name == "Invariant").Status);
            InspectedEndpoint hidden = result.Endpoints.Single(e => e.Model.Name == "Internal");
            CoreTests.Equal("declared-hidden", hidden.Status);
            CoreTests.Equal("Authored environment step.", hidden.HiddenReason);
            CoreTests.Equal("ready", result.Status);
            CoreTests.True(result.Boundary.Contains("not automatically model-only", StringComparison.Ordinal));
        });
        test("inspection keeps ambiguous selectors visible and still presents other endpoints", () =>
        {
            var fixture = Fixture(work, "ambiguous");
            fixture.Native.Declarations[1] = fixture.Native.Declarations[1] with
            {
                Binding = new("sample::step", Source: new("M:InspectionSample.Step(System.Boolean)",
                    "ExpressionStatement", Match: 1, ExpectedMatches: 7))
            };
            LinkInspectionResult result = Read(fixture);
            CoreTests.Equal("failed", result.Status);
            CoreTests.True(result.Diagnostics.Any(d => d.Message.Contains("Expected 7", StringComparison.Ordinal)));
            InspectedLink step = result.Endpoints.Single(e => e.Model.Name == "Step").Links.Single();
            CoreTests.Equal("unresolved", step.Status);
            CoreTests.True(step.Source == null);
            CoreTests.True(step.Selection.StartsWith("positional", StringComparison.Ordinal));
            CoreTests.Equal("resolved", result.Endpoints.Single(e => e.Model.Name == "Value").Links.Single().Status);
        });
        test("inspection member context stays within the member and handles CRLF source positions", () =>
        {
            var fixture = Fixture(work, "member");
            LinkInspectionResult result = Read(fixture, "--binding", "sample::step", "--context", "member");
            InspectionSource source = result.Endpoints.Single().Links.Single().Source!;
            CoreTests.True(source.Lines.First().Text.Contains("void Step", StringComparison.Ordinal));
            CoreTests.True(source.Lines.All(l => !l.Text.Contains("int value;", StringComparison.Ordinal)));
            CoreTests.True(source.Lines.All(l => !l.Text.Contains('\r')));
            LinkInspectionResult exact = Read(fixture, "--binding", "sample::step", "--context", "0");
            CoreTests.Equal(1, exact.Endpoints.Single().Links.Single().Source!.Lines.Count);
        });
        test("inspection validates filters context bounds and markdown format scope", () =>
        {
            var fixture = Fixture(work, "options");
            foreach (string context in new[] { "-1", "21", "all" })
                CoreTests.ThrowsAny<InputException>(() => Read(fixture, "--context", context));
            CoreTests.ThrowsAny<InputException>(() => Read(fixture, "--binding", "sample::missing"));
            CoreTests.ThrowsAny<InputException>(() => Read(fixture, "--endpoint", "Missing"));
            CoreTests.ThrowsAny<InputException>(() => Read(fixture, "--binding", "sample::step", "--endpoint", "Step"));
            CoreTests.ThrowsAny<InputException>(() => new CliOptions(["links", "check", "--format", "markdown"]));
            CoreTests.ThrowsAny<InputException>(() => new CliOptions(["links", "list", "--context", "3"]));
            CoreTests.Equal("markdown", new CliOptions(["links", "inspect", "--format", "markdown"]).Required("format"));
        });
        test("inspection reports missing observation targets and changed pinned inputs explicitly", () =>
        {
            var fixture = Fixture(work, "missing");
            fixture.Native.Declarations.RemoveAt(0);
            LinkInspectionResult result = Read(fixture, "--binding", "sample::step");
            CoreTests.Equal("failed", result.Status);
            CoreTests.Equal("unresolved", result.Endpoints.Single().Links.Single().Observations.Single().Status);
            fixture = Fixture(work, "drift");
            CSharpScope source = CSharpScope.Parse([fixture.Source]);
            File.AppendAllText(fixture.Source, "\n// changed\n");
            CoreTests.ThrowsAny<InputException>(() => LinkInspection.Build(fixture.Target, fixture.Native, source,
                Options(), new(FilePaths.Comparer)));
        });
        test("inspection marks nested function context without claiming the lexical lock is held", () =>
        {
            var fixture = Fixture(work, "nested");
            File.WriteAllText(fixture.Source, """
                class InspectionSample {
                  readonly object gate = new object(); int value;
                  void Step(bool enabled) {
                    lock(gate) { System.Action callback = () => { value++; }; callback(); }
                  }
                }
                """);
            LinkInspectionResult result = Read(fixture, "--binding", "sample::step");
            InspectionSource source = result.Endpoints.Single().Links.Single().Source!;
            CoreTests.True(source.InNestedFunction);
            CoreTests.Equal(0, source.Locks.Count);
            CoreTests.True(source.Context.Any(c => c.Text.Contains("invocation", StringComparison.Ordinal)));
        });
        test("inspection markdown fences accommodate literal backticks in selected source", () =>
        {
            var fixture = Fixture(work, "fences");
            File.WriteAllText(fixture.Source, "class InspectionSample { int value; void Step(bool enabled) { _ = \"```\"; } }");
            fixture.Native.Declarations[1] = fixture.Native.Declarations[1] with
            {
                Binding = new("sample::step", Source: new("M:InspectionSample.Step(System.Boolean)")),
                Captures = null
            };
            string markdown = LinkInspection.Readable(Read(fixture, "--binding", "sample::step"), true);
            CoreTests.True(markdown.Contains("````csharp", StringComparison.Ordinal));
            CoreTests.True(markdown.Contains("Capture: none", StringComparison.Ordinal));
        });
        test("inspection compact filters omit noise explicitly without changing source resolution", () =>
        {
            var fixture = Fixture(work, "compact");
            LinkInspectionResult result = Read(fixture, "--include-unlinked", "false");
            CoreTests.Equal(2, result.Endpoints.Count);
            CoreTests.Equal(2, result.OmittedUnlinkedEndpoints);
            CoreTests.True(result.Fingerprints == null);
            CoreTests.True(result.InputPinCount > 0);
            CoreTests.True(result.Endpoints.All(e => e.Links.Single().Source != null));
            string text = LinkInspection.Readable(result, false);
            CoreTests.True(text.Contains("filtered unlinked/hidden declarations: 2", StringComparison.Ordinal));
            CoreTests.True(!text.Contains("\"classification\":", StringComparison.Ordinal));
            CoreTests.True(text.Contains("classification=transition", StringComparison.Ordinal));
            CoreTests.True(text.Contains("use --calls true", StringComparison.Ordinal));
            CoreTests.ThrowsAny<InputException>(() => Read(fixture, "--view", "small"));
            CoreTests.ThrowsAny<InputException>(() => Read(fixture, "--calls", "yes"));
            CoreTests.ThrowsAny<InputException>(() => Read(fixture, "--include-unlinked", "yes"));
        });
        test("inspection block context includes preceding sibling checks without inventing path conditions", () =>
        {
            var fixture = Fixture(work, "block");
            File.WriteAllText(fixture.Source, """
                class InspectionSample {
                  readonly object gate = new object(); int value;
                  void Step(bool enabled) {
                    lock (gate) {
                      if (enabled) {
                        if (value == 0) throw new System.InvalidOperationException();
                        value++;
                      }
                    }
                  }
                }
                """);
            InspectionSource source = Read(fixture, "--binding", "sample::step", "--context", "block")
                .Endpoints.Single().Links.Single().Source!;
            CoreTests.True(source.Lines.First().Text.Contains("if (enabled)", StringComparison.Ordinal));
            CoreTests.True(source.Lines.Any(l => l.Text.Contains("value++;", StringComparison.Ordinal)));
            CoreTests.True(source.Lines.Any(l => l.Text.Contains("if (value == 0)", StringComparison.Ordinal)));
            CoreTests.True(source.Context.All(c => !c.Text.Contains("value == 0", StringComparison.Ordinal)));
            CoreTests.True(!source.Lines.Any(l => l.Text.Contains("void Step", StringComparison.Ordinal)));
        });
        test("inspection optional calls include source identities dispatch and definitions but exclude nameof", () =>
        {
            var fixture = Fixture(work, "calls");
            File.WriteAllText(fixture.Source, """
                class InspectionSample {
                  int value;
                  void Step(bool enabled) { System.Console.WriteLine(Helper() + nameof(Helper)); }
                  string Helper() => "ok";
                }
                """);
            fixture.Native.Declarations[1] = fixture.Native.Declarations[1] with
            {
                Binding = new("sample::step", Source: new("M:InspectionSample.Step(System.Boolean)")),
                Captures = null
            };
            InspectionSource source = Read(fixture, "--binding", "sample::step", "--calls", "true")
                .Endpoints.Single().Links.Single().Source!;
            CoreTests.Equal(2, source.Calls!.Count);
            InspectionCall helper = source.Calls.Single(c => c.Site.Target == "M:InspectionSample.Helper");
            CoreTests.Equal("direct", helper.Site.Dispatch);
            CoreTests.Equal(fixture.Source, helper.SourceDeclarations.Single().Path);
            CoreTests.True(source.Calls.Single(c => c.Site.Expression.StartsWith("System.Console", StringComparison.Ordinal))
                .SourceDeclarations.Count == 0);
            CoreTests.True(Read(fixture, "--binding", "sample::step").Endpoints.Single().Links.Single().Source!.Calls == null);
        });
        test("inspection distinguishes evaluation of a branch condition from execution of either branch", () =>
        {
            var fixture = Fixture(work, "condition");
            File.WriteAllText(fixture.Source, """
                class InspectionSample {
                  int value;
                  bool Ready() => true;
                  void Step(bool enabled) { if (Ready()) { value++; } }
                }
                """);
            fixture.Native.Declarations[1] = fixture.Native.Declarations[1] with
            {
                Binding = new("sample::step", Source: new("M:InspectionSample.Step(System.Boolean)",
                    "InvocationExpression", "Ready()")),
                Captures = null
            };
            InspectionSource source = Read(fixture, "--binding", "sample::step").Endpoints.Single().Links.Single().Source!;
            CoreTests.Equal("condition evaluation: Ready()", source.Context.Single().Text);
        });
        test("inspection accepts repeated exact binding or endpoint selections as unions", () =>
        {
            var fixture = Fixture(work, "unions");
            LinkInspectionResult result = Read(fixture, "--binding", "sample::step", "--binding", "sample::state");
            CoreTests.Equal(2, result.Endpoints.Count);
            CoreTests.Equal(2, result.ReturnedLinks);
            result = Read(fixture, "--endpoint", "Invariant", "--endpoint", "Step");
            CoreTests.Equal("Step", result.Endpoints[0].Model.Name);
            CoreTests.Equal("Invariant", result.Endpoints[1].Model.Name);
            CoreTests.Equal(1, result.ReturnedLinks);
            CoreTests.ThrowsAny<InputException>(() => Read(fixture, "--binding", "sample::step", "--binding", "sample::absent"));
        });
        test("inspection combines member file capture and search filters on the same binding", () =>
        {
            var fixture = Fixture(work, "combined-filters");
            fixture.Native.Declarations.Add(new(new("sample::extra-state", Source: new("F:InspectionSample.value")),
                fixture.Native.Spans[1].Endpoint, 0, [new("sample::extra-state", Guard: "gate", Type: CapturedType.Int)]));
            LinkInspectionResult result = Read(fixture, "--symbol", "M:InspectionSample.Step(System.Boolean)",
                "--source-file", Path.Combine(Path.GetDirectoryName(fixture.Source)!, ".", "Sample.cs"),
                "--capture-point", "commit", "--search", "VALUE++");
            CoreTests.Equal(1, result.Endpoints.Count);
            CoreTests.Equal("sample::step", result.Endpoints.Single().Links.Single().Binding);
            CoreTests.Equal(3, result.CandidateLinks);
            CoreTests.Equal(1, result.MatchedLinks);
            CoreTests.Equal(0, Read(fixture, "--symbol", "M:InspectionSample.Step(System.Boolean)",
                "--capture-point", "none").MatchedEndpoints);
            CoreTests.Equal(0, Read(fixture, "--source-file", Path.Combine(work, "NotSelected.cs")).MatchedEndpoints);
        });
        test("inspection native kinds statuses and capture-point unions preserve unlinked distinctions", () =>
        {
            var fixture = Fixture(work, "kind-status");
            LinkInspectionResult result = Read(fixture, "--kind", "operator", "--status", "linked", "--status", "declared-hidden");
            CoreTests.Equal(3, result.Endpoints.Count);
            CoreTests.Equal(1, Read(fixture, "--status", "unlinked").Endpoints.Count);
            CoreTests.Equal(1, Read(fixture, "--capture-point", "none").Endpoints.Count);
            CoreTests.Equal(2, Read(fixture, "--capture-point", "commit", "--capture-point", "none").Endpoints.Count);
            CoreTests.Equal(0, Read(fixture, "--kind", "function").Endpoints.Count);
            CoreTests.Equal("Invariant", Read(fixture, "--search", "INVARIANT").Endpoints.Single().Model.Name);
            CoreTests.Equal("Step", Read(fixture, "--search", "sample::step").Endpoints.Single().Model.Name);
        });
        test("inspection pagination reports matching totals and deterministic continuation offsets", () =>
        {
            var fixture = Fixture(work, "pages");
            LinkInspectionResult first = Read(fixture, "--limit", "2");
            CoreTests.Equal(4, first.CandidateEndpoints);
            CoreTests.Equal(4, first.MatchedEndpoints);
            CoreTests.Equal(2, first.Endpoints.Count);
            CoreTests.Equal(2, first.NextOffset);
            LinkInspectionResult second = Read(fixture, "--limit", "2", "--offset", "2");
            CoreTests.Equal("Invariant", second.Endpoints[0].Model.Name);
            CoreTests.Equal(4, second.MatchedEndpoints);
            CoreTests.True(second.NextOffset == null);
            LinkInspectionResult beyond = Read(fixture, "--offset", int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
            CoreTests.Equal(0, beyond.Endpoints.Count);
            CoreTests.Equal(4, beyond.MatchedEndpoints);
            CoreTests.True(beyond.NextOffset == null);
        });
        test("inspection filters and pages cannot hide diagnostics or turn failures into success", () =>
        {
            var fixture = Fixture(work, "filtered-errors");
            fixture.Native.Declarations[1] = fixture.Native.Declarations[1] with
            {
                Binding = new("sample::step", Source: new("M:InspectionSample.Step(System.Boolean)", "ExpressionStatement",
                    ExpectedMatches: 9))
            };
            foreach (string[] args in new[] { new[] { "--status", "linked" }, new[] { "--limit", "1" },
                new[] { "--search", "absent", "--section", "model" } })
            {
                LinkInspectionResult result = Read(fixture, args);
                CoreTests.Equal("failed", result.Status);
                CoreTests.True(result.Diagnostics.Any(d => d.Message.Contains("Expected 9", StringComparison.Ordinal)));
                using JsonDocument document = JsonDocument.Parse(InspectionFiltering.Json(result));
                CoreTests.True(document.RootElement.GetProperty("diagnostics").GetArrayLength() > 0);
                CoreTests.True(LinkInspection.Readable(result, false).Contains("Expected 9", StringComparison.Ordinal));
            }
        });
        test("inspection source-only projection omits unrelated sections in JSON and readable output", () =>
        {
            var fixture = Fixture(work, "source-section");
            LinkInspectionResult result = Read(fixture, "--binding", "sample::step", "--section", "source");
            using JsonDocument document = JsonDocument.Parse(InspectionFiltering.Json(result));
            JsonElement endpoint = document.RootElement.GetProperty("endpoints")[0];
            CoreTests.True(!endpoint.GetProperty("model").TryGetProperty("source", out _));
            JsonElement link = endpoint.GetProperty("links")[0], source = link.GetProperty("source");
            CoreTests.Equal("value++;", source.GetProperty("selected_text").GetString());
            CoreTests.True(source.TryGetProperty("location", out _));
            foreach (string name in new[] { "lines", "context", "boundaries", "locks", "calls" })
                CoreTests.True(!source.TryGetProperty(name, out _));
            CoreTests.True(!link.TryGetProperty("captures", out _));
            CoreTests.True(!link.TryGetProperty("observations", out _));
            string text = LinkInspection.Readable(result, false);
            CoreTests.True(text.Contains("value++;", StringComparison.Ordinal));
            CoreTests.True(!text.Contains("Context (>>>", StringComparison.Ordinal));
            CoreTests.True(!text.Contains("Capture:", StringComparison.Ordinal));
            CoreTests.True(!text.Contains("Step == value'", StringComparison.Ordinal));
        });
        test("inspection section unions and calls-only projection request resolution without an extra flag", () =>
        {
            var fixture = Fixture(work, "call-section");
            File.WriteAllText(fixture.Source, "class InspectionSample { int value; void Step(bool enabled) { System.Console.WriteLine(value); } }");
            fixture.Native.Declarations[1] = fixture.Native.Declarations[1] with
            {
                Binding = new("sample::step", Source: new("M:InspectionSample.Step(System.Boolean)")),
                Captures = null
            };
            LinkInspectionResult result = Read(fixture, "--binding", "sample::step", "--section", "calls");
            CoreTests.True(result.CallsIncluded);
            CoreTests.Equal(1, result.Endpoints.Single().Links.Single().Source!.Calls!.Count);
            using JsonDocument document = JsonDocument.Parse(InspectionFiltering.Json(result));
            JsonElement source = document.RootElement.GetProperty("endpoints")[0].GetProperty("links")[0].GetProperty("source");
            CoreTests.True(source.TryGetProperty("calls", out _));
            CoreTests.True(!source.TryGetProperty("selected_text", out _));
            CoreTests.True(!source.TryGetProperty("lines", out _));
            result = Read(fixture, "--binding", "sample::step", "--section", "model", "--section", "source");
            CoreTests.Equal(2, result.Query!.Sections.Count);
            CoreTests.True(!result.CallsIncluded);
        });
        test("inspection rejects malformed filters and action-specific repeats explicitly", () =>
        {
            var fixture = Fixture(work, "filter-validation");
            foreach (string[] args in new[]
            {
                new[] { "--limit", "0" }, new[] { "--offset", "-1" }, new[] { "--limit", "99999999999" },
                new[] { "--section", "unknown" }, new[] { "--status", "passed" }, new[] { "--capture-point", "after" },
                new[] { "--search", "" }, new[] { "--symbol", "Step" }, new[] { "--kind", "" },
                new[] { "--section", "calls", "--calls", "false" }, new[] { "--section", "model", "--calls", "true" }
            }) CoreTests.ThrowsAny<InputException>(() => Read(fixture, args));
            CoreTests.ThrowsAny<InputException>(() => new CliOptions(["links", "set", "--binding", "a::b", "--binding", "a::c"]));
            CoreTests.ThrowsAny<InputException>(() => new CliOptions(["links", "list", "--limit", "1"]));
        });
        test("inspection symbol filtering also resolves source-owned statement bindings", () =>
        {
            var fixture = Fixture(work, "inline-symbol");
            File.WriteAllText(fixture.Source, """
                class InspectionSample { int value; void Step(bool enabled) {
                  /* @link {"bind":[{"id":"sample::step"}]} */
                  value++;
                } }
                """);
            fixture.Native.Declarations[1] = fixture.Native.Declarations[1] with { Binding = new("sample::step"), Captures = null };
            LinkInspectionResult result = Read(fixture, "--symbol", "M:InspectionSample.Step(System.Boolean)");
            CoreTests.Equal(1, result.Endpoints.Count);
            CoreTests.Equal("M:InspectionSample.Step(System.Boolean)", result.Endpoints.Single().Links.Single().SourceSymbol);
        });
        test("inspection syntax filters distinguish selected statements from enclosing method regions", () =>
        {
            var fixture = Fixture(work, "syntax-filter");
            fixture.Native.Declarations.Add(new(new("sample::whole-member",
                Source: new("M:InspectionSample.Step(System.Boolean)")), fixture.Native.Spans[1].Endpoint, 0));
            LinkInspectionResult result = Read(fixture, "--search", "value++;", "--syntax", "ExpressionStatement");
            CoreTests.Equal("sample::step", result.Endpoints.Single().Links.Single().Binding);
            CoreTests.Equal("ExpressionStatement", result.Endpoints.Single().Links.Single().SourceSyntax);
            CoreTests.Equal(2, Read(fixture, "--syntax", "ExpressionStatement", "--syntax", "FieldDeclaration").ReturnedLinks);
            CoreTests.ThrowsAny<InputException>(() => Read(fixture, "--syntax", "expression"));
        });
    }

    private sealed record Inputs(string Source, FormalTarget Target, NativeLinkIndex Native);

    private static Inputs Fixture(string work, string name)
    {
        string directory = Directory.CreateDirectory(Path.Combine(work, "inspection-" + name)).FullName;
        string cs = Path.Combine(directory, "Sample.cs"), model = Path.Combine(directory, "Sample.tla");
        File.WriteAllText(cs, string.Join("\r\n", new[]
        {
            "class InspectionSample {", "  readonly object gate = new object();", "  int value;",
            "  void Step(bool enabled) {", "    try {", "      lock(gate) {", "        if (enabled) {",
            "          value++;", "        }", "      }", "    } finally { }", "  }", "}"
        }));
        string[] definitions = ["Value == value", "Step == value' = value + 1", "Invariant == TRUE", "Internal == TRUE"];
        string text = string.Join("\n", definitions);
        File.WriteAllText(model, text);
        List<NativeDeclarationSpan> spans = [];
        foreach (string definition in definitions)
        {
            int start = text.IndexOf(definition, StringComparison.Ordinal);
            spans.Add(new(new(definition.Split(' ')[0], "Sample", "operator",
                Files.At(model, text, start, start + definition.Length), definition), start, start + definition.Length));
        }
        FormalTarget target = new("sample", "tla", [model], [new("Internal", "Authored environment step.")],
            ["sample::state", "sample::step"]);
        List<FormalLinkDeclaration> links =
        [
            new(new("sample::state", Source: new("F:InspectionSample.value")), spans[0].Endpoint, 0,
                [new("sample::state", Guard: "gate", Type: CapturedType.Int)]),
            new(new("sample::step", Source: new("M:InspectionSample.Step(System.Boolean)",
                "ExpressionStatement", "value++;")), spans[1].Endpoint, 0,
                [new("sample::step", CapturePoint.Commit, CaptureClassification.Transition, Actor: "1",
                    Operation: "1", Observe: ["sample::state"])])
        ];
        return new(cs, target, new(spans, links, []));
    }

    private static CliOptions Options(params string[] args) =>
        new(["links", "inspect", "--annotation-version", "4", .. args]);

    private static LinkInspectionResult Read(Inputs fixture, params string[] args) =>
        LinkInspection.Build(fixture.Target, fixture.Native, CSharpScope.Parse([fixture.Source]),
            Options(args), new(FilePaths.Comparer));
}
