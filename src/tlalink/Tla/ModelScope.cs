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
        foreach (string path in paths)
        {
            TlaDocument document = TlaBackend.Parse(path, jar, timeout);
            result.Documents.Add(document);
            result.Inventory.AddRange(document.Endpoints.Select(endpoint => new Endpoint(endpoint.Name, endpoint.Scope, endpoint.Kind,
                new(path, endpoint.Line, endpoint.EndLine, endpoint.Column), document.Text[endpoint.Start..endpoint.End])));
            foreach (var pin in document.DependencyFingerprints) Files.Pin(result.Fingerprints, pin.Key, pin.Value);
            Files.Pin(result.Fingerprints, document.Path, document.Hash);
            result.Diagnostics.AddRange(document.Diagnostics.Select(d => new Issue(d.Code, d.Severity, d.Message,
                d.Path is null ? null : new Place(d.Path, d.Line ?? 1, d.Line ?? 1))));
            if (!document.ParserSucceeded || !annotations) continue;
            List<(int Start, int End, string Content)> comments = Markup.TlaComments(document.Text);
            foreach (var algorithm in comments.Where(c => System.Text.RegularExpressions.Regex.IsMatch(c.Content, @"^\s*--(?:fair\s+)?algorithm\b")).ToList())
                comments.AddRange(Markup.TlaComments(algorithm.Content).Select(c =>
                    (c.Start + algorithm.Start + 2, c.End + algorithm.Start + 2, c.Content)));
            List<Tag> tags = [];
            foreach (var comment in comments)
            {
                try { tags.AddRange(Markup.Parse(comment.Content, comment.Start, comment.End)); }
                catch (InputException error) { result.Diagnostics.Add(new(ErrorCode.InvalidAnnotation, error.Message, Files.At(path, document.Text, comment.Start, comment.End))); }
            }
            List<Tag> models = tags.Where(t => t.Kind == "model").ToList();
            if (models.Count == 0 && tags.Count == 0) continue;
            if (models.Count != 1)
            {
                result.Diagnostics.Add(new(ErrorCode.InvalidModelBinding, "An annotated TLA module needs exactly one model namespace.", new(path, 1, 1)));
                continue;
            }
            string model = models[0].Identity;
            HashSet<int> explicitlyBoundLabels = [];
            foreach (Tag tag in tags.Where(t => t.Kind != "model"))
            {
                TlaEndpoint? endpoint = document.Endpoints.Where(e => e.Start >= tag.End &&
                    (tag.Kind == "step" || e.Kind == "operator") &&
                    Markup.OnlyTrivia(document.Text, tag.End, e.Start, comments))
                    .OrderBy(e => e.Start).FirstOrDefault();
                if (endpoint is null)
                {
                    result.Diagnostics.Add(new(ErrorCode.InvalidAttachment, "Annotation must precede a real SANY operator or explicit PlusCal label.", Files.At(path, document.Text, tag.Start, tag.End)));
                    continue;
                }
                Add(tag, endpoint);
                if (endpoint.Kind == "label") explicitlyBoundLabels.Add(endpoint.Start);
            }
            foreach (TlaEndpoint label in document.Endpoints.Where(e => e.Kind == "label" && !explicitlyBoundLabels.Contains(e.Start)))
                Add(new("step", label.Name, [], label.Start, label.Start), label);
            void Add(Tag tag, TlaEndpoint endpoint)
            {
                int arity = endpoint.Kind == "label" ? document.Endpoints.FirstOrDefault(e =>
                    e.Kind == "operator" && e.Name == endpoint.Name)?.Arity ?? 0 : endpoint.Arity;
                result.Bindings.Add(new(model, tag, new(endpoint.Name, endpoint.Scope, endpoint.Kind,
                    new(path, endpoint.Line, endpoint.EndLine, endpoint.Column), document.Text[endpoint.Start..endpoint.End]), arity));
            }
        }
        return result;
    }
}
