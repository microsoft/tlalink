using System.Text.Json;

namespace TlaLink;

public sealed record PBinding(string Id, [property: System.Text.Json.Serialization.JsonIgnore] string Source,
    [property: System.Text.Json.Serialization.JsonIgnore] string Declaration, string Relation,
    List<string> Assumptions)
{
    [System.Text.Json.Serialization.JsonConstructor]
    public PBinding(string id, string relation, List<string> assumptions)
        : this(id, "", "", relation, assumptions) { }
}
public sealed record PBindingSelection(string Id, string Relation, List<string> Assumptions);
internal sealed record PAuthoring(int SchemaVersion, string Kind, string Name, string Engine,
    List<string> Models, List<string> Sources, List<PBindingSelection> Bindings, List<ToolPin> Dependencies,
    string? TestCase = null, int Schedules = 100, int MaxSteps = 10000, int Seed = 1,
    ToolPin? Artifact = null, PObservation? Observation = null, string? Scope = null);
public sealed record PObservation(ToolPin RuntimeJar, ToolPin MonitorJar, string Specification,
    string Parser, List<string> Logs, string InputKind, int Replay);
public sealed record PAnalysis(int SchemaVersion, string Kind, string Name, string Engine,
    List<string> Models, List<string> Sources, List<PBinding> Bindings, List<ToolPin> Dependencies,
    string? TestCase = null, int Schedules = 100, int MaxSteps = 10000, int Seed = 1,
    ToolPin? Artifact = null, PObservation? Observation = null, string? Scope = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public CorrespondenceConfiguration? Configuration { get; init; }
}
public sealed record PSourceLink(string Id, Endpoint Implementation, PDeclaration Specification,
    string Relation, List<string> Assumptions)
{
    public string Assessment => "requires-review";
    public string Refinement => "not-established";
}
public sealed record PProcessResult(string Label, string Executable, List<string> Arguments,
    string WorkingDirectory, string Stdout, string Stderr, int? ExitCode, bool TimedOut);

public sealed class PPacket
{
    public int SchemaVersion => Schema.Version;
    public string Kind => "p-result";
    public required string Command { get; init; }
    public required string Engine { get; init; }
    public required string Config { get; init; }
    public required string ConfigSha256 { get; init; }
    public required int TimeoutSeconds { get; init; }
    public string Status { get; set; } = "incomplete";
    public string Claim { get; set; } = "not-established";
    public string LinksStatus { get; set; } = "not-run";
    public string ProvenanceStatus { get; set; } = "incomplete";
    public List<PDeclaration> Inventory { get; init; } = [];
    public List<PSourceLink> Links { get; init; } = [];
    public List<SourceDisposition> Dispositions { get; init; } = [];
    public List<OmissionRuleMatch> OmissionMatches { get; init; } = [];
    public List<SourceLinkDeclaration> SourceBindings { get; init; } = [];
    public List<Issue> Diagnostics { get; init; } = [];
    public List<PProcessResult> Processes { get; init; } = [];
    public Dictionary<string, string> Fingerprints { get; init; } = new(FilePaths.Comparer);
    public Dictionary<string, string> Artifacts { get; init; } = new(FilePaths.Comparer);
    public List<string> Boundaries { get; } = [
        "P checks the supplied P program, not all executions of linked C#.",
        "Source bindings and cross-backend reviews do not compose a refinement proof.",
        "Sampled schedules, exhausted finite searches and inductive proofs are distinct claims.",
        "Liveness depends on the selected scheduler, environment and monitor assumptions.",
        "PObserve checks selected events/properties, not causal whole-model trace inclusion.",
        "External JAR provenance and log completeness require explicit review.",
        "A trusted host/compiler and pinned bytes provide consistency, not authentication."
    ];
}

public static class PInputs
{
    public static readonly string[] Engines = ["inspect", "pchecker", "pex", "psym", "pverifier", "pobserve"];
    public static bool IsStage(string kind) => kind.StartsWith("p-", StringComparison.Ordinal) &&
        Engines.Contains(kind[2..], StringComparer.Ordinal);

    public static PAnalysis Load(string path, Dictionary<string, string> pins)
    {
        path = FilePaths.Full(path);
        string text = Files.ReadPinned(path, pins);
        PAnalysis config = Parse(text);
        if (config.SchemaVersion != Schema.Version || config.Kind != "p-analysis" ||
            !Schema.IsStableId(config.Name) || !Engines.Contains(config.Engine, StringComparer.Ordinal))
            throw new InputException(ErrorCode.InvalidPInput, "Expected current p-analysis, a stable name and supported engine.");
        if (config.Models.Count is 0 or > 1000 || config.Bindings.Count > 1000 ||
            config.Dependencies.Count > 1000 || config.Schedules is < 1 or > 1000000 ||
            config.MaxSteps is < 1 or > 10000000 || config.Seed < 0)
            throw new InputException(ErrorCode.InvalidPInput, "Invalid scope or exploration bounds.");
        string Resolve(string file) => string.IsNullOrWhiteSpace(file)
            ? throw new InputException(ErrorCode.InvalidPInput, "Paths cannot be empty.") : ProjectInputs.Resolve(path, file);
        ToolPin Tool(ToolPin tool)
        {
            if (tool is null) throw new InputException(ErrorCode.InvalidPInput, "Dependencies cannot be null.");
            string file = Resolve(tool.Path);
            if (tool.Sha256 is not { Length: 64 } || tool.Sha256.Any(c => !char.IsAsciiHexDigit(c)) ||
                !File.Exists(file) || !Files.Hash(file).Equals(tool.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InputException(ErrorCode.PPrerequisiteMissing, "Missing/mismatched pinned dependency: " + file);
            Files.Pin(pins, file, Files.Hash(file));
            return tool with { Path = file };
        }
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (PBinding binding in config.Bindings)
            if (binding is null || !Schema.IsStableId(binding.Id) || !ids.Add(binding.Id) || string.IsNullOrWhiteSpace(binding.Relation) ||
                binding.Assumptions.Count == 0 || binding.Assumptions.Any(string.IsNullOrWhiteSpace))
                throw new InputException(ErrorCode.InvalidPInput, "Bindings require unique IDs, explicit endpoints, relation and assumptions.");
        if (config.Bindings.Count > 0 && config.Sources.Count == 0)
            throw new InputException(ErrorCode.InvalidPInput, "P bindings require explicit C# sources.");
        if (config.Engine is "pchecker" or "pex" or "psym" && !Schema.IsStableId(config.TestCase ?? ""))
            throw new InputException(ErrorCode.InvalidPInput, "Exploration requires an explicit stable testcase.");
        if ((config.Engine is "psym" or "pex") != (config.Artifact != null) ||
            (config.Engine == "pobserve") != (config.Observation != null))
            throw new InputException(ErrorCode.InvalidPInput, "PEx/PSym need reviewed compiled JARs; PObserve needs its observation configuration.");
        PObservation? observation = config.Observation;
        if (observation != null)
        {
            if (observation.InputKind is not ("text" or "json") || observation.Replay is < 1 or > 100000 ||
                observation.Logs.Count is 0 or > 1000 || string.IsNullOrWhiteSpace(observation.Specification) ||
                string.IsNullOrWhiteSpace(observation.Parser))
                throw new InputException(ErrorCode.InvalidPInput, "PObserve needs explicit logs, spec, parser, input kind and replay bound.");
            observation = observation with {
                RuntimeJar = Tool(observation.RuntimeJar), MonitorJar = Tool(observation.MonitorJar),
                Logs = observation.Logs.Select(Resolve).ToList()
            };
            foreach (string log in observation.Logs) Files.Pin(pins, log, Files.Hash(log));
        }
        config = config with {
            Models = Files.Discover(config.Models.Select(Resolve), ".p"),
            Sources = config.Sources.Count == 0 ? [] : Files.Discover(config.Sources.Select(Resolve), ".cs"),
            Dependencies = config.Dependencies.Select(Tool).ToList(),
            Artifact = config.Artifact == null ? null : Tool(config.Artifact), Observation = observation
        };
        foreach (string input in config.Models.Concat(config.Sources)) Files.Pin(pins, input, Files.Hash(input));
        if (config.Bindings.Count > 0)
        {
            if (string.IsNullOrWhiteSpace(config.Scope))
                throw new InputException(ErrorCode.InvalidPInput, "Inline P bindings require an explicit correspondence scope.");
            string scope = Resolve(config.Scope);
            CorrespondenceConfiguration correspondence = CorrespondenceInputs.Load(scope, pins);
            config = config with { Scope = scope, Configuration = correspondence };
        }
        return config;
    }

    internal static PAnalysis Parse(string text)
    {
        PAuthoring author = JsonSerializer.Deserialize<PAuthoring>(text, ProjectInputs.Json)
            ?? throw new InputException(ErrorCode.InvalidPInput, "Empty P analysis.");
        if (author.Bindings.Any(b => b is null))
            throw new InputException(ErrorCode.InvalidPInput, "Binding selections cannot be null.");
        return new(author.SchemaVersion, author.Kind, author.Name, author.Engine, author.Models, author.Sources,
            author.Bindings.Select(b => new PBinding(b.Id, "", "", b.Relation, b.Assumptions)).ToList(),
            author.Dependencies, author.TestCase, author.Schedules, author.MaxSteps, author.Seed,
            author.Artifact, author.Observation, author.Scope);
    }
}
