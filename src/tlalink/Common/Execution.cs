using System.Diagnostics;
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
        string target = Path.Combine(output, "Evidence.targets");
        string sources = Path.Combine(output, "compile.txt"), refs = Path.Combine(output, "references.txt"), binaryFile = Path.Combine(output, "binary.txt");
        string contextFile = Path.Combine(output, "context.txt");
        string Escape(string value) => System.Security.SecurityElement.Escape(value);
        File.WriteAllText(target, $"""
            <Project>
              <Target Name="TlaLinkCaptureInputs" BeforeTargets="CoreCompile">
                <GetFileHash Files="@(Compile)" Algorithm="SHA256"><Output TaskParameter="Items" ItemName="_TlaSources" /></GetFileHash>
                <GetFileHash Files="@(ReferencePath)" Algorithm="SHA256"><Output TaskParameter="Items" ItemName="_TlaRefs" /></GetFileHash>
                <WriteLinesToFile File="{Escape(sources)}" Lines="@(_TlaSources->'%(FullPath)|%(FileHash)')" Overwrite="true" />
                <WriteLinesToFile File="{Escape(refs)}" Lines="@(_TlaRefs->'%(FullPath)|%(FileHash)')" Overwrite="true" />
                <WriteLinesToFile File="{Escape(contextFile)}" Lines="TargetFramework|$(TargetFramework);LangVersion|$(LangVersion);Nullable|$(Nullable);AllowUnsafeBlocks|$(AllowUnsafeBlocks);Defines|$([System.String]::Copy('$(DefineConstants)').Replace(';', ','))" Overwrite="true" />
              </Target>
              <Target Name="TlaLinkCaptureBinary" AfterTargets="Build">
                <GetFileHash Files="$(TargetPath)" Algorithm="SHA256"><Output TaskParameter="Items" ItemName="_TlaBinary" /></GetFileHash>
                <WriteLinesToFile File="{Escape(binaryFile)}" Lines="@(_TlaBinary->'%(FullPath)|%(FileHash)')" Overwrite="true" />
              </Target>
            </Project>
            """);
        string sdk = Invoke(["--version"], output, timeout, "sdk").Trim();
        List<string> build = ["build", project, "--no-restore", "--no-incremental", "--nologo",
            "-p:CustomAfterMicrosoftCommonTargets=" + target];
        Invoke(build, output, timeout, "build");
        Dictionary<string, string> inputs = ReadHashes(sources), references = ReadHashes(refs), binaries = ReadHashes(binaryFile);
        Dictionary<string, string> context = File.ReadAllLines(contextFile).Select(line => line.Split('|', 2)).ToDictionary(pair => pair[0], pair => pair[1]);
        CheckContext(manifest, context);
        if (binaries.Count != 1) throw new InputException(ErrorCode.InvalidExecution, "Expected exactly one target binary; multi-target projects are not supported.");
        foreach (var generated in manifest.GeneratedFiles)
            if (!inputs.TryGetValue(generated.Key, out string? hash) || hash != generated.Value)
                throw new InputException(ErrorCode.InvalidExecution, "Generated input was not compiled exactly: " + generated.Key);
        Check(inputs); Check(references); Check(binaries);
        string binary = binaries.Single().Key;
        List<string> run = [binary, trace, .. args];
        Invoke(run, output, timeout, "run");
        Check(inputs); Check(references); Check(binaries); Check(manifest.GeneratedFiles);
        if (!File.Exists(trace)) throw new InputException(ErrorCode.InvalidExecution, "Execution did not complete its trace.");
        using JsonDocument captured = JsonDocument.Parse(Files.Read(trace));
        Files.RequireSchema(captured.RootElement, "trace");
        if (!captured.RootElement.GetProperty("complete").GetBoolean() ||
            captured.RootElement.GetProperty("binding_id").GetString() != manifest.BindingId)
            throw new InputException(ErrorCode.InvalidExecution, "Trace is incomplete or belongs to another manifest.");
        if (!captured.RootElement.GetProperty("binaries").TryGetProperty(binary, out JsonElement loaded) ||
            loaded.GetString() != binaries.Single().Value)
            throw new InputException(ErrorCode.InvalidExecution, "Trace did not execute the captured build binary.");
        ExecutionReceipt receipt = new(Schema.Version, Files.Hash(manifestPath), trace, Files.Hash(trace), inputs, references, binary,
            binaries.Single().Value, sdk, context, build, run,
            "Tool-observed compiler inputs and loaded entry binary; no compiler proof, host authentication, complete native closure or resistance to a hostile build project.");
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
        foreach (var pair in receipt.CompileInputs.Concat(receipt.References)) Files.Pin(pins, pair.Key, pair.Value);
        Files.Pin(pins, receipt.Binary, receipt.BinarySHA256);
        Files.Pin(pins, FilePaths.Full(path), Files.Hash(path));
        using JsonDocument captured = JsonDocument.Parse(Files.Read(trace));
        Files.RequireSchema(captured.RootElement, "trace");
        if (!captured.RootElement.GetProperty("binaries").TryGetProperty(receipt.Binary, out JsonElement loaded) ||
            loaded.GetString() != receipt.BinarySHA256)
            throw new InputException(ErrorCode.InvalidExecution, "Trace entry binary and receipt disagree.");
    }

    private static Dictionary<string, string> ReadHashes(string path)
    {
        if (!File.Exists(path)) throw new InputException(ErrorCode.InvalidExecution, "Build did not produce compiler evidence: " + path);
        Dictionary<string, string> result = new(FilePaths.Comparer);
        foreach (string row in File.ReadAllLines(path))
        {
            string[] fields = row.Split('|');
            if (fields.Length != 2 || fields[1].Length != 64) throw new InputException(ErrorCode.InvalidExecution, "Malformed compiler evidence.");
            Files.Pin(result, FilePaths.Full(fields[0]), fields[1].ToLowerInvariant());
        }

        if (result.Count == 0) throw new InputException(ErrorCode.InvalidExecution, "Empty compiler evidence.");
        return result;
    }

    private static void CheckContext(InstrumentManifest manifest, Dictionary<string, string> context)
    {
        string Normalize(string value) => string.Join(",", value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().Order(StringComparer.Ordinal));
        if (context.GetValueOrDefault("TargetFramework") != "net10.0" || context.GetValueOrDefault("LangVersion") is not ("14" or "14.0") ||
            context.GetValueOrDefault("Nullable") != "enable" ||
            Normalize(context.GetValueOrDefault("Defines", "")) != Normalize(manifest.Fingerprints.GetValueOrDefault("compiler:defines", "")))
            throw new InputException(ErrorCode.InvalidExecution, "Actual target/language/nullable/defines differ from the explicit Roslyn context: " + JsonSerializer.Serialize(context, Files.Json));
    }

    private static void Check(Dictionary<string, string> hashes)
    {
        foreach (var file in hashes)
            if (!File.Exists(file.Key) || Files.Hash(file.Key) != file.Value) throw new InputException(ErrorCode.InvalidExecution, "Build input changed: " + file.Key);
    }

    private static string Invoke(List<string> args, string output, int timeout, string label)
    {
        ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = output };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new InputException(ErrorCode.InvalidExecution, "Could not start dotnet.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        bool finished = process.WaitForExit(checked(timeout * 1000));
        if (!finished) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        string text = stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(output, label + ".log"), text);
        if (!finished || process.ExitCode != 0) throw new InputException(ErrorCode.InvalidExecution, $"dotnet {label} {(finished ? $"failed ({process.ExitCode})" : "timed out")}; see {Path.Combine(output, label + ".log")}");
        return text;
    }
}
