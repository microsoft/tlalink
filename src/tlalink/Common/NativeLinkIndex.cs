namespace TlaLink;

internal sealed record NativeLinkIndex(List<NativeDeclarationSpan> Spans,
    List<FormalLinkDeclaration> Declarations, List<Issue> Diagnostics)
{
    internal static NativeLinkIndex Load(IEnumerable<FormalTarget> targets, CliOptions options,
        string work, Dictionary<string, string> pins)
    {
        List<NativeDeclarationSpan> spans = [];
        List<FormalLinkDeclaration> declarations = [];
        List<Issue> diagnostics = [];
        foreach (var group in targets.GroupBy(t => t.Language, StringComparer.Ordinal))
        {
            List<string> inputs = group.SelectMany(t => t.Inputs).Distinct(FilePaths.Comparer).ToList();
            if (group.Key == "tla")
            {
                string jar = FilePaths.Full(options.Required("jar"));
                Files.Pin(pins, jar, Files.Hash(jar));
                ModelScope model = ModelScope.Load(inputs, jar, options.Timeout);
                diagnostics.AddRange(model.Diagnostics);
                foreach (var pin in model.Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
                declarations.AddRange(model.Declarations);
                foreach (TlaDocument document in model.Documents)
                    spans.AddRange(document.Endpoints.Select(e => new NativeDeclarationSpan(
                        new(e.Name, e.Scope, e.Kind, new(document.Path, e.Line, e.EndLine, e.Column),
                            document.Text[e.Start..e.End]), e.Start, e.End, e.Arity)));
            }
            else if (group.Key == "dafny")
            {
                string directory = Path.Combine(work, "dafny");
                Directory.CreateDirectory(directory);
                DafnyEvidenceState evidence = new();
                DafnyBackend backend = new(options.Required("dafny"), directory, options.Timeout, evidence);
                backend.Initialize();
                DafnyIndexResult index = backend.Index(inputs);
                declarations.AddRange(FormalCorrespondence.Dafny(index.Declarations, evidence.Fingerprints));
                spans.AddRange(index.Declarations.Select(d => new NativeDeclarationSpan(
                    new(d.Name, d.Module, d.Kind, new(d.Path, d.Line, d.EndLine, d.Column), ""), d.Start, d.End)));
                foreach (var pin in evidence.Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
            }
            else
            {
                string home = PProcess.PinCompiler(FilePaths.Full(options.Required("p")), pins);
                List<PDeclaration> index = PFrontend.Index(home, inputs, Path.Combine(work, "p"), options.Timeout, pins);
                declarations.AddRange(FormalCorrespondence.P(index, pins));
                spans.AddRange(index.Select(d => new NativeDeclarationSpan(
                    new(d.Name, "", d.Kind, new(d.Path, d.Line, d.EndLine, d.Column), ""), d.Start, d.End)));
            }
        }
        return new(spans, declarations, diagnostics);
    }
}
