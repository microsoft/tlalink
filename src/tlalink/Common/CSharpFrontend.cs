using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TlaLink;

public sealed record SourceBinding(BindAnnotation Declaration, CaptureAnnotation? Capture,
    SourceBindingKind Kind, int Start, int End, string EventIdentity, SyntaxNode Node, ISymbol? Symbol,
    SemanticModel Semantics, string Path, string Text)
{
    public string Model => Declaration.Target.Alias;
    public string NativeEndpoint => Declaration.Target.Endpoint;
    public string BindingId => Declaration.Id;
    public Tag Tag
    {
        get
        {
            Dictionary<string, string> options = Capture?.BackendOptions() ?? new(StringComparer.Ordinal);
            if (Declaration.Reason != null) options["justification"] = Declaration.Reason;
            return new(Kind.ToString().ToLowerInvariant(), EventIdentity, options, Start, End);
        }
    }
    public string Key => $"{Model}:{Tag.Kind}:{Tag.Identity}";
    public Endpoint Endpoint => new(Symbol?.Name ?? Node.Kind().ToString(),
        Symbol?.ContainingType?.ToDisplayString() ?? Node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "",
        Tag.Kind, Files.At(Path, Text, Node.SpanStart, Node.Span.End), Text[Node.SpanStart..Node.Span.End]);
    public List<string> Locks => Node.Ancestors()
        .TakeWhile(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
        .OfType<LockStatementSyntax>().Reverse().Select(n => n.Expression.ToString()).ToList();
}

public sealed partial class CSharpScope
{
    public required CSharpCompilation Compilation { get; init; }
    public List<SourceBinding> Bindings { get; } = [];
    public List<Issue> Diagnostics { get; } = [];
    public List<Access> Accesses { get; } = [];
    public Dictionary<string, string> Fingerprints { get; } = new(FilePaths.Comparer);
    public Dictionary<string, string> Texts { get; } = new(FilePaths.Comparer);
    public HashSet<string>? SelectedTargets { get; set; }
    public List<SourceLinkDeclaration> DeclaredLinks => Bindings.Select(b =>
        new SourceLinkDeclaration(b.Declaration, b.Capture, b.Kind, b.Endpoint.Location)).ToList();

    public static CSharpScope Parse(List<string> paths, IEnumerable<string>? references = null, IEnumerable<string>? defines = null,
        NullableContextOptions nullable = NullableContextOptions.Enable, bool includeFrameworkReferences = true)
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
        string[] framework = includeFrameworkReferences ? ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InputException("Cannot establish .NET framework references.")).Split(Path.PathSeparator) : [];
        string[] referencePaths = framework.Concat(references ?? []).Select(FilePaths.Full).Distinct(FilePaths.Comparer).ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create("TlaLink.ExplicitScope", trees,
            referencePaths.Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: nullable));
        CSharpScope result = new() { Compilation = compilation };
        foreach (string reference in referencePaths) Files.Pin(result.Fingerprints, reference, Files.Hash(reference));
        result.Fingerprints["compiler:defines"] = string.Join(",", symbols.Order());
        result.Fingerprints["tool:csharp-context"] = System.Text.Json.JsonSerializer.Serialize(new {
            LanguageVersion = "14", Nullable = nullable.ToString(), IncludeFrameworkReferences = includeFrameworkReferences
        }, Files.Json);
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
            List<SyntaxNode> targets = root.DescendantNodes().Where(n => n is MemberDeclarationSyntax or StatementSyntax or InvocationExpressionSyntax)
                .OrderBy(n => n.SpanStart).ThenByDescending(n => n.Span.Length).ToList();
            List<(LocatedAnnotation Annotation, SyntaxNode Node)> attached = [];
            foreach (SyntaxTrivia trivia in root.DescendantTrivia(descendIntoTrivia: false))
            {
                if (!trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) continue;
                string raw = trivia.ToString(), content = raw[2..^2];
                try
                {
                    foreach (LocatedAnnotation annotation in Markup.Parse(content, trivia.SpanStart, trivia.Span.End))
                    {
                        SyntaxNode? target = Attachment(targets, text, trivia.Span.End);
                        if (target is null || target is FieldDeclarationSyntax field && field.Declaration.Variables.Count != 1 ||
                            annotation.Declaration is ExcludeAnnotation && target is InvocationExpressionSyntax)
                            throw new InputException(ErrorCode.InvalidAttachment, "Use an unambiguous declaration, statement or supported expression.");
                        attached.Add((annotation, target));
                    }
                }
                catch (InputException error)
                {
                    result.Diagnostics.Add(new(ErrorCode.InvalidAnnotation, error.Message, Files.At(path, text, trivia.SpanStart, trivia.Span.End)));
                }
            }
            result.AttachCorrespondence(attached, semantics, path, text);
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
        result.ValidateDispositionIds();
        return result;
    }

    public void RequireCompilable()
    {
        List<Diagnostic> errors = Compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count != 0) throw new InputException(ErrorCode.UnresolvedInstrumentation, "Instrumentation requires a resolved compilation: " + string.Join("; ", errors.Take(8)));
    }

    public static string SymbolId(ISymbol symbol) => symbol.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
        + "|" + symbol.OriginalDefinition.ContainingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    internal static string DeclarationId(SourceBinding binding)
    {
        if (binding.Symbol?.GetDocumentationCommentId() is string direct) return direct;
        foreach (SyntaxNode node in binding.Node.AncestorsAndSelf())
            if (binding.Semantics.GetDeclaredSymbol(node)?.GetDocumentationCommentId() is string id) return id;
        throw new InputException(ErrorCode.InvalidIntegratedBinding, "Binding needs a resolved containing C# declaration: " + binding.BindingId);
    }

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

    internal static SyntaxNode? Attachment(IEnumerable<SyntaxNode> targets, string text, int end) =>
        targets.FirstOrDefault(n => n.SpanStart >= end && OnlyCSharpTrivia(text, end, n.SpanStart));
}
