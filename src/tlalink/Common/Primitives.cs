using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace TlaLink;

/// <summary>
/// Declared synchronization primitives: manifest loading, stage selection, Roslyn/SANY resolution and
/// inspection-support fingerprints. Guarantee discharge checks live in <see cref="SynchronizationGuarantees"/>.
/// </summary>
public static class Primitives
{
    internal static readonly string[] Kinds = ["register", "latch", "once", "admission-gate", "counting-resource",
        "channel", "cancellation-source", "custom"];
    internal static readonly string[] Roles = ["create", "query", "acquire", "try-acquire", "release", "signal", "wait",
        "close", "complete", "enqueue", "dequeue", "pure"];
    internal static readonly string[] Guarantees = ["signals-async", "non-blocking", "fifo-waiters", "cancellable-wait"];
    private static readonly string[] MutatingRoles = ["acquire", "try-acquire", "release", "signal", "close", "complete",
        "enqueue", "dequeue"];

    private sealed record Declared(string Path, string Sha256, Dictionary<string, (string Path, string Sha256, PrimitiveManifest Manifest)> Manifests);

    internal static string LibraryDirectory => FilePaths.Full(Path.Combine(AppContext.BaseDirectory, "primitives"));

    internal static string LibraryModule(string kind)
    {
        string name = "TlaLink" + string.Concat(kind.Split('-').Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        string path = Path.Combine(LibraryDirectory, name + ".tla");
        if (!File.Exists(path))
            throw new InputException(ErrorCode.WorkflowPrerequisiteMissing, "Shipped primitive kind module is missing: " + path);
        return path;
    }

    /// <summary>A library primitive declares a referenced (metadata) type and no sources.</summary>
    internal static bool IsLibrary(PrimitiveManifest manifest) => manifest.Sources.Count == 0;

    public static PrimitiveManifest Load(string path, Dictionary<string, string> pins)
    {
        path = FilePaths.Full(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024)
            throw Invalid("Primitive manifests must be existing files of at most 16 MiB: " + path);
        string text = Files.ReadPinned(path, pins);
        try
        {
            Files.RequireSchema(JsonSerializer.Deserialize<JsonElement>(text, ProjectInputs.Json), "primitive");
            PrimitiveManifest manifest = JsonSerializer.Deserialize<PrimitiveManifest>(text, ProjectInputs.Json)
                ?? throw Invalid("Empty primitive manifest: " + path);
            Validate(manifest);
            return manifest;
        }
        catch (JsonException error) { throw Invalid(path + ": " + error.Message); }
        catch (InputException error) { throw new InputException(error.Code ?? ErrorCode.InvalidWorkflowInput, path + ": " + error.Message); }
    }

    public static void Validate(PrimitiveManifest manifest)
    {
        bool Name(string? value, bool conditional = false) => value != null &&
            Regex.IsMatch(value, conditional ? @"^[A-Za-z_][A-Za-z0-9_]*\??$" : "^[A-Za-z_][A-Za-z0-9_]*$");
        if (manifest.SchemaVersion != Schema.Version || manifest.Kind != "primitive" || !Schema.IsStableId(manifest.Id) ||
            !manifest.Type.StartsWith("T:", StringComparison.Ordinal) || manifest.Type.Length < 3)
            throw Invalid("Primitive manifests require schema/kind, a stable id and a T: type documentation ID.");
        if (manifest.Sources.Count > 1000 || manifest.Sources.Any(p => string.IsNullOrWhiteSpace(p) ||
                !Path.GetExtension(p).Equals(".cs", StringComparison.OrdinalIgnoreCase)) ||
            manifest.Sources.Distinct(FilePaths.Comparer).Count() != manifest.Sources.Count)
            throw Invalid("Primitive sources must be at most 1000 unique explicit C# files; an empty list declares a library type.");
        if (!Kinds.Contains(manifest.PrimitiveKind, StringComparer.Ordinal))
            throw Invalid("Unsupported primitive_kind: " + manifest.PrimitiveKind);
        if ((manifest.PrimitiveKind == "custom") != (manifest.Module != null) || manifest.Module != null &&
            (string.IsNullOrWhiteSpace(manifest.Module) || !Path.GetExtension(manifest.Module).Equals(".tla", StringComparison.OrdinalIgnoreCase)))
            throw Invalid("A custom primitive requires a .tla module; standard kinds use the shipped module.");
        if (manifest.Operations.Count is < 1 or > 1000 || manifest.Operations.Any(o => o == null))
            throw Invalid("Primitives require 1..1000 declared operations.");
        HashSet<string> members = new(StringComparer.Ordinal);
        foreach (PrimitiveOperation operation in manifest.Operations)
        {
            if (!(operation.Member.StartsWith("M:", StringComparison.Ordinal) || operation.Member.StartsWith("P:", StringComparison.Ordinal)) ||
                operation.Member.Length < 3)
                throw Invalid("Operation members are M: or P: documentation IDs: " + operation.Member);
            if (!members.Add(operation.Member)) throw Invalid("Duplicate primitive operation member: " + operation.Member);
            if (!Roles.Contains(operation.Role, StringComparer.Ordinal))
                throw Invalid("Unsupported operation role: " + operation.Member + ": " + operation.Role);
            if (operation.Actions is { Count: > 100 } || operation.Actions?.Any(a => !Name(a, true)) == true ||
                operation.Fails is { Count: > 100 } || operation.Fails?.Any(a => !Name(a)) == true ||
                operation.Fails?.Distinct(StringComparer.Ordinal).Count() != operation.Fails?.Count ||
                operation.Precondition != null && !Name(operation.Precondition))
                throw Invalid("Operation actions, fails and preconditions name module operators: " + operation.Member);
            if ((operation.Role == "wait") != (operation.Wait != null) || operation.Wait != null && !Name(operation.Wait))
                throw Invalid("A wait predicate is required exactly for wait operations: " + operation.Member);
            if (MutatingRoles.Contains(operation.Role, StringComparer.Ordinal) && operation.Actions is not { Count: > 0 })
                throw Invalid("State-changing operations map to at least one kind action: " + operation.Member);
            if (operation.InlineCallbacks && operation.Role is "create" or "query")
                throw Invalid("Constructors and queries cannot declare inline_callbacks: " + operation.Member);
            if (manifest.PrimitiveKind == "cancellation-source" && !operation.InlineCallbacks &&
                operation.Actions?.Any(a => a.TrimEnd('?') == "Cancel") == true)
                throw Invalid("Cancel runs registered callbacks on the caller's stack; declare inline_callbacks: " + operation.Member);
        }
        List<string> guarantees = manifest.Guarantees ?? [];
        if (guarantees.Any(g => !Guarantees.Contains(g, StringComparer.Ordinal)) ||
            guarantees.Distinct(StringComparer.Ordinal).Count() != guarantees.Count)
            throw Invalid("Guarantees must be unique entries of: " + string.Join(", ", Guarantees));
        if (manifest.Verification.Scenarios.Count > 100 ||
            manifest.Verification.Scenarios.Count == 0 && !IsLibrary(manifest) ||
            manifest.Verification.Scenarios.Any(s => !Schema.IsStableId(s)) ||
            manifest.Verification.Scenarios.Distinct(StringComparer.Ordinal).Count() != manifest.Verification.Scenarios.Count ||
            !Schema.IsStableId(manifest.Verification.Inspection))
            throw Invalid("Verification requires unique scenario IDs (at least one for a source primitive) and one inspection obligation.");
        List<string> dependencies = manifest.DependsOn ?? [];
        if (dependencies.Any(d => !Schema.IsStableId(d) || d == manifest.Id) ||
            dependencies.Distinct(StringComparer.Ordinal).Count() != dependencies.Count)
            throw Invalid("depends_on lists unique other primitive IDs.");
        if (manifest.Assumptions.Any(string.IsNullOrWhiteSpace) || manifest.Limits.Count == 0 ||
            manifest.Limits.Any(string.IsNullOrWhiteSpace))
            throw Invalid("Primitives require nonempty assumption entries and at least one limit.");
        PrimitiveStateMapping.Validate(manifest);
    }

    private static Declared Scope(string path, Dictionary<string, Declared> cache)
    {
        if (cache.TryGetValue(path, out Declared? known)) return known;
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        CorrespondenceConfiguration config = CorrespondenceInputs.Load(path, pins);
        List<string> files = config.Synchronization?.Primitives
            ?? throw Invalid("Correspondence scope declares no synchronization primitives: " + path);
        Dictionary<string, (string, string, PrimitiveManifest)> manifests = new(StringComparer.Ordinal);
        foreach (string file in files.Select(p => FilePaths.Full(p, Path.GetDirectoryName(path)!)))
        {
            Dictionary<string, string> manifestPins = new(FilePaths.Comparer);
            PrimitiveManifest manifest = Load(file, manifestPins);
            if (!manifests.TryAdd(manifest.Id, (file, manifestPins[file], manifest)))
                throw Invalid("Duplicate primitive ID in scope " + path + ": " + manifest.Id);
        }
        HashSet<string> active = new(StringComparer.Ordinal), done = new(StringComparer.Ordinal);
        void Visit(PrimitiveManifest manifest)
        {
            if (done.Contains(manifest.Id)) return;
            if (!active.Add(manifest.Id)) throw Invalid("Cyclic primitive dependency: " + manifest.Id);
            foreach (string id in manifest.DependsOn ?? [])
                Visit(manifests.TryGetValue(id, out var dependency) ? dependency.Item3
                    : throw Invalid("Unknown primitive dependency " + id + " of " + manifest.Id));
            active.Remove(manifest.Id);
            done.Add(manifest.Id);
        }
        foreach (var entry in manifests.Values) Visit(entry.Item3);
        Declared declared = new(path, pins[path], manifests);
        cache.Add(path, declared);
        return declared;
    }

    internal static List<SelectedPrimitive> Select(string projectPath, AnalysisProject project, IEnumerable<AnalysisStage> stages)
    {
        Dictionary<string, SelectedPrimitive> selected = new(StringComparer.Ordinal);
        Dictionary<string, Declared> scopes = new(FilePaths.Comparer);
        foreach (AnalysisStage stage in stages)
        {
            if (stage.Primitives == null) continue;
            if (stage.Kind is not ("tlc" or "trace") || stage.Primitives.Count is < 1 or > 1000 ||
                stage.Primitives.Any(s => s == null))
                throw Invalid("Explicit primitive uses are supported on TLC/trace stages only.");
            HashSet<string> uses = new(StringComparer.Ordinal);
            foreach (PrimitiveUse use in stage.Primitives)
            {
                Declared scope = Scope(ProjectInputs.Resolve(projectPath, use.Scope), scopes);
                if (use.Ids.Count is < 1 or > 1000 || use.Ids.Any(id => !Schema.IsStableId(id)))
                    throw Invalid("Primitive uses require explicit primitive IDs.");
                void Add(string id)
                {
                    if (!scope.Manifests.TryGetValue(id, out var entry))
                        throw Invalid("Undeclared primitive: " + id);
                    if (selected.TryGetValue(id, out SelectedPrimitive? previous))
                    {
                        if (!FilePaths.Comparer.Equals(previous.Scope, scope.Path))
                            throw Invalid("A primitive ID cannot name different scopes: " + id);
                        return;
                    }
                    PrimitiveManifest manifest = entry.Manifest;
                    string module = manifest.Module == null ? LibraryModule(manifest.PrimitiveKind)
                        : FilePaths.Full(manifest.Module, Path.GetDirectoryName(entry.Path)!);
                    if (!File.Exists(module)) throw Invalid("Custom primitive module does not exist: " + module);
                    selected.Add(id, new(scope.Path, scope.Sha256, entry.Path, entry.Sha256, manifest, module));
                    foreach (string dependency in manifest.DependsOn ?? []) Add(dependency);
                }
                foreach (string id in use.Ids)
                {
                    if (!uses.Add(id)) throw Invalid("Duplicate primitive use in stage " + stage.Id + ": " + id);
                    Add(id);
                }
            }
        }
        foreach (SelectedPrimitive item in selected.Values)
        {
            PrimitiveManifest manifest = item.Primitive;
            if (!project.Obligations.Any(o => o.Id == manifest.Verification.Inspection))
                throw Invalid("Unknown primitive inspection obligation: " + manifest.Verification.Inspection);
            foreach (string id in manifest.Verification.Scenarios)
                if (project.Scenarios.SingleOrDefault(s => s.Id == id) is not { } scenario ||
                    !scenario.Stages.Any(s => s.Primitives?.Any(u => u.Ids.Contains(manifest.Id, StringComparer.Ordinal) &&
                        FilePaths.Comparer.Equals(ProjectInputs.Resolve(projectPath, u.Scope), item.Scope)) == true))
                    throw Invalid("Primitive verification scenario must exist and directly use " + manifest.Id + ": " + id);
        }
        return selected.Values.OrderBy(s => s.Primitive.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Primitives whose current support a scenario's stages need; a scenario does not need support it establishes.</summary>
    internal static List<SelectedPrimitive> Required(string projectPath, AnalysisProject project, string scenario,
        IEnumerable<AnalysisStage> stages) =>
        Select(projectPath, project, stages).Where(p => !p.Primitive.Verification.Scenarios.Contains(scenario, StringComparer.Ordinal)).ToList();

    internal static IEnumerable<string> Inspections(string projectPath, AnalysisProject project, string scenario,
        IEnumerable<AnalysisStage> stages) =>
        Required(projectPath, project, scenario, stages).Select(p => p.Primitive.Verification.Inspection).Distinct(StringComparer.Ordinal);

    /// <summary>SANY/TLC module search path for a stage: the shipped library plus directly used custom module folders.</summary>
    internal static List<string> ModulePath(string projectPath, AnalysisProject project, AnalysisStage stage)
    {
        if (stage.Primitives == null) return [];
        HashSet<string> direct = stage.Primitives.SelectMany(u => u.Ids).ToHashSet(StringComparer.Ordinal);
        List<string> result = [LibraryDirectory];
        foreach (SelectedPrimitive item in Select(projectPath, project, [stage]).Where(p => direct.Contains(p.Primitive.Id)))
            if (item.Primitive.Module != null && !result.Contains(Path.GetDirectoryName(item.Module)!, FilePaths.Comparer))
                result.Add(Path.GetDirectoryName(item.Module)!);
        return result;
    }

    internal static List<string> Direct(string projectPath, AnalysisProject project, AnalysisStage stage)
    {
        HashSet<string> direct = (stage.Primitives ?? []).SelectMany(u => u.Ids).ToHashSet(StringComparer.Ordinal);
        return Select(projectPath, project, [stage]).Where(p => direct.Contains(p.Primitive.Id)).Select(p => p.Module).ToList();
    }

    /// <summary>Module search path for standalone link and review commands over a scope that declares primitives.</summary>
    internal static List<string> ModulePath(string scopePath, CorrespondenceConfiguration config, Dictionary<string, string> pins)
    {
        if (config.Synchronization == null) return [];
        List<string> result = [LibraryDirectory];
        foreach (string file in config.Synchronization.Primitives.Select(p => FilePaths.Full(p, Path.GetDirectoryName(scopePath)!)))
            if (Load(file, pins).Module is string module &&
                Path.GetDirectoryName(FilePaths.Full(module, Path.GetDirectoryName(file)!)) is string directory &&
                !result.Contains(directory, FilePaths.Comparer))
                result.Add(directory);
        return result;
    }

    internal static IEnumerable<string> Inputs(SelectedPrimitive item) =>
        item.Primitive.Sources.Select(p => FilePaths.Full(p, Path.GetDirectoryName(item.Manifest)!))
            .Concat([item.Scope, item.Manifest, item.Module]);

    internal static CSharpScope Source(string projectPath, AnalysisProject project, SelectedPrimitive item)
    {
        List<string> paths = item.Primitive.Sources.Select(p => FilePaths.Full(p, Path.GetDirectoryName(item.Manifest)!)).ToList();
        if (paths.Any(p => !File.Exists(p)))
            throw Invalid("Primitive sources must be existing, explicitly listed C# files: " + item.Primitive.Id);
        return CSharpScope.Parse(paths, project.Compiler.References.Select(p => ProjectInputs.Resolve(projectPath, p)),
            project.Compiler.Defines, languageVersion: project.Compiler.LanguageVersion, annotations: false);
    }

    internal static List<PrimitiveResolution> Inspect(string projectPath, AnalysisProject project,
        List<SelectedPrimitive> selected, Dictionary<string, string> pins,
        Func<string, IReadOnlyList<string>, TlaDocument> parse)
    {
        List<PrimitiveResolution> results = [];
        foreach (SelectedPrimitive item in selected)
        {
            PrimitiveManifest manifest = item.Primitive;
            CSharpScope source = Source(projectPath, project, item);
            if (source.Diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.UnresolvedCompilation)))
                throw Invalid("Primitive source context is incomplete: " + manifest.Id + ": " +
                    string.Join("; ", source.Diagnostics.Select(d => d.Message)));
            foreach (var pin in source.Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
            List<string> members = Members(source, manifest);
            _ = PrimitiveStateMapping.Check(source.Compilation, Type(source.Compilation, manifest, source.Texts.Keys), manifest);
            List<string> path = [LibraryDirectory];
            if (manifest.Module != null) path.Add(Path.GetDirectoryName(item.Module)!);
            TlaDocument module = parse(item.Module, path);
            if (!module.ParserSucceeded || module.Diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.StaleTranslation)))
                throw Invalid("Primitive kind module did not resolve through SANY: " + manifest.Id + ": " +
                    string.Join("; ", module.Diagnostics.Select(d => d.Message)));
            HashSet<string> operators = module.Endpoints.Where(e => e.Kind == "operator" && e.Arity >= 1)
                .Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
            foreach (PrimitiveOperation operation in manifest.Operations)
                foreach (string name in (operation.Actions ?? []).Select(a => a.TrimEnd('?')).Concat(operation.Fails ?? [])
                    .Concat(new[] { operation.Wait, operation.Precondition }.OfType<string>()))
                    if (!operators.Contains(name))
                        throw Invalid("Operator " + name + " of " + operation.Member + " is not a parameterized operator of " +
                            module.Module + ".");
            if (manifest.StateMapping != null && !module.Endpoints.Any(e => e.Kind == "operator" && e.Name == "View" && e.Arity == 1))
                throw new InputException(ErrorCode.InvalidStateMapping, "Primitive " + manifest.Id + " maps state but its kind module " + module.Module +
                    " defines no View(i) projection to compare it with.");
            Files.Pin(pins, item.Scope, item.ScopeSha256);
            Files.Pin(pins, item.Manifest, item.ManifestSha256);
            Files.Pin(pins, module.Path, module.Hash);
            foreach (var pin in module.DependencyFingerprints) Files.Pin(pins, pin.Key, pin.Value);
            results.Add(new(item.Scope, item.Manifest, manifest, item.Module, members, SynchronizationGuarantees.Check(source, manifest)));
        }
        return results;
    }

    /// <summary>Resolves the declared type: in its own sources for a source primitive, in metadata for a library primitive.</summary>
    internal static INamedTypeSymbol Type(Compilation compilation, PrimitiveManifest manifest, IEnumerable<string> sources)
    {
        List<INamedTypeSymbol> types = DocumentationCommentId.GetSymbolsForDeclarationId(manifest.Type, compilation)
            .OfType<INamedTypeSymbol>().ToList();
        HashSet<string> files = sources.ToHashSet(FilePaths.Comparer);
        if (IsLibrary(manifest))
        {
            if (types.Count != 1 || types[0].Locations.Any(l => l.IsInSource))
                throw Invalid("Library primitive type must resolve to exactly one referenced (metadata) type: " + manifest.Type);
        }
        else if (types.Count != 1 || !types[0].Locations.All(l => l.IsInSource && files.Contains(l.SourceTree!.FilePath)))
            throw Invalid("Primitive type must resolve to exactly one type declared in its sources: " + manifest.Type);
        return types[0];
    }

    /// <summary>
    /// A source primitive declares every public or internal method, constructor and property exactly once. A library
    /// primitive declares the members a region uses; each must be a member of the type (region checks enforce use).
    /// </summary>
    internal static List<string> Members(CSharpScope source, PrimitiveManifest manifest, IEnumerable<string>? sources = null)
    {
        List<string> declared = Members(source.Compilation, Type(source.Compilation, manifest, sources ?? source.Texts.Keys),
            manifest, out List<string> missing);
        if (missing.Count > 0) throw Invalid("Undeclared primitive members of " + manifest.Id + ": " + string.Join(", ", missing));
        return declared;
    }

    /// <summary>Resolved members; <paramref name="missing"/> lists visible source members the manifest does not declare.</summary>
    internal static List<string> Members(Compilation compilation, INamedTypeSymbol type, PrimitiveManifest manifest,
        out List<string> missing)
    {
        missing = [];
        if (IsLibrary(manifest))
        {
            foreach (PrimitiveOperation operation in manifest.Operations)
                if (DocumentationCommentId.GetSymbolsForDeclarationId(operation.Member, compilation)
                    .Where(s => s is IMethodSymbol or IPropertySymbol).ToList() is not [ISymbol member] || !IsMemberOf(member, type))
                    throw Invalid("Unknown primitive operation members of " + manifest.Id + ": " + operation.Member);
            return manifest.Operations.Select(o => o.Member).Order(StringComparer.Ordinal).ToList();
        }
        List<string> declared = [];
        foreach (ISymbol member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared || member.DeclaredAccessibility is not
                (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
                continue;
            if (member is IFieldSymbol { IsConst: false } or IEventSymbol)
                throw Invalid("Primitive state must be reached through declared operations, not visible fields or events: " +
                    member.GetDocumentationCommentId());
            if (member is IPropertySymbol || member is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor or
                MethodKind.UserDefinedOperator or MethodKind.Conversion })
                declared.Add(member.GetDocumentationCommentId() ?? throw Invalid("Primitive member has no documentation ID."));
        }
        HashSet<string> listed = manifest.Operations.Select(o => o.Member).ToHashSet(StringComparer.Ordinal);
        missing = declared.Where(m => !listed.Contains(m)).Order(StringComparer.Ordinal).ToList();
        List<string> unknown = listed.Where(m => !declared.Contains(m, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToList();
        if (unknown.Count > 0) throw Invalid("Unknown primitive operation members of " + manifest.Id + ": " + string.Join(", ", unknown));
        return declared.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>A member of the declared type itself, of one of its base types, or of an interface it implements.</summary>
    internal static bool IsMemberOf(ISymbol member, INamedTypeSymbol type)
    {
        if (member.ContainingType?.OriginalDefinition is not { } owner) return false;
        for (INamedTypeSymbol? current = type.OriginalDefinition; current != null; current = current.BaseType?.OriginalDefinition)
            if (SymbolEqualityComparer.Default.Equals(current, owner)) return true;
        return type.OriginalDefinition.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, owner));
    }

    internal static CurrentPrimitiveSupport? Support(string projectPath, AnalysisProject project, string inspection,
        Dictionary<string, string> files, bool validate)
    {
        List<SelectedPrimitive> selected = Select(projectPath, project, project.Scenarios.SelectMany(s => s.Stages))
            .Where(s => s.Primitive.Verification.Inspection == inspection).ToList();
        if (selected.Count == 0) return null;
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        foreach (SelectedPrimitive item in selected)
            foreach (var pin in Source(projectPath, project, item).Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
        string sourceHash = Schema.Hash(new PrimitiveSourceContext(project.Compiler,
            pins.OrderBy(p => p.Key, FilePaths.Comparer).Select(p => new PrimitivePin(
                FilePaths.IsAbsolute(p.Key) ? ReviewPath(projectPath, p.Key) : p.Key, p.Value)).ToList()));
        if (validate)
        {
            string jar = ProjectInputs.Resolve(projectPath, project.Tools.Jar?.Path
                ?? throw Invalid("Primitive inspections require a pinned TLA+ JAR."));
            if (!Files.Hash(jar).Equals(project.Tools.Jar!.Sha256, StringComparison.OrdinalIgnoreCase))
                throw Invalid("Primitive inspection JAR differs from the project tool pin.");
            Inspect(projectPath, project, selected, pins, (model, path) => TlaBackend.Parse(model, jar, 60, path));
            Program.PinTool(pins);
            Files.Pin(pins, jar, Files.Hash(jar));
            string adapter = Path.Combine(AppContext.BaseDirectory, "TlaIndex.java");
            Files.Pin(pins, adapter, Files.Hash(adapter));
        }
        foreach (SelectedPrimitive item in selected)
        {
            Files.Pin(pins, item.Scope, item.ScopeSha256);
            Files.Pin(pins, item.Manifest, item.ManifestSha256);
            Files.Pin(pins, item.Module, Files.Hash(item.Module));
        }
        IntegratedInputs.CheckPins(pins);
        foreach (var pin in pins.Where(p => FilePaths.IsAbsolute(p.Key))) Files.Pin(files, ReviewPath(projectPath, pin.Key), pin.Value);
        return new(Schema.Hash(selected.Select(s => new PrimitiveIdentity(ReviewPath(projectPath, s.Scope),
                ReviewPath(projectPath, s.Manifest), ReviewPath(projectPath, s.Module), s.Primitive))),
            sourceHash, files.Keys.Order(FilePaths.Comparer).ToList(),
            selected.SelectMany(s => s.Primitive.Verification.Scenarios).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
    }

    internal static string ReviewPath(string project, string file)
    {
        string relative = Path.GetRelativePath(Path.GetDirectoryName(project)!, file);
        return FilePaths.IsAbsolute(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? file : relative;
    }

    private static InputException Invalid(string message) => new(ErrorCode.InvalidWorkflowInput, message);
}
