using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TlaLink;

public sealed record MethodSummary(string Binding, string Abstraction, List<string> Sources, string Inspection,
    List<string> Assumptions, List<string> Limits, List<string> DependsOn, string? Precondition = null);
public sealed record MethodSummaryUse(string Scope, List<string> Bindings);
public sealed record MethodSummaryResolution(string Scope, MethodSummary Summary, string Method,
    Endpoint Implementation, Endpoint Specification, Endpoint? Precondition);
public sealed record MethodSummarySupport(string SelectionSha256, string SourceContextSha256, List<string> Inputs);
internal sealed record SelectedMethodSummary(string Scope, string ScopeSha256,
    CorrespondenceConfiguration Configuration, MethodSummary Summary);
internal sealed record MethodSummaryPin(string Key, string Value);
internal sealed record MethodSummarySourceContext(CompilerProfile Compiler, List<MethodSummaryPin> Pins);
internal sealed record MethodSummaryIdentity(string Scope, MethodSummary Summary);

public static class MethodSummaries
{
    public static void Validate(CorrespondenceConfiguration config)
    {
        List<MethodSummary> summaries = config.MethodSummaries ?? [];
        if (summaries.Count > 1000 || summaries.Any(s => s == null))
            throw Invalid("At most 1000 nonnull method summaries are supported.");
        if (summaries.Select(s => s.Binding).Distinct(StringComparer.Ordinal).Count() != summaries.Count)
            throw Invalid("Duplicate method summary binding.");
        foreach (MethodSummary summary in summaries)
        {
            if (!Markup.IsCorrespondenceId(summary.Binding) || summary.Abstraction is not ("pure" or "atomic") ||
                !Schema.IsStableId(summary.Inspection) || summary.Sources.Count is < 1 or > 1000 ||
                summary.Sources.Any(p => string.IsNullOrWhiteSpace(p) ||
                    !Path.GetExtension(p).Equals(".cs", StringComparison.OrdinalIgnoreCase)) ||
                summary.Assumptions.Any(string.IsNullOrWhiteSpace) || summary.Limits.Count == 0 ||
                summary.Limits.Any(string.IsNullOrWhiteSpace) || summary.Precondition != null && string.IsNullOrWhiteSpace(summary.Precondition))
                throw Invalid("Method summaries require a binding, pure/atomic abstraction, explicit C# files, inspection, limits and nonempty assumption/limit entries.");
            FormalTarget target = CorrespondenceInputs.Target(config, Markup.Name(summary.Binding).Alias, "tla");
            if (!target.RequiredBindings!.Contains(summary.Binding, StringComparer.Ordinal))
                throw Invalid("A method summary must be an explicitly required TLA+ binding: " + summary.Binding);
            if (summary.DependsOn.Distinct(StringComparer.Ordinal).Count() != summary.DependsOn.Count ||
                summary.DependsOn.Any(id => !summaries.Any(s => s.Binding == id)))
                throw Invalid("Summary dependencies must be unique bindings declared in the same scope.");
        }
        HashSet<string> active = new(StringComparer.Ordinal), done = new(StringComparer.Ordinal);
        void Visit(MethodSummary summary)
        {
            if (done.Contains(summary.Binding)) return;
            if (!active.Add(summary.Binding)) throw Invalid("Cyclic method-summary dependency: " + summary.Binding);
            foreach (string id in summary.DependsOn) Visit(summaries.Single(s => s.Binding == id));
            active.Remove(summary.Binding);
            done.Add(summary.Binding);
        }
        foreach (MethodSummary summary in summaries) Visit(summary);
    }

    internal static List<SelectedMethodSummary> Select(string projectPath, AnalysisProject project, IEnumerable<AnalysisStage> stages)
    {
        Dictionary<string, SelectedMethodSummary> selected = new(StringComparer.Ordinal);
        Dictionary<string, (CorrespondenceConfiguration Configuration, string Hash)> scopes = new(FilePaths.Comparer);
        foreach (AnalysisStage stage in stages)
        {
            if (stage.MethodSummaries == null) continue;
            if (stage.Kind is not ("tlc" or "trace") || stage.MethodSummaries.Count is < 1 or > 1000 ||
                stage.MethodSummaries.Any(s => s == null))
                throw Invalid("Explicit method-summary uses are supported on TLC/trace stages only.");
            HashSet<string> uses = new(StringComparer.Ordinal);
            foreach (MethodSummaryUse use in stage.MethodSummaries)
            {
                string path = ProjectInputs.Resolve(projectPath, use.Scope);
                if (!scopes.TryGetValue(path, out var scope))
                {
                    Dictionary<string, string> pins = new(FilePaths.Comparer);
                    CorrespondenceConfiguration config = CorrespondenceInputs.Load(path, pins);
                    scopes.Add(path, scope = (config, pins[path]));
                }
                if (use.Bindings.Count is < 1 or > 1000 || use.Bindings.Any(id => !Markup.IsCorrespondenceId(id)))
                    throw Invalid("Summary uses require explicit binding IDs.");
                void Add(string id)
                {
                    MethodSummary summary = scope.Configuration.MethodSummaries?.SingleOrDefault(s => s.Binding == id)
                        ?? throw Invalid("Undeclared method summary: " + id);
                    if (!project.Obligations.Any(o => o.Id == summary.Inspection))
                        throw Invalid("Unknown summary inspection obligation: " + summary.Inspection);
                    if (selected.TryGetValue(id, out SelectedMethodSummary? previous))
                    {
                        if (!FilePaths.Comparer.Equals(previous.Scope, path))
                            throw Invalid("A summary binding cannot name different scopes: " + id);
                        return;
                    }
                    selected.Add(id, new(path, scope.Hash, scope.Configuration, summary));
                    foreach (string dependency in summary.DependsOn) Add(dependency);
                }
                foreach (string id in use.Bindings)
                {
                    if (!uses.Add(id)) throw Invalid("Duplicate summary use in stage " + stage.Id + ": " + id);
                    Add(id);
                }
            }
        }
        return selected.Values.OrderBy(s => s.Summary.Binding, StringComparer.Ordinal).ToList();
    }

    internal static Dictionary<string, CSharpScope> Sources(string projectPath, AnalysisProject project,
        List<SelectedMethodSummary> selected)
    {
        Dictionary<string, CSharpScope> result = new(StringComparer.Ordinal);
        foreach (SelectedMethodSummary item in selected)
        {
            List<string> paths = item.Summary.Sources.Select(p => ProjectInputs.Resolve(item.Scope, p)).ToList();
            if (paths.Any(p => !File.Exists(p)))
                throw Invalid("Method summaries require existing, explicitly listed C# files.");
            if (paths.Distinct(FilePaths.Comparer).Count() != paths.Count)
                throw Invalid("Method-summary source files must be unique.");
            CSharpScope source = CSharpScope.Parse(paths,
                project.Compiler.References.Select(p => ProjectInputs.Resolve(projectPath, p)), project.Compiler.Defines,
                languageVersion: project.Compiler.LanguageVersion);
            source.ApplyCorrespondence(item.Configuration);
            result.Add(item.Summary.Binding, source);
        }
        return result;
    }

    internal static List<MethodSummaryResolution> Inspect(string path, AnalysisProject project,
        List<SelectedMethodSummary> selected, Dictionary<string, string> pins,
        Func<string, TlaDocument> parse)
    {
        Dictionary<string, CSharpScope> sources = Sources(path, project, selected);
        List<MethodSummaryResolution> results = [];
        foreach (SelectedMethodSummary item in selected)
        {
            CSharpScope source = sources[item.Summary.Binding];
            if (source.Diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.UnresolvedCompilation)))
                throw Invalid("Summary source context is incomplete: " + item.Summary.Binding + ": " +
                    string.Join("; ", source.Diagnostics.Select(d => d.Message)));
            foreach (var pin in source.Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
            FormalTarget target = CorrespondenceInputs.Target(item.Configuration, Markup.Name(item.Summary.Binding).Alias, "tla");
            TlaDocument document = parse(target.Inputs.Single());
            if (!document.ParserSucceeded || document.Diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.StaleTranslation)) ||
                document.Variables.Count != 0)
                throw Invalid("A method summary requires a successfully resolved, stateless TLA+ relation module: " + item.Summary.Binding);
            ModelScope model = new();
            model.Documents.Add(document);
            model.Declarations.AddRange(FormalCorrespondence.Attach(document.Path, document.Text,
                document.Endpoints.Select(e => new NativeDeclarationSpan(
                    new(e.Name, e.Scope, e.Kind, new(document.Path, e.Line, e.EndLine, e.Column),
                        document.Text[e.Start..e.End]), e.Start, e.End, e.Arity)), Markup.TlaComments(document.Text)));
            List<Issue> issues = [];
            List<ResolvedCorrespondence> pairs = FormalCorrespondence.Match(source, model.Declarations, target.Id,
                [item.Summary.Binding], issues);
            if (issues.Count > 0 || pairs.Count != 1)
                throw Invalid("Unresolved summary pair " + item.Summary.Binding + ": " + string.Join("; ", issues.Select(i => i.Message)));
            ResolvedCorrespondence pair = pairs[0];
            SourceBinding binding = pair.Implementation;
            if (binding.Kind != SourceBindingKind.Declaration || binding.Symbol is not IMethodSymbol method ||
                method.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor) || method.IsAsync ||
                method.IsAbstract || method.IsVirtual || method.IsOverride || method.IsExtern ||
                method.ReturnsByRef || method.ReturnsByRefReadonly ||
                binding.Node.DescendantNodes().OfType<YieldStatementSyntax>().Any() ||
                new[] { "System.Threading.Tasks.Task", "System.Threading.Tasks.Task`1",
                    "System.Threading.Tasks.ValueTask", "System.Threading.Tasks.ValueTask`1" }
                    .Any(name => SymbolEqualityComparer.Default.Equals(method.ReturnType.OriginalDefinition,
                        source.Compilation.GetTypeByMetadataName(name))) ||
                binding.Capture != null ||
                !item.Summary.Sources.Select(p => ProjectInputs.Resolve(item.Scope, p)).Contains(binding.Path, FilePaths.Comparer))
                throw Invalid("Summary providers must be bound, concrete synchronous nonvirtual methods/constructors without capture metadata.");
            if (pair.Specification.Endpoint.Kind != "operator" || pair.Specification.Arity < 1)
                throw Invalid("A method summary must bind a parameterized native TLA+ operator.");
            Endpoint? precondition = null;
            if (item.Summary.Precondition != null)
            {
                TlaEndpoint? endpoint = document.Endpoints.SingleOrDefault(e => e.Kind == "operator" && e.Name == item.Summary.Precondition);
                if (endpoint == null || endpoint.Arity < 1)
                    throw Invalid("Summary precondition must resolve to a parameterized operator in its native module.");
                precondition = new(endpoint.Name, endpoint.Scope, endpoint.Kind,
                    new(document.Path, endpoint.Line, endpoint.EndLine, endpoint.Column), document.Text[endpoint.Start..endpoint.End]);
            }
            Files.Pin(pins, item.Scope, item.ScopeSha256);
            Files.Pin(pins, document.Path, document.Hash);
            foreach (var pin in document.DependencyFingerprints) Files.Pin(pins, pin.Key, pin.Value);
            results.Add(new(item.Scope, item.Summary, method.GetDocumentationCommentId()
                ?? throw Invalid("Summary method has no Roslyn declaration identity."), binding.Endpoint, pair.Specification.Endpoint, precondition));
        }
        return results;
    }

    internal static IEnumerable<string> Inspections(string path, AnalysisProject project, IEnumerable<AnalysisStage> stages) =>
        Select(path, project, stages).Select(s => s.Summary.Inspection).Distinct(StringComparer.Ordinal);

    internal static MethodSummarySupport? Support(string path, AnalysisProject project, string inspection,
        Dictionary<string, string> files, bool validate)
    {
        List<SelectedMethodSummary> selected = Select(path, project, project.Scenarios.SelectMany(s => s.Stages))
            .Where(s => s.Summary.Inspection == inspection).ToList();
        if (selected.Count == 0) return null;
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        Dictionary<string, CSharpScope> sources = Sources(path, project, selected);
        foreach (var source in sources.Values)
            foreach (var pin in source.Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
        string sourceHash = Schema.Hash(new MethodSummarySourceContext(project.Compiler,
            pins.OrderBy(p => p.Key, FilePaths.Comparer).Select(p => new MethodSummaryPin(
                FilePaths.IsAbsolute(p.Key) ? ReviewPath(path, p.Key) : p.Key, p.Value)).ToList()));
        if (validate)
        {
            string jar = ProjectInputs.Resolve(path, project.Tools.Jar?.Path ?? throw Invalid("Summary inspections require a pinned TLA+ JAR."));
            if (!Files.Hash(jar).Equals(project.Tools.Jar!.Sha256, StringComparison.OrdinalIgnoreCase))
                throw Invalid("Summary inspection JAR differs from the project tool pin.");
            Inspect(path, project, selected, pins, model => TlaBackend.Parse(model, jar, 30));
            Program.PinTool(pins);
            Files.Pin(pins, jar, Files.Hash(jar));
            string adapter = Path.Combine(AppContext.BaseDirectory, "TlaIndex.java");
            Files.Pin(pins, adapter, Files.Hash(adapter));
        }
        foreach (SelectedMethodSummary item in selected)
        {
            Files.Pin(pins, item.Scope, item.ScopeSha256);
            FormalTarget target = CorrespondenceInputs.Target(item.Configuration, Markup.Name(item.Summary.Binding).Alias, "tla");
            foreach (string model in target.Inputs) Files.Pin(pins, model, Files.Hash(model));
        }
        IntegratedInputs.CheckPins(pins);
        foreach (var pin in pins.Where(p => FilePaths.IsAbsolute(p.Key))) Files.Pin(files, ReviewPath(path, pin.Key), pin.Value);
        return new(Schema.Hash(selected.Select(s => new MethodSummaryIdentity(ReviewPath(path, s.Scope), s.Summary))),
            sourceHash, files.Keys.Order(FilePaths.Comparer).ToList());
    }

    internal static string ReviewPath(string project, string file)
    {
        string relative = Path.GetRelativePath(Path.GetDirectoryName(project)!, file);
        return FilePaths.IsAbsolute(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? file : relative;
    }

    private static InputException Invalid(string message) => new(ErrorCode.InvalidWorkflowInput, message);
}
