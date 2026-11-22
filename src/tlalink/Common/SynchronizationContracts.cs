using System.Text.Json.Serialization;

namespace TlaLink;

public sealed record SynchronizationDeclaration(List<string> Primitives, List<string>? Region = null,
    List<string>? Environments = null, List<SynchronizationLock>? Locks = null, List<SpawnDeclaration>? Spawns = null,
    List<WaitDisposition>? Waits = null);
public sealed record SynchronizationLock(string Field, List<string>? MayHold = null, string? IndexedBy = null);
public sealed record SpawnDeclaration(PreparationTarget Site, string Disposition, string Reason);
public sealed record WaitDisposition(PreparationTarget Site, string Kind, string Reason);

public sealed record EnvironmentOperation(string Member, string Role, List<string>? Actions = null, string? Wait = null,
    bool LockSafe = false);
public sealed record EnvironmentManifest(int SchemaVersion, string Kind, string Id, string EnvironmentKind,
    List<string> Types, List<EnvironmentOperation> Operations, List<string> Obligations, List<string> Evidence,
    string? Module = null, [property: JsonPropertyName("$schema")] string? EditorSchema = null);

/// <summary>Status is discharged (a static check passed), failed, or outstanding (declaration and inspection only).</summary>
public sealed record GuaranteeStatus(string Guarantee, string Status, string Check, List<string> Findings);
public sealed record RegionPrimitive(string Id, string PrimitiveKind, string Type, string Origin, List<GuaranteeStatus> Guarantees);
public sealed record RegionEnvironment(string Id, string EnvironmentKind, List<string> Types, string Manifest, string? Module,
    List<string> Operations, List<string> LockSafe, List<string> Obligations, List<string> Evidence, string Support);
public sealed record RegionSites(int Awaits, int LockStatements, int SpawnSites, int PrimitiveOperations,
    int EnvironmentOperations, int WaitDispositions);
public sealed record SynchronizationRegionReport(string Scope, string Status, List<string> Files, List<RegionPrimitive> Primitives,
    List<RegionEnvironment> Environments, List<string> Locks, RegionSites Sites, List<Issue> Diagnostics);
public sealed record PlannedRegion(string Role, List<string> Stages, SynchronizationRegionReport Region);
public sealed record SynchronizationResult(int SchemaVersion, string Kind, string Status, SynchronizationRegionReport Region,
    Dictionary<string, string> Fingerprints, List<Issue> Diagnostics, CommandInvocation Invocation, string Boundary)
    : IAnalysisResult, IInputEvidence;
