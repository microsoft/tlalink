using System.Text.Json;
using System.Text.RegularExpressions;

namespace TlaLink;

public sealed record ApalacheAnalysis(int SchemaVersion, string Kind, string Name, string Mode,
    string Model, string Init, string Next, List<string> Properties, List<string> Assumptions,
    int? Length = null, string? Invariant = null, string? ConstInit = null);
public sealed record ApalacheQuery(string Id, string Mode, string Init, List<string> Properties, int Length);
public sealed record ApalacheProcessResult(string Id, List<string> Arguments, int? ExitCode, bool TimedOut,
    double DurationSeconds = 0);
public sealed class ApalachePacket
{
    public int SchemaVersion => Schema.Version;
    public string Kind => "apalache-result";
    public required string Config { get; init; }
    public required string ConfigSha256 { get; init; }
    public required string Mode { get; init; }
    public required string Directory { get; init; }
    public required string Java { get; init; }
    public required string Jar { get; init; }
    public required string Model { get; init; }
    public required int TimeoutSeconds { get; init; }
    public string Status { get; set; } = "incomplete";
    public string Claim { get; set; } = "not-established";
    public List<ApalacheProcessResult> Processes { get; init; } = [];
    public List<Issue> Diagnostics { get; init; } = [];
    public Dictionary<string, string> Fingerprints { get; init; } = new(FilePaths.Comparer);
    public Dictionary<string, string> ModelFiles { get; init; } = new(FilePaths.Comparer);
    public Dictionary<string, string> Artifacts { get; init; } = new(StringComparer.Ordinal);
    public List<string> Boundaries { get; init; } = [
        "Checks the selected TLA+ model, not all executions of linked C#.",
        "Bounded safety is not finite-state exhaustion or an inductive proof.",
        "Induction requires initiation, preservation and safety implication over the selected domains.",
        "No temporal/liveness, deadlock-freedom or causal trace-matching claim.",
        "Assumptions and source correspondence require review; trusted tools are not authenticated."
    ];
}

public static class ApalacheInputs
{
    public const string Version = "0.62.4";
    public static bool IsStage(string kind) => kind is "apalache-typecheck" or "apalache-check" or "apalache-inductive";
    public static string Stage(string mode) => mode switch {
        "typecheck" => "apalache-typecheck", "bounded" => "apalache-check", "inductive" => "apalache-inductive",
        _ => throw new InputException(ErrorCode.InvalidApalacheInput, "Unsupported Apalache mode: " + mode)
    };
    public static string Claim(string mode) => mode switch {
        "typecheck" => "well-typed-model", "bounded" => "bounded-safety", "inductive" => "inductive-invariants",
        _ => throw new InputException(ErrorCode.InvalidApalacheInput, "Unsupported Apalache mode: " + mode)
    };

    public static void Validate(ApalacheAnalysis config)
    {
        if (config.SchemaVersion != Schema.Version || config.Kind != "apalache-analysis" ||
            !Schema.IsStableId(config.Name) || string.IsNullOrWhiteSpace(config.Model) ||
            !Path.GetExtension(config.Model).Equals(".tla", StringComparison.OrdinalIgnoreCase))
            throw new InputException(ErrorCode.InvalidApalacheInput, "Expected current apalache-analysis with a stable name and one .tla model.");
        _ = Stage(config.Mode);
        static bool Operator(string name) => Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z_0-9]*$", RegexOptions.CultureInvariant);
        if (!Operator(config.Init) || !Operator(config.Next) ||
            config.ConstInit != null && !Operator(config.ConstInit) ||
            config.Invariant != null && !Operator(config.Invariant) ||
            config.Properties.Any(p => p == null || !Operator(p)) ||
            config.Properties.Count > 100 || config.Properties.Distinct(StringComparer.Ordinal).Count() != config.Properties.Count ||
            config.Assumptions.Any(string.IsNullOrWhiteSpace) || config.Assumptions.Count > 100)
            throw new InputException(ErrorCode.InvalidApalacheInput, "Select distinct named predicates and explicit nonempty assumption descriptions.");
        if (config.Mode == "bounded" && (config.Length is null or < 0 or > 10000 || config.Properties.Count == 0 || config.Invariant != null) ||
            config.Mode == "typecheck" && (config.Length != null || config.Invariant != null || config.Properties.Count != 0 || config.ConstInit != null) ||
            config.Mode == "inductive" && (config.Length != null || config.Invariant == null || config.Properties.Count == 0))
            throw new InputException(ErrorCode.InvalidApalacheInput, "Bounded safety needs an explicit length and properties; induction needs an invariant and properties; typechecking has neither.");
    }

    public static ApalacheAnalysis Load(string path, Dictionary<string, string> pins)
    {
        path = FilePaths.Full(path);
        ApalacheAnalysis config = JsonSerializer.Deserialize<ApalacheAnalysis>(Files.ReadPinned(path, pins), ProjectInputs.Json)
            ?? throw new InputException(ErrorCode.InvalidApalacheInput, "Empty Apalache analysis.");
        Validate(config);
        config = config with { Model = ProjectInputs.Resolve(path, config.Model) };
        Files.Pin(pins, config.Model, Files.Hash(config.Model));
        return config;
    }

    public static List<ApalacheQuery> Queries(ApalacheAnalysis config) => config.Mode switch {
        "typecheck" => [new("typecheck", "typecheck", config.Init, [], 0)],
        "bounded" => [new("bounded", "check", config.Init, config.Properties, config.Length!.Value)],
        "inductive" => [
            new("initiation", "check", config.Init, [config.Invariant!], 0),
            new("preservation", "check", config.Invariant!, [config.Invariant!], 1),
            new("implication", "check", config.Invariant!, config.Properties, 0)],
        _ => throw new InputException(ErrorCode.InvalidApalacheInput, "Unsupported Apalache mode.")
    };

    public static void PinDistribution(string jar, Dictionary<string, string> pins)
    {
        jar = FilePaths.Full(jar);
        string lib = Path.GetDirectoryName(jar)!;
        if (Path.GetFileName(jar) != "apalache.jar" || Path.GetFileName(lib) != "lib" || !File.Exists(jar))
            throw new InputException(ErrorCode.ApalachePrerequisiteMissing, "--apalache must select lib\\apalache.jar in a local Apalache " + Version + " distribution.");
        string home = Path.GetDirectoryName(lib)!;
        foreach (string file in Directory.EnumerateFiles(home, "*", new EnumerationOptions {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false
        })) Files.Pin(pins, FilePaths.Full(file), Files.Hash(file));
    }
}
