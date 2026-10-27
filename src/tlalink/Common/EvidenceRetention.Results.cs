namespace TlaLink;

public static partial class EvidenceGarbageCollection
{
    private static List<TlcStoredResult> ReadResultFamily(string directory, Scan state)
    {
        List<TlcStoredResult> results = [];
        string[] entries = Directory.GetDirectories(directory);
        if (entries.Length == 0 || Directory.EnumerateFiles(directory).Any())
            throw new InputException(ErrorCode.InvalidRetention, "Unknown or unfinished TLC result family: " + directory);
        foreach (string entry in entries)
        {
            List<Issue> issues = TlcResultStore.Validate(entry, out TlcStoredResult? result);
            if (issues.Count > 0)
                throw new InputException(ErrorCode.InvalidRetention, string.Join("; ", issues.Select(i => i.Message)));
            foreach (string file in Directory.EnumerateFiles(entry))
                Files.Pin(state.DocumentHashes, file, Files.Hash(file));
            results.Add(result!);
        }
        return results;
    }

    private static HashSet<string> CurrentResultFamilies(string path, string store, Scan state, Action<string> protect)
    {
        AnalysisProject project = ProjectComposition.Read(path, file => {
            var document = state.Read(file);
            protect(file);
            return new(document, state.DocumentHashes[file]);
        });
        if (project.ResultStore == null || !FilePaths.Comparer.Equals(
            Path.Combine(ProjectInputs.Resolve(path, project.ResultStore), "tlc"), store))
            throw new InputException(ErrorCode.InvalidRetention, "Result project must select this store's TLC directory: " + path);
        HashSet<string> families = new(StringComparer.Ordinal);
        Dictionary<string, TlaDocument> parsed = new(FilePaths.Comparer);
        foreach (AnalysisStage stage in project.Scenarios.SelectMany(s => s.Stages).Where(s => s.Kind == "tlc"))
        {
            if (stage.Models is not { Count: 1 } || stage.Config == null || project.Tools.Jar == null)
                throw new InputException(ErrorCode.InvalidRetention, "Incomplete TLC declaration: " + stage.Id);
            string model = ProjectInputs.Resolve(path, stage.Models[0]);
            string jar = ProjectInputs.Resolve(path, project.Tools.Jar.Path);
            if (!Files.Hash(jar).Equals(project.Tools.Jar.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InputException(ErrorCode.InvalidRetention, "Result project's TLA+ tool pin differs: " + jar);
            if (!parsed.TryGetValue(model, out TlaDocument? document))
            {
                document = TlaBackend.Parse(model, jar, stage.TimeoutSeconds);
                if (document.Diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.StaleTranslation, ErrorCode.ToolFailure)))
                    throw new InputException(ErrorCode.InvalidRetention, "Cannot establish current TLC closure: " + model);
                parsed.Add(model, document);
            }
            TlcResultKey key = TlcResultStore.Key(model, ProjectInputs.Resolve(path, stage.Config), jar,
                document.DependencyFingerprints, stage.Fingerprint ?? 0);
            foreach (TlcKeyInput input in key.Inputs.Where(i => !i.Path.StartsWith("library:", StringComparison.Ordinal)))
            {
                NoLinks(input.Path);
                Files.Pin(state.DocumentHashes, input.Path, input.Sha256);
                protect(input.Path);
            }
            families.Add(TlcResultStore.Family(key));
        }
        return families;
    }
}
