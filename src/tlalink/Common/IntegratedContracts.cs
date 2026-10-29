using System.Collections.Immutable;

namespace TlaLink;

public sealed record IntegratedCompilerContext(string LanguageVersion, ImmutableArray<string> Defines,
    string Nullable, string Framework, string Compiler);

public sealed record IntegratedExecution(ImmutableDictionary<string, string> CompileInputs,
    ImmutableDictionary<string, string> References, IntegratedCompilerContext CompilerContext,
    string Binary, string BinarySHA256, ImmutableArray<CaseComparison> Cases,
    ImmutableArray<string> TestedBindings, ImmutableArray<string> UntestedBindings,
    string ComparisonSHA256, string Boundary);

public sealed record OracleComparisons(int SchemaVersion, string Kind, string Binary, string BinarySHA256,
    ImmutableArray<CaseComparison> Cases);

public sealed record IntegratedTlaResult(int SchemaVersion, string Kind, string Command, string LinksStatus,
    string CoverageStatus, string ConformanceStatus, ImmutableArray<Issue> Diagnostics,
    ImmutableArray<Link> Links, ImmutableArray<Endpoint> Inventory,
    ImmutableArray<SourceDisposition> Dispositions, ImmutableArray<OmissionRuleMatch> OmissionMatches,
    ImmutableArray<SourceLinkDeclaration> SourceBindings, ImmutableArray<string> SelectedTargets,
    ImmutableDictionary<string, string> Fingerprints, ImmutableArray<string> Uncertainties)
{
    public static IntegratedTlaResult From(Packet packet) => new(packet.SchemaVersion, packet.Kind,
        packet.Command, packet.LinksStatus, packet.CoverageStatus, packet.ConformanceStatus,
        [.. packet.Diagnostics], [.. packet.Links], [.. packet.Inventory], [.. packet.Dispositions],
        [.. packet.OmissionMatches], [.. packet.SourceBindings], [.. packet.SelectedTargets],
        packet.Fingerprints.ToImmutableDictionary(FilePaths.Comparer), [.. packet.Uncertainties]);
}

public sealed record IntegratedResult(int SchemaVersion, string Kind, string Command, string? Bindings,
    string LinksStatus, string CoverageStatus, string ProofStatus, string AuditStatus,
    string ConformanceStatus, string CorrespondenceStatus, string ProvenanceStatus, string Status,
    ImmutableArray<Issue> Diagnostics, ImmutableDictionary<string, string> Fingerprints,
    ImmutableDictionary<string, string> GeneratedFiles, ImmutableDictionary<string, StageResult> Stages,
    ImmutableArray<DafnyLink> Links, ImmutableArray<SourceDisposition> Dispositions,
    ImmutableArray<OmissionRuleMatch> OmissionMatches, ImmutableArray<SourceLinkDeclaration> SourceBindings,
    ImmutableArray<DafnyDeclaration> Inventory, ImmutableArray<Endpoint> SourceInventory,
    ImmutableArray<CorrespondenceResult> Correspondences, IntegratedTlaResult? Tla,
    IntegratedExecution? Execution, ImmutableArray<string> Boundaries) : IAnalysisResult, IInputEvidence
{
    List<Issue> IAnalysisResult.Diagnostics => [.. Diagnostics];
    Dictionary<string, string> IInputEvidence.Fingerprints =>
        Fingerprints.ToDictionary(p => p.Key, p => p.Value, FilePaths.Comparer);

    public ImmutableArray<SourceObligationResult> SourceObligations { get; init; } = [];
    public string? SourceObligationsStatus { get; init; }

    public static IntegratedResult From(IntegratedPacket packet) => new(packet.SchemaVersion, packet.Kind,
        packet.Command, packet.Bindings, packet.LinksStatus, packet.CoverageStatus, packet.ProofStatus,
        packet.AuditStatus, packet.ConformanceStatus, packet.CorrespondenceStatus, packet.ProvenanceStatus,
        packet.Status, [.. packet.Diagnostics], packet.Fingerprints.ToImmutableDictionary(FilePaths.Comparer),
        packet.GeneratedFiles.ToImmutableDictionary(FilePaths.Comparer), packet.Stages.ToImmutableDictionary(),
        [.. packet.Links], [.. packet.Dispositions], [.. packet.OmissionMatches], [.. packet.SourceBindings],
        [.. packet.Inventory], [.. packet.SourceInventory], [.. packet.Correspondences],
        packet.Tla == null ? null : IntegratedTlaResult.From(packet.Tla), packet.Execution, [.. packet.Boundaries])
    {
        SourceObligations = [.. packet.SourceObligations],
        SourceObligationsStatus = packet.SourceObligationsStatus
    };
}
