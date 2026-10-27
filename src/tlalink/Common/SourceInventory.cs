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
        SourceInventoryResult inventory;
        if (options.Optional("regression-result") is string result)
        {
            if (new[] { "reference", "define", "nullable", "framework-references" }.Any(options.Has))
                throw new InputException(ErrorCode.UnresolvedCompilation,
                    "--regression-result supplies the observed compiler context; do not override its references, defines or settings.");
            inventory = FromRegression(paths, FilePaths.Full(result), config);
        }
        else inventory = FromOptions(paths, options, config);
        foreach (var pin in scopePins) Files.Pin(inventory.Fingerprints, pin.Key, pin.Value);
        if (options.Optional("output") is string output) Files.WriteNew(FilePaths.Full(output), inventory);
        ProjectWorkflow.Render(inventory, options);
        return inventory.Status == "ready" ? 0 : 2;
    }

    private static SourceInventoryResult FromOptions(List<string> paths, CliOptions options, CorrespondenceConfiguration? config)
    {
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
        return Build(paths, options.Many("reference"),
            options.Has("define") ? options.Many("define") : null, nullable, framework, config);
    }

    internal static SourceInventoryResult FromRegression(List<string> paths, string resultPath, CorrespondenceConfiguration? config = null)
    {
        Dictionary<string, string> reading = new(FilePaths.Comparer);
        Files.ReadPinned(resultPath, reading);
        JsonElement document = ProjectInputs.Read<JsonElement>(resultPath);
        Files.RequireSchema(document, "csharp-regression-result");
        string directory = Path.GetDirectoryName(resultPath)!;
        List<Issue> issues = StageEvidence.Validate(document, directory, false);
        if (issues.Count == 0) issues.AddRange(CSharpRegression.Validate(document, directory, false));
        if (issues.Count > 0)
            throw new InputException(ErrorCode.UnresolvedCompilation, "Recorded compiler context is inconsistent: " +
                string.Join("; ", issues.Select(i => i.Message)));
        CSharpRegressionResult result = document.Deserialize<CSharpRegressionResult>(ProjectInputs.Json)!;
        var context = result.Build.CompilerContext;
        if (context.GetValueOrDefault("LangVersion") != "14" || context.GetValueOrDefault("AllowUnsafeBlocks") != "true" ||
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
        SourceInventoryResult inventory = Build(paths, references.Keys,
            context["Defines"].Split(',', StringSplitOptions.RemoveEmptyEntries),
            context["Nullable"] == "enable" ? NullableContextOptions.Enable : NullableContextOptions.Disable, false, config);
        foreach (var pin in selected)
            if (!inventory.Fingerprints.TryGetValue(pin.Key, out string? hash) || hash != pin.Value)
                throw new InputException(ErrorCode.UnresolvedCompilation, "Compiler input changed during inventory: " + pin.Key);
        IntegratedInputs.CheckPins(selected);
        IntegratedInputs.CheckPins(reading);
        foreach (var pin in reading) Files.Pin(inventory.Fingerprints, pin.Key, pin.Value);
        return inventory with { Compiler = inventory.Compiler with {
            RegressionResult = resultPath, TargetFramework = context.GetValueOrDefault("TargetFramework")
        } };
    }

    internal static SourceInventoryResult Build(List<string> paths, IEnumerable<string>? references = null,
        IEnumerable<string>? defines = null, NullableContextOptions nullable = NullableContextOptions.Enable,
        bool includeFrameworkReferences = true, CorrespondenceConfiguration? config = null)
    {
        CSharpScope scope = CSharpScope.Parse(paths, references, defines, nullable, includeFrameworkReferences);
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
            string path = tree.FilePath, text = scope.Texts[path];
            SemanticModel semantics = scope.Compilation.GetSemanticModel(tree);
            Place At(SyntaxNode node) => Files.At(path, text, node.SpanStart, node.Span.End);
            foreach (SyntaxNode declaration in tree.GetRoot().DescendantNodes().Where(n =>
                n is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax ||
                n is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax }))
            {
                ISymbol? symbol = semantics.GetDeclaredSymbol(declaration);
                if (symbol == null) continue;
                List<SyntaxNode> nodes = declaration.DescendantNodes().ToList();
                List<SourceCall> calls = [];
                foreach (SyntaxNode node in nodes.Where(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax))
                {
                    if (semantics.GetOperation(node) is Microsoft.CodeAnalysis.Operations.INameOfOperation) continue;
                    IMethodSymbol? target = semantics.GetSymbolInfo(node).Symbol as IMethodSymbol;
                    string dispatch = target == null ? "unresolved" : target.MethodKind == MethodKind.DelegateInvoke ? "delegate" :
                        target.ContainingType.TypeKind == TypeKind.Interface ? "interface" :
                        target.IsVirtual || target.IsAbstract || target.IsOverride ? "virtual" : "direct";
                    bool nested = node.Ancestors().TakeWhile(n => n != declaration)
                        .Any(n => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
                    calls.Add(new(target == null ? null : Symbol(target), dispatch, At(node), nested));
                    if (target == null)
                        diagnostics.Add(new(ErrorCode.UnresolvedCompilation, "No single statically resolved target for explicit call.", At(node)));
                }
                Place region = At(declaration);
                List<SourceLock> locks = nodes.OfType<LockStatementSyntax>().Select(n =>
                    new SourceLock(n.Expression.ToString(), semantics.GetSymbolInfo(n.Expression).Symbol is ISymbol guard
                        ? CSharpScope.SymbolId(guard) : null, At(n))).ToList();
                List<Place> awaits = declaration.DescendantTokens().Where(t => t.IsKind(SyntaxKind.AwaitKeyword))
                    .Select(t => Files.At(path, text, t.SpanStart, t.Span.End)).ToList();
                members.Add(new(Symbol(symbol), symbol.Kind.ToString(), region, calls, locks, awaits,
                    nodes.Where(n => n is ThrowStatementSyntax or ThrowExpressionSyntax).Select(At).ToList(),
                    nodes.Where(n => n is UsingStatementSyntax ||
                        n is LocalDeclarationStatementSyntax local && !local.UsingKeyword.IsKind(SyntaxKind.None)).Select(At).ToList(),
                    scope.Accesses.Where(a => FilePaths.Comparer.Equals(a.Location.Path, path) &&
                        a.Location.Line >= region.Line && a.Location.EndLine <= region.EndLine).ToList(),
                    SourceControlFlows.Build(declaration, semantics, At)));
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
            ], new("14", nullable.ToString().ToLowerInvariant(),
                includeFrameworkReferences ? "implicit" : "explicit", scope.Compilation.Options.AllowUnsafe,
                scope.Compilation.Options.CheckOverflow,
                scope.Fingerprints["compiler:defines"].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()), paths.ToList())
            { Dispositions = scope.Dispositions, OmissionMatches = scope.OmissionMatches, Bindings = scope.DeclaredLinks };
    }
}
