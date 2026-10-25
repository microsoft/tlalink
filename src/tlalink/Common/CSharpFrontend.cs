using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TlaLink;

public sealed record SourceBinding(string Model, Tag Tag, SyntaxNode Node, ISymbol? Symbol,
    SemanticModel Semantics, string Path, string Text)
{
    public string Key => $"{Model}:{Tag.Kind}:{Tag.Identity}";
    public Endpoint Endpoint => new(Symbol?.Name ?? Node.Kind().ToString(),
        Symbol?.ContainingType?.ToDisplayString() ?? Node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "",
        Tag.Kind, Files.At(Path, Text, Node.SpanStart, Node.Span.End), Text[Node.SpanStart..Node.Span.End]);
    public List<string> Locks => Node.Ancestors().OfType<LockStatementSyntax>().Reverse().Select(n => n.Expression.ToString()).ToList();
}

public sealed class CSharpScope
{
    public required CSharpCompilation Compilation { get; init; }
    public List<SourceBinding> Bindings { get; } = [];
    public List<Issue> Diagnostics { get; } = [];
    public List<Access> Accesses { get; } = [];
    public Dictionary<string, string> Fingerprints { get; } = new(FilePaths.Comparer);
    public Dictionary<string, string> Texts { get; } = new(FilePaths.Comparer);

    public static CSharpScope Parse(List<string> paths, IEnumerable<string>? references = null, IEnumerable<string>? defines = null)
    {
        string[] symbols = (defines ?? ["DEBUG", "TRACE", "NET", "NETCOREAPP", "NET10_0", "NET10_0_OR_GREATER", "NET9_0_OR_GREATER",
            "NET8_0_OR_GREATER", "NET7_0_OR_GREATER", "NET6_0_OR_GREATER", "NET5_0_OR_GREATER",
            "NETCOREAPP3_1_OR_GREATER", "NETCOREAPP3_0_OR_GREATER", "NETCOREAPP2_2_OR_GREATER",
            "NETCOREAPP2_1_OR_GREATER", "NETCOREAPP2_0_OR_GREATER", "NETCOREAPP1_1_OR_GREATER", "NETCOREAPP1_0_OR_GREATER"]).ToArray();
        if (symbols.Any(s => !SyntaxFacts.IsValidIdentifier(s) || s is "true" or "false"))
            throw new InputException("Invalid C# preprocessor symbol; supply individual identifier values.");
        CSharpParseOptions parse = new(LanguageVersion.CSharp14, preprocessorSymbols: symbols);
        Dictionary<string, string> sourcePins = new(FilePaths.Comparer);
        Dictionary<string, string> texts = paths.ToDictionary(p => p, p => Files.ReadPinned(p, sourcePins), FilePaths.Comparer);
        List<SyntaxTree> trees = paths.Select(p => CSharpSyntaxTree.ParseText(SourceText.From(texts[p]), parse, p)).Cast<SyntaxTree>().ToList();
        string[] framework = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InputException("Cannot establish .NET framework references.")).Split(Path.PathSeparator);
        string[] referencePaths = framework.Concat(references ?? []).Select(FilePaths.Full).Distinct(FilePaths.Comparer).ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create("TlaLink.ExplicitScope", trees,
            referencePaths.Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
        CSharpScope result = new() { Compilation = compilation };
        foreach (string reference in referencePaths) Files.Pin(result.Fingerprints, reference, Files.Hash(reference));
        result.Fingerprints["compiler:defines"] = string.Join(",", symbols.Order());
        foreach (SyntaxTree tree in trees)
        {
            string path = tree.FilePath, text = texts[path];
            result.Texts[path] = text;
            Files.Pin(result.Fingerprints, path, sourcePins[path]);
            SemanticModel semantics = compilation.GetSemanticModel(tree);
            SyntaxNode root = tree.GetRoot();
            foreach (Diagnostic syntaxError in tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                FileLinePositionSpan location = syntaxError.Location.GetLineSpan();
                result.Diagnostics.Add(new(ErrorCode.CSharpSyntaxError, syntaxError.GetMessage(),
                    new(path, location.StartLinePosition.Line + 1, location.EndLinePosition.Line + 1, location.StartLinePosition.Character + 1)));
            }
            List<SyntaxNode> targets = root.DescendantNodes().Where(n => n is TypeDeclarationSyntax or FieldDeclarationSyntax or StatementSyntax)
                .OrderBy(n => n.SpanStart).ThenByDescending(n => n.Span.Length).ToList();
            List<(Tag Tag, SyntaxNode Node)> attached = [];
            foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: false))
            {
                if (!trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) continue;
                string raw = trivia.ToString(), content = raw[2..^2];
                try
                {
                    foreach (Tag tag in Markup.Parse(content, trivia.SpanStart, trivia.Span.End))
                    {
                        SyntaxNode? target = targets.FirstOrDefault(n => n.SpanStart >= trivia.Span.End &&
                            OnlyCSharpTrivia(text, trivia.Span.End, n.SpanStart));
                        if (target is null ||
                            tag.Kind == "model" && target is not TypeDeclarationSyntax ||
                            tag.Kind == "state" && (target is not FieldDeclarationSyntax field || field.Declaration.Variables.Count != 1) ||
                            tag.Kind == "step" && target is not StatementSyntax)
                            throw new InputException(ErrorCode.InvalidAttachment, $"Invalid {tag.Kind} attachment; steps attach to statements, state to a single field, model to a type.");
                        if (tag.Options.ContainsKey("visibility")) throw new InputException(ErrorCode.InvalidAttachment, "visibility is a model-only hidden-action declaration.");
                        attached.Add((tag, target));
                    }
                }
                catch (InputException error)
                {
                    result.Diagnostics.Add(new(ErrorCode.InvalidAnnotation, error.Message, Files.At(path, text, trivia.SpanStart, trivia.Span.End)));
                }
            }
            foreach (var group in attached.Where(e => e.Tag.Kind == "model").GroupBy(e => e.Node).Where(g => g.Count() > 1))
                result.Diagnostics.Add(new(ErrorCode.DuplicateBinding, "A C# type must have exactly one model namespace annotation.",
                    Files.At(path, text, group.Key.SpanStart, group.Key.Span.End)));
            foreach (var entry in attached.Where(e => e.Tag.Kind != "model"))
            {
                var models = attached.Where(e => e.Tag.Kind == "model" && e.Node.Span.Contains(entry.Node.Span)).OrderBy(e => e.Node.Span.Length).ToList();
                if (models.Count == 0)
                {
                    result.Diagnostics.Add(new(ErrorCode.MissingModelNamespace, "C# state/step needs an enclosing annotated model type.", Files.At(path, text, entry.Tag.Start, entry.Tag.End)));
                    continue;
                }
                SyntaxNode symbolNode = entry.Node is FieldDeclarationSyntax declaration ? declaration.Declaration.Variables[0] : entry.Node;
                ISymbol? symbol = entry.Tag.Kind == "state" ? semantics.GetDeclaredSymbol(symbolNode) : null;
                result.Bindings.Add(new(models[0].Tag.Identity, entry.Tag, entry.Node, symbol, semantics, path, text));
            }
            foreach (IdentifierNameSyntax identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                ISymbol? symbol = semantics.GetSymbolInfo(identifier).Symbol;
                if (symbol is not IFieldSymbol) continue;
                SyntaxNode expression = identifier.Parent is MemberAccessExpressionSyntax member && member.Name == identifier ? member : identifier;
                string mode = IsWrite(expression) ? "read-write" : "read";
                result.Accesses.Add(new(symbol.Name, SymbolId(symbol), expression.ToString(), mode,
                    Files.At(path, text, identifier.SpanStart, identifier.Span.End)));
            }
        }
        foreach (Diagnostic error in compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            FileLinePositionSpan place = error.Location.GetLineSpan();
            result.Diagnostics.Add(new(ErrorCode.UnresolvedCompilation, $"Unresolved compilation context: {error.Id}: {error.GetMessage()}",
                new(place.Path, place.StartLinePosition.Line + 1, place.EndLinePosition.Line + 1, place.StartLinePosition.Character + 1)));
        }
        return result;
    }

    public void RequireCompilable()
    {
        List<Diagnostic> errors = Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count != 0) throw new InputException(ErrorCode.UnresolvedInstrumentation, "Instrumentation requires a resolved compilation: " + string.Join("; ", errors.Take(8)));
    }

    public static string SymbolId(ISymbol symbol) => symbol.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        + "|" + symbol.OriginalDefinition.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    public static bool IsWrite(SyntaxNode expression)
    {
        SyntaxNode target = expression;
        while (target.Parent is ArgumentSyntax { Parent: TupleExpressionSyntax tuple }) target = tuple;
        return target.Parent is AssignmentExpressionSyntax assignment && assignment.Left == target ||
            target.Parent is PrefixUnaryExpressionSyntax prefix && prefix.Kind() is
                (SyntaxKind.PreIncrementExpression or SyntaxKind.PreDecrementExpression or SyntaxKind.AddressOfExpression) ||
            target.Parent is PostfixUnaryExpressionSyntax postfix && postfix.Kind() is
                (SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression) ||
            target.Parent is ArgumentSyntax argument && !argument.RefOrOutKeyword.IsKind(SyntaxKind.None) ||
            target.Parent is RefExpressionSyntax;
    }

    public static List<Boundary> Boundaries(SourceBinding binding) =>
        binding.Node.DescendantNodesAndSelf().Where(n => n is AwaitExpressionSyntax or InvocationExpressionSyntax or ThrowStatementSyntax
            or AnonymousFunctionExpressionSyntax or CatchClauseSyntax or FinallyClauseSyntax or UsingStatementSyntax or LockStatementSyntax)
        .Select(n => new Boundary(n.Kind().ToString(), n.ToString(), Files.At(binding.Path, binding.Text, n.SpanStart, n.Span.End))).ToList();

    private static bool OnlyCSharpTrivia(string text, int start, int end) =>
        SyntaxFactory.ParseLeadingTrivia(text[start..end]).FullSpan.Length == end - start;
}
