using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

namespace TlaLink;

public static partial class ProjectWorkflow
{
    internal static IReadOnlyList<string>? CliHost { get; set; }

    public static int Run(CliOptions options)
    {
        string path = FilePaths.Full(options.Required("project"));
        if (options.Command == "expand")
        {
            ProjectExpansion expansion = ProjectComposition.Expand(path);
            if (options.Optional("output") is string destination) Files.WriteNew(FilePaths.Full(destination), expansion);
            Render(expansion, options);
            return 0;
        }
        var (project, policy) = ProjectInputs.Load(path);
        if (options.Command == "record-review") return ProjectAcceptance.RecordReview(path, project, options);
        if (options.Command is "accept" or "explain") return ProjectAcceptance.Run(path, project, policy, options);
        if (options.Command == "check-stage") return CheckStage(path, project, policy, options);
        List<AnalysisScenario> scenarios = options.Optional("scenario") is string selected
            ? [project.Scenarios.SingleOrDefault(s => s.Id == selected)
                ?? throw new InputException(ErrorCode.InvalidWorkflowInput, "Unknown scenario: " + selected)]
            : project.Scenarios;
        Dictionary<string, TlaDocument> parsed = new(FilePaths.Comparer);
        Dictionary<string, string> pins = Pins(path, project, policy, scenarios, parsed);
        List<CheckCoverage> coverage = ProjectInputs.Coverage(project, policy, scenarios.Select(s => s.Id));
        List<Issue> coverageIssues = coverage.SelectMany(o => o.Gaps.Select(gap =>
            new Issue(ErrorCode.WorkflowPrerequisiteMissing, o.Id + ": " + gap))).ToList();
        if (options.Command is "doctor" or "plan")
        {
            var report = new {
                SchemaVersion = Schema.Version, Kind = options.Command == "doctor" ? "project-preflight" : "analysis-plan",
                Project = project.Id, Status = coverageIssues.Count == 0 ? "ready" : "incomplete", Compiler = project.Compiler,
                AnnotationVersion = project.AnnotationVersion,
                CheckCoverage = coverage, Diagnostics = coverageIssues,
                Fingerprints = pins, Scenarios = scenarios.Select(s => new {
                    s.Id, Stages = ProjectInputs.Order(s).Select(stage => new {
                        stage.Id, stage.Kind, stage.DependsOn, stage.TimeoutSeconds,
                        RequestedResources = stage.Resources,
                        Resources = stage.Kind is "tlc" or "trace" ? TlcResources.Effective(stage.Kind, stage.Resources) : null,
                        Capability = stage.Kind == "oracle" ? "synchronous-int-bool-adapter; native signature resolution required" :
                            stage.Kind == "csharp-regression" ? "selected console-harness assertions; observed compiler inputs and loaded entry binary; no model conformance" :
                            stage.Kind == "dafny-verify" ? "native Dafny contracts and assumption audit; no C# correspondence or oracle execution" :
                            ApalacheInputs.IsStage(stage.Kind) ? "native Apalache " + stage.Kind + "; explicit typed safety scope; no TLC exhaustion, liveness or trace-matching claim" :
                            PInputs.IsStage(stage.Kind) ? "native P " + stage.Kind[2..] + "; explicit claim and approved configuration; no automatic C# refinement" :
                            stage.Kind == "trace" ? "guarded-scalar-entity-observations; explicit handoffs; finite trace only" :
                            stage.Kind == "tlc" ? "finite configured model; fresh JVM; no checkpoint recovery" : "native-resolution",
                            Fingerprint = stage.Kind == "tlc" ? stage.Fingerprint ?? 0 : (int?)null,
                            Stored = stage.Kind == "tlc" ? StoredOutcomes(path, project, stage, parsed) : null,
                            Requirements = policy.Requirements.Where(r => r.Scenario == s.Id && r.Stage == stage.Id)
                    })
                }),
                ReviewStatus = project.Obligations.Select(o => new {
                    o.Id, ClaimSha256 = Schema.Hash(o), Status = ProjectAcceptance.ReviewStatus(path, project, o.Id) }),
                Boundary = "Check coverage maps explicit declarations to required compatible backends, not proofs or inferred TLC properties/fairness. Preflight does not discharge reviews or establish milestone acceptance."
            };
            Render(report, options);
            return coverageIssues.Count == 0 ? 0 : 2;
        }
        if (coverageIssues.Count > 0)
            throw new InputException(ErrorCode.WorkflowPrerequisiteMissing, string.Join("; ", coverageIssues.Select(i => i.Message)));
        if (scenarios.Count != 1) throw new InputException(ErrorCode.InvalidWorkflowInput, "run requires --scenario when the project has multiple scenarios.");
        string output = FilePaths.Full(options.Required("output"));
        if (File.Exists(output) || Directory.Exists(output)) throw new InputException(ErrorCode.InvalidWorkflowInput, "Run output must be a new directory.");
        bool reuse = options.Get("fresh", "false") switch {
            "false" => true, "true" => false,
            _ => throw new InputException(ErrorCode.InvalidWorkflowInput, "--fresh must be true or false.") };
        Directory.CreateDirectory(output);
        WorkflowRun run = new() {
            Project = project.Id, ProjectDirectory = Path.GetDirectoryName(path)!, Scenario = scenarios[0].Id, Fingerprints = pins,
            ProjectSha256 = Files.Hash(path), RequirementsSha256 = Files.Hash(ProjectInputs.Resolve(path, project.Requirements))
        };
        Files.WriteNew(Path.Combine(output, "expanded-project.json"), ProjectComposition.Expansion(path, project));
        foreach (AnalysisStage stage in ProjectInputs.Order(scenarios[0]))
        {
            if (stage.DependsOn.Any(d => !run.Stages.Any(s => s.Id == d && s.Outcome == "passed")))
            {
                run.Stages.Add(new(stage.Id, stage.Kind, "blocked", "incomplete", "", "", [ErrorCode.DependencyBlocked.Id()], 0,
                    RequestedResources: stage.Resources,
                    Resources: stage.Kind is "tlc" or "trace" ? TlcResources.Effective(stage.Kind, stage.Resources) : null));
                continue;
            }
            Stopwatch clock = Stopwatch.StartNew();
            string directory = Path.Combine(output, stage.Id);
            Directory.CreateDirectory(directory);
            try
            {
                JsonElement result;
                List<StoredStageResult>? stored = null;
                TlcResources? requested = stage.Resources;
                TlcResources? effective = stage.Kind is "tlc" or "trace" ? TlcResources.Effective(stage.Kind, stage.Resources) : null;
                if (stage.Kind == "tlc")
                {
                    TlcStageRun tlc = TlcStage(path, project, stage, directory, run.Fingerprints, reuse, parsed);
                    (result, requested, effective) = (tlc.Result, tlc.Requested, tlc.Effective);
                    stored = tlc.Stored.Count == 0 ? null : tlc.Stored;
                }
                else result = Execute(path, project, stage, directory, run.Fingerprints);
                string evidence = Path.Combine(directory, "result.json");
                Files.WriteNew(evidence, result);
                string outcome = Outcome(stage.Kind, result);
                List<string> diagnostics = result.TryGetProperty("diagnostics", out JsonElement list)
                    ? list.EnumerateArray().Select(i => i.GetProperty("code").GetString()!).ToList() : [];
                run.Stages.Add(new(stage.Id, stage.Kind, outcome == "incomplete" ? "incomplete" : "completed",
                    outcome, Path.GetRelativePath(output, evidence), Files.Hash(evidence), diagnostics, clock.Elapsed.TotalSeconds,
                    stage.Kind == "tlc" && result.TryGetProperty("statistics", out JsonElement statistics)
                        ? statistics.Deserialize<TlcStatistics>(ProjectInputs.Json) : null,
                    requested, effective, stored));
            }
            catch (Exception error) when (error is InputException or IOException or UnauthorizedAccessException or
                JsonException or System.ComponentModel.Win32Exception or TimeoutException)
            {
                run.Diagnostics.Add(new(ErrorCode.WorkflowStageFailed, stage.Id + ": " + error.Message));
                run.Stages.Add(new(stage.Id, stage.Kind, "incomplete", "incomplete", "", "", [ErrorCode.WorkflowStageFailed.Id()], clock.Elapsed.TotalSeconds,
                    RequestedResources: stage.Resources,
                    Resources: stage.Kind is "tlc" or "trace" ? TlcResources.Effective(stage.Kind, stage.Resources) : null));
            }
        }
        try { IntegratedInputs.CheckPins(run.Fingerprints); }
        catch (InputException error) { run.Diagnostics.Add(new(ErrorCode.WorkflowInputChanged, error.Message)); }
        run.ExecutionStatus = run.Diagnostics.Count == 0 && run.Stages.All(s => s.ExecutionStatus == "completed") ? "completed" : "incomplete";
        Archive(output, run);
        Files.WriteNew(Path.Combine(output, "run.json"), run);
        Render(run, options);
        return run.ExecutionStatus == "completed" ? 0 : 2;
    }

    private static Dictionary<string, string> Pins(string path, AnalysisProject project, AcceptancePolicy policy, List<AnalysisScenario> scenarios,
        Dictionary<string, TlaDocument>? parsed = null)
    {
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        foreach (var pin in project.Authoring!.Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
        void Pin(string file) => Files.Pin(pins, file, Files.Hash(file));
        Pin(path);
        Pin(ProjectInputs.Resolve(path, project.Requirements));
        Program.PinTool(pins);
        bool needDafny = false, needJar = false, needP = false, needApalache = false;
        List<(ApalacheAnalysis Config, int Timeout)> apalache = [];
        foreach (string reference in project.Compiler.References) Pin(ProjectInputs.Resolve(path, reference));
        foreach (var entry in scenarios.SelectMany(s => s.Stages.Select(stage => (Scenario: s.Id, Stage: stage))))
        {
            AnalysisStage stage = entry.Stage;
            if (stage.Kind == "csharp-regression")
                _ = CSharpRegression.Load(ProjectInputs.Resolve(path, stage.Config!), pins);
            if (stage.Kind == "dafny-verify")
            {
                needDafny = true;
                _ = DafnyAnalysisBackend.Load(ProjectInputs.Resolve(path, stage.Config!), pins);
                DafnyBackend.PinDistribution(ProjectInputs.Resolve(path, project.Tools.Dafny!.Path), pins);
            }
            if (ApalacheInputs.IsStage(stage.Kind))
            {
                needApalache = true;
                needJar = true;
                apalache.Add((ApalacheInputs.Load(ProjectInputs.Resolve(path, stage.Config!), pins), stage.TimeoutSeconds));
                ApalacheInputs.PinDistribution(ProjectInputs.Resolve(path, project.Tools.Apalache!.Path), pins);
            }
            if (PInputs.IsStage(stage.Kind))
            {
                needP = true;
                _ = PInputs.Load(ProjectInputs.Resolve(path, stage.Config!), pins);
                _ = PProcess.PinCompiler(ProjectInputs.Resolve(path, project.Tools.P!.Path), pins);
            }
            if (stage.Bindings != null)
            {
                IntegratedManifest manifest = IntegratedInputs.Load(ProjectInputs.Resolve(path, stage.Bindings), pins);
                needDafny = true;
                needJar |= manifest.Models.Count > 0;
                foreach (string file in manifest.Sources.Concat(manifest.Proofs).Concat(manifest.Models)) Pin(file);
                if (manifest.Models.Count > 0 && project.Tools.Jar == null)
                    throw new InputException(ErrorCode.InvalidWorkflowInput, "A binding manifest selects TLA models without a pinned JAR.");
            }
            if (stage.Sources is { Count: > 0 })
                foreach (string file in Files.Discover(stage.Sources.Select(p => ProjectInputs.Resolve(path, p)).ToList(), ".cs")) Pin(file);
            if (stage.Models is { Count: > 0 })
            {
                needJar = true;
                foreach (string file in Files.Discover(stage.Models.Select(p => ProjectInputs.Resolve(path, p)).ToList(), ".tla")) Pin(file);
            }
            if (stage.Config != null)
            {
                string config = ProjectInputs.Resolve(path, stage.Config);
                Pin(config);
                foreach (AnalysisRequirement requirement in policy.Requirements.Where(r => r.Scenario == entry.Scenario && r.Stage == stage.Id))
                    if (!Files.Hash(config).Equals(requirement.ConfigSha256, StringComparison.OrdinalIgnoreCase))
                        throw new InputException(ErrorCode.WorkflowInputChanged, "Configuration differs from the independently approved bounds/properties: " + config);
            }
        }
        HashSet<string> requiredReviews = policy.Requirements.Where(r => scenarios.Any(s => s.Id == r.Scenario))
            .SelectMany(r => r.Reviews ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (ReviewObligation obligation in project.Obligations.Where(o => requiredReviews.Contains(o.Id)))
            foreach (string file in obligation.Inputs) Pin(ProjectInputs.Resolve(path, file));
        foreach (ToolPin tool in new[] { needDafny ? project.Tools.Dafny : null, needJar ? project.Tools.Jar : null,
            needP ? project.Tools.P : null, needApalache ? project.Tools.Apalache : null }.OfType<ToolPin>())
        {
            string file = ProjectInputs.Resolve(path, tool.Path);
            if (tool.Sha256.Length != 64 || tool.Sha256.Any(c => !char.IsAsciiHexDigit(c)) ||
                !Files.Hash(file).Equals(tool.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InputException(ErrorCode.WorkflowInputChanged, "Tool pin mismatch: " + file);
            Pin(file);
        }
        Pin(Executable("dotnet"));
        if (needJar)
            foreach (var pin in JavaToolchain.Resolve().Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
        Dictionary<string, TlaDocument> parsedModels = parsed ?? new(FilePaths.Comparer);
        TlaDocument ParsedModel(string file, int timeout)
        {
            if (!parsedModels.TryGetValue(file, out TlaDocument? model))
            {
                model = TlaBackend.Parse(file, ProjectInputs.Resolve(path, project.Tools.Jar!.Path), timeout);
                foreach (var pin in model.DependencyFingerprints) Files.Pin(pins, pin.Key, pin.Value);
                parsedModels.Add(file, model);
            }
            return model;
        }
        foreach (var analysis in apalache)
            ApalacheBackend.RequireSupportedModel(ParsedModel(analysis.Config.Model, analysis.Timeout), analysis.Config);
        foreach (AnalysisStage stage in scenarios.SelectMany(s => s.Stages).Where(s => s.Kind == "tlc"))
            RequireTlcModel(ParsedModel(ProjectInputs.Resolve(path, stage.Models![0]), stage.TimeoutSeconds));
        IntegratedInputs.CheckPins(pins);
        return pins;
    }

    internal static string Executable(string name)
    {
        if (name is "java" or "javac")
        {
            JavaInstallation java = JavaToolchain.Resolve();
            return name == "java" ? java.Java : java.Javac;
        }
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (string extension in OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd" } : new[] { "" })
            {
                string candidate = Path.Combine(folder.Trim('"'), name + extension);
                if (File.Exists(candidate)) return FilePaths.Full(candidate);
            }
        throw new InputException(ErrorCode.WorkflowPrerequisiteMissing, "Required executable not on PATH: " + name);
    }

    private static JsonElement Execute(string path, AnalysisProject project, AnalysisStage stage, string output, Dictionary<string, string> pins)
    {
        List<string> arguments = [];
        void Scope(string option, IEnumerable<string> paths)
        {
            foreach (string item in paths) arguments.AddRange([option, ProjectInputs.Resolve(path, item)]);
        }
        void Compiler()
        {
            Scope("--reference", project.Compiler.References);
            foreach (string define in project.Compiler.Defines ?? []) arguments.AddRange(["--define", define]);
        }
        void Jar() => arguments.AddRange(["--jar", ProjectInputs.Resolve(path, project.Tools.Jar!.Path)]);
        if (stage.Kind is "csharp-regression" or "dafny-verify")
        {
            arguments.Add(stage.Kind == "csharp-regression" ? "check-regression" : "check-dafny");
            arguments.AddRange(["--config", ProjectInputs.Resolve(path, stage.Config!), "--output", Path.Combine(output, "engine")]);
            if (stage.Kind == "dafny-verify")
                arguments.AddRange(["--dafny", ProjectInputs.Resolve(path, project.Tools.Dafny!.Path)]);
        }
        else if (ApalacheInputs.IsStage(stage.Kind))
        {
            arguments.Add("check-apalache");
            arguments.AddRange(["--config", ProjectInputs.Resolve(path, stage.Config!),
                "--apalache", ProjectInputs.Resolve(path, project.Tools.Apalache!.Path),
                "--output", Path.Combine(output, "engine")]);
            Jar();
        }
        else if (PInputs.IsStage(stage.Kind))
        {
            arguments.Add(stage.Kind == "p-inspect" ? "inspect-p" : "check-p");
            arguments.AddRange(["--config", ProjectInputs.Resolve(path, stage.Config!),
                "--p", ProjectInputs.Resolve(path, project.Tools.P!.Path), "--output", Path.Combine(output, "engine")]);
            Compiler();
        }
        else if (stage.Kind is "inspect" or "verify" or "oracle")
        {
            arguments.Add(stage.Kind switch { "inspect" => "inspect-integrated", "verify" => "verify-integrated", _ => "test-integrated" });
            arguments.AddRange(["--bindings", ProjectInputs.Resolve(path, stage.Bindings!), "--dafny",
                ProjectInputs.Resolve(path, project.Tools.Dafny!.Path), "--output", Path.Combine(output, "engine")]);
            if (project.Tools.Jar != null) Jar();
            Compiler();
        }
        else if (stage.Kind == "tlc") throw new InvalidOperationException("TLC stages execute through the result-store path.");
        else if (stage.Kind == "trace") return Trace(path, project, stage, output, pins);
        else
        {
            arguments.Add("inputs");
            Scope("--model", stage.Models!);
            Jar();
        }
        arguments.AddRange(["--timeout", stage.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (CliOptions.AnnotationCommands.Contains(arguments[0], StringComparer.Ordinal))
            arguments.AddRange(["--annotation-version", project.AnnotationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        JsonElement result = InvokeCli(arguments, output, stage.TimeoutSeconds * 8);
        MergePins(result, pins);
        return result;
    }

    private static JsonElement Trace(string path, AnalysisProject project, AnalysisStage stage, string output, Dictionary<string, string> pins)
    {
        string generated = Path.Combine(output, "generated");
        List<string> args = ["instrument", "--annotation-version", project.AnnotationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--output", generated, "--jar", ProjectInputs.Resolve(path, project.Tools.Jar!.Path),
            "--model", ProjectInputs.Resolve(path, stage.Models![0]), "--timeout", stage.TimeoutSeconds.ToString()];
        foreach (string source in stage.Sources!) args.AddRange(["--source", ProjectInputs.Resolve(path, source)]);
        foreach (string reference in project.Compiler.References) args.AddRange(["--reference", ProjectInputs.Resolve(path, reference)]);
        foreach (string define in project.Compiler.Defines ?? []) args.AddRange(["--define", define]);
        JsonElement instrument = InvokeCli(args, Path.Combine(output, "instrument"), stage.TimeoutSeconds * 4);
        if (!instrument.TryGetProperty("generated_files", out JsonElement sources)) return instrument;
        MergePins(instrument, pins);
        XElement properties = new("PropertyGroup", new XElement("OutputType", "Exe"), new XElement("TargetFramework", "net10.0"),
            new XElement("LangVersion", "14"), new XElement("Nullable", "enable"), new XElement("AllowUnsafeBlocks", "true"),
            new XElement("CheckForOverflowUnderflow", "false"), new XElement("EnableDefaultCompileItems", "false"));
        if (project.Compiler.Defines != null)
        {
            properties.Add(new XElement("DisableImplicitFrameworkDefines", "true"));
            properties.Add(new XElement("DefineConstants", string.Join(";", project.Compiler.Defines)));
        }
        XElement items = new("ItemGroup", sources.EnumerateObject().Where(p => Path.GetExtension(p.Name) == ".cs")
            .Select(p => new XElement("Compile", new XAttribute("Include", p.Name))));
        foreach (string reference in project.Compiler.References)
            items.Add(new XElement("Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(reference)),
                new XElement("HintPath", ProjectInputs.Resolve(path, reference))));
        string executable = Path.Combine(generated, "Capture.csproj");
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"), properties, items)).Save(executable);
        string config = Path.Combine(generated, "NuGet.Config");
        new XDocument(new XElement("configuration", new XElement("packageSources", new XElement("clear")))).Save(config);
        Invoke(Executable("dotnet"), ["restore", executable, "--configfile", config, "--nologo", "-p:NuGetAudit=false"],
            output, stage.TimeoutSeconds, "restore");
        string trace = Path.Combine(output, "trace.json");
        string execution = Path.Combine(output, "execution");
        args = ["execute", "--manifest", Path.Combine(generated, "bindings.json"), "--project", executable,
            "--trace", trace, "--output", execution, "--timeout", stage.TimeoutSeconds.ToString()];
        foreach (string arg in stage.Arguments ?? []) args.AddRange(["--arg", arg]);
        JsonElement receipt = InvokeCli(args, Path.Combine(output, "execute"), stage.TimeoutSeconds * 4);
        if (!File.Exists(Path.Combine(execution, "execution.json"))) return receipt;
        List<string> validation = ["validate-trace", "--annotation-version", project.AnnotationVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--manifest", Path.Combine(generated, "bindings.json"),
            "--trace", trace, "--execution", Path.Combine(execution, "execution.json"),
            "--model", ProjectInputs.Resolve(path, stage.Models[0]), "--config", ProjectInputs.Resolve(path, stage.Config!),
            "--jar", ProjectInputs.Resolve(path, project.Tools.Jar!.Path), "--output", Path.Combine(output, "validation"),
            "--timeout", stage.TimeoutSeconds.ToString()];
        if (stage.Resources is { } resources)
            validation.AddRange(["--workers", resources.WorkerArgument, "--max-heap-mib", resources.MaxHeapMib.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        JsonElement result = InvokeCli(validation, Path.Combine(output, "validate"), stage.TimeoutSeconds * 4);
        MergePins(result, pins);
        return result;
    }

    internal static string Outcome(string kind, JsonElement result)
    {
        string? expectedKind = kind switch {
            "csharp-regression" => "csharp-regression-result", "dafny-verify" => "dafny-result",
            "tlc" => "tlc-result", "trace" => "trace-validation", _ => null };
        if (expectedKind != null && (!result.TryGetProperty("kind", out JsonElement nativeKind) ||
            nativeKind.GetString() != expectedKind)) return "incomplete";
        if (ApalacheInputs.IsStage(kind) && (!result.TryGetProperty("kind", out JsonElement apalacheKind) ||
            apalacheKind.GetString() != "apalache-result")) return "incomplete";
        if (PInputs.IsStage(kind) && (!result.TryGetProperty("kind", out JsonElement documentKind) ||
            documentKind.GetString() != "p-result")) return "incomplete";
        string? status = result.TryGetProperty("status", out JsonElement value) ? value.GetString() : null;
        return status switch { "passed" or "ready" or "conforms" => "passed", "rejected" => "rejected", _ => "incomplete" };
    }

    private static void MergePins(JsonElement result, Dictionary<string, string> pins)
    {
        if (result.TryGetProperty("fingerprints", out JsonElement fingerprints))
            foreach (JsonProperty pin in fingerprints.EnumerateObject()) Files.Pin(pins, pin.Name, pin.Value.GetString()!);
    }

    private static JsonElement InvokeCli(List<string> arguments, string output, int timeout)
    {
        arguments.InsertRange(0, CliHost ?? [typeof(Program).Assembly.Location]);
        var execution = Invoke(Executable("dotnet"), arguments, output, timeout, "command", allowFailure: true);
        try
        {
            using JsonDocument document = JsonDocument.Parse(execution.Text, new JsonDocumentOptions { AllowDuplicateProperties = false });
            JsonElement result = document.RootElement.Clone();
            if (!result.TryGetProperty("schema_version", out JsonElement version) || version.GetInt32() != Schema.Version)
                throw new InputException(ErrorCode.InvalidCommandOutput, "Child command did not produce the current schema.");
            return result;
        }
        catch (JsonException error) { throw new InputException(ErrorCode.InvalidCommandOutput, "Invalid command output; see command.log: " + error.Message); }
    }

    private static (int ExitCode, string Text) Invoke(string executable, List<string> arguments, string output,
        int timeout, string label, bool allowFailure = false)
    {
        Directory.CreateDirectory(output);
        Files.WriteNew(Path.Combine(output, label + ".invocation.json"), new {
            SchemaVersion = Schema.Version, Kind = "process-invocation", Executable = executable,
            Arguments = arguments, WorkingDirectory = output, TimeoutSeconds = timeout
        });
        ProcessStartInfo start = new(executable) { WorkingDirectory = output, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InputException(ErrorCode.WorkflowStageFailed, "Could not start " + executable);
        // Long native searches stream their progress into the log while running.
        using StreamWriter log = new(new FileStream(Path.Combine(output, label + ".log"), FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
        System.Text.StringBuilder captured = new();
        Task stdout = Task.Run(async () =>
        {
            char[] buffer = new char[8192];
            int read;
            while ((read = await process.StandardOutput.ReadAsync(buffer)) > 0)
            {
                captured.Append(buffer, 0, read);
                log.Write(buffer, 0, read);
            }
        });
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        bool finished = process.WaitForExit(checked(timeout * 1000));
        if (!finished) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        stdout.GetAwaiter().GetResult();
        string text = captured.ToString();
        string errors = stderr.GetAwaiter().GetResult();
        log.Write("\n" + errors);
        if (!finished) throw new InputException(ErrorCode.InvalidArchivePath, "Process deadline exceeded; see " + label + ".log");
        if (process.ExitCode != 0 && !allowFailure)
            throw new InputException(ErrorCode.WorkflowStageFailed, "Process failed (" + process.ExitCode + "); see " + label + ".log");
        return (process.ExitCode, text);
    }

    private static void Archive(string output, WorkflowRun run)
    {
        string archive = Path.Combine(output, "inputs");
        Directory.CreateDirectory(archive);
        foreach (var pin in run.Fingerprints.Where(p => FilePaths.IsAbsolute(p.Key)))
        {
            string copy = Path.Combine(archive, pin.Value);
            if (!File.Exists(copy)) File.Copy(pin.Key, copy);
            if (Files.Hash(copy) != pin.Value)
                throw new InputException(ErrorCode.WorkflowInputChanged, "Input changed before archiving: " + pin.Key);
        }
        foreach (string file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(output, file);
            if (!relative.Split(Path.DirectorySeparatorChar).Contains("inputs"))
                run.Artifacts.Add(relative, Files.Hash(file));
        }
    }

    internal static void Render(object result, CliOptions options, AcceptanceWorklist? worklist = null)
    {
        if (options.Get("format", "json") == "summary") Console.WriteLine(CommandSummary.Create(result, options, worklist).ToJsonString(Files.Json));
        else if (options.Get("format", "json") == "json") Console.WriteLine(JsonSerializer.Serialize(result, Files.Json));
        else
        {
            JsonElement root = JsonSerializer.SerializeToElement(result, Files.Json);
            Console.WriteLine($"{root.GetProperty("kind").GetString()} (schema {Schema.Version})");
            foreach (string name in new[] { "project", "scenario", "status", "execution_status", "archive_status",
                "current_applicability", "acceptance_status", "path" })
                if (root.TryGetProperty(name, out JsonElement value)) Console.WriteLine($"{name}: {value}");
            void Stage(JsonElement stage)
            {
                string detail = stage.TryGetProperty("outcome", out JsonElement outcome) ? outcome.GetString()! : stage.GetProperty("kind").GetString()!;
                Console.WriteLine($"  {stage.GetProperty("id").GetString()}: {detail}");
                if (stage.TryGetProperty("depends_on", out JsonElement dependencies) && dependencies.GetArrayLength() > 0)
                    Console.WriteLine("    requires: " + string.Join(", ", dependencies.EnumerateArray().Select(d => d.GetString())));
                if (stage.TryGetProperty("evidence", out JsonElement evidence) && evidence.GetString()?.Length > 0)
                    Console.WriteLine("    evidence: " + evidence.GetString());
            }
            if (root.TryGetProperty("scenarios", out JsonElement scenarios))
                foreach (JsonElement scenario in scenarios.EnumerateArray())
                {
                    Console.WriteLine("scenario: " + scenario.GetProperty("id").GetString());
                    foreach (JsonElement stage in scenario.GetProperty("stages").EnumerateArray()) Stage(stage);
                }
            if (root.TryGetProperty("stages", out JsonElement stages))
                foreach (JsonElement stage in stages.EnumerateArray()) Stage(stage);
            if (root.TryGetProperty("requirements", out JsonElement requirements))
                foreach (JsonElement requirement in requirements.EnumerateArray())
                    Console.WriteLine($"requirement {requirement.GetProperty("id").GetString()}: {requirement.GetProperty("status").GetString()}");
            if (root.TryGetProperty("review_status", out JsonElement reviews))
                foreach (JsonElement review in reviews.EnumerateArray())
                    Console.WriteLine($"review {review.GetProperty("id").GetString()}: {review.GetProperty("status").GetString()}");
            if (root.TryGetProperty("diagnostics", out JsonElement diagnostics))
                foreach (JsonElement diagnostic in diagnostics.EnumerateArray())
                    Console.WriteLine($"{diagnostic.GetProperty("code").GetString()}: {diagnostic.GetProperty("message").GetString()}");
            if (options.Optional("output") is string output) Console.WriteLine("output: " + FilePaths.Full(output));
        }
    }

}
