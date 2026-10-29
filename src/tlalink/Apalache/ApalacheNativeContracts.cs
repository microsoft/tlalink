using System.Text.Json.Serialization;

namespace TlaLink;

public sealed record ApalacheEvidenceResult(int SchemaVersion, string Kind, string ArchiveStatus,
    string CurrentApplicability, string? AnalysisStatus, List<Issue> Diagnostics, string Boundary);

internal sealed record ApalacheNativeInvocation(int SchemaVersion, string Kind, string Executable,
    string WorkingDirectory, int TimeoutSeconds, string Environment, ApalacheProcessResult Result);

internal sealed record ApalacheNativeConfig(
    [property: JsonPropertyName("out-dir")] string OutDir,
    [property: JsonPropertyName("run-dir")] string RunDir,
    [property: JsonPropertyName("write-intermediate")] bool WriteIntermediate,
    [property: JsonPropertyName("typechecker")] ApalacheNativeTypechecker Typechecker,
    [property: JsonPropertyName("checker"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ApalacheNativeChecker? Checker);

internal sealed record ApalacheNativeTypechecker(
    [property: JsonPropertyName("infer-poly")] bool InferPoly);

internal sealed record ApalacheNativeChecker(
    [property: JsonPropertyName("init")] string Init,
    [property: JsonPropertyName("next")] string Next,
    [property: JsonPropertyName("inv")] IReadOnlyList<string> Inv,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("algo")] string Algo,
    [property: JsonPropertyName("smt-solver")] string SmtSolver,
    [property: JsonPropertyName("smt-encoding")] string SmtEncoding,
    [property: JsonPropertyName("discard-disabled")] bool DiscardDisabled,
    [property: JsonPropertyName("no-deadlock")] bool NoDeadlock,
    [property: JsonPropertyName("timeout-smt")] int TimeoutSmt,
    [property: JsonPropertyName("max-error")] int MaxError,
    [property: JsonPropertyName("cinit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Cinit);
