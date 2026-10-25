using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TlaLink;

public static class ApalacheBackend
{
    public static int Run(CliOptions options)
    {
        if (options.Command == "read-apalache") return ApalacheEvidence.Read(options);
        string configPath = FilePaths.Full(options.Required("config"));
        string output = FilePaths.Full(options.Required("output"));
        if (File.Exists(output) || Directory.Exists(output))
            throw new InputException(ErrorCode.InvalidApalacheInput, "Apalache output must be a new directory.");
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        ApalacheAnalysis config = ApalacheInputs.Load(configPath, pins);
        string jar = FilePaths.Full(options.Required("apalache"));
        ApalacheInputs.PinDistribution(jar, pins);
        JavaInstallation java = JavaToolchain.Resolve();
        foreach (var pin in java.Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
        Program.PinTool(pins);
        string adapter = Path.Combine(AppContext.BaseDirectory, "TlaIndex.java");
        Files.Pin(pins, adapter, Files.Hash(adapter));
        string sany = FilePaths.Full(options.Required("jar"));
        Files.Pin(pins, sany, Files.Hash(sany));
        Directory.CreateDirectory(output);
        ApalachePacket packet = new() {
            Config = configPath, ConfigSha256 = Files.Hash(configPath), Mode = config.Mode,
            Directory = output, Java = java.Java, Jar = jar, Model = config.Model,
            TimeoutSeconds = options.Timeout, Fingerprints = pins
        };
        try
        {
            TlaDocument model = TlaBackend.Parse(config.Model, sany, options.Timeout);
            foreach (var pin in model.DependencyFingerprints) Files.Pin(pins, pin.Key, pin.Value);
            RequireSupportedModel(model, config);
            using JsonDocument index = JsonDocument.Parse(model.RawAdapterJson!);
            Files.WriteNew(Path.Combine(output, "model-index.json"), index.RootElement);
            string sourceDirectory = Path.Combine(output, "model");
            Directory.CreateDirectory(sourceDirectory);
            foreach (string file in model.DependencyFingerprints.Keys.Append(config.Model)
                .Where(p => FilePaths.IsNativeAbsolute(p) && Path.GetExtension(p).Equals(".tla", StringComparison.OrdinalIgnoreCase))
                .Distinct(FilePaths.Comparer))
            {
                string relative = Path.Combine("model", Path.GetFileName(file));
                string destination = Path.Combine(output, relative);
                if (File.Exists(destination))
                    throw new InputException(ErrorCode.InvalidApalacheInput, "Ambiguous module basename in model closure: " + file);
                File.Copy(file, destination);
                packet.ModelFiles.Add(file, relative);
                Files.Pin(pins, file, Files.Hash(destination));
            }
            ApalacheProcessResult version = RunProcess(packet, "version", VersionArguments(packet));
            packet.Processes.Add(version);
            if (!VersionMatches(version, Text(output, version.Id)))
                throw new InputException(ErrorCode.ApalachePrerequisiteMissing, "This adapter requires Apalache " + ApalacheInputs.Version + "; see version logs.");
            foreach (ApalacheQuery query in ApalacheInputs.Queries(config))
            {
                Files.WriteNew(Path.Combine(output, query.Id + ".json"), NativeConfig(packet, config, query));
                ApalacheProcessResult process = RunProcess(packet, query.Id, Arguments(packet, query));
                packet.Processes.Add(process);
                string nativeText = Text(output, process.Id);
                string assessment = Assess(process, query, nativeText,
                    HasTrace(output, process.Id, "violation", null),
                    HasTrace(output, process.Id, "example", query.Length + 1));
                if (assessment != "passed")
                {
                    packet.Status = assessment;
                    packet.Diagnostics.Add(new(assessment == "rejected" ? RejectionCode(config.Mode) : ErrorCode.ApalacheToolFailure,
                        FailureSummary(process, assessment, nativeText)));
                    break;
                }
            }
            if (packet.Diagnostics.Count == 0)
            {
                packet.Status = config.Mode == "typecheck" ? "ready" : "passed";
                packet.Claim = ApalacheInputs.Claim(config.Mode);
            }
            IntegratedInputs.CheckPins(pins);
        }
        catch (Exception error) when (error is InputException or IOException or UnauthorizedAccessException or JsonException or
            System.ComponentModel.Win32Exception or TimeoutException)
        {
            packet.Status = "incomplete";
            packet.Claim = "not-established";
            packet.Diagnostics.Add(new(error is InputException { Code: ErrorCode code } ? code : ErrorCode.ApalacheToolFailure, error.Message));
        }
        ApalacheEvidence.Archive(output, packet);
        Files.WriteNew(Path.Combine(output, "result.json"), packet);
        ProjectWorkflow.Render(packet, options);
        return packet.Status switch { "ready" or "passed" => 0, "rejected" => 1, _ => 2 };
    }

    internal static void RequireSupportedModel(TlaDocument model, ApalacheAnalysis config)
    {
        if (model.Diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.StaleTranslation, ErrorCode.ToolFailure)) ||
            model.RawAdapterJson == null)
            throw new InputException(ErrorCode.InvalidApalacheInput, "Apalache requires a successfully parsed, fresh model: " +
                string.Join("; ", model.Diagnostics.Select(d => d.Message)));
        using JsonDocument index = JsonDocument.Parse(model.RawAdapterJson);
        RequireSupportedIndex(index.RootElement, config);
        IEnumerable<string> names = config.Mode == "typecheck" ? [] :
            new[] { config.Init, config.Next }.Concat(config.Properties)
                .Concat(config.Invariant == null ? [] : new[] { config.Invariant })
                .Concat(config.ConstInit == null ? [] : new[] { config.ConstInit });
        foreach (string name in names)
            if (!model.Endpoints.Any(e => e.Kind == "operator" && e.Name == name && e.Arity == 0))
                throw new InputException(ErrorCode.InvalidApalacheInput, "Expected a root nullary model operator: " + name);
        if (model.DependencyFingerprints.Keys.Append(config.Model)
            .Where(p => FilePaths.IsNativeAbsolute(p) && Path.GetExtension(p).Equals(".tla", StringComparison.OrdinalIgnoreCase))
            .Distinct(FilePaths.Comparer).GroupBy(Path.GetFileName, FilePaths.Comparer).Any(group => group.Count() > 1))
            throw new InputException(ErrorCode.InvalidApalacheInput, "Ambiguous module basename in model closure: " + config.Model);
    }

    internal static void RequireSupportedIndex(JsonElement index, ApalacheAnalysis config)
    {
        if (index.ValueKind != JsonValueKind.Array)
            throw new InputException(ErrorCode.InvalidApalacheInput, "Expected an authoritative SANY model index.");
        string[] assumptions = index.EnumerateArray().Where(row => row.GetProperty("kind").GetString() == "assumptions")
            .Select(row => row.GetProperty("module").GetString()!).ToArray();
        if (assumptions.Length > 0)
            throw new InputException(ErrorCode.InvalidApalacheInput, "ASSUME declarations in modules " +
                string.Join(", ", assumptions) + " are outside the supported Apalache profile; encode constraints in Init/Next/ConstInit.");
        if (config.Mode == "typecheck") return;
        void Level(string name, int maximum)
        {
            JsonElement[] rows = index.EnumerateArray().Where(row => row.GetProperty("kind").GetString() == "operator-level" &&
                row.GetProperty("name").GetString() == name).ToArray();
            if (rows.Length != 1 || rows[0].GetProperty("level").GetInt32() < 0 ||
                rows[0].GetProperty("level").GetInt32() > maximum)
                throw new InputException(ErrorCode.InvalidApalacheInput, "Unsupported predicate level for " + name + "; expected at most " + maximum + ".");
        }
        Level(config.Init, 1);
        Level(config.Next, 2);
        foreach (string property in config.Properties) Level(property, 1);
        if (config.Invariant != null) Level(config.Invariant, 1);
        if (config.ConstInit != null) Level(config.ConstInit, 0);
    }

    internal static ErrorCode RejectionCode(string mode) =>
        mode == "inductive" ? ErrorCode.ApalacheInductionRejected : ErrorCode.ApalachePropertyRejected;

    internal static string FailureSummary(ApalacheProcessResult process, string assessment, string text)
    {
        MatchCollection phases = Regex.Matches(text, @"PASS #\d+: ([A-Za-z0-9_]+)", RegexOptions.CultureInvariant);
        string phase = phases.Count == 0 ? "" : "; phase " + phases[^1].Groups[1].Value;
        string execution = process.TimedOut ? "deadline exceeded" : "exit " + (process.ExitCode?.ToString() ?? "not recorded");
        string[] errors = text.Split('\n').Select(line => line.Trim())
            .Where(line => Regex.IsMatch(line,
                @"(?:\s[EF]@\d{2}:|^(?:Assignment error:|Type error:|Parser error:|Error:|Exception in thread|\w+(?:\.\w+)*Exception:))",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            .Take(3).Select(line => Regex.Replace(line, @"\s+[EF]@\d{2}:.*$", "", RegexOptions.CultureInvariant)).ToArray();
        string detail = errors.Length == 0 ? "No recognized native error message." : string.Join(" | ", errors);
        if (detail.Length > 1500) detail = detail[..1500] + "...";
        return $"{process.Id}: native {assessment} ({execution}{phase}); {detail} " +
            $"See {process.Id}.stdout.log, {process.Id}.stderr.log and archived counterexamples.";
    }

    public static string Assess(ApalacheProcessResult process, ApalacheQuery query, string text, bool counterexample,
        bool example = false)
    {
        if (process.TimedOut || process.ExitCode == null) return "incomplete";
        if (query.Mode == "typecheck")
            return process.ExitCode == 0 && text.Contains("Type checker [OK]", StringComparison.Ordinal) &&
                text.Contains("All expressions are typed", StringComparison.Ordinal) ? "passed" : "incomplete";
        MatchCollection outcomes = Regex.Matches(text, @"The outcome is: ([A-Za-z]+)", RegexOptions.CultureInvariant);
        if (outcomes.Count != 1) return "incomplete";
        if (process.ExitCode == 0 && outcomes[0].Groups[1].Value == "NoError" &&
            Regex.IsMatch(text, @"Checker reports no error up to computation length " + query.Length + @"\b", RegexOptions.CultureInvariant) &&
            !text.Contains("SMT timeout", StringComparison.OrdinalIgnoreCase) && example) return "passed";
        return process.ExitCode == 12 && outcomes[0].Groups[1].Value == "Error" &&
            text.Contains("Checker has found an error", StringComparison.Ordinal) && counterexample ? "rejected" : "incomplete";
    }

    internal static bool VersionMatches(ApalacheProcessResult process, string text) =>
        !process.TimedOut && process.ExitCode == 0 && text.Trim() == ApalacheInputs.Version;

    internal static bool HasTrace(string output, string id, string prefix, int? stateCount,
        IEnumerable<string>? artifacts = null)
    {
        string directory = Path.Combine(output, id);
        if (!Directory.Exists(directory)) return false;
        IEnumerable<string> files = artifacts == null ? Directory.EnumerateFiles(directory, "*.itf.json", SearchOption.AllDirectories) :
            artifacts.Where(p => p.StartsWith(id + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                p.EndsWith(".itf.json", StringComparison.Ordinal)).Select(p => FilePaths.Full(p, output));
        return files.Where(file => Path.GetFileName(file).StartsWith(prefix, StringComparison.Ordinal)).Any(file => {
            using JsonDocument document = JsonDocument.Parse(Files.Read(file));
            JsonElement root = document.RootElement;
            return root.TryGetProperty("vars", out JsonElement vars) && vars.ValueKind == JsonValueKind.Array &&
                root.TryGetProperty("states", out JsonElement states) && states.ValueKind == JsonValueKind.Array &&
                states.GetArrayLength() > 0 && (stateCount == null || states.GetArrayLength() == stateCount);
        });
    }

    internal static string Text(string output, string id) =>
        Files.Read(Path.Combine(output, id + ".stdout.log")) + "\n" + Files.Read(Path.Combine(output, id + ".stderr.log"));

    internal static JsonObject NativeConfig(ApalachePacket packet, ApalacheAnalysis config, ApalacheQuery query)
    {
        JsonObject result = new() {
            ["out-dir"] = Path.Combine(packet.Directory, query.Id),
            ["run-dir"] = Path.Combine(packet.Directory, query.Id),
            ["write-intermediate"] = false,
            ["typechecker"] = new JsonObject { ["infer-poly"] = true }
        };
        if (query.Mode != "typecheck")
        {
            JsonObject checker = new() {
                ["init"] = query.Init, ["next"] = config.Next,
                ["inv"] = new JsonArray(query.Properties.Select(p => JsonValue.Create(p)).ToArray()),
                ["length"] = query.Length, ["algo"] = "incremental", ["smt-solver"] = "z3",
                ["smt-encoding"] = "oopsla19", ["discard-disabled"] = false, ["no-deadlock"] = true,
                ["timeout-smt"] = packet.TimeoutSeconds, ["max-error"] = 1
            };
            if (config.ConstInit != null) checker["cinit"] = config.ConstInit;
            result["checker"] = checker;
        }
        return result;
    }

    private static List<string> JavaArguments(ApalachePacket packet) => [
        "-Xmx1g", "-Duser.home=" + Path.Combine(packet.Directory, "home"),
        "-Djava.io.tmpdir=" + Path.Combine(packet.Directory, "temp"), "-jar", packet.Jar
    ];

    internal static List<string> VersionArguments(ApalachePacket packet) => [.. JavaArguments(packet), "version"];
    internal static List<string> Arguments(ApalachePacket packet, ApalacheQuery query) => [
        .. JavaArguments(packet), "--config-file=" + Path.Combine(packet.Directory, query.Id + ".json"), query.Mode,
        .. (query.Mode == "check" ? new[] { "--output-traces=true" } : []),
        Path.Combine(packet.Directory, "model", Path.GetFileName(packet.Model))
    ];

    private static ApalacheProcessResult RunProcess(ApalachePacket packet, string id, List<string> arguments)
    {
        foreach (string folder in new[] { "home", "temp" }) Directory.CreateDirectory(Path.Combine(packet.Directory, folder));
        ProcessStartInfo start = new(packet.Java) {
            WorkingDirectory = packet.Directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        start.Environment.Clear();
        if (systemRoot != null) start.Environment["SystemRoot"] = systemRoot;
        foreach (string name in new[] { "TEMP", "TMP", "TMPDIR" }) start.Environment[name] = Path.Combine(packet.Directory, "temp");
        start.Environment["PATH"] = Path.GetDirectoryName(packet.Java)!;
        Stopwatch clock = Stopwatch.StartNew();
        using Process process = Process.Start(start) ?? throw new InputException(ErrorCode.ApalacheToolFailure, "Could not start Apalache.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        bool finished = process.WaitForExit(checked(packet.TimeoutSeconds * 1000));
        if (!finished) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        File.WriteAllText(Path.Combine(packet.Directory, id + ".stdout.log"), stdout.GetAwaiter().GetResult());
        File.WriteAllText(Path.Combine(packet.Directory, id + ".stderr.log"), stderr.GetAwaiter().GetResult());
        ApalacheProcessResult result = new(id, arguments, finished ? process.ExitCode : null, !finished, clock.Elapsed.TotalSeconds);
        Files.WriteNew(Path.Combine(packet.Directory, id + ".invocation.json"), new {
            SchemaVersion = Schema.Version, Kind = "apalache-invocation", Executable = packet.Java,
            WorkingDirectory = packet.Directory, packet.TimeoutSeconds, Environment = "isolated", Result = result
        });
        return result;
    }
}
