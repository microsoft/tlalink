namespace TlaLink;

public sealed class ModelScope
{
    public List<ModelBinding> Bindings { get; } = [];
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
            foreach (var comment in Markup.TlaComments(document.Text))
                if (comment.Content.TrimStart().StartsWith("@link", StringComparison.Ordinal) ||
                    comment.Content.TrimStart().StartsWith("@tla", StringComparison.Ordinal))
                    result.Diagnostics.Add(new(ErrorCode.InvalidAnnotation,
                        "Formal endpoints use native declaration identities; remove old model/state/step markup.",
                        Files.At(path, document.Text, comment.Start, comment.End)));
        }
        return result;
    }

    public void BindSources(CSharpScope source, CorrespondenceConfiguration config, string? selected = null)
    {
        foreach (FormalTarget target in config.Targets.Where(t => t.Language == "tla" && (selected == null || t.Id == selected)))
            foreach (string id in target.RequiredBindings ?? [])
                if (!source.Bindings.Any(b => b.Model == target.Id && b.BindingId == id))
                    Diagnostics.Add(new(ErrorCode.MissingModelEndpoint, $"Missing required inline binding {target.Id}::{id}."));
        foreach (SourceBinding binding in source.Bindings.Where(b =>
            config.Targets.Any(t => t.Id == b.Model && t.Language == "tla") &&
            (selected == null || b.Model == selected)))
            Add(config, binding.Model, binding.Tag, binding.NativeEndpoint);
        AddHidden(config, selected);
    }

    public void BindManifest(InstrumentManifest manifest, CorrespondenceConfiguration config)
    {
        foreach (EventBinding binding in manifest.Events)
        {
            Add(config, binding.Model, new("step", binding.Step, [], 0, 0), binding.Operator);
            foreach (Observation observation in binding.Observations)
                if (!Bindings.Any(b => b.Model == binding.Model && b.Tag.Kind == "state" && b.Tag.Identity == observation.Id))
                    Add(config, binding.Model, new("state", observation.Id, [], 0, 0), observation.Operator);
        }
        foreach (string alias in manifest.Events.Select(e => e.Model).Distinct(StringComparer.Ordinal))
            AddHidden(config, alias);
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
