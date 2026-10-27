using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TlaLink;

internal static class CleanupInstrumentation
{
    internal static bool IsUsing(SyntaxNode node) => node is UsingStatementSyntax ||
        node is LocalDeclarationStatementSyntax local && !local.UsingKeyword.IsKind(SyntaxKind.None);

    internal static void Add(SourceBinding binding, string cleanup, string fault, List<TextChange> edits,
        Dictionary<(string Path, int Position), List<(int Start, string Text)>> closers)
    {
        if (binding.Locks.Count != 0 || binding.Node.Ancestors().Any(n => n is CatchFilterClauseSyntax))
            throw new InputException(ErrorCode.InvalidInstrumentation, "Cleanup capture cannot run under a source lock or exception filter.");
        ExpressionSyntax? expression;
        VariableDeclarationSyntax? declaration;
        bool asynchronous;
        if (binding.Node is UsingStatementSyntax statement)
        {
            expression = statement.Expression;
            declaration = statement.Declaration;
            asynchronous = !statement.AwaitKeyword.IsKind(SyntaxKind.None);
        }
        else
        {
            LocalDeclarationStatementSyntax local = (LocalDeclarationStatementSyntax)binding.Node;
            expression = null;
            declaration = local.Declaration;
            asynchronous = !local.AwaitKeyword.IsKind(SyntaxKind.None);
        }
        if (asynchronous || declaration != null && declaration.Variables.Count != 1)
            throw new InputException(ErrorCode.InvalidInstrumentation, "Cleanup capture supports one synchronous reference resource, not await using or multiple declarators.");
        ITypeSymbol? type = expression != null ? binding.Semantics.GetTypeInfo(expression).Type :
            (binding.Semantics.GetDeclaredSymbol(declaration!.Variables[0]) as ILocalSymbol)?.Type;
        INamedTypeSymbol? disposable = binding.Semantics.Compilation.GetTypeByMetadataName("System.IDisposable");
        if (type?.IsReferenceType != true || type.TypeKind == TypeKind.Dynamic || disposable == null ||
            disposable.Locations.Any(l => l.IsInSource) ||
            !binding.Semantics.Compilation.ClassifyConversion(type, disposable).IsImplicit)
            throw new InputException(ErrorCode.InvalidInstrumentation, "Cleanup capture requires a resolved reference-type IDisposable resource.");
        var conversion = binding.Semantics.Compilation.ClassifyConversion(type, disposable);
        if (!conversion.IsIdentity && !conversion.IsReference)
            throw new InputException(ErrorCode.InvalidInstrumentation, "User-defined and boxing disposal conversions are unsupported.");
        HashSet<string> identifiers = binding.Node.SyntaxTree.GetRoot().DescendantTokens()
            .Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText).ToHashSet(StringComparer.Ordinal);
        string resource = declaration?.Variables[0].Identifier.Text ?? "__tlalinkResource" + binding.Node.SpanStart;
        if (declaration == null) while (identifiers.Contains(resource)) resource += "_";
        string dispose = "((global::System.IDisposable)" + resource + ").Dispose();";
        if (fault.Length != 0) dispose = "try { " + dispose + " } catch { " + fault + "\nthrow; }";
        string finallyText = " finally { if (" + resource + " is not null) { " + dispose + "\n" + cleanup + "\n} }";
        if (binding.Node is UsingStatementSyntax usingStatement)
        {
            string acquisition = declaration != null ? declaration.ToString() : "var " + resource + " = " + expression;
            bool block = usingStatement.Statement is BlockSyntax;
            edits.Add(new(TextSpan.FromBounds(usingStatement.SpanStart, usingStatement.Statement.SpanStart),
                "{ " + acquisition + "; try " + (block ? "" : "{ ")));
            edits.Add(new(new TextSpan(usingStatement.Span.End, 0), (block ? "" : " }") + finallyText + " }"));
        }
        else
        {
            LocalDeclarationStatementSyntax local = (LocalDeclarationStatementSyntax)binding.Node;
            if (local.Parent is not BlockSyntax block)
                throw new InputException(ErrorCode.InvalidInstrumentation, "Using declaration cleanup requires a block lifetime.");
            edits.Add(new(local.UsingKeyword.Span, ""));
            edits.Add(new(new TextSpan(local.Span.End, 0), "\ntry {\n"));
            var key = (binding.Path, block.CloseBraceToken.SpanStart);
            if (!closers.TryGetValue(key, out var closing)) closers.Add(key, closing = []);
            closing.Add((local.SpanStart, "\n}" + finallyText + "\n"));
        }
    }
}
