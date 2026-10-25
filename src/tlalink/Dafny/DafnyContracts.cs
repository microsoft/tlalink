using System.Text.Json;
using System.Text.Json.Serialization;

namespace TlaLink;

public sealed record DafnyParameter(string Name, string Type, bool Ghost);
public sealed record DafnyDeclaration(string Name, string Module, string Kind, string Path, int Line, int EndLine,
    bool IsStatic, bool Ghost, bool HasBody, List<DafnyParameter> Inputs, List<DafnyParameter> Outputs);
public sealed record DafnyIndexResult(List<DafnyDeclaration> Declarations, List<string> Dependencies);
public sealed record ConformanceCase(string Id, List<JsonElement> Inputs);
public sealed record DafnyBinding(string Id, string Source, string Dafny, string? Adapter, List<ConformanceCase> Cases);
public sealed record Correspondence(string Id, string TlaStep, string DafnyBinding, string Relation,
    List<string> Assumptions, List<string> Obligations);
public sealed record IntegratedManifest(int SchemaVersion, List<string> Sources, List<string> Proofs,
    List<DafnyBinding> Bindings, List<string> Models, List<Correspondence> Correspondences)
{
    [JsonRequired]
    public string Kind { get; init; } = "integrated-bindings";
}
public sealed record DafnyLink(string Id, Endpoint Implementation, DafnyDeclaration Specification,
    string? Adapter, string Assessment = "unreviewed", string Refinement = "not-established");
public sealed record CorrespondenceResult(Correspondence Claim, string Status = "requires-review",
    string CompositionStatus = "not-proved");
public sealed record StageResult(string Status, string Log, int? ExitCode);

public sealed class IntegratedPacket(string command)
{
    public int SchemaVersion => Schema.Version;
    public string Kind => "integrated-result";
    public string Command { get; } = command;
    public string LinksStatus { get; set; } = "incomplete";
    public string CoverageStatus => "requires-review";
    public string ProofStatus { get; set; } = "not-run";
    public string AuditStatus { get; set; } = "not-run";
    public string ConformanceStatus { get; set; } = "not-run";
    public string CorrespondenceStatus => Correspondences.Count == 0 ? "not-specified" : "requires-review";
    public string ProvenanceStatus { get; set; } = "incomplete";
    public string Status { get; set; } = "incomplete";
    public List<Issue> Diagnostics { get; } = [];
    public List<DafnyLink> Links { get; } = [];
    public List<DafnyDeclaration> Inventory { get; } = [];
    public List<Endpoint> SourceInventory { get; } = [];
    public List<CorrespondenceResult> Correspondences { get; } = [];
    public Dictionary<string, string> Fingerprints { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> GeneratedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, StageResult> Stages { get; } = [];
    public Packet? Tla { get; set; }
    public object? Execution { get; set; }
    public List<string> Boundaries { get; } = [
        "A Dafny proof establishes contracts of the supplied Dafny program, not equivalence of linked C#.",
        "Executable conformance compares only the explicit cases against compiled Dafny oracles.",
        "Adapters, abstraction relations, preconditions, exceptions, side effects and concurrency require review.",
        "Cross-model correspondence is a documented obligation, not a composed refinement proof.",
        "TLA structural readiness is not a TLC safety, trace inclusion or liveness result.",
        "Hashes record consistency under a trusted compiler and host, not authentication."
    ];
}

public static class IntegratedInputs
{
    public static readonly JsonSerializerOptions Json = new(Files.Json)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static IntegratedManifest Load(string path, Dictionary<string, string> pins)
    {
        path = Path.GetFullPath(path);
        IntegratedManifest manifest = JsonSerializer.Deserialize<IntegratedManifest>(Files.ReadPinned(path, pins), Json)
            ?? throw new InputException(ErrorCode.InvalidIntegratedInput, "Missing integrated manifest.");
        if (manifest.SchemaVersion != Schema.Version || manifest.Kind != "integrated-bindings" ||
            manifest.Bindings.Count == 0 || manifest.Bindings.Count > 1000)
            throw new InputException(ErrorCode.InvalidIntegratedInput, "Expected current integrated-bindings schema and 1..1000 bindings.");
        if (manifest.Sources.Concat(manifest.Proofs).Concat(manifest.Models).Any(string.IsNullOrWhiteSpace) ||
            manifest.Bindings.Any(b => b is null) || manifest.Correspondences.Any(c => c is null))
            throw new InputException(ErrorCode.InvalidIntegratedInput, "Scope paths and binding/correspondence records cannot be empty or null.");
        if (manifest.Bindings.Sum(b => b.Cases.Count) > 1000 || manifest.Correspondences.Count > 1000)
            throw new InputException(ErrorCode.InvalidIntegratedInput, "Scope exceeds 1000 executable cases or correspondences.");
        string root = Path.GetDirectoryName(path)!;
        List<string> Resolve(List<string> paths) => paths.Select(p => Path.GetFullPath(p, root)).ToList();
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (DafnyBinding binding in manifest.Bindings)
        {
            if (!Schema.IsStableId(binding.Id) || !ids.Add(binding.Id) ||
                string.IsNullOrWhiteSpace(binding.Source) || string.IsNullOrWhiteSpace(binding.Dafny))
                throw new InputException(ErrorCode.InvalidIntegratedInput, "Binding IDs must be nonempty/unique and endpoints explicit.");
            if (binding.Cases.Count > 1000 || binding.Cases.Any(c => c is null || !Schema.IsStableId(c.Id)) ||
                binding.Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != binding.Cases.Count)
                throw new InputException(ErrorCode.InvalidIntegratedInput, "Case IDs must be nonempty/unique; maximum 1000 cases per binding.");
        }
        ids.Clear();
        foreach (Correspondence relation in manifest.Correspondences)
            if (!Schema.IsStableId(relation.Id) || !ids.Add(relation.Id) ||
                string.IsNullOrWhiteSpace(relation.Relation) || string.IsNullOrWhiteSpace(relation.TlaStep) ||
                relation.Assumptions.Count == 0 || relation.Obligations.Count == 0 ||
                relation.Assumptions.Concat(relation.Obligations).Any(string.IsNullOrWhiteSpace))
                throw new InputException(ErrorCode.InvalidIntegratedInput, "Correspondences need unique IDs, endpoints, a relation, assumptions and obligations.");
        return manifest with {
            Sources = Files.Discover(Resolve(manifest.Sources), ".cs"),
            Proofs = Files.Discover(Resolve(manifest.Proofs), ".dfy"),
            Models = manifest.Models.Count == 0 ? [] : Files.Discover(Resolve(manifest.Models), ".tla")
        };
    }

    public static void CheckPins(Dictionary<string, string> pins)
    {
        foreach (var pair in pins.Where(p => Path.IsPathFullyQualified(p.Key)))
            if (!File.Exists(pair.Key) || Files.Hash(pair.Key) != pair.Value)
                throw new InputException(ErrorCode.IntegratedInputChanged, "Input changed during integrated run: " + pair.Key);
    }
}
