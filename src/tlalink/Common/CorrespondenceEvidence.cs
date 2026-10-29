using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TlaLink;

internal sealed record ArchivedSourceBinding(BindAnnotation Declaration, Place Location);

internal static class CorrespondenceEvidence
{
    public static Dictionary<string, ArchivedSourceBinding> Bindings(IEnumerable<string> sources, IEnumerable<string> selectedBindings,
        Dictionary<string, string> pins, Func<string, string> read, IEnumerable<FormalLinkDeclaration>? formal = null,
        Func<string, string>? archivedFile = null)
    {
        Dictionary<string, ArchivedSourceBinding> bindings = new(StringComparer.Ordinal);
        CSharpParseOptions options = new(CSharpLanguages.Parse(CSharpLanguages.Archived(pins)),
            preprocessorSymbols: pins.GetValueOrDefault("compiler:defines", "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        List<Microsoft.CodeAnalysis.SyntaxTree> trees = [];
        foreach (string path in sources)
        {
            string text = read(path);
            var tree = CSharpSyntaxTree.ParseText(text, options, path);
            trees.Add(tree);
            SyntaxNode root = tree.GetRoot();
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
        HashSet<string> selection = selectedBindings.ToHashSet(StringComparer.Ordinal);
        List<FormalLinkDeclaration> selected = (formal ?? [])
            .Where(d => d.Binding.Source != null && selection.Contains(d.Binding.Id)).ToList();
        if (selected.Count > 0)
        {
            if (!pins.TryGetValue("tool:csharp-references", out string? encoded))
                throw new InputException(ErrorCode.InvalidAnnotation, "Formal-owned archived links require the recorded compiler reference list.");
            List<string> references = System.Text.Json.JsonSerializer.Deserialize<List<string>>(encoded, Files.Json)
                ?? throw new InputException(ErrorCode.InvalidAnnotation, "Invalid recorded compiler references.");
            var compilation = CSharpCompilation.Create("TlaLink.ArchivedConnections", trees,
                references.Select(path => MetadataReference.CreateFromImage(File.ReadAllBytes(Archive(path)))),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
            foreach (FormalLinkDeclaration declaration in selected)
            {
                SyntaxNode node = SourcePreparation.CSharpTarget(compilation, null, declaration.Binding.Source!);
                string path = node.SyntaxTree.FilePath;
                if (!bindings.TryAdd(declaration.Binding.Id, new(declaration.Binding,
                    Files.At(path, read(path), node.SpanStart, node.Span.End))))
                    throw new InputException(ErrorCode.DuplicateBinding, "Competing archived connection owners: " + declaration.Binding.Id);
            }
        }
        return bindings;

        string Archive(string path)
        {
            // Metadata references need bytes, not decoded source text.
            if (!pins.TryGetValue(path, out string? hash))
                throw new InputException(ErrorCode.InvalidAnnotation, "Missing compiler reference pin: " + path);
            string copy = archivedFile?.Invoke(hash)
                ?? throw new InputException(ErrorCode.InvalidAnnotation, "Archived compiler-reference reader is required.");
            if (!File.Exists(copy) || Files.Hash(copy) != hash)
                throw new InputException(ErrorCode.InvalidAnnotation, "Missing or changed archived compiler reference: " + path);
            return copy;
        }
    }

    public static CorrespondenceConfiguration Scope(string path, Func<string, string> read)
    {
        CorrespondenceConfiguration config = System.Text.Json.JsonSerializer.Deserialize<CorrespondenceConfiguration>(
            read(path), ProjectInputs.Json) ?? throw new InputException(ErrorCode.InvalidAnnotation, "Missing archived correspondence scope.");
        if (config.SchemaVersion != Schema.Version || config.Kind != "correspondence-scope")
            throw new InputException(ErrorCode.InvalidAnnotation, "Invalid archived correspondence scope.");
        Markup.RequireVersion(config.AnnotationVersion);
        ProjectInputs.Unique(config.Targets.Select(t => t.Id), "archived formal targets");
        MethodSummaries.Validate(config);
        return config;
    }
}
