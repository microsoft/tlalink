using System.Text.Json;
using System.Text.Json.Serialization;

namespace TlaLink;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionReceipt(int SchemaVersion, string ManifestSHA256, string TracePath, string TraceSHA256,
    Dictionary<string, string> CompileInputs, Dictionary<string, string> References, string Binary, string BinarySHA256,
    string Sdk, Dictionary<string, string> CompilerContext, List<string> BuildArguments, List<string> RunArguments, string Boundary)
{
    [JsonRequired]
    public string Kind { get; init; } = "execution-receipt";
    [JsonRequired]
    public Dictionary<string, string> BuildInputs { get; init; } = new(FilePaths.Comparer);
    [JsonRequired]
    public List<string> MissingBuildInputs { get; init; } = [];
}

public static class Execution
{
    public static ExecutionReceipt Run(string manifestPath, string project, string trace, string output, int timeout, List<string> args)
    {
        manifestPath = FilePaths.Full(manifestPath); project = FilePaths.Full(project);
        trace = FilePaths.Full(trace); output = FilePaths.Full(output);
        if (Directory.Exists(output) || File.Exists(output) || File.Exists(trace))
            throw new InputException(ErrorCode.InvalidExecution, "Execution needs new output directory and trace path.");
        InstrumentManifest manifest = Instrumentation.ReadManifest(manifestPath);
        Check(manifest.GeneratedFiles);
        Directory.CreateDirectory(output);
        ObservedBuild observed = ObservedExecution.Build(project, Path.Combine(output, "build"), timeout);
        Dictionary<string, string> inputs = observed.CompileInputs, references = observed.References;
        Dictionary<string, string> context = observed.CompilerContext;
        CheckContext(manifest, context);
        foreach (var generated in manifest.GeneratedFiles)
            if (!inputs.TryGetValue(generated.Key, out string? hash) || hash != generated.Value)
                throw new InputException(ErrorCode.InvalidExecution, "Generated input was not compiled exactly: " + generated.Key);
        ObservedExecution.Check(observed);
        string binary = observed.Binary;
        List<string> run = [binary, trace, .. args];
        ObservedExecution.RequireSuccess(ObservedExecution.Invoke(run, output, timeout, "run"));
        ObservedExecution.Check(observed); Check(manifest.GeneratedFiles);
        if (!File.Exists(trace)) throw new InputException(ErrorCode.InvalidExecution, "Execution did not complete its trace.");
        using JsonDocument captured = JsonDocument.Parse(Files.Read(trace));
        Files.RequireSchema(captured.RootElement, "trace");
        if (!captured.RootElement.GetProperty("complete").GetBoolean() ||
            captured.RootElement.GetProperty("binding_id").GetString() != manifest.BindingId)
            throw new InputException(ErrorCode.InvalidExecution, "Trace is incomplete or belongs to another manifest.");
        if (!captured.RootElement.GetProperty("binaries").TryGetProperty(binary, out JsonElement loaded) ||
            loaded.GetString() != observed.BinarySHA256)
            throw new InputException(ErrorCode.InvalidExecution, "Trace did not execute the captured build binary.");
        ExecutionReceipt receipt = new(Schema.Version, Files.Hash(manifestPath), trace, Files.Hash(trace), inputs, references, binary,
            observed.BinarySHA256, observed.Sdk, context, observed.BuildArguments, run,
            "Tool-observed compiler inputs and loaded entry binary; no compiler proof, host authentication, complete native closure or resistance to a hostile build project.")
            { BuildInputs = observed.BuildInputs, MissingBuildInputs = observed.MissingBuildInputs };
        Files.WriteNew(Path.Combine(output, "execution.json"), receipt);
        return receipt;
    }

    public static void Validate(string path, string manifest, string trace, Dictionary<string, string> pins)
    {
        using JsonDocument document = JsonDocument.Parse(Files.Read(path));
        Files.RequireSchema(document.RootElement, "execution-receipt");
        ExecutionReceipt receipt = document.RootElement.Deserialize<ExecutionReceipt>(Files.Json)
            ?? throw new InputException(ErrorCode.InvalidExecution, "Invalid execution receipt.");
        if (receipt.ManifestSHA256 != Files.Hash(manifest) ||
            !FilePaths.Full(trace).Equals(receipt.TracePath, FilePaths.Comparison) || receipt.TraceSHA256 != Files.Hash(trace))
            throw new InputException(ErrorCode.InvalidExecution, "Receipt does not bind this manifest and trace.");
        InstrumentManifest instrument = Instrumentation.ReadManifest(manifest);
        CheckContext(instrument, receipt.CompilerContext);
        foreach (var generated in instrument.GeneratedFiles)
            if (!receipt.CompileInputs.TryGetValue(generated.Key, out string? value) || value != generated.Value)
                throw new InputException(ErrorCode.InvalidExecution, "Receipt is missing exact generated compile input.");
        if (receipt.BuildInputs.Count == 0) throw new InputException(ErrorCode.InvalidExecution, "Receipt lacks observed build inputs.");
        if (receipt.MissingBuildInputs.Any(p => File.Exists(p) || Directory.Exists(p)))
            throw new InputException(ErrorCode.InvalidExecution, "An absent optional compiler configuration appeared after execution.");
        foreach (var pair in receipt.CompileInputs.Concat(receipt.References).Concat(receipt.BuildInputs)) Files.Pin(pins, pair.Key, pair.Value);
        Files.Pin(pins, receipt.Binary, receipt.BinarySHA256);
        Files.Pin(pins, FilePaths.Full(path), Files.Hash(path));
        using JsonDocument captured = JsonDocument.Parse(Files.Read(trace));
        Files.RequireSchema(captured.RootElement, "trace");
        if (!captured.RootElement.GetProperty("binaries").TryGetProperty(receipt.Binary, out JsonElement loaded) ||
            loaded.GetString() != receipt.BinarySHA256)
            throw new InputException(ErrorCode.InvalidExecution, "Trace entry binary and receipt disagree.");
    }

    private static void CheckContext(InstrumentManifest manifest, Dictionary<string, string> context)
    {
        string Normalize(string value) => string.Join(",", value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().Order(StringComparer.Ordinal));
        if (context.GetValueOrDefault("TargetFramework") != "net10.0" || context.GetValueOrDefault("LangVersion") is not ("14" or "14.0") ||
            context.GetValueOrDefault("Nullable") != "enable" ||
            Normalize(context.GetValueOrDefault("Defines", "")) != Normalize(manifest.Fingerprints.GetValueOrDefault("compiler:defines", "")))
            throw new InputException(ErrorCode.InvalidExecution, "Actual target/language/nullable/defines differ from the explicit Roslyn context: " + JsonSerializer.Serialize(context, Files.Json));
    }

    private static void Check(Dictionary<string, string> hashes) => ObservedExecution.Check(hashes);
}
