using System.Text.Json.Serialization;

namespace TlaLink;

public sealed record SynchronizationDeclaration(List<string> Primitives);
public sealed record PrimitiveOperation(string Member, string Role, List<string>? Actions = null,
    List<string>? Fails = null, string? Wait = null, string? Precondition = null);
public sealed record PrimitiveVerification(List<string> Scenarios, string Inspection);
public sealed record PrimitiveManifest(int SchemaVersion, string Kind, string Id, string Type, List<string> Sources,
    string PrimitiveKind, List<PrimitiveOperation> Operations, PrimitiveVerification Verification,
    List<string> Assumptions, List<string> Limits, string? Module = null, List<string>? Guarantees = null,
    List<string>? DependsOn = null,
    [property: JsonPropertyName("$schema")] string? EditorSchema = null);
public sealed record PrimitiveUse(string Scope, List<string> Ids);
public sealed record PrimitiveVerificationEvidence(string Scenario, string Receipt);
public sealed record PrimitiveSupport(string SelectionSha256, string SourceContextSha256, List<string> Inputs,
    List<PrimitiveVerificationEvidence> Verification);
public sealed record PrimitiveResolution(string Scope, string Manifest, PrimitiveManifest Primitive, string Module,
    List<string> Members);
public sealed record PlannedPrimitive(string Scope, string Manifest, PrimitiveManifest Primitive, string Module,
    string InspectionStatus, ReviewOrigin InspectionOrigin, List<string> OutstandingGuarantees, string Boundary);
public sealed record PrimitiveVerificationStatus(string Scenario, string? Receipt, string Status);
public sealed record AcceptancePrimitive(string Scope, string Id, string PrimitiveKind, string Type, string Use,
    string Inspection, string InspectionStatus, List<PrimitiveVerificationStatus> Verification, string SupportStatus,
    List<string> DependsOn, List<string> OutstandingGuarantees, List<string> Assumptions, List<string> Limits,
    string Boundary);

internal sealed record SelectedPrimitive(string Scope, string ScopeSha256, string Manifest, string ManifestSha256,
    PrimitiveManifest Primitive, string Module);
internal sealed record CurrentPrimitiveSupport(string SelectionSha256, string SourceContextSha256, List<string> Inputs,
    List<string> Scenarios);
internal sealed record PrimitivePin(string Key, string Value);
internal sealed record PrimitiveSourceContext(CompilerProfile Compiler, List<PrimitivePin> Pins);
internal sealed record PrimitiveIdentity(string Scope, string Manifest, string Module, PrimitiveManifest Primitive);
