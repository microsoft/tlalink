using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TlaLink;

public sealed record SourceReferenceSite(string? Member, string Kind, string Resolution,
    Place Location, string Expression, bool InNestedFunction);
public sealed record SourceCallSite(string? Caller, string? Target, string? ConstructedTarget,
    string Dispatch, Place Location, string Expression, bool InNestedFunction);
public sealed record SourceMemberDescription(string Source, SourceMember Facts);
public sealed record SourceQueryDetails(List<SourceReferenceSite>? References,
    List<SourceCallSite>? Calls, List<SourceMemberDescription>? Members);

internal static class SourceQueries
{
    internal static ISymbol Definition(ISymbol symbol)
    {
        if (symbol is IAliasSymbol alias) symbol = alias.Target;
        if (symbol is IMethodSymbol method)
            symbol = (method.ReducedFrom ?? method).PartialDefinitionPart ?? method.ReducedFrom ?? method;
        return symbol.OriginalDefinition;
    }

    internal static IEnumerable<SyntaxNode> InvocationNodes(SyntaxNode root) =>
        root.DescendantNodesAndSelf().Where(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or
            ImplicitObjectCreationExpressionSyntax or ConstructorInitializerSyntax);

    internal static string Dispatch(IMethodSymbol? target) => target == null ? "unresolved" :
        target.MethodKind == MethodKind.DelegateInvoke ? "delegate" :
        target.MethodKind == MethodKind.LocalFunction ? "local-function" :
        target.ContainingType.TypeKind == TypeKind.Interface ? "interface" :
        target.IsVirtual || target.IsAbstract || target.IsOverride ? "virtual" : "direct";

    internal static bool Nested(SyntaxNode node, SyntaxNode? owner) =>
        node.Ancestors().TakeWhile(n => n != owner).Any(n => n is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);

    internal static SourceCallSite CallSite(string text, SyntaxNode node, string? caller, SyntaxNode? owner,
        IMethodSymbol? called)
    {
        string dispatch = node is ConstructorInitializerSyntax initializer && initializer.IsKind(SyntaxKind.BaseConstructorInitializer) ||
            node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } }
            ? "base" : Dispatch(called);
        return new(caller, called == null ? null : Definition(called).GetDocumentationCommentId(),
            called?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), dispatch,
            Files.At(node.SyntaxTree.FilePath, text, node.SpanStart, node.Span.End),
            node.ToString(), Nested(node, owner));
    }

    internal static SourceQueryDetails Read(CSharpScope scope, List<SourceSymbols.Declaration> declarations,
        ISymbol target, string action, List<Issue> diagnostics)
    {
        if (action == "callers" && (target is not IMethodSymbol method ||
            method.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion or MethodKind.StaticConstructor))
            throw Invalid("callers requires an ordinary method or instance constructor; implicit/operator calls are not enumerated.");
        List<SourceSymbols.Declaration> selected = declarations.Where(d =>
            SymbolEqualityComparer.Default.Equals(Definition(d.Symbol), Definition(target))).ToList();
        if (action is "callees" or "describe" && selected.Any(d => d.Node is not (
            BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or VariableDeclaratorSyntax)))
            throw Invalid("callees/describe requires a source method, constructor, property, indexer, event or field.");

        Dictionary<SyntaxNode, SourceSymbols.Declaration> owners = declarations.ToDictionary(d => d.Node);
        SourceSymbols.Declaration? Owner(SyntaxNode node) =>
            node.Ancestors().Select(parent => owners.GetValueOrDefault(parent)).FirstOrDefault(d => d != null);
        Place At(SyntaxNode node) => Files.At(node.SyntaxTree.FilePath, scope.Texts[node.SyntaxTree.FilePath],
            node.SpanStart, node.Span.End);
        bool IsTarget(ISymbol? symbol) => symbol != null &&
            SymbolEqualityComparer.Default.Equals(Definition(symbol), Definition(target));

        List<SourceReferenceSite>? references = action is "references" or "describe" ? [] : null;
        List<SourceCallSite>? calls = action is "callers" or "callees" or "describe" ? [] : null;
        foreach (SyntaxTree tree in scope.Compilation.SyntaxTrees)
        {
            SemanticModel semantics = scope.Compilation.GetSemanticModel(tree);
            SyntaxNode root = tree.GetRoot();
            if (references != null)
            {
                foreach (SyntaxNode node in root.DescendantNodes(descendIntoTrivia: true).Where(n =>
                    n is SimpleNameSyntax or PredefinedTypeSyntax or CrefSyntax or ElementAccessExpressionSyntax))
                {
                    if (node.Ancestors().Any(n => n is CrefSyntax)) continue;
                    SymbolInfo info = semantics.GetSymbolInfo(node);
                    bool resolved = IsTarget(info.Symbol);
                    if (!resolved && !info.CandidateSymbols.Any(IsTarget)) continue;
                    SourceSymbols.Declaration? owner = Owner(node);
                    bool documentation = node is CrefSyntax;
                    bool nameOf = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
                        .Any(n => semantics.GetOperation(n) is Microsoft.CodeAnalysis.Operations.INameOfOperation);
                    bool invoked = node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>()
                        .Any(n => n.Expression.Span.Contains(node.Span));
                    string kind = documentation ? "documentation" : nameOf ? "nameof" :
                        node is ElementAccessExpressionSyntax ? "indexer" :
                        target is IMethodSymbol ? invoked ? "invocation" : "method-group" : "name";
                    references.Add(new(owner?.Id, kind, resolved ? "resolved" : "candidate", At(node), node.ToString(),
                        Nested(node, owner?.Node)));
                }
            }
            if (calls == null && target is not IMethodSymbol { MethodKind: MethodKind.Constructor }) continue;
            foreach (SyntaxNode node in InvocationNodes(root))
            {
                SourceSymbols.Declaration? owner = Owner(node);
                if (calls != null && action != "callers" && !IsTarget(owner?.Symbol)) continue;
                if (semantics.GetOperation(node) is Microsoft.CodeAnalysis.Operations.INameOfOperation) continue;
                IMethodSymbol? called = semantics.GetSymbolInfo(node).Symbol as IMethodSymbol;
                if (references != null && target is IMethodSymbol { MethodKind: MethodKind.Constructor } && IsTarget(called))
                    references.Add(new(owner?.Id, "constructor", "resolved", At(node), node.ToString(), Nested(node, owner?.Node)));
                if (calls == null || (action == "callers" ? !IsTarget(called) : !IsTarget(owner?.Symbol))) continue;
                calls.Add(CallSite(scope.Texts[node.SyntaxTree.FilePath], node, owner?.Id, owner?.Node, called));
                if (called == null && action != "describe")
                    diagnostics.Add(new(ErrorCode.UnresolvedCompilation,
                        "No single statically resolved target for explicit call; dynamic calls are not resolved by this query.", At(node)));
            }
        }
        List<SourceMemberDescription>? members = action == "describe" ? selected.Select(d => new SourceMemberDescription(
            scope.Texts[d.Node.SyntaxTree.FilePath][d.Node.SpanStart..d.Node.Span.End],
            SourceInventory.ReadMember(scope, d.Node, d.Symbol, diagnostics, flow: false))).ToList() : null;
        return new(references?.OrderBy(r => r.Location.Path, FilePaths.Comparer).ThenBy(r => r.Location.Line)
            .ThenBy(r => r.Location.Column).ToList(), calls?.OrderBy(c => c.Location.Path, FilePaths.Comparer)
            .ThenBy(c => c.Location.Line).ThenBy(c => c.Location.Column).ToList(), members);
    }

    private static InputException Invalid(string message) => new(ErrorCode.InvalidSymbolQuery, message);
}
