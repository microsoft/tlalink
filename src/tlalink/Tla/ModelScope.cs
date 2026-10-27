namespace TlaLink;

public sealed class ModelScope
{
    public List<ModelBinding> Bindings { get; } = [];
    public List<FormalLinkDeclaration> Declarations { get; } = [];
    public List<Issue> Diagnostics { get; } = [];
    public List<TlaDocument> Documents { get; } = [];
    public List<Endpoint> Inventory { get; } = [];
    public Dictionary<string, string> Fingerprints { get; } = new(FilePaths.Comparer);

    public static ModelScope Load(List<string> paths, string jar, int timeout, bool annotations = true)
    {
        ModelScope result = new();
        foreach (string path in paths.Distinct(FilePaths.Comparer))
        {
            TlaDocument document = TlaBackend.Parse(path, jar, timeout);
            result.Documents.Add(document);
            result.Inventory.AddRange(document.Endpoints.Select(endpoint => new Endpoint(endpoint.Name, endpoint.Scope, endpoint.Kind,
                new(path, endpoint.Line, endpoint.EndLine, endpoint.Column), document.Text[endpoint.Start..endpoint.End])));
            foreach (var pin in document.DependencyFingerprints) Files.Pin(result.Fingerprints, pin.Key, pin.Value);
            Files.Pin(result.Fingerprints, document.Path, document.Hash);
            result.Diagnostics.AddRange(document.Diagnostics.Select(d => new Issue(d.Code, d.Severity, d.Message,
                d.Path is null ? null : new Place(d.Path, d.Line ?? 1, d.Line ?? 1))));
            if (!annotations) continue;
            List<(int Start, int End, string Content)> comments = Markup.TlaComments(document.Text);
            foreach (var algorithm in comments.Where(c => c.Content.TrimStart().StartsWith("--algorithm", StringComparison.Ordinal) ||
                c.Content.TrimStart().StartsWith("--fair algorithm", StringComparison.Ordinal)).ToList())
                comments.AddRange(Markup.TlaComments(algorithm.Content).Select(c =>
                    (c.Start + algorithm.Start + 2, c.End + algorithm.Start + 2, c.Content)));
            try
            {
                result.Declarations.AddRange(FormalCorrespondence.Attach(path, document.Text,
                    document.Endpoints.Select(e => new NativeDeclarationSpan(
                        new(e.Name, e.Scope, e.Kind, new(path, e.Line, e.EndLine, e.Column),
                            document.Text[e.Start..e.End]), e.Start, e.End,
                        e.Kind == "label" ? document.Endpoints.Single(o => o.Kind == "operator" && o.Name == e.Name).Arity : e.Arity)), comments));
            }
            catch (InputException error) { result.Diagnostics.Add(new(error.Code ?? ErrorCode.InvalidModelBinding, error.Message)); }
        }
        return result;
    }

    public void BindSources(CSharpScope source, CorrespondenceConfiguration config, string? selected = null)
    {
        foreach (FormalTarget target in config.Targets.Where(t => t.Language == "tla" && (selected == null || t.Id == selected)))
        {
            List<string> ids = Declarations.Where(d => Markup.Name(d.Binding.Id).Alias == target.Id).Select(d => d.Binding.Id)
                .Concat(source.Bindings.Where(b => b.Model == target.Id).Select(b => b.BindingId))
                .Concat(target.RequiredBindings ?? []).Distinct(StringComparer.Ordinal).ToList();
            foreach (ResolvedCorrespondence pair in FormalCorrespondence.Match(source, Declarations, target.Id, ids, Diagnostics))
                Bindings.Add(new(target.Id, pair.Implementation.Tag, pair.Specification.Endpoint, pair.Specification.Arity));
        }
        AddHidden(config, selected);
    }

    public void BindManifest(InstrumentManifest manifest, CorrespondenceConfiguration config)
    {
        foreach (EventBinding binding in manifest.Events)
        {
            AddDeclared(binding.Model, new("step", binding.Step, [], 0, 0), binding.Binding, binding.Operator);
            foreach (Observation observation in binding.Observations)
                if (!Bindings.Any(b => b.Model == binding.Model && b.Tag.Kind == "state" && b.Tag.Identity == observation.Id))
                    AddDeclared(binding.Model, new("state", observation.Id, [], 0, 0), observation.Id, observation.Operator);
        }

        foreach (string alias in manifest.Events.Select(e => e.Model).Distinct(StringComparer.Ordinal))
            AddHidden(config, alias);
    }

    private void AddDeclared(string alias, Tag tag, string id, string recordedOperator)
    {
        List<FormalLinkDeclaration> matches = Declarations.Where(d => d.Binding.Id == id && Markup.Name(id).Alias == alias).ToList();
        if (matches.Count != 1 || matches[0].Endpoint.Name != recordedOperator)
        {
            Diagnostics.Add(new(ErrorCode.MissingModelEndpoint, "Recorded event disagrees with paired formal endpoint: " + id));
            return;
        }
        Bindings.Add(new(alias, tag, matches[0].Endpoint, matches[0].Arity));
    }

    private void AddHidden(CorrespondenceConfiguration config, string? selected)
    {
        foreach (FormalTarget target in config.Targets.Where(t => t.Language == "tla" && (selected == null || t.Id == selected)))
            foreach (HiddenModelAction action in target.HiddenActions ?? [])
            {
                if (Bindings.Any(b => b.Model == target.Id && b.Endpoint.Name == action.Endpoint &&
                    b.Tag.Options.GetValueOrDefault("visibility") != "hidden"))
                {
                    Diagnostics.Add(new(ErrorCode.InvalidModelBinding, "A native action cannot be both observed and hidden: " +
                        target.Id + "::" + action.Endpoint));
                    continue;
                }
                Add(config, target.Id, new("step", action.Endpoint,
                    new() { ["visibility"] = "hidden", ["justification"] = action.Reason }, 0, 0), action.Endpoint);
            }
    }

    private void Add(CorrespondenceConfiguration config, string alias, Tag tag, string native)
    {
        FormalTarget target = CorrespondenceInputs.Target(config, alias, "tla");
        TlaDocument? document = Documents.SingleOrDefault(d => FilePaths.Comparer.Equals(d.Path, target.Inputs.Single()));
        List<TlaEndpoint> matches = document?.Endpoints.Where(e => e.Name == native).ToList() ?? [];
        if (matches.Any(e => e.Kind == "label")) matches = matches.Where(e => e.Kind == "label").ToList();
        if (matches.Count != 1 || document == null)
        {
            Diagnostics.Add(new(ErrorCode.MissingModelEndpoint, $"Expected one authoritative endpoint {alias}::{native}, found {matches.Count}."));
            return;
        }
        TlaEndpoint endpoint = matches[0];
        int arity = endpoint.Kind == "label" ? document.Endpoints.Single(e => e.Kind == "operator" && e.Name == endpoint.Name).Arity : endpoint.Arity;
        Bindings.Add(new(alias, tag, new(endpoint.Name, endpoint.Scope, endpoint.Kind,
            new(document.Path, endpoint.Line, endpoint.EndLine, endpoint.Column),
            document.Text[endpoint.Start..endpoint.End]), arity));
    }
}
