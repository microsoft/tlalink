using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TlaLink;

public sealed record HiddenModelAction(string Endpoint, string Reason);
public sealed record FormalTarget(string Id, string Language, List<string> Inputs,
    List<HiddenModelAction>? HiddenActions = null, List<string>? RequiredBindings = null);
public sealed record OmissionRule(string Id, List<string> Targets, List<string> Calls, string Reason,
    List<string> Assumptions);
public sealed record CorrespondenceConfiguration(int SchemaVersion, string Kind, int AnnotationVersion,
    List<FormalTarget> Targets, List<OmissionRule> Omissions, List<MethodSummary>? MethodSummaries = null);
public sealed record SourceDisposition(string Id, string Target, string Status, string Reason, Place Location,
    List<string> Assumptions, List<Boundary> Boundaries);
public sealed record OmissionRuleMatch(string Id, string Target, int MatchedSites);
internal sealed record SourceExclusion(ExcludeAnnotation Declaration, int Start, int End,
    SyntaxNode Node, SemanticModel Semantics, string Path, string Text);

public static class CorrespondenceInputs
{
    public static CorrespondenceConfiguration Load(string path, Dictionary<string, string> pins)
    {
        path = FilePaths.Full(path);
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InputException(ErrorCode.InvalidAnnotation, "Correspondence scope exceeds 16 MiB.");
        CorrespondenceConfiguration config = System.Text.Json.JsonSerializer.Deserialize<CorrespondenceConfiguration>(
            Files.ReadPinned(path, pins), ProjectInputs.Json)
            ?? throw new InputException(ErrorCode.InvalidAnnotation, "Empty correspondence scope.");
        if (config.SchemaVersion != Schema.Version || config.Kind != "correspondence-scope")
            throw new InputException(ErrorCode.InvalidAnnotation, "Expected current correspondence-scope.");
        Markup.RequireVersion(config.AnnotationVersion);
        if (config.Targets.Count is 0 or > 1000 || config.Omissions.Count > 1000 ||
            config.Targets.Any(t => t is null) || config.Omissions.Any(r => r is null))
            throw new InputException(ErrorCode.InvalidAnnotation, "Correspondence scope requires 1..1000 targets and at most 1000 omission rules.");
        ProjectInputs.Unique(config.Targets.Select(t => t.Id), "formal targets");
        ProjectInputs.Unique(config.Omissions.Select(t => t.Id), "omission rules");
        string root = Path.GetDirectoryName(path)!;
        List<FormalTarget> targets = [];
        foreach (FormalTarget target in config.Targets)
        {
            if (target.Language is not ("tla" or "dafny" or "p") || target.Inputs.Count is 0 or > 1000 ||
                target.Inputs.Any(string.IsNullOrWhiteSpace) || target.Language == "tla" && target.Inputs.Count != 1)
                throw new InputException(ErrorCode.InvalidAnnotation, "Formal targets need a native language and explicit inputs; TLA+ requires one root file.");
            string extension = target.Language switch { "tla" => ".tla", "dafny" => ".dfy", _ => ".p" };
            List<string> inputs = target.Inputs.Select(p => FilePaths.Full(p, root)).ToList();
            if (inputs.Any(p => !File.Exists(p) || !Path.GetExtension(p).Equals(extension, StringComparison.OrdinalIgnoreCase)) ||
                inputs.Distinct(FilePaths.Comparer).Count() != inputs.Count)
                throw new InputException(ErrorCode.InvalidAnnotation, "Formal target inputs must be distinct existing native source files.");
            foreach (string input in inputs) Files.Pin(pins, input, Files.Hash(input));
            targets.Add(target with { Inputs = inputs });
            if (target.HiddenActions != null && (target.Language != "tla" ||
                target.HiddenActions.Any(a => a is null || string.IsNullOrWhiteSpace(a.Endpoint) || string.IsNullOrWhiteSpace(a.Reason)) ||
                target.HiddenActions.Select(a => a.Endpoint).Distinct(StringComparer.Ordinal).Count() != target.HiddenActions.Count))
                throw new InputException(ErrorCode.InvalidAnnotation, "Hidden actions require distinct native TLA+ endpoints and reasons.");
            if (target.Language == "tla")
            {
                if (target.RequiredBindings is not { Count: > 0 and <= 1000 } ||
                    target.RequiredBindings.Any(id => !Markup.IsCorrespondenceId(id) || Markup.Name(id).Alias != target.Id) ||
                    target.RequiredBindings.Distinct(StringComparer.Ordinal).Count() != target.RequiredBindings.Count)
                    throw new InputException(ErrorCode.InvalidAnnotation, "TLA+ targets require explicit unique required_bindings IDs.");
            }
            else if (target.RequiredBindings != null)
                throw new InputException(ErrorCode.InvalidAnnotation, "Dafny/P select binding IDs in their native configuration, not twice in the target.");
        }
        foreach (OmissionRule rule in config.Omissions)
        {
            if (string.IsNullOrWhiteSpace(rule.Reason) || rule.Targets.Count is 0 or > 1000 || rule.Calls.Count is 0 or > 1000 ||
                rule.Assumptions.Count is 0 or > 1000 || rule.Assumptions.Any(string.IsNullOrWhiteSpace) ||
                rule.Calls.Any(c => string.IsNullOrWhiteSpace(c) || !c.StartsWith("M:", StringComparison.Ordinal)) ||
                rule.Targets.Distinct(StringComparer.Ordinal).Count() != rule.Targets.Count ||
                rule.Calls.Distinct(StringComparer.Ordinal).Count() != rule.Calls.Count ||
                rule.Targets.Any(t => !targets.Any(d => d.Id == t)))
                throw new InputException(ErrorCode.InvalidAnnotation, "Omissions require exact method declaration IDs, explicit unique targets, a reason and boundary assumptions.");
        }
        config = config with { Targets = targets };
        MethodSummaries.Validate(config);
        return config;
    }

    public static FormalTarget Target(CorrespondenceConfiguration config, string alias, string language) =>
        config.Targets.SingleOrDefault(t => t.Id == alias && t.Language == language)
        ?? throw new InputException(ErrorCode.InvalidAnnotation, $"Expected an explicit {language} target alias: {alias}");
}

public sealed partial class CSharpScope
{
    internal void AttachFormal(FormalLinkDeclaration declaration)
    {
        foreach (ExcludeAnnotation exclusion in declaration.Exclusions ?? [])
        {
            if (exclusions.Any(e => e.Declaration.Id == exclusion.Id && e.Declaration.From.SequenceEqual(exclusion.From)))
                continue;
            SyntaxNode excluded = SourcePreparation.CSharpTarget(Compilation, null, exclusion.Source!);
            string excludedPath = excluded.SyntaxTree.FilePath;
            exclusions.Add(new(exclusion, excluded.SpanStart, excluded.Span.End, excluded,
                Compilation.GetSemanticModel(excluded.SyntaxTree), excludedPath, Texts[excludedPath]));
        }
        List<SourceBinding> existing = Bindings.Where(b => b.BindingId == declaration.Binding.Id).ToList();
        if (existing.Count > 0)
        {
            if (existing.Any(b => b.Declaration != declaration.Binding))
                throw new InputException(ErrorCode.DuplicateBinding, "A connection has competing source/formal owners: " + declaration.Binding.Id);
            return;
        }
        SyntaxNode node = SourcePreparation.CSharpTarget(Compilation, null, declaration.Binding.Source!);
        string path = node.SyntaxTree.FilePath, text = Texts[path];
        List<(LocatedAnnotation Annotation, SyntaxNode Node)> annotations = [
            (new(declaration.Binding, node.SpanStart, node.Span.End), node)
        ];
        annotations.AddRange((declaration.Captures ?? []).Select(c =>
            (new LocatedAnnotation(c, node.SpanStart, node.Span.End), node)));
        AttachCorrespondence(annotations, Compilation.GetSemanticModel(node.SyntaxTree), path, text);
    }

    private readonly List<SourceExclusion> exclusions = [];
    private readonly HashSet<Issue> correspondenceDiagnostics = new(ReferenceEqualityComparer.Instance);
    internal IReadOnlyList<SourceExclusion> Exclusions => exclusions;
    public List<SourceDisposition> Dispositions { get; } = [];
    public List<OmissionRuleMatch> OmissionMatches { get; } = [];

    private void AttachCorrespondence(List<(LocatedAnnotation Annotation, SyntaxNode Node)> attached, SemanticModel semantics,
        string path, string text)
    {
        foreach (var duplicate in attached.Where(e => e.Annotation.Declaration is BindAnnotation)
            .GroupBy(e => e.Annotation.Declaration.Identity, StringComparer.Ordinal).Where(g => g.Count() > 1))
            Diagnostics.Add(new(ErrorCode.DuplicateBinding, "Duplicate inline binding ID: " + duplicate.Key));
        foreach (var entry in attached.Where(e => e.Annotation.Declaration is ExcludeAnnotation))
            exclusions.Add(new((ExcludeAnnotation)entry.Annotation.Declaration, entry.Annotation.Start, entry.Annotation.End,
                entry.Node, semantics, path, text));
        foreach (var entry in attached.Where(e => e.Annotation.Declaration is BindAnnotation))
        {
            BindAnnotation declaration = (BindAnnotation)entry.Annotation.Declaration;
            SyntaxNode symbolNode = entry.Node is FieldDeclarationSyntax field ? field.Declaration.Variables[0] : entry.Node;
            ISymbol? symbol = semantics.GetDeclaredSymbol(symbolNode);
            SourceBindingKind kind = entry.Node is FieldDeclarationSyntax ? SourceBindingKind.State :
                entry.Node is StatementSyntax or InvocationExpressionSyntax ? SourceBindingKind.Step : SourceBindingKind.Declaration;
            List<CaptureAnnotation> captures = attached.Where(e => e.Annotation.Declaration is CaptureAnnotation c &&
                c.Binding == declaration.Id && e.Node == entry.Node)
                .Select(e => (CaptureAnnotation)e.Annotation.Declaration).ToList();
            if (captures.Count == 0)
                Add(null);
            else
            {
                if (captures.GroupBy(c => c.Point).Any(g => g.Count() != 1))
                    Diagnostics.Add(new(ErrorCode.DuplicateBinding, "Duplicate capture point: " + declaration.Id,
                        Files.At(path, text, entry.Annotation.Start, entry.Annotation.End)));
                foreach (CaptureAnnotation capture in captures)
                {
                    if (kind == SourceBindingKind.Declaration || kind == SourceBindingKind.State && capture.Point != null ||
                        kind == SourceBindingKind.Step && (entry.Node is InvocationExpressionSyntax || capture.Point == null ||
                            capture.Classification == null || capture.Actor == null || capture.Operation == null || capture.Type != null || capture.Units != null) ||
                        kind == SourceBindingKind.State && (capture.Type == null || capture.Guard == null ||
                            capture.Actor != null || capture.Operation != null || capture.Arguments != null || capture.Observe != null ||
                            capture.Classification != null || capture.Connection != null || capture.Entity != null || capture.Hook != null))
                        Diagnostics.Add(new(ErrorCode.InvalidAttachment, "Unsupported or incomplete capture: " + declaration.Id,
                            Files.At(path, text, entry.Annotation.Start, entry.Annotation.End)));
                    Add(capture, captures.Count > 1 ? declaration.Id + "." + capture.Point?.ToString().ToLowerInvariant() : declaration.Id);
                }
            }
            void Add(CaptureAnnotation? capture, string? identity = null)
            {
                Bindings.Add(new(declaration, capture, kind, entry.Annotation.Start, entry.Annotation.End,
                    identity ?? declaration.Id, entry.Node, symbol, semantics, path, text));
            }
        }
        foreach (var entry in attached.Where(e => e.Annotation.Declaration is CaptureAnnotation))
            if (!attached.Any(e => e.Annotation.Declaration is BindAnnotation b &&
                b.Id == entry.Annotation.Declaration.Identity && e.Node == entry.Node))
                Diagnostics.Add(new(ErrorCode.InvalidAnnotation, "capture must reference a binding at the same source endpoint.",
                    Files.At(path, text, entry.Annotation.Start, entry.Annotation.End)));
    }

    private void ValidateDispositionIds()
    {
        var declarations = Bindings.Select(b => (b.BindingId, b.Path, b.Tag.Start))
            .Distinct().Concat(exclusions.Select(e => (e.Declaration.Id, e.Path, e.Start)));
        foreach (var duplicate in declarations.GroupBy(e => e.Item1, StringComparer.Ordinal).Where(g => g.Count() > 1))
            Diagnostics.Add(new(ErrorCode.DuplicateBinding, "Duplicate correspondence disposition ID: " + duplicate.Key));
    }

    public void ApplyCorrespondence(CorrespondenceConfiguration config)
    {
        Diagnostics.RemoveAll(correspondenceDiagnostics.Contains);
        correspondenceDiagnostics.Clear();
        Dispositions.Clear();
        OmissionMatches.Clear();
        int initialDiagnostics = Diagnostics.Count;
        HashSet<string> aliases = config.Targets.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, HashSet<ISymbol>> ruleMethods = new(StringComparer.Ordinal);
        foreach (SourceBinding binding in Bindings)
        {
            if (!aliases.Contains(binding.Model))
                Diagnostics.Add(new(ErrorCode.MissingModelEndpoint, "Undeclared formal target alias: " + binding.Model, binding.Endpoint.Location));
            if (binding.Tag.Options.Count > 0 && config.Targets.Any(t => t.Id == binding.Model && t.Language != "tla") &&
                (binding.Tag.Options.ContainsKey("point") || binding.Tag.Options.ContainsKey("type")))
                Diagnostics.Add(new(ErrorCode.InvalidInstrumentation, "No capture adapter for target: " + binding.Model, binding.Endpoint.Location));
        }
        foreach (OmissionRule rule in config.Omissions)
        {
            if (Bindings.Any(b => b.BindingId == rule.Id) || exclusions.Any(e => e.Declaration.Id == rule.Id))
                Diagnostics.Add(new(ErrorCode.DuplicateBinding, "Omission rule conflicts with disposition ID: " + rule.Id));
            HashSet<ISymbol> methods = new(SymbolEqualityComparer.Default);
            bool valid = true;
            foreach (string call in rule.Calls)
            {
                ISymbol[] matches = DocumentationCommentId.GetSymbolsForDeclarationId(call, Compilation)
                    .Distinct(SymbolEqualityComparer.Default).ToArray();
                if (matches.Length != 1 || matches[0] is not IMethodSymbol method)
                {
                    Diagnostics.Add(new(ErrorCode.InvalidAnnotation,
                        "Omission method must resolve uniquely in the explicit compiler context: " + call));
                    valid = false;
                }
                else methods.Add(method.OriginalDefinition);
            }
            if (!valid) methods.Clear();
            ruleMethods[rule.Id] = methods;
        }
        foreach (SourceExclusion exclusion in exclusions)
        {
            foreach (string alias in exclusion.Declaration.From)
            {
                if (!aliases.Contains(alias))
                    Diagnostics.Add(new(ErrorCode.MissingModelEndpoint, "Undeclared exclusion target: " + alias));
                if (Bindings.Any(b => b.Model == alias && b.Path == exclusion.Path && exclusion.Node.Span.Contains(b.Node.Span)) ||
                    exclusions.Any(e => e != exclusion && e.Path == exclusion.Path &&
                        (exclusion.Node.Span.Contains(e.Node.Span) || e.Node.Span.Contains(exclusion.Node.Span)) &&
                        e.Declaration.From.Contains(alias, StringComparer.Ordinal)))
                    Diagnostics.Add(new(ErrorCode.InvalidAnnotation, "Conflicting binding or overlapping exclusion: " + exclusion.Declaration.Id));
                Dispositions.Add(new(exclusion.Declaration.Id, alias, "excluded", exclusion.Declaration.Reason,
                    Files.At(exclusion.Path, exclusion.Text, exclusion.Node.SpanStart, exclusion.Node.Span.End), [],
                    RegionBoundaries(exclusion.Node, exclusion.Path, exclusion.Text)));
                CheckOmittedEffects(exclusion.Node, exclusion.Semantics, exclusion.Path, exclusion.Text, alias);
            }
        }
        foreach (SyntaxTree tree in Compilation.SyntaxTrees)
        {
            SemanticModel semantics = Compilation.GetSemanticModel(tree);
            string text = Texts[tree.FilePath];
            foreach (ExpressionStatementSyntax statement in tree.GetRoot().DescendantNodes().OfType<ExpressionStatementSyntax>())
            {
                if (statement.Expression is not InvocationExpressionSyntax invocation ||
                    semantics.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method ||
                    !method.ReturnsVoid || method.IsAsync) continue;
                string? id = method.OriginalDefinition.GetDocumentationCommentId();
                foreach (string alias in aliases)
                {
                    List<OmissionRule> rules = config.Omissions.Where(r => r.Targets.Contains(alias, StringComparer.Ordinal) &&
                        ruleMethods[r.Id].Contains(method.OriginalDefinition)).ToList();
                    if (rules.Count == 0 || exclusions.Any(e => e.Path == tree.FilePath &&
                        e.Node.Span.Contains(statement.Span) && e.Declaration.From.Contains(alias, StringComparer.Ordinal))) continue;
                    if (rules.Count != 1 || Bindings.Any(b => b.Model == alias && b.Path == tree.FilePath && b.Node.Span.Contains(invocation.Span)))
                        Diagnostics.Add(new(ErrorCode.InvalidAnnotation, "Overlapping omission rules or binding at: " + id,
                            Files.At(tree.FilePath, text, statement.SpanStart, statement.Span.End)));
                    foreach (OmissionRule rule in rules)
                        Dispositions.Add(new(rule.Id, alias, "rule-omitted", rule.Reason,
                            Files.At(tree.FilePath, text, statement.SpanStart, statement.Span.End), rule.Assumptions,
                            RegionBoundaries(statement, tree.FilePath, text)));
                    CheckOmittedEffects(statement, semantics, tree.FilePath, text, alias);
                }
            }
            foreach (OmissionRule rule in config.Omissions)
                foreach (string alias in rule.Targets)
                    OmissionMatches.Add(new(rule.Id, alias,
                        Dispositions.Count(d => d.Id == rule.Id && d.Target == alias && d.Status == "rule-omitted")));
        }
        foreach (Issue issue in Diagnostics.Skip(initialDiagnostics)) correspondenceDiagnostics.Add(issue);
    }

    private void CheckOmittedEffects(SyntaxNode node, SemanticModel semantics, string path, string text, string alias)
    {
        HashSet<ISymbol> states = new(SymbolEqualityComparer.Default);
        foreach (SourceBinding state in Bindings.Where(b => b.Model == alias && b.Tag.Kind == "state" && b.Symbol != null))
            states.Add(state.Symbol!.OriginalDefinition);
        Queue<(SyntaxNode Node, SemanticModel Semantics)> pending = new();
        pending.Enqueue((node, semantics));
        HashSet<ISymbol> visited = new(SymbolEqualityComparer.Default);
        while (pending.TryDequeue(out var region))
        {
            string regionPath = region.Node.SyntaxTree.FilePath, regionText = Texts[regionPath];
            foreach (IdentifierNameSyntax identifier in region.Node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                ISymbol? symbol = region.Semantics.GetSymbolInfo(identifier).Symbol;
                SyntaxNode expression = identifier.Parent is MemberAccessExpressionSyntax member && member.Name == identifier ? member : identifier;
                if (symbol != null && states.Contains(symbol.OriginalDefinition) && IsWrite(expression))
                    Diagnostics.Add(new(ErrorCode.OmissionBoundaryConflict, "Omitted region/callee may write or escape modeled state: " + symbol.Name,
                        Files.At(regionPath, regionText, identifier.SpanStart, identifier.Span.End)));
                if (symbol is IPropertySymbol)
                    Diagnostics.Add(new(ErrorCode.OmissionBoundaryIncomplete, "Omitted expression retains property-access effects.",
                        Files.At(regionPath, regionText, identifier.SpanStart, identifier.Span.End)));
            }
            foreach (InvocationExpressionSyntax invocation in region.Node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
            {
                if (region.Semantics.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method ||
                    method.DeclaringSyntaxReferences.Length == 0 || method.IsVirtual || method.ContainingType.TypeKind == TypeKind.Interface)
                {
                    Diagnostics.Add(new(ErrorCode.OmissionBoundaryIncomplete, "Omitted region retains unresolved callee/dispatch effects and boundary assumptions.",
                        Files.At(regionPath, regionText, invocation.SpanStart, invocation.Span.End)));
                    continue;
                }
                if (!visited.Add(method.OriginalDefinition)) continue;
                if (visited.Count > 1000)
                {
                    Diagnostics.Add(new(ErrorCode.OmissionBoundaryIncomplete, "Omitted-call closure exceeds the explicit 1000-method inspection limit.",
                        Files.At(path, text, node.SpanStart, node.Span.End)));
                    return;
                }
                foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
                {
                    SyntaxNode declaration = reference.GetSyntax();
                    pending.Enqueue((declaration, Compilation.GetSemanticModel(declaration.SyntaxTree)));
                }
            }
        }
    }

    private static List<Boundary> RegionBoundaries(SyntaxNode node, string path, string text) =>
        node.DescendantNodesAndSelf().Where(n => n is InvocationExpressionSyntax or AwaitExpressionSyntax or
            AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or UsingStatementSyntax or ThrowStatementSyntax)
        .Select(n => new Boundary(n.Kind().ToString(), n.ToString(), Files.At(path, text, n.SpanStart, n.Span.End))).ToList();
}
