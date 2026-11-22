using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static TlaLink.SynchronizationGuarantees;

namespace TlaLink;

/// <summary>
/// Synchronization regions: explicit C# files in which only declared primitives, environments and locks synchronize.
/// Roslyn checks over those files and region methods they call; no alias, data-flow or runtime analysis.
/// </summary>
public static partial class SynchronizationRegion
{
    internal static readonly string[] SpawnDispositions = ["joined", "detached"];
    internal static readonly string[] WaitKinds = ["join", "timer", "yield", "external"];
    // Roles whose operation can complete another caller's wait; try-acquire is included as a state-changing try-* role.
    internal static readonly string[] ResumingRoles = ["release", "signal", "complete", "close", "enqueue", "try-acquire"];
    private static readonly string[] RawTypes = [
        "System.Threading.Interlocked", "System.Threading.Volatile", "System.Threading.Monitor", "System.Threading.Lock",
        "System.Threading.SemaphoreSlim", "System.Threading.Semaphore", "System.Threading.Mutex", "System.Threading.EventWaitHandle",
        "System.Threading.ManualResetEvent", "System.Threading.ManualResetEventSlim", "System.Threading.AutoResetEvent",
        "System.Threading.CountdownEvent", "System.Threading.Barrier", "System.Threading.SpinLock",
        "System.Threading.ReaderWriterLock", "System.Threading.ReaderWriterLockSlim",
        "System.Threading.Tasks.TaskCompletionSource", "System.Threading.Tasks.TaskCompletionSource`1",
        "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1"];
    public const string Boundary = "Roslyn checks over the explicit region files and the region methods they call: declared " +
        "primitives, environments, locks, spawns and wait dispositions. Not alias, data-flow or runtime analysis; code outside " +
        "the region, implicit callbacks and declared dispositions are trusted, and passing checks are not a progress proof.";

    /// <summary>Structural validation; with a <paramref name="root"/> the declared files must also exist.</summary>
    public static void Validate(SynchronizationDeclaration? declaration, string? root)
    {
        if (declaration == null) return;
        void Paths(List<string>? paths, string extension, int minimum, string name)
        {
            if (paths == null) return;
            if (paths.Count < minimum || paths.Count > 1000 || paths.Any(p => string.IsNullOrWhiteSpace(p) ||
                    !Path.GetExtension(p).Equals(extension, StringComparison.OrdinalIgnoreCase)) ||
                paths.Distinct(FilePaths.Comparer).Count() != paths.Count)
                throw Invalid("synchronization." + name + " requires " + minimum + "..1000 unique " + extension + " paths relative to the scope.");
            if (root != null && paths.Any(p => !File.Exists(FilePaths.Full(p, root))))
                throw Invalid("Declared synchronization." + name + " files must exist.");
        }
        Paths(declaration.Primitives ?? throw Invalid("synchronization.primitives is required."), ".json",
            declaration.Region == null ? 1 : 0, "primitives");
        Paths(declaration.Region, ".cs", 1, "region");
        Paths(declaration.Environments, ".json", 0, "environments");
        if (declaration.Region == null && new[] { declaration.Environments?.Count, declaration.Locks?.Count, declaration.Spawns?.Count,
                declaration.Waits?.Count }.Any(c => c > 0))
            throw Invalid("synchronization environments, locks, spawns and waits require an explicit region.");
        if (declaration.Locks is { } locks)
        {
            if (locks.Count > 1000 || locks.Any(l => l == null || !Documentation(l.Field, "F:") ||
                    l.IndexedBy != null && !Documentation(l.IndexedBy, "T:")) ||
                locks.Select(l => l.Field).Distinct(StringComparer.Ordinal).Count() != locks.Count)
                throw Invalid("synchronization.locks requires at most 1000 unique F: lock fields; indexed_by is a T: type ID.");
            Dictionary<string, SynchronizationLock> declared = locks.ToDictionary(l => l.Field, StringComparer.Ordinal);
            foreach (SynchronizationLock item in locks)
                if (item.MayHold is { } held && (held.Count > 1000 || held.Distinct(StringComparer.Ordinal).Count() != held.Count ||
                    held.Any(h => h == item.Field || !declared.ContainsKey(h))))
                    throw Invalid("may_hold lists other declared locks without duplicates: " + item.Field);
            HashSet<string> active = new(StringComparer.Ordinal), done = new(StringComparer.Ordinal);
            void Visit(string field)
            {
                if (done.Contains(field)) return;
                if (!active.Add(field)) throw Invalid("The declared may_hold lock order is cyclic at " + field + ".");
                foreach (string next in declared[field].MayHold ?? []) Visit(next);
                active.Remove(field);
                done.Add(field);
            }
            foreach (string field in declared.Keys) Visit(field);
        }
        if (declaration.Spawns is { } spawns && (spawns.Count > 1000 || spawns.Any(s => s?.Site == null ||
                !Markup.IsSelector(s.Site) || !SpawnDispositions.Contains(s.Disposition, StringComparer.Ordinal) ||
                string.IsNullOrWhiteSpace(s.Reason))))
            throw Invalid("synchronization.spawns allows at most 1000 entries, each with a source selector site, joined or detached, and a reason.");
        if (declaration.Waits is { } waits && (waits.Count > 1000 || waits.Any(w => w?.Site == null ||
                !Markup.IsSelector(w.Site) || !WaitKinds.Contains(w.Kind, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(w.Reason))))
            throw Invalid("synchronization.waits allows at most 1000 entries, each with a source selector site, a kind (" +
                string.Join(", ", WaitKinds) + ") and a reason.");
    }

    private static bool Documentation(string? id, string prefix) => id != null && id.Length > prefix.Length &&
        id.StartsWith(prefix, StringComparison.Ordinal);

    public static int Run(CliOptions options, string[] args)
    {
        string? jar = options.Optional("jar") is string selected ? FilePaths.Full(selected) : null;
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        SynchronizationRegionReport report = Analyze(FilePaths.Full(options.Required("scope")), options.Many("reference"),
            options.Has("define") ? options.Many("define") : null, options.Get("language-version", CSharpLanguages.Default), pins,
            jar == null ? null : (model, path) => TlaBackend.Parse(model, jar, options.Timeout, path));
        Program.PinTool(pins);
        if (jar != null)
        {
            Files.Pin(pins, jar, Files.Hash(jar));
            string adapter = Path.Combine(AppContext.BaseDirectory, "TlaIndex.java");
            Files.Pin(pins, adapter, Files.Hash(adapter));
        }
        ProjectWorkflow.Render(new SynchronizationResult(Schema.Version, "synchronization-result", report.Status, report, pins,
            report.Diagnostics, new(args, Environment.CurrentDirectory, typeof(Program).Assembly.Location), Boundary), options);
        return report.Status switch { "passed" => 0, "rejected" => 1, _ => 2 };
    }

    /// <summary>
    /// Loads the scope's region, primitives and environments, compiles them together and runs every region check. When the
    /// scope has TLA+ targets, instance and site checks resolve them through <paramref name="parse"/>; <paramref name="check"/>
    /// parses generated instance-expression modules without pinning them (defaults to <paramref name="parse"/>).
    /// </summary>
    public static SynchronizationRegionReport Analyze(string scopePath, IEnumerable<string> references, IEnumerable<string>? defines,
        string languageVersion, Dictionary<string, string> pins, Func<string, IReadOnlyList<string>, TlaDocument>? parse,
        Func<string, IReadOnlyList<string>, TlaDocument>? check = null)
    {
        scopePath = FilePaths.Full(scopePath);
        CorrespondenceConfiguration config = CorrespondenceInputs.Load(scopePath, pins);
        if (config.Synchronization is not { Region: not null } declaration)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Correspondence scope declares no synchronization region: " + scopePath);
        string root = Path.GetDirectoryName(scopePath)!;
        List<string> region = declaration.Region.Select(p => FilePaths.Full(p, root)).ToList();
        List<(string Path, PrimitiveManifest Manifest, List<string> Sources)> primitives = [];
        foreach (string file in declaration.Primitives.Select(p => FilePaths.Full(p, root)))
        {
            PrimitiveManifest manifest = Primitives.Load(file, pins);
            if (primitives.Any(p => p.Manifest.Id == manifest.Id))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Duplicate primitive ID in scope " + scopePath + ": " + manifest.Id);
            primitives.Add((file, manifest, manifest.Sources.Select(p => FilePaths.Full(p, Path.GetDirectoryName(file)!)).ToList()));
        }
        List<(string Path, EnvironmentManifest Manifest)> environments = [];
        foreach (string file in (declaration.Environments ?? []).Select(p => FilePaths.Full(p, root)))
        {
            EnvironmentManifest manifest = Environments.Load(file, pins);
            if (environments.Any(e => e.Manifest.Id == manifest.Id))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Duplicate environment ID in scope " + scopePath + ": " + manifest.Id);
            environments.Add((file, manifest));
        }
        List<string> files = region.Concat(primitives.SelectMany(p => p.Sources)).Distinct(FilePaths.Comparer).ToList();
        if (files.Any(f => !File.Exists(f)))
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Region and primitive sources must be existing explicit C# files.");
        CSharpScope source = CSharpScope.Parse(files, references, defines, languageVersion: languageVersion, annotations: false);
        foreach (var pin in source.Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
        return new Analysis(scopePath, declaration, region, source, primitives, environments, pins, parse, config, check ?? parse).Run();
    }

    internal static InputException Invalid(string message) => new(ErrorCode.InvalidAnnotation, message);

    private sealed class Spawn(SyntaxNode node, string kind, bool discarded)
    {
        public SyntaxNode Node { get; } = node;
        public string Kind { get; } = kind;
        public bool Discarded { get; set; } = discarded;
        public bool Awaited { get; set; }
    }

    private sealed record Entry(SyntaxNode? Node, string Description);

    private sealed partial class Analysis(string scope, SynchronizationDeclaration declaration, List<string> region, CSharpScope source,
        List<(string Path, PrimitiveManifest Manifest, List<string> Sources)> primitives,
        List<(string Path, EnvironmentManifest Manifest)> environments, Dictionary<string, string> pins,
        Func<string, IReadOnlyList<string>, TlaDocument>? parse, CorrespondenceConfiguration config,
        Func<string, IReadOnlyList<string>, TlaDocument>? check)
    {
        private readonly CSharpCompilation compilation = source.Compilation;
        private readonly List<Issue> issues = [];
        private readonly HashSet<string> reported = new(StringComparer.Ordinal);
        private readonly Dictionary<SyntaxTree, SemanticModel> models = [];
        private readonly Dictionary<SyntaxTree, List<TextSpan>> exempt = [];
        private readonly HashSet<SyntaxTree> regionTrees = [];
        private readonly Dictionary<INamedTypeSymbol, (PrimitiveManifest Manifest, Dictionary<string, PrimitiveOperation> Operations)> primitiveTypes =
            new(SymbolEqualityComparer.Default);
        private readonly Dictionary<INamedTypeSymbol, EnvironmentManifest> environmentTypes = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<string, Dictionary<ISymbol, EnvironmentOperation>> environmentOperations = new(StringComparer.Ordinal);
        private readonly Dictionary<IFieldSymbol, SynchronizationLock> lockFields = new(SymbolEqualityComparer.Default);
        private readonly HashSet<SyntaxNode> lockDeclarations = [];
        private readonly HashSet<string> usedLocks = new(StringComparer.Ordinal);
        private readonly List<(LockStatementSyntax Statement, SynchronizationLock Lock)> lockSites = [];
        private readonly List<Spawn> spawns = [];
        private readonly List<SyntaxNode> awaits = [];
        private readonly List<(SyntaxNode Node, ExpressionSyntax Value, string Kind)> discarded = [];
        private int lockStatements, primitiveSites, environmentSites;

        public SynchronizationRegionReport Run()
        {
            HashSet<string> regionFiles = region.ToHashSet(FilePaths.Comparer);
            foreach (SyntaxTree tree in compilation.SyntaxTrees.Where(t => regionFiles.Contains(t.FilePath))) regionTrees.Add(tree);
            List<RegionPrimitive> regionPrimitives = [];
            List<RegionEnvironment> regionEnvironments = [];
            List<Diagnostic> errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                foreach (Diagnostic error in errors.Take(100))
                {
                    FileLinePositionSpan span = error.Location.GetLineSpan();
                    issues.Add(new(ErrorCode.SynchronizationRegionIncomplete, Path.GetFileName(span.Path) + ":" +
                        (span.StartLinePosition.Line + 1) + ": " + error.Id + ": " + error.GetMessage(),
                        new(span.Path, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, span.StartLinePosition.Character + 1)));
                }
                return Report("incomplete", regionPrimitives, regionEnvironments);
            }
            ResolvePrimitives(regionPrimitives);
            ResolveEnvironments(regionEnvironments);
            ResolveLocks();
            foreach (SyntaxTree tree in regionTrees) Scan(tree);
            List<(WaitDisposition Entry, SyntaxNode? Node)> waits = Resolve(declaration.Waits ?? [], w => w.Site, "wait disposition");
            List<(SpawnDeclaration Entry, SyntaxNode? Node)> spawnEntries = Resolve(declaration.Spawns ?? [], s => s.Site, "spawn");
            foreach (var item in discarded)
            {
                ExpressionSyntax value = Strip(item.Value);
                if (spawns.FirstOrDefault(s => s.Node == value) is { } primary) primary.Discarded = true;
                else spawns.Add(new(item.Node, item.Kind, true));
            }
            Waits(waits);
            Spawns(spawnEntries);
            foreach (var site in lockSites) LockRegion(site.Statement, site.Lock);
            foreach (SynchronizationLock item in declaration.Locks ?? [])
                if (lockFields.Values.Contains(item) && !usedLocks.Contains(item.Field))
                    Declaration(ErrorCode.StaleSynchronizationDeclaration, "Declared lock " + item.Field + " is not the target of any lock statement in the region.");
            coverage = config.Targets.Any(t => t.Language == "tla") ? Instances() : null;
            return Report(issues.Any(i => i.Code == ErrorCode.SynchronizationRegionIncomplete.Id()) ? "incomplete" :
                issues.Any(i => i.Severity == "error") ? "rejected" : "passed", regionPrimitives, regionEnvironments);
        }

        private RegionCoverage? coverage;

        private SynchronizationRegionReport Report(string status, List<RegionPrimitive> regionPrimitives, List<RegionEnvironment> regionEnvironments)
        {
            string root = Path.GetDirectoryName(scope)!;
            return new(scope, status, region.Select(p => Path.GetRelativePath(root, p)).ToList(), regionPrimitives, regionEnvironments,
                (declaration.Locks ?? []).Select(l => l.Field).ToList(),
                new(awaits.Count, lockStatements, spawns.Count, primitiveSites, environmentSites, declaration.Waits?.Count ?? 0),
                issues.OrderBy(i => i.Location?.Path ?? "", FilePaths.Comparer).ThenBy(i => i.Location?.Line ?? 0)
                    .ThenBy(i => i.Code, StringComparer.Ordinal).ThenBy(i => i.Message, StringComparer.Ordinal).ToList(), coverage);
        }

        private SemanticModel Model(SyntaxTree tree)
        {
            if (!models.TryGetValue(tree, out SemanticModel? model)) models[tree] = model = compilation.GetSemanticModel(tree);
            return model;
        }

        private bool Exempt(SyntaxNode node) => exempt.TryGetValue(node.SyntaxTree, out List<TextSpan>? spans) &&
            spans.Any(s => s.Contains(node.Span));

        private void Report(ErrorCode code, SyntaxNode node, string message, string? key = null)
        {
            string text = Locate(node, Model(node.SyntaxTree), message);
            if (reported.Add(code.Id() + "|" + (key ?? text))) issues.Add(new(code, text, PlaceOf(node)));
        }

        // One raw-synchronization diagnostic per type and line, however many names on that line bind to it.
        private void Raw(SyntaxNode node, INamedTypeSymbol raw, string message) => Report(ErrorCode.RawSynchronization, node, message,
            node.SyntaxTree.FilePath + "|" + node.GetLocation().GetLineSpan().StartLinePosition.Line + "|" + raw.ToDisplayString());

        private void Declaration(ErrorCode code, string message)
        {
            string text = Path.GetFileName(scope) + ": " + message;
            if (reported.Add(code.Id() + "|" + text)) issues.Add(new(code, text, new(scope, 1, 1)));
        }

        private void ResolvePrimitives(List<RegionPrimitive> result)
        {
            foreach (var (_, manifest, sources) in primitives)
            {
                INamedTypeSymbol? type = null;
                List<GuaranteeStatus> guarantees = [];
                try
                {
                    type = Primitives.Type(compilation, manifest, sources);
                    _ = Primitives.Members(compilation, type, manifest, out List<string> missing);
                    foreach (string member in missing)
                        Report(ErrorCode.UndeclaredOperation, type.DeclaringSyntaxReferences[0].GetSyntax(),
                            "visible member " + member + " of primitive " + manifest.Id + " is not declared in its manifest.");
                }
                catch (InputException error) { Declaration(ErrorCode.StaleSynchronizationDeclaration, error.Message); }
                if (type != null)
                {
                    if (primitiveTypes.ContainsKey(type.OriginalDefinition))
                        Declaration(ErrorCode.StaleSynchronizationDeclaration, "Two primitives declare " + manifest.Type + ".");
                    primitiveTypes[type.OriginalDefinition] = (manifest, manifest.Operations.ToDictionary(o => o.Member, StringComparer.Ordinal));
                    foreach (SyntaxReference reference in type.DeclaringSyntaxReferences.Where(r => regionTrees.Contains(r.SyntaxTree)))
                    {
                        if (!exempt.TryGetValue(reference.SyntaxTree, out List<TextSpan>? spans)) exempt[reference.SyntaxTree] = spans = [];
                        spans.Add(reference.Span);
                    }
                    guarantees = Check(compilation, manifest, Primitives.IsLibrary(manifest) ? null : type);
                    foreach (GuaranteeStatus failed in guarantees.Where(g => g.Status == "failed"))
                        foreach (string finding in failed.Findings)
                        {
                            string text = finding + " Primitive " + manifest.Id + " claims " + failed.Guarantee + ".";
                            if (reported.Add(ErrorCode.GuaranteeCheckFailed.Id() + "|" + text))
                                issues.Add(new(ErrorCode.GuaranteeCheckFailed, text, PlaceOf(type.DeclaringSyntaxReferences[0].GetSyntax())));
                        }
                }
                result.Add(new(manifest.Id, manifest.PrimitiveKind, manifest.Type, Primitives.IsLibrary(manifest) ? "library" : "source", guarantees));
            }
        }

        private void ResolveEnvironments(List<RegionEnvironment> result)
        {
            foreach (var (path, manifest) in environments)
            {
                try
                {
                    Dictionary<ISymbol, EnvironmentOperation> operations = Environments.Resolve(compilation, manifest, out List<INamedTypeSymbol> types);
                    foreach (INamedTypeSymbol type in types)
                    {
                        if (primitiveTypes.ContainsKey(type.OriginalDefinition) || environmentTypes.ContainsKey(type.OriginalDefinition))
                            Declaration(ErrorCode.StaleSynchronizationDeclaration, "Type " + type.GetDocumentationCommentId() +
                                " is declared by more than one primitive or environment.");
                        environmentTypes[type.OriginalDefinition] = manifest;
                    }
                    environmentOperations[manifest.Id] = operations.ToDictionary(o => o.Key.OriginalDefinition, o => o.Value,
                        SymbolEqualityComparer.Default);
                }
                catch (InputException error) { Declaration(ErrorCode.StaleSynchronizationDeclaration, error.Message); }
                if (manifest.Module != null)
                {
                    if (parse == null)
                        throw new InputException(ErrorCode.WorkflowPrerequisiteMissing,
                            "Environment modules resolve through SANY; supply the pinned TLA+ JAR: " + manifest.Id);
                    try { Environments.ResolveModule(path, manifest, pins, parse); }
                    catch (InputException error) { Declaration(ErrorCode.StaleSynchronizationDeclaration, error.Message); }
                }
                result.Add(new(manifest.Id, manifest.EnvironmentKind, manifest.Types, path,
                    manifest.Module == null ? null : FilePaths.Full(manifest.Module, Path.GetDirectoryName(path)!),
                    manifest.Operations.Select(o => o.Member).ToList(),
                    manifest.Operations.Where(o => o.LockSafe).Select(o => o.Member).ToList(), manifest.Obligations, manifest.Evidence,
                    manifest.Evidence.Count > 0 ? "declared-evidence" : "unsupported-assumption"));
            }
        }

        private void ResolveLocks()
        {
            foreach (SynchronizationLock item in declaration.Locks ?? [])
            {
                List<IFieldSymbol> fields = DocumentationCommentId.GetSymbolsForDeclarationId(item.Field, compilation).OfType<IFieldSymbol>().ToList();
                if (fields.Count != 1 || fields[0].DeclaringSyntaxReferences.Length != 1 ||
                    !regionTrees.Contains(fields[0].DeclaringSyntaxReferences[0].SyntaxTree) ||
                    Exempt(fields[0].DeclaringSyntaxReferences[0].GetSyntax()))
                {
                    Declaration(ErrorCode.StaleSynchronizationDeclaration, "Declared lock " + item.Field + " does not resolve to one field declared in the region.");
                    continue;
                }
                lockFields[fields[0].OriginalDefinition] = item;
                if (fields[0].DeclaringSyntaxReferences[0].GetSyntax().Parent?.Parent is FieldDeclarationSyntax field) lockDeclarations.Add(field);
                if (item.IndexedBy != null &&
                    DocumentationCommentId.GetSymbolsForDeclarationId(item.IndexedBy, compilation).OfType<INamedTypeSymbol>().Count() != 1)
                    Declaration(ErrorCode.StaleSynchronizationDeclaration, "indexed_by " + item.IndexedBy + " of " + item.Field + " does not resolve to one type.");
            }
        }

        private void Scan(SyntaxTree tree)
        {
            SemanticModel model = Model(tree);
            foreach (SyntaxNode node in tree.GetRoot().DescendantNodes(n => !Exempt(n)))
            {
                if (Exempt(node)) continue;
                bool lockDeclaration = node.AncestorsAndSelf().Any(lockDeclarations.Contains);
                switch (node)
                {
                    case BaseTypeDeclarationSyntax type when model.GetDeclaredSymbol(type) is INamedTypeSymbol declared &&
                        declared.AllInterfaces.Any(i => IsType(i, "System.Threading.Tasks.Sources.IValueTaskSource", "System.Threading.Tasks.Sources.IValueTaskSource`1")):
                        Report(ErrorCode.RawSynchronization, type, "type implements IValueTaskSource; declare it as a primitive instead.");
                        break;
                    case FieldDeclarationSyntax field when field.Modifiers.Any(SyntaxKind.VolatileKeyword):
                        Report(ErrorCode.RawSynchronization, field, "volatile field; publish state through a declared primitive or lock.");
                        break;
                    case SimpleNameSyntax name:
                        Name(name, model, lockDeclaration);
                        break;
                    case BaseObjectCreationExpressionSyntax creation when model.GetSymbolInfo(creation).Symbol is IMethodSymbol constructor:
                        bool declaredOperation = Operation(constructor, creation);
                        if (!declaredOperation && !lockDeclaration && creation is ImplicitObjectCreationExpressionSyntax && Raw(constructor.ContainingType) is { } raw)
                            Raw(creation, raw, "uses raw synchronization type " + raw.ToDisplayString() + "; synchronize through a declared primitive or lock.");
                        if (IsType(constructor.ContainingType, "System.Threading.Thread")) spawns.Add(new(creation, "new Thread", false));
                        break;
                    case LockStatementSyntax statement:
                        lockStatements++;
                        if (model.GetSymbolInfo(statement.Expression).Symbol is IFieldSymbol target &&
                            lockFields.TryGetValue(target.OriginalDefinition, out SynchronizationLock? held))
                        {
                            usedLocks.Add(held.Field);
                            lockSites.Add((statement, held));
                        }
                        else Report(ErrorCode.RawSynchronization, statement, "lock (" + statement.Expression +
                            ") targets an object that is not a declared lock; declare the field under synchronization.locks.");
                        break;
                    case InvocationExpressionSyntax invocation when model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method:
                        if (SpawnKind(method) is string kind) spawns.Add(new(invocation, kind, false));
                        else if (method.OriginalDefinition is { IsAsync: true, ReturnsVoid: true })
                            spawns.Add(new(invocation, "async void call", true));
                        break;
                    case AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.Text: "_" } left } assignment
                        when model.GetSymbolInfo(left).Symbol is null or IDiscardSymbol && IsTask(model.GetTypeInfo(assignment.Right).Type):
                        discarded.Add((assignment, assignment.Right, "discarded task"));
                        break;
                    case ExpressionStatementSyntax statement when statement.Expression is not (AssignmentExpressionSyntax or AwaitExpressionSyntax) &&
                        IsTask(model.GetTypeInfo(statement.Expression).Type):
                        discarded.Add((statement.Expression, statement.Expression, "unawaited task"));
                        break;
                }
                if (IsAwait(node)) awaits.Add(node);
            }
        }

        private void Name(SimpleNameSyntax name, SemanticModel model, bool lockDeclaration)
        {
            SymbolInfo info = model.GetSymbolInfo(name);
            ISymbol? symbol = info.Symbol ?? (info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] : null);
            if (symbol == null) return;
            if (symbol is IMethodSymbol or IPropertySymbol or IEventSymbol or IFieldSymbol && Operation(symbol, name)) return;
            if (!lockDeclaration && RawOf(symbol) is { } raw)
                Raw(name, raw, "uses raw synchronization type " + raw.ToDisplayString() + "; synchronize through a declared primitive or lock.");
        }

        /// <summary>Counts declared primitive/environment operation sites and reports undeclared members; true for declared operations.</summary>
        private bool Operation(ISymbol symbol, SyntaxNode node)
        {
            if (Classify(symbol, node) is not { } member) return false;
            ISymbol original = symbol.OriginalDefinition;
            if (member.Primitive is { } primitive)
            {
                if (member.Operation != null || original.IsImplicitlyDeclared && !Primitives.IsLibrary(primitive))
                {
                    primitiveSites++;
                    if (member.Operation != null) Site(node, primitive, member.Operation);
                    return true;
                }
                if (original is IMethodSymbol or IPropertySymbol or IEventSymbol)
                    Report(ErrorCode.UndeclaredOperation, node, "uses " + member.Id + " of primitive " + primitive.Id +
                        ", which its manifest does not declare.");
                return true;
            }
            if (member.EnvironmentOperation != null) { environmentSites++; return true; }
            if (original is IMethodSymbol or IPropertySymbol or IEventSymbol)
                Report(ErrorCode.UndeclaredOperation, node, "uses " + member.Id + " of environment " + member.Environment!.Id +
                    ", which its manifest does not declare.");
            return true;
        }

        private sealed record Declared(string Id, PrimitiveManifest? Primitive, PrimitiveOperation? Operation,
            EnvironmentManifest? Environment, EnvironmentOperation? EnvironmentOperation);

        /// <summary>
        /// The declared owner of a member use: its containing type, or the receiver's static type when that is a declared
        /// environment or library primitive type and the member is inherited from a base type or interface.
        /// </summary>
        private Declared? Classify(ISymbol symbol, SyntaxNode node)
        {
            ISymbol original = symbol.OriginalDefinition;
            if (original.ContainingType?.OriginalDefinition is not { } owner) return null;
            string id = original.GetDocumentationCommentId() ?? original.ToDisplayString();
            if (!primitiveTypes.ContainsKey(owner) && !environmentTypes.ContainsKey(owner) && !original.IsStatic &&
                symbol is not IMethodSymbol { IsExtensionMethod: true } && Receiver(node) is { } receiver &&
                Model(node.SyntaxTree).GetTypeInfo(receiver).Type is INamedTypeSymbol type &&
                (environmentTypes.ContainsKey(type.OriginalDefinition) || primitiveTypes.TryGetValue(type.OriginalDefinition, out var library) &&
                    Primitives.IsLibrary(library.Manifest)) && Primitives.IsMemberOf(original, type))
                owner = type.OriginalDefinition;
            if (primitiveTypes.TryGetValue(owner, out var primitive))
                return new(id, primitive.Manifest, primitive.Operations.GetValueOrDefault(id), null, null);
            if (environmentTypes.TryGetValue(owner, out EnvironmentManifest? environment))
                return new(id, null, null, environment, environmentOperations.GetValueOrDefault(environment.Id)?.GetValueOrDefault(original));
            return null;
        }

        /// <summary>The explicit receiver of a member name: <c>r.M</c>, or <c>r?.M</c> through its conditional access.</summary>
        private static ExpressionSyntax? Receiver(SyntaxNode node) => node switch {
            SimpleNameSyntax { Parent: MemberAccessExpressionSyntax access } name when access.Name == name => access.Expression,
            SimpleNameSyntax { Parent: MemberBindingExpressionSyntax binding } =>
                binding.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>()?.Expression,
            _ => null
        };

        private INamedTypeSymbol? Raw(ITypeSymbol? type) =>
            type is INamedTypeSymbol named && !primitiveTypes.ContainsKey(named.OriginalDefinition) &&
            !environmentTypes.ContainsKey(named.OriginalDefinition) &&
            (RawTypes.Contains(MetadataName(named.OriginalDefinition), StringComparer.Ordinal) ||
                named.ContainingNamespace?.ToDisplayString() == "System.Threading.Channels") ? named : null;

        private INamedTypeSymbol? RawOf(ISymbol symbol) => symbol switch {
            INamedTypeSymbol type => Raw(type),
            IMethodSymbol method => Raw(method.ContainingType) ?? Raw(method.ReturnType),
            IPropertySymbol property => Raw(property.ContainingType) ?? Raw(property.Type),
            IFieldSymbol field => Raw(field.ContainingType) ?? Raw(field.Type),
            IEventSymbol @event => Raw(@event.ContainingType),
            ILocalSymbol local => Raw(local.Type),
            IParameterSymbol parameter => Raw(parameter.Type),
            _ => null
        };

        private static string? SpawnKind(IMethodSymbol method)
        {
            string type = MetadataName(method.ContainingType.OriginalDefinition);
            return type == "System.Threading.Tasks.Task" && method.Name == "Run" ? "Task.Run" :
                type is "System.Threading.Tasks.TaskFactory" or "System.Threading.Tasks.TaskFactory`1" && method.Name == "StartNew" ? "TaskFactory.StartNew" :
                type == "System.Threading.ThreadPool" && method.Name.Contains("QueueUserWorkItem", StringComparison.Ordinal) ? "ThreadPool.QueueUserWorkItem" :
                null;
        }

        private static bool IsTask(ITypeSymbol? type) => DerivesFrom(type, "System.Threading.Tasks.Task") ||
            IsType(type, "System.Threading.Tasks.ValueTask", "System.Threading.Tasks.ValueTask`1");

        /// <summary>The member name an expression ends in (<c>a.b.M(...)</c>, <c>a?.M()</c>), or the expression itself.</summary>
        private static SyntaxNode NameOf(ExpressionSyntax expression) => expression switch {
            InvocationExpressionSyntax invocation => NameOf(invocation.Expression),
            MemberAccessExpressionSyntax access => access.Name,
            MemberBindingExpressionSyntax binding => binding.Name,
            ConditionalAccessExpressionSyntax conditional => NameOf(conditional.WhenNotNull),
            ParenthesizedExpressionSyntax parenthesized => NameOf(parenthesized.Expression),
            _ => expression
        };

        private static ExpressionSyntax Strip(ExpressionSyntax expression)
        {
            while (true)
            {
                if (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression;
                else if (expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ConfigureAwait" } access })
                    expression = access.Expression;
                else return expression;
            }
        }

        private bool IsRegionMethod(IMethodSymbol? method) => method?.OriginalDefinition is { DeclaringSyntaxReferences.Length: > 0 } original &&
            original.DeclaringSyntaxReferences.All(r => regionTrees.Contains(r.SyntaxTree) && !Exempt(r.GetSyntax()));

        private List<(T Entry, SyntaxNode? Node)> Resolve<T>(List<T> entries, Func<T, PreparationTarget> site, string label)
        {
            List<(T, SyntaxNode?)> result = [];
            foreach (T entry in entries)
            {
                PreparationTarget target = site(entry);
                SyntaxNode? node = null;
                try
                {
                    node = SourcePreparation.CSharpTarget(compilation, null, target);
                    if (!regionTrees.Contains(node.SyntaxTree) || Exempt(node))
                    {
                        Declaration(ErrorCode.StaleSynchronizationDeclaration, "The " + label + " site in " + target.Symbol + " is outside the region.");
                        node = null;
                    }
                }
                catch (InputException error)
                {
                    Declaration(ErrorCode.StaleSynchronizationDeclaration, "The " + label + " site in " + target.Symbol +
                        " does not resolve: " + error.Message);
                }
                result.Add((entry, node));
            }
            return result;
        }

        private static bool Same(SyntaxNode? a, SyntaxNode b) => a != null && a.SyntaxTree == b.SyntaxTree && a.Span == b.Span &&
            a.RawKind == b.RawKind;

        private void Waits(List<(WaitDisposition Entry, SyntaxNode? Node)> entries)
        {
            int[] uses = new int[entries.Count];
            foreach (SyntaxNode node in awaits)
            {
                SemanticModel model = Model(node.SyntaxTree);
                string? accepted = null, description;
                if (node is AwaitExpressionSyntax expression)
                {
                    ExpressionSyntax operand = Strip(expression.Expression);
                    if (spawns.FirstOrDefault(s => s.Node == operand) is { } spawn)
                    {
                        spawn.Awaited = true;
                        accepted = description = "spawn";
                    }
                    else (accepted, description) = Target(operand, model.GetSymbolInfo(operand).Symbol);
                }
                else description = "an implicit await (await foreach or await using) is not a declared wait.";
                List<int> covering = Enumerable.Range(0, entries.Count).Where(i => Same(entries[i].Node, node)).ToList();
                foreach (int i in covering) uses[i]++;
                if (accepted != null)
                {
                    if (covering.Count > 0)
                        Report(ErrorCode.StaleSynchronizationDeclaration, node, "a wait disposition covers an await that is already a declared wait (" + accepted + ").");
                }
                else if (covering.Count > 1)
                    Report(ErrorCode.StaleSynchronizationDeclaration, node, "the await is covered by " + covering.Count + " wait dispositions.");
                else if (covering.Count == 0)
                    Report(ErrorCode.UndeclaredWait, node, description + " Await a primitive wait, an environment operation, a region method or a declared spawn, or add a synchronization.waits disposition.");
            }
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Node != null && uses[i] == 0)
                    Declaration(ErrorCode.StaleSynchronizationDeclaration, "The wait disposition in " + entries[i].Entry.Site.Symbol +
                        " selects no await in the region.");
        }

        private (string? Accepted, string Description) Target(ExpressionSyntax operand, ISymbol? symbol)
        {
            ISymbol? original = symbol?.OriginalDefinition;
            string id = original?.GetDocumentationCommentId() ?? operand.ToString();
            if (symbol is IMethodSymbol or IPropertySymbol && Classify(symbol, NameOf(operand)) is { } member)
            {
                if (member.Primitive is { } primitive)
                {
                    if (member.Operation is not { } operation) return ("undeclared operation", "");
                    return operation.Role == "wait" ? ("primitive wait", "") :
                        (null, "awaits " + id + " of primitive " + primitive.Id + " with role " + operation.Role + "; only wait operations may be awaited.");
                }
                return (member.EnvironmentOperation != null ? "environment operation" : "undeclared operation", "");
            }
            return original switch {
                IMethodSymbol method when operand is InvocationExpressionSyntax && IsRegionMethod(method) => ("region call", ""),
                IMethodSymbol => (null, "awaits the result of " + id + ", which is not a primitive wait, an environment operation or a region method."),
                IFieldSymbol => (null, "awaits field " + id + "."),
                IPropertySymbol => (null, "awaits property " + id + "."),
                ILocalSymbol local => (null, "awaits local " + local.Name + "."),
                IParameterSymbol parameter => (null, "awaits parameter " + parameter.Name + "."),
                _ => (null, "awaits " + operand + ".")
            };
        }

        private void Spawns(List<(SpawnDeclaration Entry, SyntaxNode? Node)> entries)
        {
            static StatementSyntax? Statement(SyntaxNode node) => node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
            static bool Matches(SyntaxNode? selected, SyntaxNode site) => selected != null && selected.SyntaxTree == site.SyntaxTree &&
                selected.Span.Contains(site.Span) && Statement(selected) is { } statement && statement == Statement(site);
            int[] uses = new int[entries.Count];
            foreach (Spawn site in spawns)
            {
                List<int> matching = Enumerable.Range(0, entries.Count).Where(i => Matches(entries[i].Node, site.Node)).ToList();
                foreach (int i in matching) uses[i]++;
                if (matching.Count == 0)
                {
                    Report(ErrorCode.UndeclaredSpawn, site.Node, "undeclared spawn (" + site.Kind + "); declare it under synchronization.spawns as joined or detached with a reason.");
                    continue;
                }
                if (matching.Count > 1)
                {
                    Report(ErrorCode.StaleSynchronizationDeclaration, site.Node, "the spawn site is matched by " + matching.Count + " spawns entries.");
                    continue;
                }
                string disposition = entries[matching[0]].Entry.Disposition;
                if (site.Awaited && disposition == "detached")
                    Report(ErrorCode.UndeclaredSpawn, site.Node, "the spawn (" + site.Kind + ") is awaited here but declared detached.");
                if (disposition == "joined" && (site.Discarded || site.Kind is "ThreadPool.QueueUserWorkItem"))
                    Report(ErrorCode.UndeclaredSpawn, site.Node, "the spawn (" + site.Kind + ") leaves no handle to join but is declared joined.");
            }
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Node != null && uses[i] != 1)
                    Declaration(ErrorCode.StaleSynchronizationDeclaration, "The spawns entry in " + entries[i].Entry.Site.Symbol +
                        (uses[i] == 0 ? " matches no spawn site." : " matches " + uses[i] + " spawn sites."));
        }

        /// <summary>
        /// Checks a lock body and, transitively, the bodies of region methods, constructors and accessors it calls.
        /// Each callee is visited once per lock statement (cycle guard); deferred lambdas and local functions are not entered
        /// unless called. Called async methods are checked in full, conservatively including code after their first await.
        /// </summary>
        private void LockRegion(LockStatementSyntax statement, SynchronizationLock held)
        {
            HashSet<ISymbol> visited = new(SymbolEqualityComparer.Default);
            FileLinePositionSpan at = statement.GetLocation().GetLineSpan();
            string lockText = " inside lock " + held.Field + " (" + Path.GetFileName(at.Path) + ":" + (at.StartLinePosition.Line + 1);
            void Walk(SyntaxNode body, string? via)
            {
                SemanticModel model = Model(body.SyntaxTree);
                string inside = lockText + (via == null ? ")" : " via " + via + ")");
                foreach (SyntaxNode node in body.DescendantNodesAndSelf(n => n == body ||
                    n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) && !Exempt(n)))
                {
                    if (node != body && node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax || Exempt(node)) continue;
                    if (node is LockStatementSyntax nested && model.GetSymbolInfo(nested.Expression).Symbol is IFieldSymbol field &&
                        lockFields.TryGetValue(field.OriginalDefinition, out SynchronizationLock? inner) &&
                        !(held.MayHold ?? []).Contains(inner.Field, StringComparer.Ordinal))
                        Report(ErrorCode.LockRegionViolation, nested, "acquires " + inner.Field + inside + ", which is not in its may_hold list.");
                    if (node is InvocationExpressionSyntax invocation &&
                        model.GetSymbolInfo(invocation).Symbol is IMethodSymbol { MethodKind: MethodKind.DelegateInvoke })
                        Report(ErrorCode.LockRegionViolation, invocation, "invokes delegate " + (invocation.Expression is MemberBindingExpressionSyntax binding &&
                            invocation.FirstAncestorOrSelf<ConditionalAccessExpressionSyntax>() is { } access
                                ? access.Expression + "?" + binding : invocation.Expression.ToString()) + inside + "; foreign code cannot run under a lock.");
                    ISymbol? called = node switch {
                        SimpleNameSyntax name when model.GetSymbolInfo(name).Symbol is { } symbol && Called(name, symbol) => symbol,
                        BaseObjectCreationExpressionSyntax creation => model.GetSymbolInfo(creation).Symbol,
                        _ => null
                    };
                    if (called == null) continue;
                    Operation(called, node, inside);
                    IEnumerable<IMethodSymbol?> methods = called switch {
                        IMethodSymbol method => [method], IPropertySymbol property => [property.GetMethod, property.SetMethod], _ => []
                    };
                    foreach (IMethodSymbol method in methods.OfType<IMethodSymbol>().Where(IsRegionMethod))
                    {
                        if (!visited.Add(method.OriginalDefinition)) continue;
                        string id = method.OriginalDefinition.GetDocumentationCommentId() ?? method.Name;
                        foreach (SyntaxNode declaration in method.OriginalDefinition.DeclaringSyntaxReferences.Select(r => r.GetSyntax()))
                            if (declaration switch {
                                BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                                LocalFunctionStatementSyntax l => (SyntaxNode?)l.Body ?? l.ExpressionBody,
                                AccessorDeclarationSyntax a => (SyntaxNode?)a.Body ?? a.ExpressionBody,
                                ArrowExpressionClauseSyntax arrow => arrow,
                                _ => null } is { } callee)
                                Walk(callee, via == null ? id : via + " -> " + id);
                    }
                }
            }
            Walk(statement.Statement, null);
        }

        private static bool Called(SimpleNameSyntax name, ISymbol symbol)
        {
            if (symbol is IPropertySymbol) return true;
            if (symbol is not IMethodSymbol) return false;
            SyntaxNode invoked = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access :
                name.Parent is MemberBindingExpressionSyntax binding ? binding : name;
            return invoked.Parent is InvocationExpressionSyntax invocation && invocation.Expression == invoked;
        }

        private void Operation(ISymbol called, SyntaxNode node, string inside)
        {
            if (Classify(called, node) is not { } member) return;
            if (member.Primitive is { } primitive && member.Operation is { } operation)
            {
                List<string> guarantees = primitive.Guarantees ?? [];
                string name = "calls " + member.Id + " of primitive " + primitive.Id + inside;
                string? reason = operation.InlineCallbacks ? "the operation declares inline_callbacks" :
                    operation.Role == "wait" ? "it is a wait operation" :
                    ResumingRoles.Contains(operation.Role) && HasWaiters(primitive) && !guarantees.Contains("signals-async")
                        ? "role " + operation.Role + " can resume waiters inline and the primitive does not claim signals-async" : null;
                if (reason != null) Report(ErrorCode.LockRegionViolation, node, name + "; " + reason + ".");
            }
            else if (member.EnvironmentOperation is { LockSafe: false })
                Report(ErrorCode.LockRegionViolation, node, "calls environment operation " + member.Id + " of " + member.Environment!.Id + inside +
                    "; it is not declared lock_safe.");
        }

        /// <summary>Only a primitive with a declared wait operation has waiters a release, signal or completion could resume.</summary>
        private static bool HasWaiters(PrimitiveManifest primitive) => primitive.Operations.Any(o => o.Role == "wait");
    }
}
