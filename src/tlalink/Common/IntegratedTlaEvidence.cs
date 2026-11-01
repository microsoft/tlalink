namespace TlaLink;

internal static class IntegratedTlaEvidence
{
    internal static void ValidateArchive(IntegratedTlaResult result, IntegratedManifest manifest,
        string manifestPath, CorrespondenceConfiguration scope, Dictionary<string, string> pins,
        string archive)
    {
        if (result.SchemaVersion != Schema.Version || result.Kind != "source-review" ||
            result.LinksStatus != "passed" || result.ConformanceStatus != "not-run" ||
            result.Diagnostics.Any(d => d.Severity == "error") || result.Links.Length == 0)
            throw Invalid("Selected TLA+ inspection lacks successful structural links.");
        Dictionary<string, string> files = new(FilePaths.Comparer);
        foreach (var pin in pins.Where(p => FilePaths.IsAbsolute(p.Key)))
        {
            StageEvidence.RequireHash(pin.Value);
            if (!files.TryAdd(pin.Key, pin.Value))
                throw Invalid("TLA+ inputs contain aliased paths: " + pin.Key);
        }
        foreach (var pin in result.Fingerprints)
            if (!(FilePaths.IsAbsolute(pin.Key) ? files : pins).TryGetValue(pin.Key, out string? hash) || hash != pin.Value)
                throw Invalid("Nested TLA+ input differs from its parent: " + pin.Key);
        string ReadInput(string path)
        {
            if (!files.TryGetValue(path, out string? hash))
                throw Invalid("Missing archived TLA+/source input: " + path);
            string copy = Path.Combine(archive, hash);
            if (!File.Exists(copy) || Files.Hash(copy) != hash)
                throw Invalid("Changed archived TLA+/source input: " + path);
            return Files.Read(copy);
        }
        string scopePath = ProjectInputs.Resolve(manifestPath, manifest.Scope);
        HashSet<string> models = scope.Targets.Where(t => manifest.TlaTargets.Contains(t.Id, StringComparer.Ordinal))
            .SelectMany(t => t.Inputs.Select(p => ProjectInputs.Resolve(scopePath, p))).ToHashSet(FilePaths.Comparer);
        List<FormalLinkDeclaration> formal = [];
        foreach (var file in result.Inventory.GroupBy(e => e.Location.Path, FilePaths.Comparer))
        {
            if (!models.Contains(file.Key)) throw Invalid("Unselected TLA+ inventory input: " + file.Key);
            string text = ReadInput(file.Key);
            List<NativeDeclarationSpan> spans = [];
            foreach (Endpoint endpoint in file)
            {
                int start = TlaBackend.ArchivedOffset(text, endpoint.Location.Line, endpoint.Location.Column);
                if (endpoint.Source.Length == 0 || start > text.Length - endpoint.Source.Length ||
                    endpoint.Location.EndLine != endpoint.Location.Line + endpoint.Source.Count(c => c == '\n') ||
                    !text.AsSpan(start, endpoint.Source.Length).SequenceEqual(endpoint.Source.AsSpan()))
                    throw Invalid("TLA+ inventory span differs from archived source: " + endpoint.Name);
                spans.Add(new(endpoint, start, start + endpoint.Source.Length));
            }
            formal.AddRange(FormalCorrespondence.Attach(file.Key, text, spans, ModelScope.FormalComments(text)));
        }
        formal = formal.Where(d => manifest.TlaTargets.Contains(Markup.Name(d.Binding.Id).Alias, StringComparer.Ordinal)).ToList();
        HashSet<string> expected = formal.Select(d => d.Binding.Id).ToHashSet(StringComparer.Ordinal);
        expected.UnionWith(scope.Targets.Where(t => manifest.TlaTargets.Contains(t.Id, StringComparer.Ordinal))
            .SelectMany(t => t.RequiredBindings ?? []));
        if (expected.Count == 0 || result.Links.Length != expected.Count ||
            !result.Links.Select(l => l.Key).ToHashSet(StringComparer.Ordinal).SetEquals(expected))
            throw Invalid("TLA+ links differ from selected formal declarations and required bindings.");
        Dictionary<string, ArchivedSourceBinding> sources = CorrespondenceEvidence.Bindings(
            manifest.Sources.Select(p => ProjectInputs.Resolve(manifestPath, p)), expected, pins, ReadInput, formal,
            hash => Path.Combine(archive, hash));
        foreach (Link link in result.Links)
        {
            List<FormalLinkDeclaration> target = formal.Where(d => d.Binding.Id == link.Key).ToList();
            if (target.Count != 1 || !sources.TryGetValue(link.Key, out ArchivedSourceBinding? source) ||
                !SamePlace(source.Location, link.Implementation.Location) ||
                !SameEndpoint(target[0].Endpoint, link.Specification) ||
                link.Model != Markup.Name(link.Key).Alias)
                throw Invalid("TLA+ link differs from its archived formal/source attachment: " + link.Key);
        }
    }

    private static bool SamePlace(Place left, Place right) =>
        FilePaths.Comparer.Equals(left.Path, right.Path) && left.Line == right.Line &&
        left.EndLine == right.EndLine && left.Column == right.Column;

    private static bool SameEndpoint(Endpoint left, Endpoint right) =>
        left.Name == right.Name && left.Scope == right.Scope && left.Kind == right.Kind &&
        left.Source == right.Source && SamePlace(left.Location, right.Location);

    private static InputException Invalid(string message) => new(ErrorCode.InvalidIntegratedEvidence, message);
}
