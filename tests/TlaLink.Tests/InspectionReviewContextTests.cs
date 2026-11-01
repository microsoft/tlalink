using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class InspectionReviewContextTests
{
    public static void Register(Action<string, Action> test, string work)
    {
        test("member grouping orders attachments by source and deduplicates shared context", () =>
        {
            Inputs fixture = Fixture(work, "group");
            LinkInspectionResult result = Read(fixture, "--group-by", "member", "--context", "member");
            InspectionMemberGroup group = result.MemberGroups.Single(g => g.Member.Symbol == "M:InspectionExample.ReviewCounter.Adjust(System.Boolean)");
            CoreTests.Equal(4, group.Attachments.Count);
            CoreTests.Equal("review::read", group.Attachments[0].Binding);
            CoreTests.Equal("review::release", group.Attachments[^1].Binding);
            CoreTests.Equal(group.Lines.Count, group.Lines.Select(l => l.Number).Distinct().Count());
            CoreTests.True(group.Lines.Any(l => l.Text.Contains("finally", StringComparison.Ordinal)));
            CoreTests.Equal(4, group.Lines.Count(l => l.Selected));
            using JsonDocument document = JsonDocument.Parse(InspectionFiltering.Json(result));
            CoreTests.True(document.RootElement.GetProperty("member_groups").GetArrayLength() > 0);
            JsonElement source = document.RootElement.GetProperty("endpoints")[0].GetProperty("links")[0].GetProperty("source");
            CoreTests.True(!source.TryGetProperty("lines", out _));
            CoreTests.True(source.TryGetProperty("context", out _));
            string text = LinkInspection.Readable(result, false);
            CoreTests.Equal(1, Count(text, "Shared context"));
            CoreTests.True(text.Contains("not execution order", StringComparison.Ordinal));
            CoreTests.True(!text.Contains("Context (>>>", StringComparison.Ordinal));
        });
        test("member grouping preserves page counts without treating a partial page as whole coverage", () =>
        {
            Inputs fixture = Fixture(work, "group-page");
            LinkInspectionResult result = Read(fixture, "--group-by", "member", "--limit", "1", "--context", "member");
            InspectionMemberGroup group = result.MemberGroups.Single();
            CoreTests.Equal(1, group.Attachments.Count);
            CoreTests.Equal(4, group.MatchingAttachments);
            CoreTests.Equal(1, result.NextOffset);
            using JsonDocument document = JsonDocument.Parse(InspectionFiltering.Json(
                Read(fixture, "--group-by", "member", "--section", "source")));
            CoreTests.True(!document.RootElement.GetProperty("member_groups")[0].TryGetProperty("lines", out _));
            CoreTests.True(Read(fixture).MemberGroups.Count == 0);
        });
        test("callee expansion emits each selected source declaration once with no recursive body traversal", () =>
        {
            Inputs fixture = Fixture(work, "callees");
            LinkInspectionResult result = Read(fixture, "--callee-source", "true");
            CoreTests.True(result.CallsIncluded);
            CoreTests.Equal(3, result.CalleeSources.Count);
            CoreTests.True(result.CalleeSources.All(c => c.Status == "included"));
            CoreTests.True(!result.CalleeSources.Any(c => c.Symbol.Contains("Observe", StringComparison.Ordinal)));
            InspectionCall[] commits = result.Endpoints.SelectMany(e => e.Links).SelectMany(l => l.Source!.Calls!)
                .Where(c => c.Site.Target == "M:InspectionExample.ReviewCounter.Commit(System.Int32)").ToArray();
            CoreTests.Equal(2, commits.Length);
            CoreTests.Equal(commits[0].CalleeKeys.Single(), commits[1].CalleeKeys.Single());
            string text = LinkInspection.Readable(result, true);
            CoreTests.Equal(1, Count(text, "Selected-source callees"));
            CoreTests.Equal(1, Count(text, "this.Observe(next);"));
            CoreTests.True(text.Contains("not a runtime dispatch", StringComparison.Ordinal));
        });
        test("callee budgets report truncated and omitted declarations and retain structural readiness", () =>
        {
            Inputs fixture = Fixture(work, "budgets");
            LinkInspectionResult result = Read(fixture, "--callee-source", "true", "--callee-limit", "1", "--callee-max-lines", "2");
            CoreTests.Equal("ready", result.Status);
            CoreTests.Equal(1, result.CalleeSources.Count(c => c.Status == "truncated"));
            CoreTests.Equal(2, result.CalleeSources.Count(c => c.Status == "budget-omitted"));
            CoreTests.Equal(2, result.CalleeSources.Sum(c => c.Lines.Count));
            CoreTests.True(result.CalleeSources.All(c => c.OmittedLines == c.TotalLines - c.Lines.Count));
            CoreTests.True(result.Endpoints.SelectMany(e => e.Links).SelectMany(l => l.Source!.Calls!)
                .Any(c => c.ExpansionStatus == "budget-omitted"));
            LinkInspectionResult paged = Read(fixture, "--callee-source", "true", "--limit", "1");
            CoreTests.Equal(1, paged.CalleeSources.Count);
            CoreTests.Equal("M:InspectionExample.ReviewCounter.Commit(System.Int32)", paged.CalleeSources.Single().Symbol);
        });
        test("callee expansion keeps interface virtual and metadata boundaries explicit", () =>
        {
            Inputs fixture = Fixture(work, "dispatch", WholeMember("Dispatch"));
            LinkInspectionResult result = Read(fixture, "--callee-source", "true", "--section", "calls");
            List<InspectionCall> calls = result.Endpoints.Single().Links.Single().Source!.Calls!;
            CoreTests.Equal("declaration-only", calls.Single(c => c.Site.Dispatch == "interface").ExpansionStatus);
            CoreTests.Equal("included", calls.Single(c => c.Site.Dispatch == "virtual").ExpansionStatus);
            CoreTests.Equal("no-selected-source", calls.Single(c => c.Site.Target!.Contains("Console", StringComparison.Ordinal)).ExpansionStatus);
            CoreTests.True(result.CalleeSources.Single(c => c.Symbol.Contains("ISink", StringComparison.Ordinal)).Lines.Count > 0);
            CoreTests.True(result.CalleeSources.All(c => c.Symbol != "M:InspectionExample.ReviewCounter.Dispatch(InspectionExample.ISink)"));
            using JsonDocument json = JsonDocument.Parse(InspectionFiltering.Json(result));
            CoreTests.True(json.RootElement.GetProperty("callee_sources").GetArrayLength() == 2);
            CoreTests.True(!json.RootElement.GetProperty("endpoints")[0].GetProperty("model").TryGetProperty("source", out _));
        });
        test("callee expansion handles original generic extension and partial implementation identities", () =>
        {
            Inputs fixture = Fixture(work, "generics", WholeMember("Generic"));
            LinkInspectionResult result = Read(fixture, "--callee-source", "true");
            CoreTests.True(result.CalleeSources.Any(c => c.Symbol.Contains("Echo``1", StringComparison.Ordinal)));
            CoreTests.True(result.CalleeSources.Any(c => c.Symbol.Contains("ReviewExtensions.Identity``1", StringComparison.Ordinal)));
            List<InspectionCalleeSource> observe = result.CalleeSources.Where(c => c.Symbol.Contains("Observe", StringComparison.Ordinal)).ToList();
            CoreTests.Equal(2, observe.Count);
            CoreTests.True(observe.Any(c => c.Status == "included" && c.Lines.Any(l => l.Text.Contains("this.value = item;", StringComparison.Ordinal))));
            CoreTests.True(observe.Any(c => c.Status == "declaration-only"));
        });
        test("callee expansion marks nested execution and does not invent a delegate destination", () =>
        {
            Inputs fixture = Fixture(work, "nested", WholeMember("Nested"));
            LinkInspectionResult result = Read(fixture, "--callee-source", "true");
            List<InspectionCall> calls = result.Endpoints.Single().Links.Single().Source!.Calls!;
            CoreTests.True(calls.Single(c => c.Site.Target == "M:InspectionExample.ReviewCounter.Release").Site.InNestedFunction);
            CoreTests.Equal("no-selected-source", calls.Single(c => c.Site.Dispatch == "delegate").ExpansionStatus);
            CoreTests.Equal(1, result.CalleeSources.Count);
        });
        test("review options reject contradictory budgets and keep source-only sections small", () =>
        {
            Inputs fixture = Fixture(work, "invalid");
            foreach (string[] args in new[]
            {
                new[] { "--group-by", "call" }, new[] { "--callee-source", "yes" },
                new[] { "--callee-source", "true", "--calls", "false" },
                new[] { "--callee-source", "true", "--section", "source" },
                new[] { "--callee-limit", "1" },
                new[] { "--callee-source", "true", "--callee-limit", "0" },
                new[] { "--callee-source", "true", "--callee-limit", "101" },
                new[] { "--callee-source", "true", "--callee-max-lines", "501" }
            }) CoreTests.ThrowsAny<InputException>(() => Read(fixture, args));
            CoreTests.ThrowsAny<InputException>(() => new CliOptions(["links", "check", "--group-by", "member"]));
            CoreTests.ThrowsAny<InputException>(() => new CliOptions(["links", "set", "--callee-source", "true"]));
            using JsonDocument document = JsonDocument.Parse(InspectionFiltering.Json(
                Read(fixture, "--group-by", "member", "--section", "source")));
            CoreTests.True(!document.RootElement.TryGetProperty("callee_sources", out _));
        });
        test("grouped review retains unresolved entries diagnostics and empty-page semantics", () =>
        {
            Inputs fixture = Fixture(work, "unresolved");
            fixture.Native.Declarations[0] = fixture.Native.Declarations[0] with
            {
                Binding = new("review::commita", Source: new("M:InspectionExample.ReviewCounter.Missing"))
            };
            LinkInspectionResult result = Read(fixture, "--group-by", "member", "--callee-source", "true");
            CoreTests.Equal("failed", result.Status);
            CoreTests.True(result.Diagnostics.Count > 0);
            CoreTests.Equal(3, result.MemberGroups.Single().Attachments.Count);
            CoreTests.Equal("unresolved", result.Endpoints[0].Links.Single().Status);
            LinkInspectionResult empty = Read(fixture, "--group-by", "member", "--callee-source", "true", "--offset", "99");
            CoreTests.Equal("failed", empty.Status);
            CoreTests.Equal(0, empty.MemberGroups.Count);
            CoreTests.Equal(0, empty.CalleeSources.Count);
            CoreTests.True(LinkInspection.Readable(empty, false).Contains("Diagnostics", StringComparison.Ordinal));
        });
    }

    public static void RegisterNative(Action<string, Action> test, string root, string work)
    {
        test("native CLI groups member context and projects bounded callee source across output formats", () =>
        {
            string directory = Directory.CreateDirectory(Path.Combine(work, "review-context-native")).FullName;
            string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "InspectionReview");
            string cs = Path.Combine(directory, "ReviewCounter.cs"), model = Path.Combine(directory, "Review.tla");
            File.Copy(Path.Combine(fixture, "ReviewCounter.cs"), cs);
            File.Copy(Path.Combine(fixture, "Review.tla"), model);
            string scope = Path.Combine(directory, "scope.json");
            Files.WriteNew(scope, new CorrespondenceConfiguration(Schema.Version, "correspondence-scope", Markup.Version,
                [new("review", "tla", [model], RequiredBindings: ["review::commita", "review::commitb", "review::read", "review::release"])], []));
            string sourceHash = Files.Hash(cs), modelHash = Files.Hash(model);
            (int Code, string Json) Run(params string[] args) =>
                TestCli.Run(["links", "inspect", "--annotation-version", "4", "--scope", scope, "--target", "review",
                    "--source", cs, "--jar", TestTools.Jar(root), "--group-by", "member", "--context", "member",
                    "--callee-source", "true", "--callee-limit", "1", "--callee-max-lines", "2", .. args]);
            var json = Run();
            if (json.Code != 0) throw new InvalidOperationException(json.Json);
            using (JsonDocument result = JsonDocument.Parse(json.Json))
            {
                CoreTests.Equal(1, result.RootElement.GetProperty("member_groups").GetArrayLength());
                CoreTests.Equal(3, result.RootElement.GetProperty("callee_sources").GetArrayLength());
                CoreTests.Equal(2, result.RootElement.GetProperty("callee_sources").EnumerateArray()
                    .Count(c => c.GetProperty("status").GetString() == "budget-omitted"));
            }
            using (JsonDocument summary = JsonDocument.Parse(Run("--format", "summary").Json))
            {
                CoreTests.Equal(1, summary.RootElement.GetProperty("member_groups").GetInt32());
                CoreTests.Equal(2, summary.RootElement.GetProperty("omitted_callee_declarations").GetInt32());
                CoreTests.Equal(1, summary.RootElement.GetProperty("truncated_callee_sources").GetInt32());
            }
            var text = Run("--format", "text");
            CoreTests.Equal(0, text.Code);
            CoreTests.Equal(1, Count(text.Json, "Shared context"));
            string report = Path.Combine(directory, "review.md");
            var markdown = Run("--format", "markdown", "--output", report);
            CoreTests.Equal(0, markdown.Code);
            CoreTests.Equal(markdown.Json.Trim(), Files.Read(report).Trim());
            CoreTests.True(Files.Read(report).Contains("budget-omitted", StringComparison.Ordinal));
            CoreTests.Equal(sourceHash, Files.Hash(cs));
            CoreTests.Equal(modelHash, Files.Hash(model));
        });
    }

    private static int Count(string text, string value) =>
        text.Split(value, StringSplitOptions.None).Length - 1;

    private sealed record Inputs(string Source, string Model, FormalTarget Target, NativeLinkIndex Native);

    private static string WholeMember(string name) => "M:InspectionExample.ReviewCounter." + name +
        (name == "Dispatch" ? "(InspectionExample.ISink)" : "");

    private static Inputs Fixture(string work, string name, string? member = null)
    {
        string directory = Directory.CreateDirectory(Path.Combine(work, "review-context-" + name)).FullName;
        string cs = Path.Combine(directory, "ReviewCounter.cs"), model = Path.Combine(directory, "Review.tla");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "InspectionReview", "ReviewCounter.cs"), cs);
        List<(string Name, PreparationTarget Selector)> selectors = member == null ?
        [
            ("CommitA", new("M:InspectionExample.ReviewCounter.Adjust(System.Boolean)", "ExpressionStatement", "this.Commit(next);", 1, 2)),
            ("CommitB", new("M:InspectionExample.ReviewCounter.Adjust(System.Boolean)", "ExpressionStatement", "this.Commit(next);", 2, 2)),
            ("Read", new("M:InspectionExample.ReviewCounter.Adjust(System.Boolean)", "LocalDeclarationStatement", "int next = this.ReadNext();")),
            ("Release", new("M:InspectionExample.ReviewCounter.Adjust(System.Boolean)", "ExpressionStatement", "this.Release();"))
        ] : [(name, new(member))];
        List<NativeDeclarationSpan> spans = [];
        List<FormalLinkDeclaration> declarations = [];
        string text = string.Join("\n", selectors.Select(s => s.Name + " == TRUE"));
        File.WriteAllText(model, text);
        foreach (var selection in selectors)
        {
            string definition = selection.Name + " == TRUE";
            int start = text.IndexOf(definition, StringComparison.Ordinal);
            Endpoint endpoint = new(selection.Name, "Review", "operator",
                Files.At(model, text, start, start + definition.Length), definition);
            spans.Add(new(endpoint, start, start + definition.Length));
            declarations.Add(new(new("review::" + selection.Name.ToLowerInvariant(), Source: selection.Selector), endpoint, 0));
        }
        FormalTarget target = new("review", "tla", [model], RequiredBindings: declarations.Select(d => d.Binding.Id).ToList());
        return new(cs, model, target, new(spans, declarations, []));
    }

    private static LinkInspectionResult Read(Inputs fixture, params string[] args) =>
        LinkInspection.Build(fixture.Target, fixture.Native, CSharpScope.Parse([fixture.Source]),
            new(["links", "inspect", "--annotation-version", "4", .. args]), new(FilePaths.Comparer));
}
