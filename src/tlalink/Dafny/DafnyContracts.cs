using System.Text.Json;
using System.Text.Json.Serialization;

namespace TlaLink;

public sealed record DafnyParameter(string Name, string Type, bool Ghost);
public sealed record DafnyDeclaration(string Name, string Module, string Kind, string Path, int Line, int EndLine,
    bool IsStatic, bool Ghost, bool HasBody, List<DafnyParameter> Inputs, List<DafnyParameter> Outputs)
{
    public int Start { get; init; }
    public int End { get; init; }
    public int Column { get; init; } = 1;
}
public sealed record DafnyIndexResult(List<DafnyDeclaration> Declarations, List<string> Dependencies);
public sealed record ConformanceCase(string Id, List<JsonElement> Inputs);
public sealed record DafnyBindingSelection(string Id, string? Adapter, List<ConformanceCase> Cases);
internal sealed record IntegratedAuthoring(int SchemaVersion, string Kind, List<string> Sources,
    string Scope, List<DafnyBindingSelection> Bindings, List<Correspondence> Correspondences)
{
    public List<SourceObligation> SourceObligations { get; init; } = [];
    public List<string> TlaTargets { get; init; } = [];
}
public sealed record DafnyBinding(string Id, [property: JsonIgnore] string Source,
    [property: JsonIgnore] string Dafny, string? Adapter, List<ConformanceCase> Cases)
{
    [JsonConstructor]
    public DafnyBinding(string id, string? adapter, List<ConformanceCase> cases)
        : this(id, "", "", adapter, cases) { }
}
public sealed record Correspondence(string Id, string TlaStep, string DafnyBinding, string Relation,
    List<string> Assumptions, List<string> Obligations);
public sealed record IntegratedManifest(int SchemaVersion, List<string> Sources, [property: JsonIgnore] List<string> Proofs,
    List<DafnyBinding> Bindings, [property: JsonIgnore] List<string> Models, List<Correspondence> Correspondences)
{
    [JsonRequired]
    public string Kind { get; init; } = "integrated-bindings";
    public List<SourceObligation> SourceObligations { get; init; } = [];
    [JsonRequired]
    public string Scope { get; init; } = "";
    [JsonIgnore]
    public CorrespondenceConfiguration? Configuration { get; init; }
    public List<string> TlaTargets { get; init; } = [];
    [JsonConstructor]
    public IntegratedManifest(int schemaVersion, List<string> sources, List<DafnyBinding> bindings,
        List<Correspondence> correspondences)
        : this(schemaVersion, sources, [], bindings, [], correspondences) { }
}
public sealed record DafnyLink(string Id, Endpoint Implementation, DafnyDeclaration Specification,
    string? Adapter, string Assessment = "unreviewed", string Refinement = "not-established");
public sealed record CorrespondenceResult(Correspondence Claim, string Status = "requires-review",
    string CompositionStatus = "not-proved");
public sealed record StageResult(string Status, string Log, int? ExitCode);

public class DafnyEvidenceState
{
    public string ProofStatus { get; set; } = "not-run";
    public string AuditStatus { get; set; } = "not-run";
    public List<Issue> Diagnostics { get; } = [];
    public Dictionary<string, string> Fingerprints { get; } = new(FilePaths.Comparer);
    public Dictionary<string, string> GeneratedFiles { get; } = new(FilePaths.Comparer);
    public Dictionary<string, StageResult> Stages { get; } = [];
}

public sealed class IntegratedPacket(string command) : DafnyEvidenceState
{
    public int SchemaVersion => Schema.Version;
    public string Kind => "integrated-result";
    public string Command { get; } = command;
    public string? Bindings { get; set; }
    public string LinksStatus { get; set; } = "incomplete";
    public string CoverageStatus => "requires-review";
    public string ConformanceStatus { get; set; } = "not-run";
    public string CorrespondenceStatus => Correspondences.Count == 0 ? "not-specified" : "requires-review";
    public string ProvenanceStatus { get; set; } = "incomplete";
    public string Status { get; set; } = "incomplete";
    public List<DafnyLink> Links { get; } = [];
    public List<SourceDisposition> Dispositions { get; } = [];
    public List<OmissionRuleMatch> OmissionMatches { get; } = [];
    public List<SourceLinkDeclaration> SourceBindings { get; } = [];
    public List<DafnyDeclaration> Inventory { get; } = [];
    public List<Endpoint> SourceInventory { get; } = [];
    public List<CorrespondenceResult> Correspondences { get; } = [];
    public List<SourceObligationResult> SourceObligations { get; } = [];
    public string SourceObligationsStatus => TlaLink.SourceObligations.Status(SourceObligations);
    public Packet? Tla { get; set; }
    public object? Execution { get; set; }
    public List<string> Boundaries { get; } = [
        "A Dafny proof establishes contracts of the supplied Dafny program, not equivalence of linked C#.",
        "Executable conformance compares only the explicit cases against compiled Dafny oracles.",
        "Adapters, abstraction relations, preconditions, exceptions, side effects and concurrency require review.",
        "Cross-model correspondence is a documented obligation, not a composed refinement proof.",
        "Source obligations check selected structural premises, not CLR semantics, callee effects or C#/model refinement.",
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
        path = FilePaths.Full(path);
        IntegratedManifest manifest = Parse(Files.ReadPinned(path, pins));
        if (manifest.SchemaVersion != Schema.Version || manifest.Kind != "integrated-bindings" ||
            manifest.Bindings.Count == 0 || manifest.Bindings.Count > 1000)
            throw new InputException(ErrorCode.InvalidIntegratedInput, "Expected current integrated-bindings schema and 1..1000 bindings.");
        if (manifest.Sources.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(manifest.Scope) ||
            manifest.Bindings.Any(b => b is null) || manifest.Correspondences.Any(c => c is null))
            throw new InputException(ErrorCode.InvalidIntegratedInput, "Scope paths and binding/correspondence records cannot be empty or null.");
        if (manifest.Bindings.Sum(b => b.Cases.Count) > 1000 || manifest.Correspondences.Count > 1000)
            throw new InputException(ErrorCode.InvalidIntegratedInput, "Scope exceeds 1000 executable cases or correspondences.");
        string root = Path.GetDirectoryName(path)!;
        List<string> Resolve(List<string> paths) => paths.Select(p => FilePaths.Full(p, root)).ToList();
        string scopePath = FilePaths.Full(manifest.Scope, root);
        CorrespondenceConfiguration config = CorrespondenceInputs.Load(scopePath, pins);
        ProjectInputs.Unique(manifest.TlaTargets, "selected TLA+ targets");
        foreach (string alias in manifest.TlaTargets) CorrespondenceInputs.Target(config, alias, "tla");
        List<string> sources = Files.Discover(Resolve(manifest.Sources), ".cs");
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (DafnyBinding binding in manifest.Bindings)
        {
            if (!Markup.IsCorrespondenceId(binding.Id) || !ids.Add(binding.Id))
                throw new InputException(ErrorCode.InvalidIntegratedInput, "Binding IDs must be nonempty/unique.");
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
        if (manifest.SourceObligations == null)
            throw new InputException(ErrorCode.InvalidSourceObligation, "Source obligations must be an array, not null.");
        SourceObligations.Validate(manifest.SourceObligations, manifest.Bindings.Select(b => b.Id));
        return manifest with {
            Sources = sources, Scope = scopePath, Configuration = config,
            Proofs = config.Targets.Where(t => t.Language == "dafny").SelectMany(t => t.Inputs).Distinct(FilePaths.Comparer).ToList(),
            Models = config.Targets.Where(t => manifest.TlaTargets.Contains(t.Id, StringComparer.Ordinal)).SelectMany(t => t.Inputs)
                .Distinct(FilePaths.Comparer).ToList()
        };
    }

    public static IntegratedManifest ResolveBindings(IntegratedManifest manifest, CSharpScope source)
    {
        List<DafnyBinding> bindings = [];
        foreach (DafnyBinding binding in manifest.Bindings)
        {
            List<SourceBinding> matches = source.Bindings.Where(b => b.BindingId == binding.Id).ToList();
            if (matches.Count != 1)
                throw new InputException(ErrorCode.InvalidIntegratedBinding, "Expected one inline Dafny binding: " + binding.Id);
            SourceBinding inline = matches[0];
            CorrespondenceInputs.Target(manifest.Configuration!, inline.Model, "dafny");
            string sourceId = CSharpScope.DeclarationId(inline);
            bindings.Add(binding with { Source = sourceId });
        }

        HashSet<string> aliases = source.Bindings.Where(b => bindings.Any(d => d.Id == b.BindingId))
            .Select(b => b.Model).ToHashSet(StringComparer.Ordinal);
        return manifest with {
            Bindings = bindings,
            Proofs = manifest.Configuration!.Targets.Where(t => t.Language == "dafny" && aliases.Contains(t.Id))
                .SelectMany(t => t.Inputs).Distinct(FilePaths.Comparer).ToList(),
            Models = manifest.Configuration.Targets.Where(t => manifest.TlaTargets.Contains(t.Id, StringComparer.Ordinal))
                .SelectMany(t => t.Inputs).Distinct(FilePaths.Comparer).ToList()
        };
    }

    internal static IntegratedManifest Parse(string text)
    {
        IntegratedAuthoring author = JsonSerializer.Deserialize<IntegratedAuthoring>(text, Json)
            ?? throw new InputException(ErrorCode.InvalidIntegratedInput, "Missing integrated manifest.");
        if (author.Bindings.Any(b => b is null))
            throw new InputException(ErrorCode.InvalidIntegratedInput, "Binding selections cannot be null.");
        return new(author.SchemaVersion, author.Sources, [], author.Bindings.Select(b =>
            new DafnyBinding(b.Id, "", "", b.Adapter, b.Cases)).ToList(), [], author.Correspondences) {
            Kind = author.Kind, Scope = author.Scope, SourceObligations = author.SourceObligations, TlaTargets = author.TlaTargets
        };
    }

    public static void CheckPins(Dictionary<string, string> pins)
    {
        foreach (var pair in pins.Where(p => FilePaths.IsAbsolute(p.Key)))
            if (!FilePaths.IsNativeAbsolute(pair.Key) || !File.Exists(pair.Key) || Files.Hash(pair.Key) != pair.Value)
                throw new InputException(ErrorCode.IntegratedInputChanged, "Input changed during integrated run: " + pair.Key);
    }
}
