using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static TlaLink.SynchronizationGuarantees;

namespace TlaLink;

/// <summary>
/// Instance and site checks of a region scope with TLA+ targets: region fields of declared primitive types must be declared
/// as model instances, every operation site on them must be bound or excluded, and each covering bind's action must reference
/// the kind operators of the site's operation through the declared kind instance (SANY reference resolution).
/// </summary>
public static partial class SynchronizationRegion
{
    internal static readonly string[] SiteRoles = ["acquire", "try-acquire", "release", "signal", "wait", "close", "complete",
        "enqueue", "dequeue"];
    public const string CoverageBoundary = "Sites are Roslyn member uses on region fields of declared primitive types; receivers that " +
        "are not such fields are unresolved, not traced (no alias analysis). A bind covers a site when its resolved source selection " +
        "contains it and its target declares an instance for the site's field; binds of other targets are not checked against the " +
        "kind contract for that field. Contract checks compare which kind operators a bound action references through the declared kind instance, " +
        "directly or through author-module operators it uses; they do not check that the arguments equal the instance expression, " +
        "the order of steps, or that the action is reachable.";

    private sealed record PrimitiveSite(SyntaxNode Node, PrimitiveManifest Primitive, PrimitiveOperation Operation,
        IFieldSymbol? Field, string Receiver);

    private sealed record FormalModel(FormalTarget Target, TlaDocument Document, List<FormalLinkDeclaration> Binds,
        List<FormalInstanceDeclaration> Instances, List<ExcludeAnnotation> Exclusions);

    private sealed record DeclaredInstance(FormalModel Model, FormalInstanceDeclaration Declaration, bool Valid);

    private sealed partial class Analysis
    {
        private readonly List<PrimitiveSite> instanceSites = [];

        /// <summary>Records a state-changing or waiting operation site and resolves its receiver to a region field.</summary>
        private void Site(SyntaxNode node, PrimitiveManifest primitive, PrimitiveOperation operation)
        {
            if (!SiteRoles.Contains(operation.Role, StringComparer.Ordinal) || node is not SimpleNameSyntax) return;
            SyntaxNode access = node.Parent is MemberAccessExpressionSyntax member && member.Name == node ? member :
                node.Parent is MemberBindingExpressionSyntax binding ? binding : node;
            SyntaxNode site = access.Parent is InvocationExpressionSyntax invocation && invocation.Expression == access ? invocation : access;
            ExpressionSyntax? receiver = Receiver(node);
            while (receiver is ParenthesizedExpressionSyntax parenthesized) receiver = parenthesized.Expression;
            ISymbol? symbol = receiver == null ? null : Model(node.SyntaxTree).GetSymbolInfo(receiver).Symbol;
            IFieldSymbol? field = symbol is IFieldSymbol candidate && InRegion(candidate) ? candidate.OriginalDefinition : null;
            string described = receiver == null ? "an implicit this" : symbol switch {
                IFieldSymbol f => (field == null ? "field outside the region " : "field ") + Id(f),
                ILocalSymbol local => "local " + local.Name,
                IParameterSymbol parameter => "parameter " + parameter.Name,
                IPropertySymbol property => "property " + Id(property),
                IMethodSymbol method => "the result of " + Id(method),
                _ => "expression " + receiver
            };
            instanceSites.Add(new(site, primitive, operation, field, described));
        }

        private static string Id(ISymbol symbol) => symbol.OriginalDefinition.GetDocumentationCommentId() ?? symbol.ToDisplayString();

        private static readonly string[] CompletionProperties = ["IsCompleted", "IsCompletedSuccessfully", "IsFaulted", "IsCanceled", "Status"];

        /// <summary>The completion property read directly on a wait site's task, as in <c>f.Drained.IsCompleted</c>, if any.</summary>
        private static string? CompletionProbe(SyntaxNode site)
        {
            SyntaxNode node = site;
            while (node.Parent is ParenthesizedExpressionSyntax parenthesized) node = parenthesized;
            return node.Parent is MemberAccessExpressionSyntax member && member.Expression == node &&
                CompletionProperties.Contains(member.Name.Identifier.ValueText, StringComparer.Ordinal) ? member.Name.Identifier.ValueText : null;
        }

        private bool InRegion(ISymbol symbol) => symbol.OriginalDefinition.DeclaringSyntaxReferences is { Length: > 0 } references &&
            references.All(r => regionTrees.Contains(r.SyntaxTree) && !Exempt(r.GetSyntax()));

        private static bool Covers(SyntaxNode selected, SyntaxNode node) =>
            selected.SyntaxTree == node.SyntaxTree && selected.Span.Contains(node.Span);

        private void Incomplete(string message) => Declaration(ErrorCode.SynchronizationRegionIncomplete, message);

        private void Stale(Place location, string message)
        {
            string text = Path.GetFileName(location.Path) + ":" + location.Line + ": " + message;
            if (reported.Add(ErrorCode.StaleInstance.Id() + "|" + text)) issues.Add(new(ErrorCode.StaleInstance, text, location));
        }

        private RegionCoverage Instances()
        {
            if (parse == null)
                throw new InputException(ErrorCode.WorkflowPrerequisiteMissing,
                    "Instance and site checks resolve the scope's TLA+ targets through SANY; supply the pinned TLA+ JAR: " + scope);
            List<string> modulePath = Primitives.ModulePath(scope, config, pins);
            List<FormalModel> models = Models(modulePath);
            List<(SyntaxNode Node, ExcludeAnnotation Exclusion)> exclusions = [];
            List<(FormalModel Model, FormalLinkDeclaration Bind, SyntaxNode Node)> binds = [];
            foreach (FormalModel model in models)
            {
                foreach (ExcludeAnnotation exclusion in model.Exclusions)
                    if (Select(exclusion.Source!) is { } node) exclusions.Add((node, exclusion));
                foreach (FormalLinkDeclaration bind in model.Binds.Where(b => b.Binding.Source != null))
                    if (Select(bind.Binding.Source!) is { } node) binds.Add((model, bind, node));
            }
            bool FieldExcluded(IFieldSymbol field) => exclusions.Any(e => field.DeclaringSyntaxReferences.Any(r =>
                r.SyntaxTree == e.Node.SyntaxTree && e.Node.Span.Contains(r.Span)));

            Dictionary<IFieldSymbol, List<DeclaredInstance>> declared = new(SymbolEqualityComparer.Default);
            foreach (FormalModel model in models) DeclareInstances(model, modulePath, declared);

            List<RegionSite> sites = [];
            Dictionary<string, (FormalLinkDeclaration Bind, int Sites, SortedSet<string> Missing)> contracts = new(StringComparer.Ordinal);
            foreach (PrimitiveSite site in instanceSites.OrderBy(s => s.Node.SyntaxTree.FilePath, FilePaths.Comparer).ThenBy(s => s.Node.SpanStart))
            {
                // Only targets that model the site's field with a kind instance cover it; other binds may select the statement for
                // their own correspondence and are ignored by the site and contract checks of that field.
                List<DeclaredInstance> fieldDeclarations = site.Field == null ? [] : declared.GetValueOrDefault(site.Field) ?? [];
                List<(FormalModel Model, FormalLinkDeclaration Bind, SyntaxNode Node)> covering = binds.Where(b => Covers(b.Node, site.Node) &&
                    fieldDeclarations.Any(d => d.Model == b.Model)).ToList();
                bool excluded = exclusions.Any(e => Covers(e.Node, site.Node)) || site.Field != null && FieldExcluded(site.Field);
                string operation = site.Operation.Member + " (" + site.Operation.Role + ") of primitive " + site.Primitive.Id;
                if (!excluded && site.Operation.Role == "wait" && CompletionProbe(site.Node) is { } probe)
                    Report(ErrorCode.SynchronousWaitProbe, site.Node, "synchronous wait probe: " + operation + " is read through " + probe +
                        ". Kind waits are resumptions guarded by the wait predicate, and a kind may complete its signal in the step that " +
                        "enables it, so a completion check can observe a state the kind does not represent; await it, model the check " +
                        "with a custom kind, or exclude the site with a reason.");
                string status = site.Field == null ? excluded ? "excluded" : "unresolved" :
                    covering.Count > 0 ? "bound" : excluded ? "excluded" : "unbound";
                if (status == "unresolved")
                    Report(ErrorCode.UnresolvedInstanceReceiver, site.Node, "unresolved instance receiver: " + operation + " is applied to " +
                        site.Receiver + "; instances are region fields, so apply it to a declared field or exclude the site.");
                else if (status == "unbound")
                    Report(ErrorCode.UnboundSite, site.Node, "unbound site: " + operation + " on " + Id(site.Field!) +
                        " is not selected by any bind source selector of a TLA+ target that declares an instance for it, nor by an exclusion.");
                else if (status == "bound")
                    foreach (var (model, bind, _) in covering)
                    {
                        List<string> missing = Contract(site, fieldDeclarations.First(d => d.Model == model), bind);
                        string key = bind.Binding.Id;
                        if (!contracts.TryGetValue(key, out var entry)) entry = (bind, 0, new(StringComparer.Ordinal));
                        entry.Sites++;
                        foreach (string item in missing) entry.Missing.Add(item);
                        contracts[key] = entry;
                        if (missing.Count > 0)
                            Report(ErrorCode.ContractMismatch, site.Node, "contract mismatch: bind " + key + " (operator " + bind.Endpoint.Name +
                                ") covers " + operation + " on " + Id(site.Field!) + " but does not reference " + string.Join(", ", missing) + ".",
                                key + "|" + site.Node.SyntaxTree.FilePath + "|" + site.Node.SpanStart);
                    }
                sites.Add(new(site.Field == null ? null : Id(site.Field), site.Operation.Member, site.Operation.Role, site.Receiver,
                    PlaceOf(site.Node), status, covering.Select(c => c.Bind.Binding.Id).Distinct(StringComparer.Ordinal).ToList()));
            }

            List<RegionInstance> instances = [];
            foreach (IFieldSymbol field in InstanceFields())
            {
                string id = Id(field);
                PrimitiveManifest primitive = primitiveTypes[((INamedTypeSymbol)field.Type).OriginalDefinition].Manifest;
                List<DeclaredInstance> declarations = declared.GetValueOrDefault(field) ?? [];
                string status = declarations.Count > 0 ? "declared" : FieldExcluded(field) ? "excluded" : "undeclared";
                if (status == "undeclared")
                    Report(ErrorCode.UndeclaredInstance, field.DeclaringSyntaxReferences[0].GetSyntax(), "undeclared instance: field " + id +
                        " of primitive " + primitive.Id + " has no instance declaration in the scope's TLA+ targets and no exclusion.");
                List<RegionSite> own = sites.Where(s => s.Field == id).ToList();
                instances.Add(new(id, primitive.Id, status, declarations.Select(d => d.Declaration.Instance.Id).ToList(),
                    own.Count(s => s.Status == "bound"), own.Count(s => s.Status == "excluded"), own.Count(s => s.Status == "unbound")));
            }
            return new(instances, sites, contracts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => new RegionContract(c.Key,
                c.Value.Bind.Endpoint.Name, c.Value.Sites, c.Value.Missing.Count == 0 ? "matched" : "mismatch", [.. c.Value.Missing])).ToList(),
                CoverageBoundary);
        }

        /// <summary>Region fields whose declared type is a declared primitive, in source order.</summary>
        private List<IFieldSymbol> InstanceFields()
        {
            List<IFieldSymbol> result = [];
            foreach (SyntaxTree tree in regionTrees.OrderBy(t => t.FilePath, FilePaths.Comparer))
                foreach (VariableDeclaratorSyntax declarator in tree.GetRoot().DescendantNodes(n => !Exempt(n)).OfType<VariableDeclaratorSyntax>())
                    if (declarator.Parent?.Parent is FieldDeclarationSyntax && !Exempt(declarator) &&
                        Model(tree).GetDeclaredSymbol(declarator) is IFieldSymbol { Type: INamedTypeSymbol type } field &&
                        primitiveTypes.ContainsKey(type.OriginalDefinition))
                        result.Add(field.OriginalDefinition);
            return result;
        }

        private SyntaxNode? Select(PreparationTarget target)
        {
            try
            {
                SyntaxNode node = SourcePreparation.CSharpTarget(compilation, null, target);
                return regionTrees.Contains(node.SyntaxTree) ? node : null;
            }
            catch (InputException) { return null; }
        }

        private List<FormalModel> Models(List<string> modulePath)
        {
            List<FormalModel> models = [];
            foreach (FormalTarget target in config.Targets.Where(t => t.Language == "tla"))
            {
                string path = target.Inputs.Single();
                TlaDocument document = parse!(path, modulePath);
                if (!document.ParserSucceeded || document.Diagnostics.Any(d => d.Severity == "error"))
                {
                    Incomplete("TLA+ target " + target.Id + " did not resolve through SANY: " +
                        string.Join("; ", document.Diagnostics.Select(d => d.Message)));
                    continue;
                }
                Files.Pin(pins, document.Path, document.Hash);
                foreach (var pin in document.DependencyFingerprints) Files.Pin(pins, pin.Key, pin.Value);
                try
                {
                    List<(int Start, int End, string Content)> comments = ModelScope.FormalComments(document.Text);
                    List<FormalLinkDeclaration> binds = FormalCorrespondence.Attach(path, document.Text, ModelScope.Spans(document, path),
                        comments, instances: true);
                    List<ExcludeAnnotation> exclusions = comments.SelectMany(c => Markup.Parse(c.Content, c.Start, c.End))
                        .Select(a => a.Declaration).OfType<ExcludeAnnotation>()
                        .Where(e => e.Source != null && e.From.Contains(target.Id, StringComparer.Ordinal)).ToList();
                    models.Add(new(target, document, binds.Where(b => Markup.Name(b.Binding.Id).Alias == target.Id).ToList(),
                        FormalCorrespondence.Instances(path, document.Text, document.Instances, comments), exclusions));
                }
                catch (InputException error) { Incomplete("TLA+ target " + target.Id + " annotations do not attach: " + error.Message); }
            }
            return models;
        }

        private string PrimitiveModule(PrimitiveManifest manifest) => manifest.Module == null ? Primitives.LibraryModule(manifest.PrimitiveKind) :
            FilePaths.Full(manifest.Module, Path.GetDirectoryName(primitives.Single(p => p.Manifest.Id == manifest.Id).Path)!);

        private void DeclareInstances(FormalModel model, List<string> modulePath, Dictionary<IFieldSymbol, List<DeclaredInstance>> declared)
        {
            HashSet<string> binds = model.Binds.Select(b => b.Binding.Id).ToHashSet(StringComparer.Ordinal);
            List<FormalInstanceDeclaration> parsed = [];
            foreach (FormalInstanceDeclaration item in model.Instances)
            {
                InstanceAnnotation instance = item.Instance;
                string label = "instance " + instance.Id + " (" + Path.GetFileName(item.Location.Path) + ")";
                if (Markup.Name(instance.Id).Alias != model.Target.Id || binds.Contains(instance.Id))
                {
                    Stale(item.Location, label + " needs an ID of target " + model.Target.Id + " that no bind uses.");
                    continue;
                }
                List<IFieldSymbol> fields = DocumentationCommentId.GetSymbolsForDeclarationId(instance.Field, compilation).OfType<IFieldSymbol>().ToList();
                if (fields is not [IFieldSymbol field] || !InRegion(field) || field.Type is not INamedTypeSymbol type ||
                    !primitiveTypes.TryGetValue(type.OriginalDefinition, out var primitive))
                {
                    Stale(item.Location, label + ": " + instance.Field + " is not a region field whose type is a declared primitive.");
                    continue;
                }
                field = field.OriginalDefinition;
                string module = PrimitiveModule(primitive.Manifest);
                bool valid = true;
                if (instance.KindInstance != item.Definition)
                {
                    Stale(item.Location, label + ": kind_instance " + instance.KindInstance + " must name the INSTANCE definition it precedes (" +
                        item.Definition + ").");
                    valid = false;
                }
                else if (item.Module != Path.GetFileNameWithoutExtension(module) ||
                    !model.Document.DependencyFingerprints.Keys.Contains(module, FilePaths.Comparer))
                {
                    Stale(item.Location, label + ": " + item.Definition + " instantiates " + item.Module + ", not " +
                        Path.GetFileName(module) + " of primitive " + primitive.Manifest.Id + ".");
                    valid = false;
                }
                if (declared.TryGetValue(field, out List<DeclaredInstance>? existing) && existing.Any(d => d.Model == model))
                {
                    Stale(item.Location, label + ": target " + model.Target.Id + " already declares an instance for " + instance.Field + ".");
                    continue;
                }
                if (existing == null) declared[field] = existing = [];
                existing.Add(new(model, item, valid));
                if (valid) parsed.Add(item);
            }
            CheckExpressions(model, parsed, modulePath);
        }

        /// <summary>
        /// Parses each model expression through SANY as the body of an operator over its index names, in a generated module that
        /// extends the target's root module. A parse and level check only: membership in the kind instance's set is not checked.
        /// </summary>
        private void CheckExpressions(FormalModel model, List<FormalInstanceDeclaration> declarations, List<string> modulePath)
        {
            if (declarations.Count == 0) return;
            List<string> path = [Path.GetDirectoryName(model.Document.Path)!, .. modulePath];
            List<TlaBackendDiagnostic>? Parse(IEnumerable<FormalInstanceDeclaration> items)
            {
                string directory = Path.Combine(AppContext.BaseDirectory, ".tlalink-instances-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                try
                {
                    string file = Path.Combine(directory, "TlaLinkInstanceModels.tla");
                    List<string> lines = ["---- MODULE TlaLinkInstanceModels ----", "EXTENDS " + model.Document.Module];
                    int index = 0;
                    foreach (FormalInstanceDeclaration item in items)
                        lines.Add("TlaLinkInstance" + ++index + (item.Instance.Index is { } names ? "(" + string.Join(", ", names) + ")" : "") +
                            " == " + item.Instance.Model);
                    lines.Add("====");
                    File.WriteAllText(file, string.Join("\n", lines) + "\n");
                    TlaDocument document = check!(file, path);
                    return document.ParserSucceeded && document.Diagnostics.All(d => d.Severity != "error") ? null : [.. document.Diagnostics];
                }
                finally { Directory.Delete(directory, recursive: true); }
            }
            if (Parse(declarations) == null) return;
            foreach (FormalInstanceDeclaration item in declarations)
                if (Parse([item]) is { } diagnostics)
                    Stale(item.Location, "instance " + item.Instance.Id + ": model expression " + item.Instance.Model + " does not parse in " +
                        model.Document.Module + " with index names bound: " +
                        string.Join("; ", diagnostics.Select(d => d.Message.Split('\n').FirstOrDefault(l => l.Contains("rror", StringComparison.Ordinal))
                            ?? d.Message.Split('\n')[0])));
        }

        /// <summary>
        /// Kind operators the site's operation requires that the bound action does not reference through its target's instance.
        /// Each non-optional action A is met by A, AEnabled or AApply; a wait operation also needs its wait predicate.
        /// </summary>
        private List<string> Contract(PrimitiveSite site, DeclaredInstance instance, FormalLinkDeclaration bind)
        {
            if (!instance.Valid) return [];
            string prefix = instance.Declaration.Instance.KindInstance;
            HashSet<string> referenced = References(instance.Model, bind.Endpoint.Name, prefix);
            List<string> missing = [];
            foreach (string action in (site.Operation.Actions ?? []).Where(a => !a.EndsWith('?')))
                if (!referenced.Contains(action) && !referenced.Contains(action + "Enabled") && !referenced.Contains(action + "Apply"))
                    missing.Add(prefix + "!" + action);
            if (site.Operation.Role == "wait" && site.Operation.Wait is { } wait && !referenced.Contains(wait))
                missing.Add(prefix + "!" + wait);
            return missing;
        }

        private HashSet<string> References(FormalModel model, string start, string prefix) =>
            KindReferences(model.Document, start, prefix, primitives.Select(p => Path.GetFileNameWithoutExtension(PrimitiveModule(p.Manifest)))
                .ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// Operators an operator references through a kind instance prefix, directly or through the author-module operators it
    /// uses; the closure does not descend into instantiated or kind modules. Shared by contract and fairness checks.
    /// </summary>
    internal static HashSet<string> KindReferences(TlaDocument document, string start, string prefix, IReadOnlySet<string> kinds)
    {
        ILookup<string, TlaReference> uses = document.References.ToLookup(r => r.From, StringComparer.Ordinal);
        HashSet<string> result = new(StringComparer.Ordinal), visited = new(StringComparer.Ordinal) { start };
        Queue<string> pending = new([start]);
        while (pending.TryDequeue(out string? current))
            foreach (TlaReference reference in uses[current])
            {
                if (reference.Instance != null)
                {
                    if (reference.Instance == prefix) result.Add(reference.Name);
                }
                else if (!kinds.Contains(reference.Module) && visited.Add(reference.Name)) pending.Enqueue(reference.Name);
            }
        return result;
    }
}
