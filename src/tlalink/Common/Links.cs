using System.Text.Json;

namespace TlaLink;

public sealed record LinkEdit(string Path, string BeforeSha256, string AfterSha256);
public sealed record LinkMaintenanceResult(int SchemaVersion, string Kind, string Action, string Status,
    string Target, string Language, List<FormalLinkDeclaration> Links, LinkEdit? Edit,
    List<Issue> Diagnostics, string Boundary);

public static class Links
{
    public static int Run(CliOptions options)
    {
        if (options.Action == "migrate") return LinkMigration.Run(options);
        string scopePath = FilePaths.Full(options.Required("scope")), alias = options.Required("target");
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        CorrespondenceConfiguration config = CorrespondenceInputs.Load(scopePath, pins);
        FormalTarget target = config.Targets.SingleOrDefault(t => t.Id == alias)
            ?? throw Invalid("Unknown target alias: " + alias);
        string action = options.Action!;
        if (action == "inspect")
        {
            if (new[] { "tokens", "match", "expected-matches", "capture", "clear-capture",
                "reason", "apply", "strip-source", "reason-policy" }.Any(options.Has))
                throw Invalid("Edit options are not accepted by links inspect.");
            LinkInspection.ValidateOptions(options);
        }
        if (action is "list" or "check" && new[] { "binding", "endpoint", "symbol", "syntax", "tokens",
            "match", "expected-matches", "capture", "clear-capture", "reason" }.Any(options.Has))
            throw Invalid("Edit options require links set/remove.");
        if (action == "remove" && new[] { "endpoint", "symbol", "syntax", "tokens", "match",
            "expected-matches", "capture", "clear-capture", "reason" }.Any(options.Has))
            throw Invalid("Source/capture edit options require links set, not remove.");
        if (options.Get("clear-capture", "false") is not ("true" or "false"))
            throw Invalid("--clear-capture must be true or false.");
        List<NativeDeclarationSpan> spans = [];
        List<FormalLinkDeclaration> declarations = [];
        List<Issue> diagnostics = [];
        string work = Directory.CreateTempSubdirectory("tlalink-links-").FullName;
        bool completed = false;
        try
        {
            NativeLinkIndex native = NativeLinkIndex.Load([target], options, work, pins);
            spans.AddRange(native.Spans);
            declarations.AddRange(native.Declarations);
            diagnostics.AddRange(native.Diagnostics);
            if (diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.ToolFailure, ErrorCode.StaleTranslation)))
                throw Invalid("Native resolution is incomplete: " + string.Join("; ", diagnostics.Select(d => d.Message)));
            declarations = declarations.Where(d => Markup.Name(d.Binding.Id).Alias == alias).ToList();
            List<string> sources = options.Many("source").Count > 0 ? Files.Discover(options.Many("source"), ".cs") : [];
            SourceInventoryContext? context = sources.Count == 0 ? null : SourceInventory.LoadContext(sources, options);
            CSharpScope? source = context?.Scope;
            if (source != null)
            {
                source.RequireCompilable();
                foreach (var pin in source.Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
            }
            if (action == "inspect")
            {
                if (source == null) throw Invalid("links inspect requires explicit C# --source inputs.");
                LinkInspectionResult inspection = LinkInspection.Build(target, native, source, options, pins, context!.Compiler);
                LinkInspection.Render(inspection, options);
                completed = true;
                return inspection.Status == "ready" ? 0 : 1;
            }
            LinkEdit? edit = null;
            if (action is "set" or "remove")
            {
                string id = options.Required("binding");
                if (!Markup.IsCorrespondenceId(id) || Markup.Name(id).Alias != alias)
                    throw Invalid("--binding must belong to the selected target alias.");
                FormalLinkDeclaration? existing = declarations.SingleOrDefault(d => d.Binding.Id == id);
                if (action == "remove" && existing == null) throw Invalid("Unknown binding: " + id);
                NativeDeclarationSpan? endpoint = existing == null ? null : spans.SingleOrDefault(s =>
                    s.Endpoint.Name == existing.Endpoint.Name && s.Endpoint.Kind == existing.Endpoint.Kind &&
                    FilePaths.Comparer.Equals(s.Endpoint.Location.Path, existing.Endpoint.Location.Path));
                if (action == "set")
                {
                    if (source == null) throw Invalid("links set requires explicit C# --source inputs.");
                    string name = options.Optional("endpoint") ?? existing?.Endpoint.Name ?? throw Invalid("New links require --endpoint.");
                    List<NativeDeclarationSpan> matches = spans.Where(s => s.Endpoint.Name == name).ToList();
                    if (matches.Any(s => s.Endpoint.Kind == "label")) matches = matches.Where(s => s.Endpoint.Kind == "label").ToList();
                    if (matches.Count != 1) throw Invalid("Native endpoint must resolve uniquely: " + name);
                    endpoint = matches[0];
                    if (existing != null && (existing.Endpoint.Name != endpoint.Endpoint.Name ||
                        !FilePaths.Comparer.Equals(existing.Endpoint.Location.Path, endpoint.Endpoint.Location.Path)))
                        throw Invalid("Remove the old binding explicitly before moving it to another endpoint.");
                }
                string path = endpoint!.Endpoint.Location.Path;
                string text = Files.ReadPinned(path, pins);
                List<(int Start, int End, string Content)> comments = Comments(text, target.Language);
                var attached = comments.Where(c => Markup.Parse(c.Content, c.Start, c.End)
                    .Any(a => a.Declaration is BindAnnotation b && b.Id == id)).ToList();
                if (attached.Count > 1) throw Invalid("Duplicate binding comments: " + id);
                AnnotationPayload payload = attached.Count == 0 ? new() :
                    JsonSerializer.Deserialize<AnnotationPayload>(attached[0].Content.Trim()[5..], Markup.AnnotationJson)!;
                payload.Bind.RemoveAll(b => b.Id == id);
                if (action == "remove") payload.Capture.RemoveAll(c => c.Binding == id);
                else
                {
                    int Number(string key) => !options.Has(key) ? 1 :
                        int.TryParse(options.Required(key), out int n) && n > 0 ? n : throw Invalid("--" + key + " must be positive.");
                    PreparationTarget selector = new(options.Required("symbol"), options.Optional("syntax"),
                        options.Optional("tokens"), Number("match"), Number("expected-matches"));
                    SourcePreparation.CSharpTarget(source!.Compilation, null, selector);
                    payload.Bind.Add(new(id, options.Optional("reason") ?? existing?.Binding.Reason, selector));
                    if (options.Has("capture") || options.Get("clear-capture", "false") == "true")
                        payload.Capture.RemoveAll(c => c.Binding == id);
                    foreach (string json in options.Many("capture"))
                    {
                        CaptureAnnotation capture = JsonSerializer.Deserialize<CaptureAnnotation>(json, Markup.AnnotationJson)
                            ?? throw Invalid("Capture must be a typed JSON object.");
                        if (capture.Binding != id || target.Language != "tla")
                            throw Invalid("Captures must name this binding and require a TLA+ target.");
                        payload.Capture.Add(capture);
                    }
                    string encoded = Markup.Encode(payload);
                    List<LocatedAnnotation> parsed = Markup.Parse("@link " + encoded, 0, 0);
                    FormalLinkDeclaration candidate = new(payload.Bind.Single(b => b.Id == id), endpoint.Endpoint, endpoint.Arity,
                        parsed.Select(a => a.Declaration).OfType<CaptureAnnotation>().Where(c => c.Binding == id).ToList());
                    source.AttachFormal(candidate);
                    if (source.Diagnostics.Any(d => d.Severity == "error")) throw Invalid("Unsupported source/capture attachment.");
                }
                string comment = payload.Bind.Count + payload.Capture.Count + payload.Exclude.Count == 0 ? "" :
                    (target.Language == "tla" ? "(* @link " : "/* @link ") + Markup.Encode(payload) +
                    (target.Language == "tla" ? " *)" : " */");
                int start = attached.Count == 0 ? endpoint.Start : attached[0].Start;
                int length = attached.Count == 0 ? 0 : attached[0].End - start;
                string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                string changed = text.Remove(start, length).Insert(start, comment + (length == 0 ? newline : ""));
                IntegratedInputs.CheckPins(pins);
                byte[] original = File.ReadAllBytes(path);
                if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original))
                    .Equals(pins[path], StringComparison.OrdinalIgnoreCase))
                    throw Invalid("Formal file changed before exclusive edit.");
                byte[] bytes = Files.EncodeLike(changed, original);
                using (FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    byte[] current = new byte[stream.Length];
                    stream.ReadExactly(current);
                    if (!current.AsSpan().SequenceEqual(original))
                        throw Invalid("Formal file changed before exclusive edit.");
                    stream.Position = 0;
                    stream.Write(bytes);
                    stream.SetLength(bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                edit = new(path, pins[path], Files.Hash(path));
                declarations.RemoveAll(d => d.Binding.Id == id);
                if (action == "set")
                    declarations.Add(new(payload.Bind.Single(b => b.Id == id), endpoint.Endpoint, endpoint.Arity,
                        payload.Capture.Where(c => c.Binding == id).ToList()));
            }
            else if (action == "check")
            {
                if (source == null) throw Invalid("links check requires explicit --source inputs.");
                FormalCorrespondence.Match(source, declarations, alias,
                    declarations.Select(d => d.Binding.Id).Concat(target.RequiredBindings ?? []).Distinct(StringComparer.Ordinal), diagnostics);
                source.ApplyCorrespondence(config);
                diagnostics.AddRange(source.Diagnostics);
                foreach (var pin in source.Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
                IntegratedInputs.CheckPins(pins);
            }
            bool ready = diagnostics.All(d => d.Severity != "error");
            LinkMaintenanceResult result = new(Schema.Version, "link-maintenance-result", action,
                ready ? "ready" : "failed", alias, target.Language, declarations, edit, diagnostics,
                "Native attachment and compiler-selector maintenance, not source/model refinement, trace conformance or inspection approval.");
            if (options.Optional("output") is string output) Files.WriteNew(FilePaths.Full(output), result);
            ProjectWorkflow.Render(result, options);
            completed = true;
            return ready ? 0 : 1;
        }
        finally
        {
            if (completed || !Directory.EnumerateFileSystemEntries(work).Any())
                Directory.Delete(work, recursive: true);
        }
    }

    internal static List<(int Start, int End, string Content)> Comments(string text, string language)
    {
        if (language != "tla") return FormalCorrespondence.Comments(text, nested: language == "dafny");
        List<(int Start, int End, string Content)> result = Markup.TlaComments(text);
        foreach (var algorithm in result.Where(c => c.Content.TrimStart().StartsWith("--algorithm", StringComparison.Ordinal) ||
            c.Content.TrimStart().StartsWith("--fair algorithm", StringComparison.Ordinal)).ToList())
            result.AddRange(Markup.TlaComments(algorithm.Content).Select(c =>
                (c.Start + algorithm.Start + 2, c.End + algorithm.Start + 2, c.Content)));
        return result;
    }

    private static InputException Invalid(string message) => new(ErrorCode.InvalidAnnotation, message);
}
