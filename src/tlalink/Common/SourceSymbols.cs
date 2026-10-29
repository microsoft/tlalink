using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TlaLink;

public sealed record SymbolDeclaration(Place Location, int EndColumn, int Start, int Length);
public sealed record SourceSymbol(string Id, string Name, string Kind, string Signature,
    string? ContainingType, List<SymbolDeclaration> Declarations, string? Assembly = null);
public sealed record SourceSymbolQuery(string Action, string? Name, string? Id, string? File, int? Line, int? Column);
public sealed record SourceSymbolsResult(int SchemaVersion, string Kind, string Status, SourceSymbolQuery Query,
    int MatchCount, List<SourceSymbol> Symbols, SourceCompilerContext Compiler, List<string> Sources,
    Dictionary<string, string> Fingerprints, List<Issue> Diagnostics, List<string> Boundaries)
{
    public SourceQueryDetails? Details { get; init; }
    public int? ReferenceCount => Details?.References?.Count;
    public int? CallCount => Details?.Calls?.Count;
}

public static class SourceSymbols
{
    internal sealed record Declaration(ISymbol Symbol, SyntaxNode Node, string Id);

    public static int Run(CliOptions options)
    {
        SourceSymbolQuery query = Query(options);
        List<string> paths = Files.Discover(options.Many("source"), ".cs");
        SourceInventoryContext context = SourceInventory.LoadContext(paths, options, annotations: false);
        CSharpScope scope = context.Scope;
        List<Issue> diagnostics = [.. scope.Diagnostics];
        List<SourceSymbol> symbols = [];
        SourceQueryDetails? details = null;
        bool incomplete = diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.UnresolvedCompilation));
        if (!incomplete)
        {
            List<Declaration> declarations = Declarations(scope);
            IEnumerable<Declaration> selected;
            if (query.Action == "find")
                selected = declarations.Where(d => Name(d.Symbol).Equals(query.Name, StringComparison.Ordinal) ||
                    d.Symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat).Equals(query.Name, StringComparison.Ordinal) ||
                    QualifiedName(d.Symbol).Equals(query.Name, StringComparison.Ordinal));
            else if (query.Action != "resolve")
            {
                List<ISymbol> resolved = ResolveId(query.Id!, scope.Compilation);
                HashSet<string> ids = resolved.Select(s => s.GetDocumentationCommentId())
                    .OfType<string>().Where(id => id == query.Id).ToHashSet(StringComparer.Ordinal);
                selected = declarations.Where(d => ids.Contains(d.Id));
            }
            else
            {
                string file = query.File ?? (paths.Count == 1 ? paths[0] :
                    throw Invalid("Position lookup with multiple sources requires --file."));
                file = FilePaths.Full(file);
                SyntaxTree tree = scope.Compilation.SyntaxTrees.SingleOrDefault(t => FilePaths.Comparer.Equals(t.FilePath, file))
                    ?? throw Invalid("--file must identify one of the selected compilation sources.");
                query = query with { File = tree.FilePath };
                var text = tree.GetText();
                if (query.Line > text.Lines.Count) throw Invalid("--line is outside the selected source file.");
                var line = text.Lines[query.Line!.Value - 1];
                if (query.Column > line.Span.Length + 1) throw Invalid("--column is outside the selected source line.");
                int? position = query.Column is int column ? line.Start + column - 1 : null;
                bool Contains(SyntaxNode node) => position is int offset ? node.Span.Contains(offset) :
                    node.GetLocation().GetLineSpan().StartLinePosition.Line <= query.Line - 1 &&
                    node.GetLocation().GetLineSpan().EndLinePosition.Line >= query.Line - 1;
                List<Declaration> inFile = declarations.Where(d => d.Node.SyntaxTree == tree).ToList();
                List<Declaration> hits = inFile.Where(d => Contains(d.Node)).ToList();
                foreach (var group in inFile.Where(d => d.Node is VariableDeclaratorSyntax)
                    .GroupBy(d => d.Node.Parent!.Parent!))
                    if (Contains(group.Key) && !group.Any(d => hits.Contains(d))) hits.AddRange(group);
                selected = hits.Where(candidate => !hits.Any(other =>
                    other.Node.Ancestors().Any(parent => ReferenceEquals(parent, candidate.Node))));
            }
            symbols = selected.GroupBy(d => d.Id, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => Describe(g.Key, g.First().Symbol, g.Select(d => d.Node))).ToList();
            if (query.Action is "references" or "callers")
                symbols = ResolveId(query.Id!, scope.Compilation).Select(symbol => Describe(query.Id!, symbol,
                    declarations.Where(d => SymbolEqualityComparer.Default.Equals(SourceQueries.Definition(d.Symbol),
                        SourceQueries.Definition(symbol))).Select(d => d.Node))).ToList();
            if (symbols.Count == 0)
                diagnostics.Add(new(ErrorCode.SourceSymbolNotFound, "No source declaration matched the query in the explicit compilation."));
            else if (query.Action != "find" && symbols.Count != 1)
                diagnostics.Add(new(ErrorCode.SourceSymbolAmbiguous,
                    "The query matches multiple declarations. Specify a column or disambiguate the compiler context; no declaration was chosen implicitly."));
            if (symbols.Count == 1 && query.Action is "references" or "callers" or "callees" or "describe")
            {
                ISymbol target = ResolveId(query.Id!, scope.Compilation).Single();
                details = SourceQueries.Read(scope, declarations, target, query.Action, diagnostics);
                incomplete = diagnostics.Any(d => d.Is(ErrorCode.UnresolvedCompilation) || d.Severity == "error");
            }
        }
        Program.PinTool(scope.Fingerprints);
        IntegratedInputs.CheckPins(scope.Fingerprints);
        string status = incomplete ? "incomplete" : symbols.Count == 0 ? "not-found" :
            query.Action != "find" && symbols.Count != 1 ? "ambiguous" : query.Action == "find" ? "found" : "resolved";
        SourceSymbolsResult result = new(Schema.Version, "source-symbols", status, query, symbols.Count, symbols,
            context.Compiler, paths, scope.Fingerprints, diagnostics, [
                "Compiler-resolved source declarations under explicit sources/references/defines; not runtime dispatch, code validation or model correspondence.",
                "Names and declaration IDs compare ordinally. Partial declarations share one ID and retain all selected source spans.",
                "Positions are physical, one-based UTF-16 line/column coordinates; spans use zero-based UTF-16 offsets with exclusive ends.",
                "Accessors, local functions and implicit/generated members are not separate endpoints. Primary constructors are not listed separately.",
                "Comments and @link payloads are not interpreted. Source bytes and compiler context remain fingerprinted.",
                "Reference and call queries scan selected sources only. Callers are explicit call sites, not inferred runtime dispatch or method-group invocation.",
                "Nameof/cref uses and candidate method groups are references, not execution edges. Unknown dynamic calls cannot be attributed to an ID by callers/references; outgoing dynamic calls make callees/describe incomplete.",
                "Implicit property/disposal/awaiter/operator/runtime calls and reflection are not enumerated. Nested functions are attributed to their containing declaration and marked, not assumed executed."
            ]) { Details = details };
        if (options.Optional("output") is string output) Files.WriteNew(FilePaths.Full(output), result);
        ProjectWorkflow.Render(result, options);
        return incomplete ? 2 : diagnostics.Any(d => d.Severity == "error") ? 1 : 0;
    }

    private static SourceSymbolQuery Query(CliOptions options)
    {
        if (options.Many("source").Count == 0) throw Invalid("Symbol queries require explicit --source inputs.");
        string action = options.Action ?? throw Invalid("Select a symbols action.");
        string[] selectors = action switch { "find" => ["name"], "resolve" => ["file", "line", "column"], _ => ["id"] };
        if (new[] { "name", "id", "file", "line", "column" }.Except(selectors).Any(options.Has))
            throw Invalid("Query selectors do not match the selected symbols action.");
        string? name = action == "find" ? options.Optional("name") ?? throw Invalid("Name discovery requires --name.") : null;
        string? id = action is not ("find" or "resolve") ? options.Optional("id") ?? throw Invalid("This symbols action requires --id.") : null;
        if (name != null && string.IsNullOrWhiteSpace(name) || id != null && string.IsNullOrWhiteSpace(id))
            throw Invalid("Names and declaration IDs must be nonempty.");
        int? Positive(string option)
        {
            if (!options.Has(option)) return null;
            if (!int.TryParse(options.Required(option), out int value) || value < 1)
                throw Invalid("--" + option + " must be a positive one-based coordinate.");
            return value;
        }
        int? line = Positive("line"), column = Positive("column");
        if (action == "resolve" && line == null) throw Invalid("Position lookup requires --line.");
        return new(action, name, id, options.Optional("file"), line, column);
    }

    internal static List<ISymbol> ResolveId(string id, Compilation compilation)
    {
        try
        {
            return DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation)
                .Where(s => s.GetDocumentationCommentId() == id).Select(SourceQueries.Definition)
                .Distinct(SymbolEqualityComparer.Default).ToList();
        }
        catch (ArgumentException error)
        {
            throw new InputException(ErrorCode.InvalidSymbolQuery, "Invalid declaration ID: " + error.Message);
        }
    }

    private static List<Declaration> Declarations(CSharpScope scope)
    {
        List<Declaration> declarations = [];
        foreach (SyntaxTree tree in scope.Compilation.SyntaxTrees)
        {
            SemanticModel semantics = scope.Compilation.GetSemanticModel(tree);
            foreach (SyntaxNode node in tree.GetRoot().DescendantNodes().Where(n =>
                n is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or BaseMethodDeclarationSyntax or
                    BasePropertyDeclarationSyntax or EnumMemberDeclarationSyntax ||
                n is VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax }))
            {
                ISymbol? symbol = semantics.GetDeclaredSymbol(node);
                if (symbol?.GetDocumentationCommentId() is string id)
                    declarations.Add(new(symbol, node, id));
            }
        }
        return declarations;
    }

    private static SourceSymbol Describe(string id, ISymbol symbol, IEnumerable<SyntaxNode> declarations)
    {
        string kind = symbol switch {
            INamedTypeSymbol => "type",
            IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
            IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator or MethodKind.Conversion } => "operator",
            IPropertySymbol { IsIndexer: true } => "indexer",
            IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "enum-member",
            _ => symbol.Kind.ToString().ToLowerInvariant()
        };
        SymbolDisplayFormat format = SymbolDisplayFormat.CSharpErrorMessageFormat;
        format = format.WithMemberOptions(format.MemberOptions | SymbolDisplayMemberOptions.IncludeType |
            SymbolDisplayMemberOptions.IncludeAccessibility | SymbolDisplayMemberOptions.IncludeModifiers);
        return new(id, Name(symbol), kind, symbol.ToDisplayString(format),
            symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            declarations.OrderBy(n => n.SyntaxTree.FilePath, FilePaths.Comparer).ThenBy(n => n.SpanStart)
                .Select(node => {
                    FileLinePositionSpan span = node.GetLocation().GetLineSpan();
                    return new SymbolDeclaration(new(node.SyntaxTree.FilePath, span.StartLinePosition.Line + 1,
                        span.EndLinePosition.Line + 1, span.StartLinePosition.Character + 1),
                        span.EndLinePosition.Character + 1, node.SpanStart, node.Span.Length);
                }).ToList(), symbol.ContainingAssembly?.Identity.ToString());
    }

    private static string Name(ISymbol symbol) => symbol switch {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => symbol.ContainingType.Name,
        IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 } method => method.ExplicitInterfaceImplementations[0].Name,
        IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 } property => property.ExplicitInterfaceImplementations[0].Name,
        IEventSymbol { ExplicitInterfaceImplementations.Length: > 0 } item => item.ExplicitInterfaceImplementations[0].Name,
        _ => symbol.Name
    };

    private static string QualifiedName(ISymbol symbol) => symbol is INamedTypeSymbol
        ? symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)
        : symbol.ContainingType?.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat) + "." + Name(symbol);

    private static InputException Invalid(string message) => new(ErrorCode.InvalidSymbolQuery, message);
}
