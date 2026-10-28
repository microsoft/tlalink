using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TlaLink;

internal sealed record ArchivedSourceBinding(BindAnnotation Declaration, Place Location);

internal static class CorrespondenceEvidence
{
    public static Dictionary<string, ArchivedSourceBinding> Bindings(IEnumerable<string> sources,
        Dictionary<string, string> pins, Func<string, string> read)
    {
        Dictionary<string, ArchivedSourceBinding> bindings = new(StringComparer.Ordinal);
        CSharpParseOptions options = new(LanguageVersion.CSharp14,
            preprocessorSymbols: pins.GetValueOrDefault("compiler:defines", "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        foreach (string path in sources)
        {
            string text = read(path);
            SyntaxNode root = CSharpSyntaxTree.ParseText(text, options, path).GetRoot();
            List<SyntaxNode> targets = root.DescendantNodes().Where(n =>
                n is MemberDeclarationSyntax or StatementSyntax or InvocationExpressionSyntax)
                .OrderBy(n => n.SpanStart).ThenByDescending(n => n.Span.Length).ToList();
            foreach (SyntaxTrivia trivia in root.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.MultiLineCommentTrivia)))
            {
                foreach (BindAnnotation binding in Markup.Parse(trivia.ToString()[2..^2], trivia.SpanStart, trivia.Span.End)
                    .Select(a => a.Declaration).OfType<BindAnnotation>())
                {
                    SyntaxNode node = CSharpScope.Attachment(targets, text, trivia.Span.End)
                        ?? throw new InputException(ErrorCode.InvalidAnnotation, "Archived binding has no source endpoint: " + binding.Id);
                    if (!bindings.TryAdd(binding.Id, new(binding, Files.At(path, text, node.SpanStart, node.Span.End))))
                        throw new InputException(ErrorCode.DuplicateBinding, "Duplicate archived binding ID: " + binding.Id);
                }
            }
        }
        return bindings;
    }

    public static CorrespondenceConfiguration Scope(string path, Func<string, string> read)
    {
        CorrespondenceConfiguration config = System.Text.Json.JsonSerializer.Deserialize<CorrespondenceConfiguration>(
            read(path), ProjectInputs.Json) ?? throw new InputException(ErrorCode.InvalidAnnotation, "Missing archived correspondence scope.");
        if (config.SchemaVersion != Schema.Version || config.Kind != "correspondence-scope")
            throw new InputException(ErrorCode.InvalidAnnotation, "Invalid archived correspondence scope.");
        Markup.RequireVersion(config.AnnotationVersion);
        ProjectInputs.Unique(config.Targets.Select(t => t.Id), "archived formal targets");
        return config;
    }
}
