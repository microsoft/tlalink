using System.Text.Json;
using System.Text.Json.Serialization;

namespace TlaLink;

public sealed record ProjectAuthoring(int SchemaVersion, string Kind, string Id, ProjectTools Tools,
    CompilerProfile Compiler, string Requirements, List<ScenarioAuthoring> Scenarios, string ReviewDirectory,
    List<ReviewAuthoring>? Obligations = null, List<CheckObligation>? CheckObligations = null,
    List<string>? Components = null, List<string>? InputSets = null, List<string>? ExecutionProfiles = null,
    [property: JsonPropertyName("$schema")] string? EditorSchema = null, string? ResultStore = null,
    [property: JsonRequired] int AnnotationVersion = 0);
public sealed record ScenarioAuthoring(string Id, List<StageAuthoring>? Stages = null, List<string>? Components = null);
public sealed record StageAuthoring(string Id, string Kind, List<string>? DependsOn = null,
    int? TimeoutSeconds = null, string? Bindings = null, List<string>? Sources = null,
    List<string>? Models = null, string? Config = null, List<string>? Arguments = null,
    TlcResources? Resources = null, string? Profile = null, int? Fingerprint = null,
    string? Scope = null, string? Target = null, List<MethodSummaryUse>? MethodSummaries = null);
public sealed record ReviewAuthoring(string Id, string Claim, List<string> Assumptions,
    List<string>? Inputs = null, List<string>? InputSets = null, ReviewOrigin Origin = ReviewOrigin.Human);
public sealed record ProjectComponent(int SchemaVersion, string Kind, string Id, List<StageAuthoring> Stages,
    List<ReviewAuthoring>? Obligations = null, List<CheckObligation>? CheckObligations = null,
    List<string>? Components = null, [property: JsonPropertyName("$schema")] string? EditorSchema = null);
public sealed record ProjectInputSet(string Id, List<string> Inputs, List<string>? PreparedSources = null);
public sealed record ProjectInputSets(int SchemaVersion, string Kind, List<ProjectInputSet> Sets,
    [property: JsonPropertyName("$schema")] string? EditorSchema = null);
public sealed record ExecutionProfile(string Id, string Kind, int TimeoutSeconds, TlcResources? Resources = null);
public sealed record ProjectExecutionProfiles(int SchemaVersion, string Kind, List<ExecutionProfile> Profiles,
    [property: JsonPropertyName("$schema")] string? EditorSchema = null);
public sealed record ProjectExpansion(int SchemaVersion, string Kind, string Project, string ProjectDirectory,
    AnalysisProject ExpandedProject, Dictionary<string, string> Fingerprints);

internal sealed class ProjectAuthoringMetadata
{
    public Dictionary<string, string> Fingerprints { get; } = new(FilePaths.Comparer);
    public Dictionary<string, string> StageOrigins { get; } = new(StringComparer.Ordinal);
    public bool Composed { get; set; }
}

public static class ProjectComposition
{
    internal sealed record Source(JsonElement Document, string Sha256);

    public static AnalysisProject Read(string path) => Read(path, ReadSource);

    internal static AnalysisProject Read(string path, Func<string, Source> reader)
    {
        path = FilePaths.Full(path);
        Loader loader = new(path, reader);
        return loader.Load();
    }

    internal static Source ReadSource(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Project authoring document exceeds 16 MiB: " + path);
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        JsonElement document = JsonSerializer.Deserialize<JsonElement>(Files.ReadPinned(path, pins), ProjectInputs.Json);
        return new(document, pins[path]);
    }

    public static ProjectExpansion Expand(string path)
    {
        path = FilePaths.Full(path);
        AnalysisProject project = Read(path);
        return Expansion(path, project);
    }

    internal static ProjectExpansion Expansion(string path, AnalysisProject project) =>
        new(Schema.Version, "project-expansion", project.Id, Path.GetDirectoryName(path)!, project,
            new(project.Authoring!.Fingerprints, FilePaths.Comparer));

    internal static List<Issue> ValidateArchive(string path, AnalysisProject project, WorkflowRun run, string directory)
    {
        const string snapshot = "expanded-project.json";
        if (!run.Artifacts.ContainsKey(snapshot))
            return project.Authoring?.Composed == true
                ? [new(ErrorCode.WorkflowProvenanceInconsistent, "Composed run is missing its expanded project snapshot.")]
                : [];
        try
        {
            ProjectExpansion saved = ProjectInputs.Read<ProjectExpansion>(Path.Combine(directory, snapshot));
            if (saved.SchemaVersion != Schema.Version || saved.Kind != "project-expansion" ||
                saved.Project != run.Project || saved.ProjectDirectory != run.ProjectDirectory)
                throw new InputException("Expanded project identity contradicts the run.");
            string recordedRoot = FilePaths.Full(Path.GetFileName(path), run.ProjectDirectory);
            AnalysisProject archived = Read(recordedRoot, file => {
                if (!run.Fingerprints.TryGetValue(file, out string? hash))
                    throw new InputException("Unrecorded project authoring dependency: " + file);
                string copy = ProjectAcceptance.SafePath(directory, Path.Combine("inputs", hash));
                Source source = ReadSource(copy);
                if (source.Sha256 != hash) throw new InputException("Changed archived project authoring input: " + file);
                return source;
            });
            if (Schema.Hash(saved.ExpandedProject) != Schema.Hash(archived) ||
                saved.Fingerprints.Count != archived.Authoring!.Fingerprints.Count ||
                saved.Fingerprints.Any(p => !archived.Authoring.Fingerprints.TryGetValue(p.Key, out string? hash) || hash != p.Value))
                throw new InputException("Expanded project does not match its archived authoring files.");
            foreach (var pin in project.Authoring!.Fingerprints)
            {
                string recorded = FilePaths.Full(Path.GetRelativePath(Path.GetDirectoryName(path)!, pin.Key), run.ProjectDirectory);
                if (!run.Fingerprints.TryGetValue(recorded, out string? hash) || hash != pin.Value)
                    return [new(ErrorCode.WorkflowProjectChanged, "Project composition differs from the run: " + pin.Key)];
            }
            return [];
        }
        catch (Exception error) when (error is InputException or IOException or UnauthorizedAccessException or JsonException)
        {
            return [new(ErrorCode.WorkflowProvenanceInconsistent, "Expanded project: " + error.Message)];
        }
    }

    private sealed class Loader(string root, Func<string, Source> reader)
    {
        private readonly ProjectAuthoringMetadata metadata = new();
        private readonly Dictionary<string, (ProjectComponent Component, string File)> components = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> inputSets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExecutionProfile> profiles = new(StringComparer.Ordinal);
        private readonly HashSet<string> visited = new(FilePaths.Comparer);
        private readonly HashSet<string> active = new(FilePaths.Comparer);
        private readonly Dictionary<string, string> stageIds = new(StringComparer.Ordinal);

        private T Document<T>(string file, string kind)
        {
            if (metadata.Fingerprints.Count >= 1000) throw Error(file, "Project composition exceeds 1,000 documents.");
            try
            {
                Source source = reader(file);
                Files.RequireSchema(source.Document, kind);
                Files.Pin(metadata.Fingerprints, file, source.Sha256);
                return source.Document.Deserialize<T>(ProjectInputs.Json)
                    ?? throw Error(file, "Empty authoring document.");
            }
            catch (JsonException error)
            {
                throw Error(file, $"JSON {error.Path}, line {error.LineNumber + 1}: {error.Message}");
            }
            catch (InputException error)
            {
                throw Error(file, error.Message);
            }
            catch (IOException error)
            {
                throw Error(file, error.Message);
            }
        }

        internal AnalysisProject Load()
        {
            ProjectAuthoring source = Document<ProjectAuthoring>(root, "analysis-project");
            Markup.RequireVersion(source.AnnotationVersion);
            Id(root, source.Id);
            metadata.Composed = (source.Components?.Count ?? 0) + (source.InputSets?.Count ?? 0) +
                (source.ExecutionProfiles?.Count ?? 0) > 0;
            foreach (string selected in source.InputSets ?? [])
            {
                string file = Import(root, selected);
                ProjectInputSets document = Document<ProjectInputSets>(file, "project-input-sets");
                foreach (ProjectInputSet set in document.Sets)
                {
                    Id(file, set.Id);
                    List<string> inputs = set.Inputs.Select(p => Rebase(file, p)).ToList();
                    HashSet<string> selectedLedgers = new(FilePaths.Comparer);
                    foreach (string reference in set.PreparedSources ?? [])
                    {
                        string ledgerPath = Import(file, reference);
                        if (!selectedLedgers.Add(ledgerPath)) throw Error(file, "Duplicate prepared-source reference.");
                        PreparedSources ledger = Document<PreparedSources>(ledgerPath, "prepared-sources");
                        if (ledger.Status != "prepared" || ledger.AnnotationVersion != source.AnnotationVersion ||
                            ledger.Files.Count is < 1 or > 1000)
                            throw Error(ledgerPath, "Invalid prepared-source file list.");
                        inputs.Add(Rebase(ledgerPath, ledgerPath));
                        HashSet<string> destinations = new(FilePaths.Comparer);
                        foreach (PreparedFile prepared in ledger.Files)
                        {
                            if (prepared == null || FilePaths.IsAbsolute(prepared.Destination))
                                throw Error(ledgerPath, "Prepared-source destinations must be relative.");
                            string path = ProjectAcceptance.SafePath(Path.GetDirectoryName(ledgerPath)!, prepared.Destination);
                            if (!destinations.Add(path)) throw Error(ledgerPath, "Duplicate prepared-source destination.");
                            inputs.Add(Rebase(ledgerPath, path));
                        }
                    }
                    if (inputs.Count == 0) throw Error(file, "Input sets cannot be empty: " + set.Id);
                    if (!inputSets.TryAdd(set.Id, inputs))
                        throw Error(file, "Duplicate input set: " + set.Id);
                }
            }
            foreach (string selected in source.ExecutionProfiles ?? [])
            {
                string file = Import(root, selected);
                ProjectExecutionProfiles document = Document<ProjectExecutionProfiles>(file, "project-execution-profiles");
                foreach (ExecutionProfile profile in document.Profiles)
                {
                    Id(file, profile.Id);
                    if (profile.TimeoutSeconds is < 1 or > 7200)
                        throw Error(file, "Profile timeout must be 1..7200 seconds: " + profile.Id);
                    Budget(file, profile.Kind, profile.Resources);
                    if (!profiles.TryAdd(profile.Id, profile)) throw Error(file, "Duplicate execution profile: " + profile.Id);
                }
            }
            foreach (string selected in source.Components ?? []) Component(ProjectInputs.Resolve(root, selected), 0);
            List<AnalysisScenario> scenarios = [];
            foreach (ScenarioAuthoring scenario in source.Scenarios)
            {
                List<AnalysisStage> stages = [];
                foreach (StageAuthoring stage in scenario.Stages ?? [])
                {
                    stages.Add(Stage(root, stage));
                    metadata.StageOrigins[scenario.Id + "/" + stage.Id] = root;
                }
                HashSet<string> selected = new(StringComparer.Ordinal);
                foreach (string id in scenario.Components ?? [])
                {
                    if (!selected.Add(id)) throw Error(root, "Duplicate selected component: " + id);
                    if (!components.TryGetValue(id, out var entry)) throw Error(root, "Unknown component: " + id);
                    foreach (StageAuthoring stage in entry.Component.Stages)
                    {
                        stages.Add(Stage(entry.File, stage));
                        metadata.StageOrigins[scenario.Id + "/" + stage.Id] = entry.File;
                    }
                }
                if (stages.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != stages.Count)
                    throw Error(root, "Duplicate stage IDs in scenario: " + scenario.Id);
                scenarios.Add(new(scenario.Id, stages));
            }
            List<ReviewObligation> obligations = (source.Obligations ?? []).Select(o => Review(root, o)).ToList();
            List<CheckObligation> checks = [.. source.CheckObligations ?? []];
            foreach (var entry in components.Values)
            {
                obligations.AddRange((entry.Component.Obligations ?? []).Select(o => Review(entry.File, o)));
                checks.AddRange(entry.Component.CheckObligations ?? []);
            }
            Unique(obligations.Select(o => o.Id), root, "review obligation");
            Unique(checks.Select(o => o.Id), root, "check obligation");
            Unique(scenarios.Select(s => s.Id), root, "scenario");
            return new AnalysisProject(source.SchemaVersion, source.Kind, source.Id, source.Tools,
                source.Compiler, source.Requirements, scenarios, obligations, source.ReviewDirectory,
                source.CheckObligations == null && components.Count == 0 ? null : checks, source.ResultStore,
                source.AnnotationVersion) { Authoring = metadata };
        }

        private void Component(string file, int depth)
        {
            file = FilePaths.Full(file);
            if (depth > 32) throw Error(file, "Component imports exceed 32 levels.");
            if (active.Contains(file)) throw Error(file, "Cyclic component import.");
            if (!visited.Add(file)) throw Error(file, "Duplicate authoring import.");
            active.Add(file);
            ProjectComponent component = Document<ProjectComponent>(file, "project-component");
            Id(file, component.Id);
            if (!components.TryAdd(component.Id, (component, file))) throw Error(file, "Duplicate component ID: " + component.Id);
            foreach (StageAuthoring stage in component.Stages)
            {
                Id(file, stage.Id);
                if (!stageIds.TryAdd(stage.Id, file)) throw Error(file, "Duplicate component stage ID: " + stage.Id);
                _ = Stage(file, stage);
            }
            foreach (string selected in component.Components ?? []) Component(ProjectInputs.Resolve(file, selected), depth + 1);
            active.Remove(file);
        }

        private string Import(string declaring, string selected)
        {
            string file = ProjectInputs.Resolve(declaring, selected);
            if (!visited.Add(file)) throw Error(file, "Duplicate authoring import.");
            return file;
        }

        private AnalysisStage Stage(string file, StageAuthoring source)
        {
            Id(file, source.Id);
            int timeout;
            TlcResources? resources = source.Resources;
            if (source.Profile != null)
            {
                if (source.TimeoutSeconds != null || source.Resources != null)
                    throw Error(file, source.Id + ": select a profile or inline timeout/resources, not both.");
                if (!profiles.TryGetValue(source.Profile, out ExecutionProfile? profile))
                    throw Error(file, source.Id + ": unknown execution profile: " + source.Profile);
                if (profile.Kind != source.Kind) throw Error(file, source.Id + ": execution profile kind does not match stage kind.");
                timeout = profile.TimeoutSeconds;
                resources = profile.Resources;
            }
            else timeout = source.TimeoutSeconds ?? throw Error(file, source.Id + ": timeout_seconds or profile is required.");
            if (timeout is < 1 or > 7200) throw Error(file, source.Id + ": timeout must be 1..7200 seconds.");
            Budget(file, source.Kind, resources);
            return new(source.Id, source.Kind, source.DependsOn ?? [], timeout,
                source.Bindings == null ? null : Rebase(file, source.Bindings),
                source.Sources?.Select(p => Rebase(file, p)).ToList(),
                source.Models?.Select(p => Rebase(file, p)).ToList(),
                source.Config == null ? null : Rebase(file, source.Config), source.Arguments, resources, source.Fingerprint,
                source.Scope == null ? null : Rebase(file, source.Scope), source.Target,
                source.MethodSummaries?.Select(s => s with { Scope = Rebase(file, s.Scope) }).ToList());
        }

        private ReviewObligation Review(string file, ReviewAuthoring source)
        {
            List<string> inputs = (source.Inputs ?? []).Select(p => Rebase(file, p)).ToList();
            Unique(source.InputSets ?? [], file, "input-set reference");
            foreach (string id in source.InputSets ?? [])
            {
                if (!inputSets.TryGetValue(id, out List<string>? set)) throw Error(file, source.Id + ": unknown input set: " + id);
                inputs.AddRange(set);
            }
            if (source.InputSets is { Count: > 0 })
            {
                HashSet<string> identities = new(FilePaths.Comparer);
                inputs = inputs.Where(p => identities.Add(ProjectInputs.Resolve(root, p))).ToList();
            }
            return new(source.Id, source.Claim, source.Assumptions, inputs, source.Origin);
        }

        private string Rebase(string file, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw Error(file, "File references cannot be empty.");
            if (FilePaths.Comparer.Equals(file, root)) return value;
            string absolute = ProjectInputs.Resolve(file, value);
            return Path.GetRelativePath(Path.GetDirectoryName(root)!, absolute);
        }

        private static void Budget(string file, string kind, TlcResources? resources)
        {
            if (!ProjectInputs.StageKinds.Contains(kind, StringComparer.Ordinal))
                throw Error(file, "Unsupported stage/profile kind: " + kind);
            if (resources != null && kind is not ("tlc" or "trace"))
                throw Error(file, "Only TLC/trace execution profiles support resources.");
            if (kind is "tlc" or "trace") _ = TlcResources.Effective(kind, resources);
        }

        private static void Id(string file, string id)
        {
            if (!Schema.IsStableId(id)) throw Error(file, "Invalid stable ID: " + id);
        }

        private static void Unique(IEnumerable<string> ids, string file, string label)
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                Id(file, id);
                if (!seen.Add(id)) throw Error(file, "Duplicate " + label + ": " + id);
            }
        }

        private static InputException Error(string file, string message) => new(ErrorCode.InvalidWorkflowInput, file + ": " + message);
    }
}
