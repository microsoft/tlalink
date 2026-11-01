namespace TlaLink.Tests;

public static class PTranslationTests
{
    private static readonly string[] ConfigVariables =
        ["TLALINK_P_VERIFIER_CONFIG", "TLALINK_P_OBSERVE_CONFIG", "TLALINK_P_EX_CONFIG"];

    public static IEnumerable<string> ImmutableAssets()
    {
        string? generator = Environment.GetEnvironmentVariable("TLALINK_P_TRANSLATION_COMPILER");
        if (!string.IsNullOrWhiteSpace(generator))
        {
            string path = FilePaths.Full(generator);
            if (!File.Exists(path)) throw new FileNotFoundException("Selected P translation compiler is missing.", path);
            foreach (string file in Distribution(Path.GetDirectoryName(path)!)) yield return file;
        }
        foreach (string variable in ConfigVariables)
        {
            string? path = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(path)) continue;
            PAnalysis config = PInputs.Load(FilePaths.Full(path), new(FilePaths.Comparer));
            foreach (ToolPin dependency in config.Dependencies) yield return dependency.Path;
            if (config.Artifact is not null) yield return config.Artifact.Path;
            if (config.Observation is not null)
            {
                yield return config.Observation.RuntimeJar.Path;
                yield return config.Observation.MonitorJar.Path;
            }
        }
    }

    public static void Register(Action<string, Action> test, string compiler, string work)
    {
        string directory = Directory.CreateDirectory(Path.Combine(work, "translation-controls")).FullName;
        foreach (string source in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PTranslation")))
            File.Copy(source, Path.Combine(directory, Path.GetFileName(source)));
        string Fresh(string name) => Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N"));
        string Source(string name) => Path.Combine(directory, name);
        PPacket Check(PAnalysis config, string name, int expected)
        {
            string configPath = Fresh(name) + ".json";
            Files.WriteNew(configPath, config);
            string output = Fresh(name);
            var response = TestCli.Run(["check-p", "--config", configPath, "--p", compiler,
                "--output", output, "--timeout", "120"]);
            if (response.Code != expected) throw new InvalidOperationException(response.Json);
            PPacket packet = ProjectInputs.Read<PPacket>(Path.Combine(output, "result.json"));
            CoreTests.True(packet.Processes.Count > 0 && packet.Processes.All(p => !p.TimedOut && p.ExitCode is not null));
            CoreTests.Equal(0, TestCli.Run(["read-p", "--result", Path.Combine(output, "result.json")]).Code);
            return packet;
        }
        PAnalysis Inline(string model) => new(Schema.Version, "p-analysis", "TranslationState", "pchecker",
            [Source(model), Source("InlineStateCases.p")], [], [], [], "tcInlineState", 10, 100, 7);
        test("P translation inline send retains field state in the subsequent event", () =>
        {
            PPacket packet = Check(Inline("InlineState.p"), "inline-healthy", 0);
            CoreTests.Equal("passed", packet.Status);
            CoreTests.Equal("explored-schedules", packet.Claim);
            CoreTests.True(packet.Processes.Any(p => p.Label == "p-check" && p.ExitCode == 0));
        });
        test("P translation named value-corruption control reaches a native assertion", () =>
        {
            PPacket packet = Check(Inline("ValueCorruption.p"), "inline-corrupt", 1);
            CoreTests.Equal("rejected", packet.Status);
            CoreTests.Equal("model-counterexample", packet.Claim);
            CoreTests.True(packet.Diagnostics.Any(d => d.Is(ErrorCode.PAssertionFailed)));
            PProcessResult process = packet.Processes.Single(p => p.Label == "p-check");
            CoreTests.True(PResults.NativeText(Path.GetDirectoryName(process.Stdout)!, process, "pchecker")
                .Contains("inline-event-state-persistence", StringComparison.Ordinal));
        });
        test("PVerifier helper guard rejects transitive field and send effects before proof", () =>
        {
            string home = Path.GetDirectoryName(compiler)!;
            var indexed = PFrontend.Index(home, [Source("HelperSend.p")], Fresh("helper-index"), 60, new(FilePaths.Comparer));
            CoreTests.True(indexed.Any(d => d.Name == "HelperSend.Apply") && indexed.Any(d => d.Name == "HelperSend.Publish"));
            string output = Fresh("helper-guard");
            CoreTests.ThrowsAny<InputException>(() => PFrontend.Index(home, [Source("HelperSend.p")],
                output, 60, new(FilePaths.Comparer), "pverifier"));
            CoreTests.True(Files.Read(Path.Combine(output, "p-index.stderr.log")).Contains("overwrite helper effects", StringComparison.Ordinal));
            CoreTests.True(!File.Exists(Path.Combine(output, "p-index.json")));
        });

        PAnalysis? Optional(string variable, string engine)
        {
            string? path = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(path))
            {
                Console.WriteLine($"SKIP P translation {engine}: select {variable} with an existing pinned p-analysis configuration.");
                return null;
            }
            PAnalysis config = PInputs.Load(FilePaths.Full(path), new(FilePaths.Comparer));
            if (config.Engine != engine) throw new InvalidOperationException(variable + " must select engine " + engine + ".");
            return config;
        }
        PAnalysis? verifier = Optional(ConfigVariables[0], "pverifier");
        if (verifier is not null)
        {
            foreach ((string model, int exit, string status, string claim) in new[] {
                ("InlineVerifier.p", 0, "passed", "inductive-invariants"),
                ("VerifierValueCorruption.p", 1, "rejected", "inductive-counterexample") })
                test("PVerifier native inline translation " + model, () =>
                {
                    PPacket packet = Check(verifier with { Name = "TranslationVerifier", Models = [Source(model)],
                        Sources = [], Bindings = [], Scope = null }, model, exit);
                    CoreTests.Equal(status, packet.Status);
                    CoreTests.Equal(claim, packet.Claim);
                    CoreTests.True(packet.Processes.Any(p => p.Label == "p-compile"));
                    if (status == "rejected")
                        CoreTests.True(packet.Diagnostics.Any(d => d.Is(ErrorCode.PInvariantFailed)));
                });
        }

        string? generator = Environment.GetEnvironmentVariable("TLALINK_P_TRANSLATION_COMPILER");
        foreach ((string variable, string engine) in new[] { (ConfigVariables[1], "pobserve"), (ConfigVariables[2], "pex") })
        {
            PAnalysis? config = Optional(variable, engine);
            if (config is null) continue;
            if (string.IsNullOrWhiteSpace(generator))
            {
                Console.WriteLine($"SKIP P translation {engine}: select TLALINK_P_TRANSLATION_COMPILER; unpinned upstream builds are not invoked.");
                continue;
            }
            string nativeCompiler = FilePaths.Full(generator);
            ToolPin java = Executable(config, "java"), javac = Executable(config, "javac"), jar = Executable(config, "jar");
            string[] jars = config.Dependencies.Select(p => p.Path).Where(p => Path.GetExtension(p) == ".jar")
                .Concat(config.Observation is null ? [] : [config.Observation.RuntimeJar.Path])
                .Concat(config.Artifact is null ? [] : [config.Artifact.Path]).Distinct(FilePaths.Comparer).ToArray();
            string classpath = string.Join(Path.PathSeparator, jars);
            string generated = Fresh(engine + "-generated");
            string classes = Fresh(engine + "-classes");
            string? artifact = null;
            test("P native " + engine + " generated Java compiles against selected runtime", () =>
            {
                PProcessResult version = PProcess.Run(PProcess.Executable("dotnet"), [nativeCompiler, "--version"],
                    Fresh(engine + "-version-log"), "version", 30, config.Dependencies);
                Completed(version, 0);
                CoreTests.True(Text(version).Contains("P version 3.1.0.0", StringComparison.Ordinal));
                PProcessResult generatedProcess = PProcess.Run(PProcess.Executable("dotnet"),
                    [nativeCompiler, "compile", "--mode", engine, "--projname", "TranslationControl",
                        .. (engine == "pobserve" ? new[] { "--pobserve-package", "translation.controls.generated" } : []),
                        "--outdir", generated, "--pfiles", Source(engine == "pobserve" ? "TranslationMonitor.p" : "InlineState.p"),
                        .. (engine == "pex" ? new[] { Source("InlineStateCases.p") } : [])],
                    Fresh(engine + "-generate-log"), "generate", 120, config.Dependencies);
                Completed(generatedProcess, 0);
                CoreTests.True(Text(generatedProcess).Contains("Compilation succeeded.", StringComparison.Ordinal));
                string[] sources = Directory.GetFiles(generated, "*.java", SearchOption.AllDirectories);
                CoreTests.True(sources.Length > 0);
                if (engine == "pobserve") sources = [.. sources, Source("TranslationParser.java")];
                Directory.CreateDirectory(classes);
                PProcessResult compiled = PProcess.Run(javac.Path,
                    ["-XDrawDiagnostics", "-proc:none", "-classpath", classpath, "-d", classes, .. sources],
                    Fresh(engine + "-javac-log"), "javac", 60, config.Dependencies);
                Completed(compiled, 0);
                if (Text(compiled).Contains("compiler.err.", StringComparison.Ordinal))
                    throw new InvalidOperationException("Native Java compilation returned error diagnostics:\n" + Text(compiled));
                if (engine == "pobserve")
                {
                    string monitorClass = Path.Combine(classes, "translation", "controls", "generated",
                        "PMachines$TranslationMonitor.class");
                    if (!File.Exists(monitorClass))
                        throw new InvalidOperationException("Expected native monitor class " + monitorClass +
                            "\nGenerated classes:\n" + string.Join("\n", Directory.GetFiles(classes, "*.class", SearchOption.AllDirectories)) +
                            "\nNative javac stdout/stderr:\n" + Text(compiled) +
                            "\nNative generator stdout/stderr:\n" + Text(generatedProcess));
                    artifact = Fresh("translation-monitor") + ".jar";
                    PProcessResult packed = PProcess.Run(jar.Path, ["--create", "--file", artifact, "-C", classes, "."],
                        Fresh("monitor-jar-log"), "jar", 60, config.Dependencies);
                    Completed(packed, 0);
                }
            });
            test("P native " + engine + " named generator-defect control is a completed Java type failure", () =>
            {
                string source = engine == "pobserve" ? "MonitorTypeMismatch.java" : "UnknownUtilityImport.java";
                string output = Fresh(engine + "-bad-classes");
                Directory.CreateDirectory(output);
                PProcessResult failed = PProcess.Run(javac.Path,
                    ["-XDrawDiagnostics", "-proc:none", "-classpath", classpath, "-d", output, Source(source)],
                    Fresh(engine + "-bad-log"), "javac", 60, config.Dependencies);
                Completed(failed, 1);
                string diagnostic = Text(failed);
                CoreTests.True(diagnostic.Contains(source, StringComparison.Ordinal));
                CoreTests.True(diagnostic.Contains(engine == "pobserve"
                    ? "compiler.err.cant.resolve.location" : "compiler.err.doesnt.exist: pex.utils", StringComparison.Ordinal));
                CoreTests.Equal(engine == "pobserve" ? 2 : 1,
                    diagnostic.Split("compiler.err.", StringSplitOptions.None).Length - 1);
                if (engine == "pobserve") CoreTests.True(diagnostic.Contains("TranslationMonitor", StringComparison.Ordinal));
                CoreTests.True(!Directory.GetFiles(output, "*.class", SearchOption.AllDirectories).Any());
            });
            if (engine != "pobserve") continue;
            foreach ((string log, int exit, string status, string claim) in new[] {
                ("healthy.log", 0, "passed", "observed-properties"),
                ("wrong-state.log", 1, "rejected", "observed-property-violation"),
                ("parser-error.log", 2, "incomplete", "not-established") })
                test("PObserve native generated monitor " + log, () =>
                {
                    if (artifact is null) throw new InvalidOperationException("Native generated-monitor compilation did not complete.");
                    PObservation observation = config.Observation! with {
                        MonitorJar = new(artifact, Files.Hash(artifact)), Specification = "TranslationMonitor",
                        Parser = "TranslationParser", Logs = [Source(log)], InputKind = "text", Replay = 10 };
                    PPacket packet = Check(config with { Name = "TranslationObserve", Models = [Source("TranslationMonitor.p")],
                        Sources = [], Bindings = [], Scope = null, Observation = observation }, log, exit);
                    CoreTests.Equal(status, packet.Status);
                    CoreTests.Equal(claim, packet.Claim);
                    PProcessResult process = packet.Processes.Single(p => p.Label == "p-external");
                    CoreTests.Equal(java.Path, process.Executable);
                    CoreTests.Equal<int?>(log == "healthy.log" ? 0 : log == "wrong-state.log" ? 2 : 3, process.ExitCode);
                    CoreTests.True(process.Arguments.SequenceEqual(new[] {
                        "-Xmx512m", "-jar", observation.RuntimeJar.Path, "--jars", artifact,
                        "--spec", "TranslationMonitor", "--parser", "TranslationParser", "--logs", Source(log),
                        "--outputDir", Path.Combine(process.WorkingDirectory, "observations"),
                        "--inputkind", "TEXT", "--replaySize", "10" }));
                    if (status == "rejected")
                        CoreTests.True(packet.Diagnostics.Any(d => d.Is(ErrorCode.PMonitorFailed)));
                });
        }
    }

    private static IEnumerable<string> Distribution(string directory) => Directory.EnumerateFiles(directory, "*",
        new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false });

    private static ToolPin Executable(PAnalysis config, string name)
    {
        string filename = FilePaths.ExecutableName(name);
        ToolPin[] selected = config.Dependencies.Where(p => FilePaths.Comparer.Equals(Path.GetFileName(p.Path), filename)).ToArray();
        if (selected.Length != 1) throw new InvalidOperationException("Translation controls require exactly one pinned " + filename + ".");
        return selected[0];
    }

    private static string Text(PProcessResult process) => Files.Read(process.Stdout) + "\n" + Files.Read(process.Stderr);

    private static void Completed(PProcessResult process, int exit)
    {
        if (process.TimedOut || process.ExitCode != exit)
            throw new InvalidOperationException($"Expected native completed exit {exit}; exit={process.ExitCode}, timeout={process.TimedOut}.\n{Text(process)}");
    }
}
