using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TlaLink;

public sealed record InspectionLine(int Number, string Text, bool Selected);
public sealed record InspectionSource(Place Location, string SelectedText, List<InspectionLine> Lines,
    List<Boundary> Context, List<Boundary> Boundaries, List<string> Locks, bool InNestedFunction,
    List<InspectionCall>? Calls);
public sealed record InspectionCall(SourceCallSite Site, List<Place> SourceDeclarations);
public sealed record InspectionObservation(string Binding, string Status, string? Expression, Place? Location,
    string? Guard, CapturedType? Type);
public sealed record InspectedLink(string Binding, string? Reason, PreparationTarget? Selector,
    string Selection, string Status, InspectionSource? Source, List<CaptureAnnotation> Captures,
    List<InspectionObservation> Observations)
{
    public string? SourceSymbol { get; init; }
    public string? SourceSyntax { get; init; }
}
public sealed record InspectedEndpoint(Endpoint Model, string Status, string? HiddenReason, List<InspectedLink> Links);
public sealed record LinkInspectionResult(int SchemaVersion, string Kind, string Status, string Target,
    string Language, string Context, List<InspectedEndpoint> Endpoints, List<Issue> Diagnostics,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, string>? Fingerprints,
    string Boundary)
{
    public string View { get; init; } = "compact";
    public bool CallsIncluded { get; init; }
    public int OmittedUnlinkedEndpoints { get; init; }
    public int InputPinCount { get; init; }
    public SourceCompilerContext? Compiler { get; init; }
    public InspectionQuery? Query { get; init; }
    public int CandidateEndpoints { get; init; }
    public int MatchedEndpoints { get; init; }
    public int CandidateLinks { get; init; }
    public int MatchedLinks { get; init; }
    public int ReturnedLinks => Endpoints.Sum(e => e.Links.Count);
    public int? NextOffset { get; init; }
}

public sealed record LinkInspectionSummary(int SchemaVersion, string Kind, string Status, string Target,
    int Endpoints, int Links, int UnlinkedEndpoints, int OmittedUnlinkedEndpoints, string View,
    bool CallsIncluded, int InputPinCount, InspectionQuery? Query, int CandidateEndpoints,
    int MatchedEndpoints, int? NextOffset, int CandidateLinks, int MatchedLinks, int ReturnedLinks,
    List<Issue> Diagnostics, string Boundary);

internal static class LinkInspection
{
    private const string BoundaryText = "Read-only presentation of native declarations and compiler-resolved source attachments. " +
        "Unlinked endpoints are not automatically model-only or coverage gaps. Context is lexical, not a path condition, " +
        "call-effect analysis, atomicity proof, trace result or correspondence approval.";

    internal static void ValidateOptions(CliOptions options)
    {
        string context = options.Get("context", "3");
        if (context is not ("member" or "block") && (!int.TryParse(context, out int lines) || lines is < 0 or > 20))
            throw Invalid("--context must be 0..20 surrounding lines, block or member.");
        if (options.Get("view", "compact") is not ("compact" or "full"))
            throw Invalid("--view must be compact or full.");
        foreach (string flag in new[] { "calls", "include-unlinked" })
            if (options.Get(flag, flag == "calls" ? "false" : "true") is not ("true" or "false"))
                throw Invalid("--" + flag + " must be true or false.");
        if (options.Has("binding") && options.Has("endpoint"))
            throw Invalid("Select either --binding or --endpoint, not both.");
        InspectionFiltering.Read(options);
    }

    internal static LinkInspectionResult Build(FormalTarget target, NativeLinkIndex native, CSharpScope source,
        CliOptions options, Dictionary<string, string> pins, SourceCompilerContext? compiler = null)
    {
        ValidateOptions(options);
        InspectionQuery query = InspectionFiltering.Read(options);
        List<FormalLinkDeclaration> declarations = native.Declarations
            .Where(d => Markup.Name(d.Binding.Id).Alias == target.Id).ToList();
        bool Same(Endpoint left, Endpoint right) => left.Name == right.Name && left.Kind == right.Kind &&
            FilePaths.Comparer.Equals(left.Location.Path, right.Location.Path) &&
            left.Location.Line == right.Location.Line && left.Location.Column == right.Location.Column;
        List<NativeDeclarationSpan> spans = native.Spans.Where(s =>
            target.Inputs.Contains(s.Endpoint.Location.Path, FilePaths.Comparer) ||
            declarations.Any(d => Same(d.Endpoint, s.Endpoint))).ToList();
        if (query.Bindings.Count > 0)
        {
            foreach (string selectedId in query.Bindings)
                if (declarations.Count(d => d.Binding.Id == selectedId) != 1)
                    throw Invalid("Binding must resolve uniquely in the selected target: " + selectedId);
            spans = spans.Where(s => declarations.Any(d => query.Bindings.Contains(d.Binding.Id, StringComparer.Ordinal) &&
                Same(s.Endpoint, d.Endpoint))).ToList();
        }
        if (query.Endpoints.Count > 0)
        {
            foreach (string endpoint in query.Endpoints)
                if (spans.Count(s => s.Endpoint.Name == endpoint) != 1)
                    throw Invalid("Endpoint must resolve uniquely in the selected target: " + endpoint);
            spans = spans.Where(s => query.Endpoints.Contains(s.Endpoint.Name, StringComparer.Ordinal)).ToList();
        }
        if (spans.Count == 0) throw Invalid("No native declarations in the selected inspection.");
        List<FormalLinkDeclaration> selected = declarations.Where(d => spans.Any(s => Same(s.Endpoint, d.Endpoint)) &&
            (query.Bindings.Count == 0 || query.Bindings.Contains(d.Binding.Id, StringComparer.Ordinal))).ToList();
        HashSet<string> needed = selected.Select(d => d.Binding.Id).Concat(selected
            .SelectMany(d => d.Captures ?? []).SelectMany(c => c.Observe ?? [])).ToHashSet(StringComparer.Ordinal);
        List<Issue> diagnostics = [.. native.Diagnostics];
        Dictionary<string, SourceBinding?> resolved = new(StringComparer.Ordinal);
        foreach (string id in needed.Order(StringComparer.Ordinal))
        {
            try
            {
                List<ResolvedCorrespondence> matches = FormalCorrespondence.Match(source, declarations, target.Id, [id], diagnostics);
                resolved[id] = matches.FirstOrDefault()?.Implementation;
            }
            catch (InputException error)
            {
                diagnostics.Add(new(error.Code ?? ErrorCode.InvalidAttachment, id + ": " + error.Message));
                resolved[id] = null;
            }
        }
        diagnostics.AddRange(source.Diagnostics);
        if (!options.Has("binding") && !options.Has("endpoint"))
            foreach (string missing in (target.RequiredBindings ?? []).Except(declarations.Select(d => d.Binding.Id), StringComparer.Ordinal))
                diagnostics.Add(new(ErrorCode.MissingModelEndpoint, "Required binding is missing: " + missing));
        List<InspectedEndpoint> entries = [];
        foreach (NativeDeclarationSpan span in spans.OrderBy(s => s.Endpoint.Location.Path, FilePaths.Comparer).ThenBy(s => s.Start))
        {
            string text = Files.ReadPinned(span.Endpoint.Location.Path, pins);
            if (span.Start < 0 || span.End <= span.Start || span.End > text.Length)
                throw Invalid("Native declaration span is outside its pinned file: " + span.Endpoint.Name);
            List<InspectedLink> links = [];
            foreach (FormalLinkDeclaration declaration in selected.Where(d => Same(d.Endpoint, span.Endpoint)))
            {
                SourceBinding? binding = resolved.GetValueOrDefault(declaration.Binding.Id);
                List<CaptureAnnotation> captures = declaration.Captures ??
                    (binding?.Capture == null ? [] : [binding.Capture]);
                List<InspectionObservation> observations = [];
                foreach (string observed in captures.SelectMany(c => c.Observe ?? []).Distinct(StringComparer.Ordinal))
                {
                    SourceBinding? field = resolved.GetValueOrDefault(observed);
                    observations.Add(new(observed, field == null ? "unresolved" : "resolved", field?.Endpoint.Source,
                        field?.Endpoint.Location, field?.Capture?.Guard, field?.Capture?.Type));
                }
                PreparationTarget? selector = declaration.Binding.Source;
                string selection = selector == null ? "source-owned" : selector.Syntax == null ? "declaration" :
                    selector.Tokens == null ? "positional; count checks do not detect same-count statement replacement or reordering" :
                    "token-matched; ignores trivia, not semantic changes";
                links.Add(new(declaration.Binding.Id, declaration.Binding.Reason, selector, selection,
                    binding == null ? "unresolved" : "resolved",
                    binding == null ? null : Source(binding, options.Get("context", "3"),
                        InspectionFiltering.IncludeCalls(options, query)), captures, observations)
                {
                    SourceSymbol = declaration.Binding.Source?.Symbol ??
                        (binding == null ? null : InspectionFiltering.SourceSymbol(binding)),
                    SourceSyntax = binding?.Node.Kind().ToString() ?? selector?.Syntax
                });
            }
            string? hidden = target.HiddenActions?.SingleOrDefault(a => a.Endpoint == span.Endpoint.Name)?.Reason;
            entries.Add(new(span.Endpoint with { Source = text[span.Start..span.End] },
                links.Count == 0 ? hidden == null ? "unlinked" : "declared-hidden" :
                links.Any(l => l.Status == "unresolved") ? "unresolved" : "linked", hidden, links));
        }
        foreach (var pin in source.Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
        IntegratedInputs.CheckPins(pins);
        int candidates = entries.Count;
        int candidateLinks = entries.Sum(e => e.Links.Count);
        int omitted = 0;
        if (options.Get("include-unlinked", "true") == "false")
        {
            omitted = entries.Count(e => e.Status is "unlinked" or "declared-hidden");
            entries.RemoveAll(e => e.Status is "unlinked" or "declared-hidden");
        }
        entries = InspectionFiltering.Apply(entries, query);
        int matched = entries.Count;
        int matchedLinks = entries.Sum(e => e.Links.Count);
        entries = entries.Skip(query.Offset).Take(query.Limit ?? int.MaxValue).ToList();
        return new(Schema.Version, "link-inspection", diagnostics.Any(d => d.Severity == "error") ? "failed" : "ready",
            target.Id, target.Language, options.Get("context", "3"), entries, diagnostics,
            options.Get("view", "compact") == "full" ? pins : null, BoundaryText)
        {
            View = options.Get("view", "compact"), CallsIncluded = InspectionFiltering.IncludeCalls(options, query),
            OmittedUnlinkedEndpoints = omitted, InputPinCount = pins.Count, Compiler = compiler,
            Query = query, CandidateEndpoints = candidates, MatchedEndpoints = matched,
            CandidateLinks = candidateLinks, MatchedLinks = matchedLinks,
            NextOffset = query.Offset < matched && entries.Count < matched - query.Offset ? query.Offset + entries.Count : null
        };
    }

    internal static InspectionSource Source(SourceBinding binding, string context, bool calls = false)
    {
        SyntaxNode node = binding.Node;
        SyntaxNode member = node.AncestorsAndSelf().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or
            BasePropertyDeclarationSyntax or FieldDeclarationSyntax or EventFieldDeclarationSyntax) ?? node;
        var lines = node.SyntaxTree.GetText().Lines;
        int first = lines.GetLineFromPosition(node.SpanStart).LineNumber;
        int last = lines.GetLineFromPosition(Math.Max(node.SpanStart, node.Span.End - 1)).LineNumber;
        SyntaxNode region = context == "block" ? node.Ancestors().OfType<BlockSyntax>().FirstOrDefault() ?? member : member;
        int start = context is "member" or "block" ? lines.GetLineFromPosition(region.SpanStart).LineNumber :
            Math.Max(0, first - int.Parse(context, System.Globalization.CultureInfo.InvariantCulture));
        int end = context is "member" or "block" ? lines.GetLineFromPosition(Math.Max(region.SpanStart, region.Span.End - 1)).LineNumber :
            Math.Min(lines.Count - 1, last + int.Parse(context, System.Globalization.CultureInfo.InvariantCulture));
        List<InspectionLine> excerpt = Enumerable.Range(start, end - start + 1)
            .Select(i => new InspectionLine(i + 1, lines[i].ToString(), i >= first && i <= last)).ToList();
        List<Boundary> frames = [];
        foreach (SyntaxNode ancestor in node.Ancestors().TakeWhile(n => n is not BaseTypeDeclarationSyntax).Reverse())
        {
            string? header = ancestor switch
            {
                LockStatementSyntax value => (value.Statement.Span.Contains(node.Span) ? "lock (" :
                    "lock expression evaluation, not yet held (") + value.Expression + ")",
                IfStatementSyntax value => (value.Condition.Span.Contains(node.Span) ? "condition evaluation: " :
                    value.Statement.Span.Contains(node.Span) ? "then: " : "else: ") + value.Condition,
                SwitchStatementSyntax value => "switch (" + value.Expression + ")",
                SwitchSectionSyntax value => string.Join(" ", value.Labels.Select(l => l.ToString())),
                TryStatementSyntax value when value.Block.Span.Contains(node.Span) => "try",
                CatchClauseSyntax value => "catch" + value.Declaration + (value.Filter == null ? "" : " " + value.Filter),
                FinallyClauseSyntax => "finally",
                UsingStatementSyntax value => "using (" + (value.Declaration?.ToString() ?? value.Expression?.ToString()) + ")",
                ForStatementSyntax value => "for (" + value.Declaration + "; " + value.Condition + "; " + string.Join(", ", value.Incrementors) + ")",
                ForEachStatementSyntax value => "foreach (" + value.Type + " " + value.Identifier + " in " + value.Expression + ")",
                WhileStatementSyntax value => "while (" + value.Condition + ")",
                DoStatementSyntax value => "do/while (" + value.Condition + ")",
                LocalFunctionStatementSyntax value => "local function " + value.Identifier,
                AnonymousFunctionExpressionSyntax => "nested anonymous function; enclosing lexical conditions/locks may not apply at invocation",
                _ => null
            };
            if (header != null) frames.Add(new(ancestor.Kind().ToString(), header,
                Files.At(binding.Path, binding.Text, ancestor.SpanStart, ancestor.Span.End)));
        }
        List<InspectionCall>? callSites = null;
        if (calls)
        {
            callSites = [];
            foreach (SyntaxNode call in SourceQueries.InvocationNodes(node))
            {
                if (binding.Semantics.GetOperation(call) is Microsoft.CodeAnalysis.Operations.INameOfOperation) continue;
                IMethodSymbol? called = binding.Semantics.GetSymbolInfo(call).Symbol as IMethodSymbol;
                List<Place> declarations = called?.DeclaringSyntaxReferences.Select(r =>
                {
                    SyntaxNode declaration = r.GetSyntax();
                    return Files.At(declaration.SyntaxTree.FilePath, declaration.SyntaxTree.GetText().ToString(),
                        declaration.SpanStart, declaration.Span.End);
                }).ToList() ?? [];
                callSites.Add(new(SourceQueries.CallSite(binding.Text, call,
                    binding.Declaration.Source?.Symbol, member, called), declarations));
            }
        }
        List<string> locks = node.Ancestors()
            .TakeWhile(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .OfType<LockStatementSyntax>().Where(l => l.Statement.Span.Contains(node.Span))
            .Reverse().Select(l => l.Expression.ToString()).ToList();
        return new(binding.Endpoint.Location, binding.Endpoint.Source, excerpt, frames, CSharpScope.Boundaries(binding),
            locks, SourceQueries.Nested(node, member), callSites);
    }

    internal static void Render(LinkInspectionResult result, CliOptions options)
    {
        string format = options.Get("format", "json");
        string rendered = format switch
        {
            "text" => Readable(result, markdown: false),
            "markdown" => Readable(result, markdown: true),
            "summary" => JsonSerializer.Serialize(new LinkInspectionSummary(result.SchemaVersion, result.Kind, result.Status, result.Target,
                result.Endpoints.Count, result.Endpoints.Sum(e => e.Links.Count),
                result.Endpoints.Count(e => e.Status == "unlinked"),
                result.OmittedUnlinkedEndpoints, result.View, result.CallsIncluded, result.InputPinCount,
                result.Query, result.CandidateEndpoints, result.MatchedEndpoints, result.NextOffset,
                result.CandidateLinks, result.MatchedLinks, result.ReturnedLinks,
                result.Diagnostics, result.Boundary), Files.Json),
            _ => InspectionFiltering.Json(result)
        };
        if (options.Optional("output") is string output)
        {
            using FileStream file = new(FilePaths.Full(output), FileMode.CreateNew, FileAccess.Write);
            using StreamWriter writer = new(file, new UTF8Encoding(false));
            writer.Write(rendered);
        }
        Console.WriteLine(rendered);
    }

    internal static string Readable(LinkInspectionResult result, bool markdown)
    {
        StringBuilder text = new();
        void Heading(int level, string title) => text.AppendLine(markdown ? new string('#', level) + " " + title : title);
        void Code(string language, string value)
        {
            string fence = "```";
            while (value.Contains(fence, StringComparison.Ordinal)) fence += "`";
            if (markdown) text.AppendLine(fence + language);
            text.AppendLine(value);
            if (markdown) text.AppendLine(fence);
        }
        Heading(1, "Correspondence inspection: " + result.Target);
        text.AppendLine("Status: " + result.Status);
        text.AppendLine(result.Boundary);
        bool Includes(string section) => result.Query?.Sections.Contains(section, StringComparer.Ordinal) != false;
        text.AppendLine($"Endpoints: {result.Endpoints.Count} returned / {result.MatchedEndpoints} matched / {result.CandidateEndpoints} candidates" +
            $"; next offset: {result.NextOffset?.ToString() ?? "none"}.");
        text.AppendLine($"Bindings: {result.ReturnedLinks} returned / {result.MatchedLinks} matched / {result.CandidateLinks} candidates.");
        if (result.MatchedEndpoints == 0) text.AppendLine("No matching endpoints; this is not a coverage or correspondence conclusion.");
        if (result.Query != null)
        {
            text.AppendLine("Sections: " + string.Join(", ", result.Query.Sections) + " (identities and diagnostics always retained).");
            text.AppendLine("Query: " + JsonSerializer.Serialize(result.Query, Markup.AnnotationJson));
        }
        text.AppendLine($"View: {result.View}; source context: {result.Context}; explicit calls: " +
            (result.CallsIncluded ? "included (not a transitive or implicit call graph)" : "omitted; use --calls true") +
            $"; filtered unlinked/hidden declarations: {result.OmittedUnlinkedEndpoints}.");
        if (result.Compiler is SourceCompilerContext compiler)
            text.AppendLine($"Compiler: C# {compiler.LanguageVersion}, nullable={compiler.Nullable}, target={compiler.TargetFramework ?? "explicit compilation"}" +
                (compiler.RegressionResult == null ? "" : "; recorded context: " + compiler.RegressionResult));
        foreach (InspectedEndpoint endpoint in result.Endpoints)
        {
            text.AppendLine();
            Heading(2, endpoint.Model.Name + " [" + endpoint.Status + "]");
            text.AppendLine("Model: " + Location(endpoint.Model.Location));
            if (Includes("model")) Code(result.Language, endpoint.Model.Source);
            if (endpoint.HiddenReason != null) text.AppendLine("Declared hidden: " + endpoint.HiddenReason);
            foreach (InspectedLink link in endpoint.Links)
            {
                Heading(3, link.Binding + " [" + link.Status + "]");
                if (link.Reason != null) text.AppendLine("Reason: " + link.Reason);
                if (Includes("source")) text.AppendLine("Selector: " + link.Selection);
                if (link.SourceSymbol != null) text.AppendLine("Source member: " + link.SourceSymbol);
                if (Includes("source") && link.Selector != null)
                {
                    if (link.Selector.Syntax != null)
                        text.AppendLine($"Syntax: {link.Selector.Syntax}, match {link.Selector.Match} of {link.Selector.ExpectedMatches}");
                    if (result.View == "full") text.AppendLine(JsonSerializer.Serialize(link.Selector, Markup.AnnotationJson));
                }
                if (link.Source is InspectionSource source)
                {
                    text.AppendLine("C#: " + Location(source.Location));
                    if (Includes("source") && (!Includes("context") || result.View == "full" || !source.SelectedText.Contains('\n')))
                    {
                        text.AppendLine("Exact selected syntax:");
                        Code("csharp", source.SelectedText);
                    }
                    if (Includes("context"))
                    {
                        text.AppendLine("Context (>>> marks lines intersecting the selection):");
                        Code("text", string.Join("\n", source.Lines.Select(l => $"{(l.Selected ? ">>>" : "   ")} {l.Number,5} | {l.Text}")));
                        foreach (Boundary frame in source.Context)
                            text.AppendLine("Enclosing: " + frame.Text + " at " + Location(frame.Location));
                        text.AppendLine("Enclosing source locks: " + (source.Locks.Count == 0 ? "none" : string.Join(", ", source.Locks)));
                        foreach (Boundary boundary in source.Boundaries)
                            text.AppendLine("Selected-syntax boundary: " + boundary.Kind + " at " + Location(boundary.Location));
                    }
                    if (source.InNestedFunction)
                        text.AppendLine("Nested function: lexical outer context does not establish invocation-time guards.");
                    foreach (InspectionCall call in Includes("calls") ? source.Calls ?? [] : [])
                    {
                        text.AppendLine($"Explicit call [{call.Site.Dispatch}]: {call.Site.Expression} -> " +
                            (call.Site.Target ?? "unresolved") +
                            (call.SourceDeclarations.Count == 0 ? " (no selected source body)" :
                                " at " + string.Join(", ", call.SourceDeclarations.Select(Location))));
                        if (call.Site.InNestedFunction)
                            text.AppendLine("Call is in a nested function; its execution is not established by the enclosing member.");
                    }
                }
                if (Includes("captures") && link.Captures.Count == 0) text.AppendLine("Capture: none (review-only attachment).");
                foreach (CaptureAnnotation capture in Includes("captures") ? link.Captures : [])
                {
                    text.AppendLine("Capture: " + CaptureMeaning(capture));
                    if (result.View == "full") Code("json", JsonSerializer.Serialize(capture, Files.Json));
                    else text.AppendLine("  " + string.Join("; ", capture.BackendOptions().Select(p => p.Key + "=" + p.Value)));
                }
                foreach (InspectionObservation observation in Includes("observations") ? link.Observations : [])
                    text.AppendLine($"Observes: {observation.Binding} [{observation.Status}] -> {observation.Expression}" +
                        $" (declared guard: {observation.Guard ?? "none"}; type: {observation.Type?.ToString() ?? "unspecified"})" +
                        (observation.Location == null ? "" : " at " + Location(observation.Location)));
            }
        }
        if (result.Diagnostics.Count > 0) Heading(2, "Diagnostics");
        foreach (Issue diagnostic in result.Diagnostics)
            text.AppendLine($"{diagnostic.Severity} {diagnostic.Code}: {diagnostic.Message}" +
                (diagnostic.Location == null ? "" : " at " + Location(diagnostic.Location)));
        return text.ToString();
    }

    private static string CaptureMeaning(CaptureAnnotation capture) => capture.Point switch
    {
        CapturePoint.Invoke => "before the selected syntax executes; not completion of a returned Task",
        CapturePoint.Commit => "after successful execution of the selected syntax; not necessarily whole-method completion",
        CapturePoint.Complete => "after successful execution of the selected syntax (complete label); not necessarily whole-method or returned Task completion",
        CapturePoint.Fault => "exception observation at the supported selected statement/lifetime; not successful cleanup",
        CapturePoint.Cleanup => "after successful implicit Dispose for the supported selected using lifetime",
        _ => "state metadata; declared guards are not proof that every access uses the guard"
    };

    private static string Location(Place place) => $"{place.Path}:{place.Line}:{place.Column}";
    private static InputException Invalid(string message) => new(ErrorCode.InvalidAnnotation, message);
}
