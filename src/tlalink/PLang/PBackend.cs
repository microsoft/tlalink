using System.Text.Json;

namespace TlaLink;

public static class PBackend
{
    public static int Run(CliOptions options)
    {
        if (options.Command == "read-p") return PEvidence.Read(options);
        string configPath = Path.GetFullPath(options.Required("config"));
        string output = Path.GetFullPath(options.Required("output"));
        if (Directory.Exists(output) || File.Exists(output))
            throw new InputException(ErrorCode.InvalidPInput, "P runs require a new output directory.");
        Dictionary<string, string> pins = new(StringComparer.OrdinalIgnoreCase);
        PAnalysis config = PInputs.Load(configPath, pins);
        Directory.CreateDirectory(output);
        PPacket packet = new() { Command = options.Command, Engine = config.Engine, Config = configPath,
            ConfigSha256 = Files.Hash(configPath), TimeoutSeconds = options.Timeout };
        foreach (var pin in pins) Files.Pin(packet.Fingerprints, pin.Key, pin.Value);
        try
        {
            string compiler = Path.GetFullPath(options.Required("p"));
            string home = PProcess.PinCompiler(compiler, packet.Fingerprints);
            Program.PinTool(packet.Fingerprints);
            string nuget = Path.Combine(AppContext.BaseDirectory, "NuGet.Config");
            Files.Pin(packet.Fingerprints, nuget, Files.Hash(nuget));
            File.Copy(nuget, Path.Combine(output, "NuGet.Config"));
            string dotnet = PProcess.Executable("dotnet");
            Files.Pin(packet.Fingerprints, dotnet, Files.Hash(dotnet));
            PProcessResult Run(string label, List<string> args)
            {
                PProcessResult result = PProcess.Run(dotnet, [compiler, .. args], output, label, options.Timeout, config.Dependencies);
                packet.Processes.Add(result);
                return result;
            }
            PProcessResult version = Run("p-version", ["--version"]);
            if (version.TimedOut || version.ExitCode != 0 ||
                !Files.Read(version.Stdout).Contains("P version 3.1.0.0", StringComparison.Ordinal))
                throw new InputException(ErrorCode.PPrerequisiteMissing, "This adapter requires P 3.1.0; see p-version logs.");
            packet.Inventory.AddRange(PFrontend.Index(home, config.Models, Path.Combine(output, "frontend"),
                options.Timeout, packet.Fingerprints, config.Engine == "pverifier" ? "pverifier" : "pchecker"));
            if (config.Engine is "pchecker" or "pex" or "psym")
            {
                List<PDeclaration> tests = packet.Inventory.Where(d => d.Kind == "testcase" &&
                    d.Name.StartsWith(config.TestCase!, StringComparison.Ordinal)).ToList();
                if (tests.Count != 1 || tests[0].Name != config.TestCase)
                    throw new InputException(ErrorCode.InvalidPInput, "Testcase must resolve exactly; native prefix matching must not select extra tests.");
            }
            Bind(config, options, packet);
            if (options.Command == "inspect-p" || config.Engine == "inspect")
            {
                packet.Status = "ready";
                packet.Claim = "structural-links";
            }
            else
            {
                PAssessment assessment = Analyze(config, output, options.Timeout, packet, Run);
                packet.Status = assessment.Status;
                packet.Claim = assessment.Claim;
                if (assessment.Diagnostic is ErrorCode diagnostic)
                    packet.Diagnostics.Add(new(diagnostic, "Native " + config.Engine + " result: " + assessment.Claim + "; see archived process logs."));
            }
            IntegratedInputs.CheckPins(packet.Fingerprints);
            packet.ProvenanceStatus = "consistent";
        }
        catch (Exception error) when (error is InputException or IOException or UnauthorizedAccessException or JsonException or
            System.ComponentModel.Win32Exception or TimeoutException or BadImageFormatException)
        {
            packet.Status = "incomplete";
            packet.Claim = "not-established";
            packet.Diagnostics.Add(new(error is InputException { Code: ErrorCode code } ? code : ErrorCode.PToolFailure, error.Message));
        }
        PEvidence.Archive(output, packet);
        Files.WriteNew(Path.Combine(output, "result.json"), packet);
        ProjectWorkflow.Render(packet, options);
        return packet.Status switch { "passed" or "ready" => 0, "rejected" => 1, _ => 2 };
    }

    private static void Bind(PAnalysis config, CliOptions options, PPacket packet)
    {
        if (config.Sources.Count > 0)
        {
            CSharpScope source = CSharpScope.Parse(config.Sources, options.Many("reference"),
                options.Has("define") ? options.Many("define") : null);
            source.RequireCompilable();
            packet.Diagnostics.AddRange(source.Diagnostics);
            foreach (var pin in source.Fingerprints) Files.Pin(packet.Fingerprints, pin.Key, pin.Value);
            var symbols = IntegratedReview.SourceSymbols(source);
            foreach (PBinding binding in config.Bindings)
            {
                var implementation = IntegratedReview.Resolve(symbols, binding.Source);
                List<PDeclaration> declarations = packet.Inventory.Where(d => d.Name == binding.Declaration).ToList();
                if (declarations.Count != 1)
                    throw new InputException(ErrorCode.InvalidPBinding, $"{binding.Id}: expected one resolved P declaration {binding.Declaration}, found {declarations.Count}.");
                packet.Links.Add(new(binding.Id, implementation.Endpoint, declarations[0], binding.Relation, binding.Assumptions));
            }
            if (packet.Diagnostics.Any(d => d.Severity == "error"))
                throw new InputException(ErrorCode.InvalidPBinding, "Invalid source scope; see diagnostics.");
        }
        packet.LinksStatus = config.Bindings.Count == 0 ? "not-specified" : "passed";
    }

    private static PAssessment Analyze(PAnalysis config, string output, int timeout, PPacket packet,
        Func<string, List<string>, PProcessResult> run)
    {
        if (config.Engine is "pex" or "psym" or "pobserve") return External(config, output, timeout, packet);
        string mode = config.Engine == "pchecker" ? "bugfinding" : "pverifier";
        string generated = Path.Combine(output, "generated");
        List<string> compile = ["compile", "--mode", mode, "--projname", config.Name,
            "--outdir", generated, "--pfiles", .. config.Models];
        if (config.Engine == "pverifier")
        {
            if (!config.Dependencies.Any(d => Path.GetFileNameWithoutExtension(d.Path).Equals("uclid", StringComparison.OrdinalIgnoreCase)))
                throw new InputException(ErrorCode.PPrerequisiteMissing, "PVerifier requires an explicitly pinned UCLID5 distribution and solver dependencies.");
            compile.AddRange(["--timeout", timeout.ToString(System.Globalization.CultureInfo.InvariantCulture), "--jobs", "1"]);
        }
        PProcessResult compiled = run("p-compile", compile);
        string compilation = Files.Read(compiled.Stdout) + "\n" + Files.Read(compiled.Stderr);
        if (config.Engine == "pverifier")
            return PResults.Classify(config.Engine, compiled.ExitCode, compiled.TimedOut, compilation, config.Schedules);
        if (compiled.TimedOut || compiled.ExitCode != 0 || !compilation.Contains("Compilation succeeded.", StringComparison.Ordinal))
            return new("incomplete", "not-established", ErrorCode.PToolFailure);
        string[] binaries = Directory.GetFiles(generated, config.Name + ".dll", SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar).Contains("obj")).ToArray();
        if (binaries.Length != 1)
            throw new InputException(ErrorCode.PToolFailure, "Expected exactly one generated backend executable, found " + binaries.Length + ".");
        return Check(config, binaries[0], output, packet, run);
    }

    private static PAssessment Check(PAnalysis config, string binary, string output, PPacket packet,
        Func<string, List<string>, PProcessResult> run)
    {
        List<string> check = ["check", binary, "--mode", "bugfinding", "--memout", "0.5",
            "--testcase", config.TestCase!, "--outdir", Path.Combine(output, "checker"),
            "--schedules", config.Schedules.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-steps", config.MaxSteps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--seed", config.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        check.Add("--fail-on-maxsteps");
        PProcessResult checkedResult = run("p-check", check);
        return PResults.Classify(config.Engine, checkedResult.ExitCode, checkedResult.TimedOut,
            PResults.NativeText(output, checkedResult, config.Engine), config.Schedules);
    }

    private static PAssessment External(PAnalysis config, string output, int timeout, PPacket packet)
    {
        ToolPin java = config.Dependencies.SingleOrDefault(d =>
            Path.GetFileNameWithoutExtension(d.Path).Equals("java", StringComparison.OrdinalIgnoreCase))
            ?? throw new InputException(ErrorCode.PPrerequisiteMissing, "An external backend requires one explicitly pinned Java executable.");
        List<string> args;
        string observations = Path.Combine(output, "observations");
        if (config.Engine is "pex" or "psym")
            args = ["-Xmx512m", "-jar", config.Artifact!.Path, "--testcase", config.TestCase!,
                "--strategy", config.Engine == "pex" ? "dfs" : "symbolic",
                "--outdir", Path.Combine(output, "checker"), "--timeout",
                timeout.ToString(System.Globalization.CultureInfo.InvariantCulture), "--memout", "0.5",
                "--schedules", config.Schedules.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--max-steps", config.MaxSteps.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--seed", config.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        else
        {
            PObservation observe = config.Observation!;
            if (observe.Logs.Count != 1)
                throw new InputException(ErrorCode.InvalidPInput, "The finite PObserve adapter requires one explicit log file per analysis.");
            args = ["-Xmx512m", "-jar", observe.RuntimeJar.Path, "--jars", observe.MonitorJar.Path,
                "--spec", observe.Specification, "--parser", observe.Parser, "--logs", observe.Logs[0],
                "--outputDir", observations, "--inputKind", observe.InputKind, "--replay",
                observe.Replay.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        }
        PProcessResult result = PProcess.Run(java.Path, args, output, "p-external", timeout, config.Dependencies);
        packet.Processes.Add(result);
        string text = Files.Read(result.Stdout) + "\n" + Files.Read(result.Stderr);
        if (config.Engine is "pex" or "psym") return PResults.Classify(config.Engine, result.ExitCode, result.TimedOut, text, config.Schedules);
        string metrics = Path.Combine(observations, "PObserveMetrics.txt");
        return PResults.Observe(result.ExitCode, result.TimedOut, text,
            File.Exists(metrics) ? Files.Read(metrics) : "",
            Directory.Exists(observations) ? Directory.GetFiles(observations, "*Error_*.txt") : []);
    }
}
