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
/// <summary>A region field of a declared primitive type; Status is declared, excluded or undeclared.</summary>
public sealed record RegionInstance(string Field, string Primitive, string Status, List<string> Declarations, int Bound,
    int Excluded, int Unbound);
/// <summary>An operation site on an instance; Status is bound, excluded, unbound, or unresolved when the receiver is not a region field.</summary>
public sealed record RegionSite(string? Field, string Operation, string Role, string Receiver, Place Location, string Status,
    List<string> Bindings);
/// <summary>A bind covering sites; Missing lists kind operators its action does not reference (Status matched or mismatch).</summary>
public sealed record RegionContract(string Binding, string Operator, int Sites, string Status, List<string> Missing);
public sealed record RegionCoverage(List<RegionInstance> Instances, List<RegionSite> Sites, List<RegionContract> Contracts,
    string Boundary);
public sealed record SynchronizationRegionReport(string Scope, string Status, List<string> Files, List<RegionPrimitive> Primitives,
    List<RegionEnvironment> Environments, List<string> Locks, RegionSites Sites, List<Issue> Diagnostics,
    RegionCoverage? Coverage = null);
public sealed record PlannedRegion(string Role, List<string> Stages, SynchronizationRegionReport Region);
public sealed record SynchronizationResult(int SchemaVersion, string Kind, string Status, SynchronizationRegionReport Region,
    Dictionary<string, string> Fingerprints, List<Issue> Diagnostics, CommandInvocation Invocation, string Boundary)
    : IAnalysisResult, IInputEvidence;
