using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Text.Json;

namespace TlaLink;

public sealed record SourceCall(string? Target, string Dispatch, Place Location, bool InNestedFunction);
public sealed record SourceLock(string Expression, string? Symbol, Place Location);
public sealed record SourceMember(string Symbol, string Kind, Place Location, List<SourceCall> Calls,
    List<SourceLock> Locks, List<Place> Awaits, List<Place> Throws, List<Place> Using, List<Access> FieldAccesses,
    SourceControlFlow? Flow = null);
public sealed record SourceCompilerContext(string LanguageVersion, string Nullable, string FrameworkReferences,
    bool AllowUnsafe, bool Checked, List<string> Defines, string? RegressionResult = null, string? TargetFramework = null);
internal sealed record SourceInventoryContext(CSharpScope Scope, SourceCompilerContext Compiler);
public sealed record SourceInventoryResult(int SchemaVersion, string Kind, string Status, int AnnotationVersion,
    int SourceCount, int MemberCount, int CallCount, int AwaitCount, List<SourceMember> Members,
    Dictionary<string, string> Fingerprints, List<Issue> Diagnostics, List<string> Boundaries,
    SourceCompilerContext Compiler, List<string> Sources)
{
    public List<SourceDisposition> Dispositions { get; init; } = [];
    public List<OmissionRuleMatch> OmissionMatches { get; init; } = [];
    public List<SourceLinkDeclaration> Bindings { get; init; } = [];
}

public static class SourceInventory
{
    private static string Symbol(ISymbol symbol) => symbol.GetDocumentationCommentId() ??
        symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    public static int Run(CliOptions options)
    {
        List<string> paths = Files.Discover(options.Many("source"), ".cs");
        Dictionary<string, string> scopePins = new(FilePaths.Comparer);
        CorrespondenceConfiguration? config = options.Optional("scope") is string scopePath
            ? CorrespondenceInputs.Load(scopePath, scopePins) : null;
        SourceInventoryContext context = LoadContext(paths, options);
        SourceInventoryResult inventory = Build(context.Scope, context.Compiler, paths, config);
        foreach (var pin in scopePins) Files.Pin(inventory.Fingerprints, pin.Key, pin.Value);
        if (options.Optional("output") is string output) Files.WriteNew(FilePaths.Full(output), inventory);
        ProjectWorkflow.Render(inventory, options);
        return inventory.Status == "ready" ? 0 : 2;
    }

    internal static SourceInventoryContext LoadContext(List<string> paths, CliOptions options, bool annotations = true)
    {
        if (options.Optional("regression-result") is string result)
        {
            if (new[] { "reference", "define", "nullable", "framework-references", "language-version" }.Any(options.Has))
                throw new InputException(ErrorCode.UnresolvedCompilation,
                    "--regression-result supplies the observed compiler context; do not override its references, defines or settings.");
            return RegressionContext(paths, FilePaths.Full(result), annotations);
        }
        NullableContextOptions nullable = options.Get("nullable", "enable") switch {
            "enable" => NullableContextOptions.Enable, "disable" => NullableContextOptions.Disable,
            _ => throw new InputException(ErrorCode.UnresolvedCompilation, "--nullable must be enable or disable.")
        };
        bool framework = options.Get("framework-references", "implicit") switch {
            "implicit" => true, "explicit" => false,
            _ => throw new InputException(ErrorCode.UnresolvedCompilation, "--framework-references must be implicit or explicit.")
        };
        if (!framework && options.Many("reference").Count == 0)
            throw new InputException(ErrorCode.UnresolvedCompilation, "Explicit framework references require --reference paths.");
        string language = options.Get("language-version", CSharpLanguages.Default);
        CSharpScope scope = CSharpScope.Parse(paths, options.Many("reference"),
            options.Has("define") ? options.Many("define") : null, nullable, framework, language, annotations);
        return new(scope, CompilerContext(scope, language, nullable, framework));
    }

    internal static SourceInventoryResult FromRegression(List<string> paths, string resultPath, CorrespondenceConfiguration? config = null)
    {
        SourceInventoryContext context = RegressionContext(paths, resultPath, annotations: true);
        return Build(context.Scope, context.Compiler, paths, config);
    }

    private static SourceInventoryContext RegressionContext(List<string> paths, string resultPath, bool annotations)
    {
        Dictionary<string, string> reading = new(FilePaths.Comparer);
        Files.ReadPinned(resultPath, reading);
        CSharpRegressionResult result = ProjectInputs.Read<CSharpRegressionResult>(resultPath);
        if (result.SchemaVersion != Schema.Version || result.Kind != "csharp-regression-result")
            throw new InputException(ErrorCode.UnresolvedCompilation, "Expected current csharp-regression-result.");
        string directory = Path.GetDirectoryName(resultPath)!;
        List<Issue> issues = StageEvidence.Validate(result, directory, false);
        if (issues.Count == 0) issues.AddRange(CSharpRegression.Validate(result, directory, false));
        if (issues.Count > 0)
            throw new InputException(ErrorCode.UnresolvedCompilation, "Recorded compiler context is inconsistent: " +
                string.Join("; ", issues.Select(i => i.Message)));
        var context = result.Build.CompilerContext;
        if (!CSharpLanguages.Observed(context.GetValueOrDefault("LangVersion"), out string languageVersion) ||
            context.GetValueOrDefault("AllowUnsafeBlocks") != "true" ||
            context.GetValueOrDefault("CheckForOverflowUnderflow") != "false" ||
            context.GetValueOrDefault("Nullable") is not ("enable" or "disable") || result.Build.References.Count == 0)
            throw new InputException(ErrorCode.UnresolvedCompilation, "Recorded compiler settings are outside the supported inventory context.");
        Dictionary<string, string> sources = StageEvidence.PathPins(result.Build.CompileInputs);
        Dictionary<string, string> references = StageEvidence.PathPins(result.Build.References);
        Dictionary<string, string> selected = new(FilePaths.Comparer);
        foreach (string source in paths)
        {
            if (!sources.TryGetValue(source, out string? hash))
                throw new InputException(ErrorCode.UnresolvedCompilation, "Selected source was not an observed compiler input: " + source);
            Files.Pin(selected, source, hash);
        }
        foreach (var pin in references) Files.Pin(selected, pin.Key, pin.Value);
        IntegratedInputs.CheckPins(selected);
        NullableContextOptions nullable = context["Nullable"] == "enable" ? NullableContextOptions.Enable : NullableContextOptions.Disable;
        CSharpScope scope = CSharpScope.Parse(paths, references.Keys,
            context["Defines"].Split(',', StringSplitOptions.RemoveEmptyEntries),
            nullable, false, languageVersion, annotations);
        foreach (var pin in selected)
            if (!scope.Fingerprints.TryGetValue(pin.Key, out string? hash) || hash != pin.Value)
                throw new InputException(ErrorCode.UnresolvedCompilation, "Compiler input changed during inventory: " + pin.Key);
        IntegratedInputs.CheckPins(selected);
        IntegratedInputs.CheckPins(reading);
        foreach (var pin in reading) Files.Pin(scope.Fingerprints, pin.Key, pin.Value);
        return new(scope, CompilerContext(scope, languageVersion, nullable, false) with {
            RegressionResult = resultPath, TargetFramework = context.GetValueOrDefault("TargetFramework")
        });
    }

    internal static SourceInventoryResult Build(List<string> paths, IEnumerable<string>? references = null,
        IEnumerable<string>? defines = null, NullableContextOptions nullable = NullableContextOptions.Enable,
        bool includeFrameworkReferences = true, CorrespondenceConfiguration? config = null,
        string languageVersion = CSharpLanguages.Default)
    {
        CSharpScope scope = CSharpScope.Parse(paths, references, defines, nullable, includeFrameworkReferences, languageVersion);
        return Build(scope, CompilerContext(scope, languageVersion, nullable, includeFrameworkReferences), paths, config);
    }

    private static SourceCompilerContext CompilerContext(CSharpScope scope, string language,
        NullableContextOptions nullable, bool framework) =>
        new(language, nullable.ToString().ToLowerInvariant(), framework ? "implicit" : "explicit",
            scope.Compilation.Options.AllowUnsafe, scope.Compilation.Options.CheckOverflow,
            scope.Fingerprints["compiler:defines"].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList());

    private static SourceInventoryResult Build(CSharpScope scope, SourceCompilerContext compiler,
        List<string> paths, CorrespondenceConfiguration? config)
    {
        if (config != null) scope.ApplyCorrespondence(config);
        List<Issue> diagnostics = [.. scope.Diagnostics];
        List<SourceMember> members = [];
        foreach (Diagnostic error in scope.Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            FileLinePositionSpan location = error.Location.GetLineSpan();
            diagnostics.Add(new(ErrorCode.UnresolvedCompilation.Id(), "error", error.ToString(),
                error.Location.IsInSource ? new(location.Path, location.StartLinePosition.Line + 1,
                    location.EndLinePosition.Line + 1, location.StartLinePosition.Character + 1) : null));
        }
        foreach (SyntaxTree tree in scope.Compilation.SyntaxTrees)
        {
            SemanticModel semantics = scope.Compilation.GetSemanticModel(tree);
            foreach (SyntaxNode declaration in tree.GetRoot().DescendantNodes().Where(n =>
                n is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax ||
                n is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax }))
            {
                ISymbol? symbol = semantics.GetDeclaredSymbol(declaration);
                if (symbol == null) continue;
                members.Add(ReadMember(scope, declaration, symbol, diagnostics));
            }
        }
        if (scope.Compilation.SyntaxTrees.Any(t => t.GetRoot().DescendantNodes().Any(n => n is GlobalStatementSyntax)))
            diagnostics.Add(new(ErrorCode.UnresolvedCompilation, "Top-level statements need an explicit member-based source scope for this inventory."));
        Program.PinTool(scope.Fingerprints);
        bool ready = diagnostics.All(d => !d.Is(ErrorCode.UnresolvedCompilation) && d.Severity != "error");
        return new(Schema.Version, "source-inventory", ready ? "ready" : "incomplete", Markup.Version,
            scope.Texts.Count, members.Count, members.Sum(m => m.Calls.Count), members.Sum(m => m.Awaits.Count),
            members, scope.Fingerprints, diagnostics, [
                "Resolved C# source facts under explicit sources/references/defines, not model coverage, atomicity or refinement.",
                "Calls identify declared targets, not runtime dispatch targets. Nested-function calls are marked separately.",
                "Field accesses are candidate accesses within each member's source lines. Properties are not inlined.",
                "Field initializers are inventoried separately. Implicit compiler/runtime effects, using-disposal calls and generated members are not enumerated.",
                "An inventory is not validated code or an acceptance receipt."
            ], compiler, paths.ToList())
            { Dispositions = scope.Dispositions, OmissionMatches = scope.OmissionMatches, Bindings = scope.DeclaredLinks };
    }

    internal static SourceMember ReadMember(CSharpScope scope, SyntaxNode declaration, ISymbol symbol,
        List<Issue> diagnostics, bool flow = true)
    {
        string path = declaration.SyntaxTree.FilePath, text = scope.Texts[path];
        SemanticModel semantics = scope.Compilation.GetSemanticModel(declaration.SyntaxTree);
        Place At(SyntaxNode node) => Files.At(path, text, node.SpanStart, node.Span.End);
        List<SyntaxNode> nodes = declaration.DescendantNodes().ToList();
        List<SourceCall> calls = [];
        foreach (SyntaxNode node in SourceQueries.InvocationNodes(declaration))
        {
            if (semantics.GetOperation(node) is Microsoft.CodeAnalysis.Operations.INameOfOperation) continue;
            IMethodSymbol? target = semantics.GetSymbolInfo(node).Symbol as IMethodSymbol;
            calls.Add(new(target == null ? null : Symbol(target), SourceQueries.Dispatch(target), At(node),
                SourceQueries.Nested(node, declaration)));
            if (target == null)
                diagnostics.Add(new(ErrorCode.UnresolvedCompilation, "No single statically resolved target for explicit call.", At(node)));
        }
        Place region = At(declaration);
        List<SourceLock> locks = nodes.OfType<LockStatementSyntax>().Select(n =>
            new SourceLock(n.Expression.ToString(), semantics.GetSymbolInfo(n.Expression).Symbol is ISymbol guard
                ? CSharpScope.SymbolId(guard) : null, At(n))).ToList();
        List<Place> awaits = declaration.DescendantTokens().Where(t => t.IsKind(SyntaxKind.AwaitKeyword))
            .Select(t => Files.At(path, text, t.SpanStart, t.Span.End)).ToList();
        return new(Symbol(symbol), symbol.Kind.ToString(), region, calls, locks, awaits,
            nodes.Where(n => n is ThrowStatementSyntax or ThrowExpressionSyntax).Select(At).ToList(),
            nodes.Where(n => n is UsingStatementSyntax ||
                n is LocalDeclarationStatementSyntax local && !local.UsingKeyword.IsKind(SyntaxKind.None)).Select(At).ToList(),
            scope.Accesses.Where(a => FilePaths.Comparer.Equals(a.Location.Path, path) &&
                declaration.Span.Contains(declaration.SyntaxTree.GetText().Lines.GetPosition(
                    new Microsoft.CodeAnalysis.Text.LinePosition(a.Location.Line - 1, a.Location.Column - 1)))).ToList(),
            flow ? SourceControlFlows.Build(declaration, semantics, At) : null);
    }
}
