using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TlaLink;

public sealed class DafnyBackend(string executable, string output, int timeout, DafnyEvidenceState packet)
{
    private List<string> dependencies = [];
    public string Executable { get; } = FilePaths.Full(executable);
    public string Output { get; } = FilePaths.Full(output);
    public string Home => Path.GetDirectoryName(Executable)!;
    public int TimeoutSeconds => timeout;

    public StageResult Run(string stage, string executable, IEnumerable<string> arguments, string? directory = null)
    {
        List<string> args = arguments.ToList();
        ProcessStartInfo start = new(executable) {
            WorkingDirectory = directory ?? Output, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        Files.WriteNew(Path.Combine(Output, stage + ".invocation.json"), new {
            SchemaVersion = Schema.Version, Kind = "process-invocation",
            Executable = executable, Arguments = args, start.WorkingDirectory, TimeoutSeconds = timeout
        });
        using Process process = Process.Start(start) ?? throw new InputException(ErrorCode.IntegratedToolFailure, "Could not start " + executable);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        bool finished = process.WaitForExit(checked(timeout * 1000));
        if (!finished) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        string log = Path.Combine(Output, stage + ".log");
        File.WriteAllText(log, stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult());
        StageResult result = new(!finished ? "incomplete" : process.ExitCode == 0 ? "passed" : "failed", log,
            finished ? process.ExitCode : null);
        packet.Stages.Add(stage, result);
        if (!finished) packet.Diagnostics.Add(new(ErrorCode.IntegratedToolFailure, "Stage timed out; retained log: " + log));
        return result;
    }

    public void Initialize()
    {
        if (!File.Exists(Executable) || !File.Exists(Path.Combine(Home, "DafnyCore.dll")))
            throw new InputException(ErrorCode.InvalidDafnyTool, "--dafny must select a trusted local Dafny 4.11.0 distribution executable.");
        PinDistribution(Executable, packet.Fingerprints);
        StageResult version = Run("dafny-version", Executable, ["--version"]);
        if (version.Status != "passed" || !Files.Read(version.Log).Trim().StartsWith("4.11.0+", StringComparison.Ordinal))
            throw new InputException(ErrorCode.InvalidDafnyTool, "This backend requires pinned Dafny 4.11.0; see " + version.Log);
        BuildIndex();
    }

    public static void PinDistribution(string executable, Dictionary<string, string> fingerprints)
    {
        string home = Path.GetDirectoryName(FilePaths.Full(executable))!;
        if (!File.Exists(executable) || !File.Exists(Path.Combine(home, "DafnyCore.dll")))
            throw new InputException(ErrorCode.InvalidDafnyTool, "Select a complete local Dafny 4.11.0 distribution.");
        foreach (string file in Directory.EnumerateFiles(home, "*", new EnumerationOptions {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false
        })) Files.Pin(fingerprints, FilePaths.Full(file), Files.Hash(file));
    }

    private void BuildIndex()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "DafnyIndex.source.cs");
        string source = Files.ReadPinned(path, packet.Fingerprints);
        string[] framework = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InputException(ErrorCode.InvalidDafnyTool, "Framework references unavailable.")).Split(Path.PathSeparator);
        Dictionary<string, string> references = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in framework) references.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        foreach (string file in Directory.EnumerateFiles(Home, "*.dll"))
        {
            // Native libraries are not compiler metadata references.
            try
            {
                AssemblyName name = AssemblyName.GetAssemblyName(file);
                references.TryAdd(name.Name!, file);
            }
            catch (BadImageFormatException) { }
        }
        CSharpCompilation compilation = CSharpCompilation.Create("TlaLink.DafnyIndex",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14))],
            references.Values.Select(p => MetadataReference.CreateFromFile(p)),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable, deterministic: true));
        string binary = Path.Combine(Output, "DafnyIndex.dll");
        var emitted = compilation.Emit(binary);
        if (!emitted.Success)
            throw new InputException(ErrorCode.InvalidDafnyTool, "Cannot build authoritative frontend bridge: " + string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        // runtimeconfig.json is a dotnet contract, not the evidence snake_case schema.
        File.WriteAllText(Path.Combine(Output, "DafnyIndex.runtimeconfig.json"),
            """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");
        foreach (string generated in new[] { binary, Path.Combine(Output, "DafnyIndex.runtimeconfig.json") })
            Files.Pin(packet.GeneratedFiles, generated, Files.Hash(generated));
        packet.Stages.Add("dafny-bridge", new("passed", binary, 0));
    }

    public DafnyIndexResult Index(List<string> roots)
    {
        DafnyIndexResult Read(string stage)
        {
            string index = Path.Combine(Output, stage + ".json");
            StageResult result = Run(stage, "dotnet", [Path.Combine(Output, "DafnyIndex.dll"), Home, index, .. roots]);
            if (result.Status != "passed")
                throw new InputException(ErrorCode.DafnyResolutionFailed, "Dafny parsing/resolution did not complete: " + result.Log);
            return JsonSerializer.Deserialize<DafnyIndexResult>(Files.Read(index), Files.Json)
                ?? throw new InputException(ErrorCode.DafnyResolutionFailed, "Missing authoritative index.");
        }
        foreach (string root in roots) Files.Pin(packet.Fingerprints, root, Files.Hash(root));
        DafnyIndexResult first = Read("dafny-discover");
        foreach (string path in first.Dependencies.Concat(roots).Distinct(FilePaths.Comparer))
        {
            if (Path.GetExtension(path) != ".dfy")
                throw new InputException(ErrorCode.DafnyResolutionFailed, "Only .dfy source dependencies are supported: " + path);
            Files.Pin(packet.Fingerprints, FilePaths.Full(path), Files.Hash(path));
        }
        IntegratedInputs.CheckPins(packet.Fingerprints);
        IntegratedInputs.CheckPins(packet.GeneratedFiles);
        DafnyIndexResult resolved = Read("dafny-resolve");
        if (!first.Dependencies.Order(FilePaths.Comparer).SequenceEqual(
            resolved.Dependencies.Order(FilePaths.Comparer), FilePaths.Comparer))
            throw new InputException(ErrorCode.IntegratedInputChanged, "Dafny dependency closure changed between frontend passes.");
        IntegratedInputs.CheckPins(packet.Fingerprints);
        dependencies = resolved.Dependencies.Concat(roots).Distinct(FilePaths.Comparer).ToList();
        return resolved;
    }

    public bool Verify(List<string> roots, string prefix = "dafny")
    {
        StageResult proof = Run(prefix + "-verify", Executable,
            ["verify", .. roots, "--verify-included-files", "--json-output", "--show-snippets=false",
                "--verification-time-limit", timeout.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        packet.ProofStatus = ClassifyVerification(proof, Files.Read(proof.Log));
        if (packet.ProofStatus != "verified")
        {
            packet.Diagnostics.Add(new(ErrorCode.DafnyVerificationFailed, "Dafny verification " + packet.ProofStatus + ": " + proof.Log));
            return false;
        }
        string auditFile = Path.Combine(Output, prefix + "-audit.txt");
        StageResult audit = Run(prefix + "-audit", Executable,
            ["audit", .. dependencies, "--report-file", auditFile, "--report-format", "txt"]);
        packet.AuditStatus = audit.Status != "passed" || !File.Exists(auditFile) ? "incomplete" :
            string.IsNullOrWhiteSpace(Files.Read(auditFile)) ? "passed" : "findings";
        if (packet.AuditStatus != "passed")
            packet.Diagnostics.Add(new(ErrorCode.DafnyAuditFailed, "Dafny assumption audit " + packet.AuditStatus + ": " + auditFile));
        return packet.AuditStatus == "passed";
    }

    public static string ClassifyVerification(StageResult stage, string log)
    {
        if (stage.Status == "incomplete" || stage.ExitCode is not (0 or 4)) return "incomplete";
        int summaries = 0, errors = 0;
        bool verifierError = false;
        foreach (string line in log.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement item = document.RootElement;
                string? type = item.GetProperty("type").GetString();
                JsonElement value = item.GetProperty("value");
                if (type == "status")
                {
                    string message = value.GetString() ?? "";
                    if (!message.Contains("Dafny program verifier finished", StringComparison.Ordinal)) continue;
                    // Only a complete native summary without resource/timeout suffixes establishes an outcome.
                    Match summary = Regex.Match(message.Trim(),
                        @"\ADafny program verifier finished with [0-9]+ verified, ([0-9]+) errors?\z",
                        RegexOptions.CultureInvariant);
                    if (!summary.Success || !int.TryParse(summary.Groups[1].Value, out errors)) return "incomplete";
                    summaries++;
                }
                else if (type == "diagnostic")
                {
                    if (value.GetProperty("severity").GetInt32() == 1)
                    {
                        if (value.GetProperty("source").GetString() != "Verifier") return "incomplete";
                        string message = value.GetProperty("defaultFormatMessage").GetString() ?? "";
                        if (Regex.IsMatch(message,
                            @"\A(?:Verification (?:timed out|out of resource|inconclusive)|(?:Prover|Solver) (?:died|exception)|Out of memory)",
                            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "incomplete";
                        verifierError = true;
                    }
                }
                else return "incomplete";
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                return "incomplete";
            }
        }
        return summaries != 1 ? "incomplete" :
            stage.Status == "passed" && stage.ExitCode == 0 && errors == 0 && !verifierError ? "verified" :
            stage.Status == "failed" && stage.ExitCode == 4 && errors > 0 && verifierError ? "rejected" : "incomplete";
    }
}
