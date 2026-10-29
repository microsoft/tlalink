using System.Text.Json;

namespace TlaLink;

public sealed record CSharpRegressionAnalysis(int SchemaVersion, string Kind, string Project, List<string> Sources,
    CompilerProfile Compiler, List<string> Tests, List<string> Inputs, List<string> Arguments, string Configuration = "Debug",
    List<int>? PathArguments = null);
public sealed record RegressionCaseResult(string Id, string Status, string Message);
public sealed record RegressionSelection(int SchemaVersion, string Kind, List<string> Tests);
public sealed record RegressionObservations(int SchemaVersion, string Kind, bool Complete, string Binary,
    string BinarySha256, List<string> Selection, List<RegressionCaseResult> Results);
public sealed record CSharpRegressionResult(int SchemaVersion, string Kind, string Status, string Claim,
    string Config, string ConfigSha256, string Directory, int TimeoutSeconds, ObservedBuild Build,
    ObservedProcess Process, string RuntimeBinary, string Observations, List<RegressionCaseResult> Tests,
    Dictionary<string, string> Fingerprints, Dictionary<string, string> Artifacts, List<Issue> Diagnostics, string Boundary)
    : IAnalysisResult, IArtifactEvidence;

public static class CSharpRegression
{
    public static CSharpRegressionAnalysis Load(string path, Dictionary<string, string> pins)
    {
        path = FilePaths.Full(path);
        CSharpRegressionAnalysis analysis = JsonSerializer.Deserialize<CSharpRegressionAnalysis>(Files.ReadPinned(path, pins), ProjectInputs.Json)
            ?? throw new InputException(ErrorCode.InvalidWorkflowInput, "Empty C# regression config.");
        ValidateDefinition(analysis);
        CompilerProfile compiler = analysis.Compiler;
        List<string> Resolve(List<string> files) => files.Select(p => ProjectInputs.Resolve(path, p)).ToList();
        CSharpRegressionAnalysis resolved = analysis with {
            Project = ProjectInputs.Resolve(path, analysis.Project), Sources = Resolve(analysis.Sources),
            Inputs = Resolve(analysis.Inputs), Compiler = compiler with { References = Resolve(compiler.References) },
            Arguments = ResolveArguments(path, analysis)
        };
        if (Path.GetExtension(resolved.Project) != ".csproj" || resolved.Sources.Any(p => Path.GetExtension(p) != ".cs") ||
            resolved.Sources.Distinct(FilePaths.Comparer).Count() != resolved.Sources.Count)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Regression scope requires one .csproj and distinct explicit .cs files.");
        foreach (string file in resolved.Sources.Concat(resolved.Inputs).Concat(resolved.Compiler.References).Append(resolved.Project)
            .Concat((analysis.PathArguments ?? []).Select(i => resolved.Arguments[i])))
            Files.Pin(pins, file, Files.Hash(file));
        return resolved;
    }

    private static List<string> ResolveArguments(string path, CSharpRegressionAnalysis analysis) =>
        analysis.Arguments.Select((argument, index) => analysis.PathArguments?.Contains(index) == true
            ? ProjectInputs.Resolve(path, argument) : argument).ToList();

    private static void ValidateDefinition(CSharpRegressionAnalysis analysis)
    {
        if (analysis.SchemaVersion != Schema.Version || analysis.Kind != "csharp-regression" ||
            analysis.Configuration is not ("Debug" or "Release") || analysis.Sources.Count is < 1 or > 1000 || analysis.Inputs.Count > 1000 ||
            string.IsNullOrWhiteSpace(analysis.Project) ||
            analysis.Sources.Concat(analysis.Inputs).Concat(analysis.Arguments).Concat(analysis.Compiler.References).Any(string.IsNullOrWhiteSpace) ||
            analysis.Arguments.Count > 1000 || analysis.Tests.Count is < 1 or > 1000 ||
            analysis.Tests.Any(id => !Schema.IsStableId(id)) ||
            analysis.Tests.Distinct(StringComparer.Ordinal).Count() != analysis.Tests.Count)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Regression configs require explicit sources and 1..1000 unique stable test IDs.");
        if (analysis.PathArguments != null && (analysis.PathArguments.Distinct().Count() != analysis.PathArguments.Count ||
            analysis.PathArguments.Any(i => i < 0 || i >= analysis.Arguments.Count)))
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Regression path_arguments require distinct zero-based argument indices.");
        CompilerProfile compiler = analysis.Compiler;
        if (compiler.TargetFramework is not ("net10.0" or "net10.0-windows") ||
            !CSharpLanguages.Supported(compiler.LanguageVersion) || compiler.Nullable is not ("enable" or "disable") ||
            compiler.Defines == null || compiler.Defines.Any(string.IsNullOrWhiteSpace) ||
            compiler.Defines.Distinct(StringComparer.Ordinal).Count() != compiler.Defines.Count)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Regression compiler context requires .NET 10, C# 8 or 14, explicit nullable/checked/unsafe settings and exact defines (including SDK symbols).");
    }

    public static int Run(CliOptions options)
    {
        if (options.Command == "read-regression") return StageEvidence.Read<CSharpRegressionResult>(options, "csharp-regression-result", Validate);
        string config = FilePaths.Full(options.Required("config")), output = FilePaths.Full(options.Required("output"));
        if (File.Exists(output) || System.IO.Directory.Exists(output))
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Regression output must be a new directory.");
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        CSharpRegressionAnalysis analysis = Load(config, pins);
        Program.PinTool(pins);
        System.IO.Directory.CreateDirectory(output);
        ObservedBuild build = ObservedExecution.Build(analysis.Project, Path.Combine(output, "build"), options.Timeout, analysis.Configuration);
        CheckBuild(analysis, build);
        foreach (var pin in build.CompileInputs.Concat(build.References).Concat(build.BuildInputs))
            Files.Pin(pins, pin.Key, pin.Value);
        Files.Pin(pins, build.Binary, build.BinarySHA256);
        IntegratedInputs.CheckPins(pins);
        string runtime = System.IO.Directory.CreateDirectory(Path.Combine(output, "runtime")).FullName;
        Dictionary<string, string> runtimeFiles = new(FilePaths.Comparer);
        string binaryDirectory = Path.GetDirectoryName(build.Binary)!;
        foreach (string file in System.IO.Directory.EnumerateFiles(binaryDirectory, "*", new EnumerationOptions {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false
        }))
        {
            string hash = Files.Hash(file);
            Files.Pin(pins, FilePaths.Full(file), hash);
            string copy = ProjectAcceptance.SafePath(runtime, Path.GetRelativePath(binaryDirectory, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
            if (Files.Hash(copy) != hash) throw new InputException(ErrorCode.InvalidExecution, "Runtime output changed during snapshot: " + file);
            runtimeFiles.Add(copy, hash);
        }
        string binary = Path.Combine(runtime, Path.GetFileName(build.Binary));
        string selection = Path.Combine(output, "selection.json"), observations = Path.Combine(output, "observations.json");
        Files.WriteNew(selection, new RegressionSelection(Schema.Version, "regression-selection", analysis.Tests));
        ObservedProcess process = ObservedExecution.Invoke([binary, observations, selection, .. analysis.Arguments],
            output, options.Timeout, "run");
        ObservedExecution.Check(build);
        ObservedExecution.Check(runtimeFiles);
        IntegratedInputs.CheckPins(pins);
        List<Issue> diagnostics = [];
        List<RegressionCaseResult> tests = [];
        string status = "incomplete";
        if (!process.TimedOut && File.Exists(observations))
        {
            RegressionObservations observed = ProjectInputs.Read<RegressionObservations>(observations);
            try
            {
                status = Outcome(analysis, build, process, binary, observed);
                tests = observed.Results;
                diagnostics.AddRange(tests.Where(t => t.Status == "failed")
                    .Select(t => new Issue(ErrorCode.RegressionAssertionFailed, t.Id + ": " + t.Message)));
            }
            catch (InputException error) { diagnostics.Add(new(ErrorCode.InvalidExecution, error.Message)); }
        }
        else diagnostics.Add(new(ErrorCode.InvalidExecution, "Regression execution timed out or did not publish observations."));
        CSharpRegressionResult result = new(Schema.Version, "csharp-regression-result", status,
            status == "passed" ? "selected-csharp-regressions" : "not-established", config, Files.Hash(config),
            output, options.Timeout, build, process with { Log = Path.GetRelativePath(output, process.Log) },
            Path.GetRelativePath(output, binary), Path.GetRelativePath(output, observations), tests, pins,
            StageEvidence.Seal(output, pins), diagnostics,
            "Selected assertions in one console-harness execution under the recorded .NET build/runtime. The producing harness, build project and host are trusted; no test-framework integration, full native closure attestation, model conformance or general C# refinement.");
        Files.WriteNew(Path.Combine(output, "result.json"), result);
        ProjectWorkflow.Render(result, options);
        return status == "passed" ? 0 : status == "rejected" ? 1 : 2;
    }

    private static void CheckBuild(CSharpRegressionAnalysis analysis, ObservedBuild build)
    {
        CompilerProfile profile = analysis.Compiler;
        Dictionary<string, string> context = build.CompilerContext;
        string Defines(IEnumerable<string> values) => string.Join(",", values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        if (!FilePaths.Comparer.Equals(analysis.Project, build.Project) || context.GetValueOrDefault("Configuration") != analysis.Configuration ||
            context.GetValueOrDefault("TargetFramework") != profile.TargetFramework ||
            !CSharpLanguages.Matches(context.GetValueOrDefault("LangVersion"), profile.LanguageVersion) ||
            context.GetValueOrDefault("Nullable") != profile.Nullable ||
            !bool.TryParse(context.GetValueOrDefault("AllowUnsafeBlocks"), out bool unsafeEnabled) || unsafeEnabled != profile.AllowUnsafe ||
            !bool.TryParse(context.GetValueOrDefault("CheckForOverflowUnderflow"), out bool checkedEnabled) || checkedEnabled != profile.Checked ||
            Defines(context.GetValueOrDefault("Defines", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) != Defines(profile.Defines!))
            throw new InputException(ErrorCode.InvalidExecution, "Observed compiler context differs from the regression config.");
        HashSet<string> expected = analysis.Sources.ToHashSet(FilePaths.Comparer);
        foreach (string key in new[] { "GeneratedAssemblyInfoFile", "TargetFrameworkMonikerAssemblyAttributesPath", "GeneratedGlobalUsingsFile" })
            if (context.TryGetValue(key, out string? file) && file.Length > 0)
                expected.Add(FilePaths.Full(file, Path.GetDirectoryName(build.Project)!));
        if (analysis.Sources.Any(p => !build.CompileInputs.Keys.Contains(p, FilePaths.Comparer)) ||
            build.CompileInputs.Keys.Any(p => !expected.Contains(p)) ||
            profile.References.Any(p => !build.References.Keys.Contains(p, FilePaths.Comparer)))
            throw new InputException(ErrorCode.InvalidExecution, "Missing/unexpected compiler sources or missing explicit references.");
    }

    private static string Outcome(CSharpRegressionAnalysis analysis, ObservedBuild build, ObservedProcess process,
        string binary, RegressionObservations observed)
    {
        if (observed.SchemaVersion != Schema.Version || observed.Kind != "regression-observations" || !observed.Complete ||
            !FilePaths.Comparer.Equals(observed.Binary, binary) || observed.BinarySha256 != build.BinarySHA256 ||
            !observed.Selection.SequenceEqual(analysis.Tests, StringComparer.Ordinal) ||
            observed.Results.Count != analysis.Tests.Count || observed.Results.Any(t => t is null) ||
            observed.Results.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != analysis.Tests.Count ||
            !observed.Results.Select(t => t.Id).Order(StringComparer.Ordinal).SequenceEqual(analysis.Tests.Order(StringComparer.Ordinal)) ||
            observed.Results.Any(t => t.Status is not ("passed" or "failed") ||
                t.Status == "failed" && string.IsNullOrWhiteSpace(t.Message)))
            throw new InputException(ErrorCode.InvalidExecution, "Incomplete/incorrect selection, test results or loaded entry-assembly identity.");
        bool failed = observed.Results.Any(t => t.Status == "failed");
        if (process.TimedOut || process.ExitCode != (failed ? 1 : 0))
            throw new InputException(ErrorCode.InvalidExecution, "Exit code contradicts completed regression observations.");
        return failed ? "rejected" : "passed";
    }

    public static List<Issue> Validate(JsonElement document, string directory) => Validate(document, directory, false);

    public static List<Issue> Validate(JsonElement document, string directory, bool current)
    {
        Files.RequireSchema(document, "csharp-regression-result");
        CSharpRegressionResult result = document.Deserialize<CSharpRegressionResult>(ProjectInputs.Json)!;
        return Validate(result, directory, current);
    }

    public static List<Issue> Validate(CSharpRegressionResult result, string directory, bool current = false)
    {
        if (result.SchemaVersion != Schema.Version || result.Kind != "csharp-regression-result")
            throw new InputException(ErrorCode.WorkflowStageInconsistent, "Expected current csharp-regression-result.");
        result = result with { Fingerprints = StageEvidence.PathPins(result.Fingerprints) };
        List<Issue> issues = [];
        void Error(string text) => issues.Add(new(ErrorCode.WorkflowStageInconsistent, text));
        if (current)
            foreach (string absent in result.Build.MissingBuildInputs)
                if (File.Exists(absent) || System.IO.Directory.Exists(absent))
                    issues.Add(new(ErrorCode.WorkflowEvidenceStale, "An absent optional compiler configuration now exists: " + absent));
        string FileFor(string relative)
        {
            if (!result.Artifacts.ContainsKey(relative)) throw new InputException(ErrorCode.WorkflowProvenanceInconsistent, "Unpinned regression artifact: " + relative);
            return ProjectAcceptance.SafePath(directory, relative);
        }
        if (!result.Fingerprints.TryGetValue(result.Config, out string? hash) || hash != result.ConfigSha256)
        {
            Error("Regression config is not in the input closure.");
            return issues;
        }
        CSharpRegressionAnalysis raw = ProjectInputs.Read<CSharpRegressionAnalysis>(StageEvidence.Archived(directory, hash));
        ValidateDefinition(raw);
        CSharpRegressionAnalysis analysis = raw with {
            Project = ProjectInputs.Resolve(result.Config, raw.Project),
            Sources = raw.Sources.Select(p => ProjectInputs.Resolve(result.Config, p)).ToList(),
            Compiler = raw.Compiler with { References = raw.Compiler.References.Select(p => ProjectInputs.Resolve(result.Config, p)).ToList() },
            Arguments = ResolveArguments(result.Config, raw)
        };
        foreach (int index in raw.PathArguments ?? [])
            if (!result.Fingerprints.ContainsKey(analysis.Arguments[index]))
                Error("Regression path argument is absent from the archived input closure: " + analysis.Arguments[index]);
        try { CheckBuild(analysis, result.Build); }
        catch (InputException error) { Error(error.Message); }
        foreach (var pin in result.Build.CompileInputs.Concat(result.Build.References).Concat(result.Build.BuildInputs)
            .Append(new(result.Build.Binary, result.Build.BinarySHA256)))
            if (!result.Fingerprints.TryGetValue(pin.Key, out string? actual) || actual != pin.Value)
                Error("Observed build is absent from the archived input closure: " + pin.Key);
        bool Same(Dictionary<string, string> left, Dictionary<string, string> right) =>
            left.Count == right.Count && left.All(p => right.TryGetValue(p.Key, out string? value) && value == p.Value);
        if (!Same(result.Build.CompileInputs, ObservedExecution.ReadHashes(FileFor(Path.Combine("build", "compile.txt")))) ||
            !Same(result.Build.References, ObservedExecution.ReadHashes(FileFor(Path.Combine("build", "references.txt")))) ||
            !Same(result.Build.CompilerContext, ObservedExecution.ReadContext(FileFor(Path.Combine("build", "context.txt")))))
            Error("Regression build inputs differ from compiler-time observations.");
        NativeProcessInvocation buildInvocation = ProjectInputs.Read<NativeProcessInvocation>(FileFor(Path.Combine("build", "build.invocation.json")));
        List<string> buildArguments = ObservedExecution.BuildArguments(analysis.Project, Path.Combine(result.Directory, "build"), analysis.Configuration);
        Dictionary<string, string> buildInputs = ObservedExecution.ReadHashes(FileFor(Path.Combine("build", "build-inputs.txt")));
        string host = buildInvocation.Executable;
        if (!result.Fingerprints.TryGetValue(host, out string? hostHash)) Error("Missing observed .NET host.");
        else Files.Pin(buildInputs, host, hostHash);
        Dictionary<string, string> binaryObservation = ObservedExecution.ReadHashes(FileFor(Path.Combine("build", "binary.txt")));
        List<string> missing = File.ReadAllLines(FileFor(Path.Combine("build", "missing-build-inputs.txt")))
            .Select(p => FilePaths.Full(p)).Distinct(FilePaths.Comparer).ToList();
        if (!Same(buildInputs, result.Build.BuildInputs) ||
            !Same(binaryObservation, new(FilePaths.Comparer) { [result.Build.Binary] = result.Build.BinarySHA256 }) ||
            !missing.SequenceEqual(result.Build.MissingBuildInputs, FilePaths.Comparer) ||
            result.Build.Sdk != Files.Read(FileFor(Path.Combine("build", "sdk.log"))).Trim() ||
            !result.Build.BuildArguments.SequenceEqual(buildArguments) ||
            !buildInvocation.Arguments.SequenceEqual(buildArguments) ||
            buildInvocation.TimeoutSeconds != result.TimeoutSeconds ||
            buildInvocation.WorkingDirectory != Path.Combine(result.Directory, "build") ||
            result.Process.Executable != host)
            Error("Regression observed build contradicts the captured build invocation/context.");
        string originalRuntime = Path.GetDirectoryName(result.Build.Binary)! + Path.DirectorySeparatorChar;
        foreach (var pin in result.Fingerprints.Where(p => p.Key.StartsWith(originalRuntime, FilePaths.Comparison)))
        {
            string copy = Path.Combine("runtime", Path.GetRelativePath(Path.GetDirectoryName(result.Build.Binary)!, pin.Key));
            if (!result.Artifacts.TryGetValue(copy, out string? copied) || copied != pin.Value)
                Error("Regression runtime snapshot differs from the captured output: " + copy);
        }
        NativeProcessInvocation invocation = ProjectInputs.Read<NativeProcessInvocation>(FileFor("run.invocation.json"));
        string binary = FilePaths.Full(result.RuntimeBinary, result.Directory);
        string[] expected = [binary, FilePaths.Full(result.Observations, result.Directory),
            Path.Combine(result.Directory, "selection.json"), .. analysis.Arguments];
        if (result.Process.TimeoutSeconds != result.TimeoutSeconds || result.Process.WorkingDirectory != result.Directory ||
            !result.Process.Arguments.SequenceEqual(expected) ||
            invocation.TimeoutSeconds != result.TimeoutSeconds ||
            invocation.Executable != result.Process.Executable ||
            invocation.WorkingDirectory != result.Directory ||
            !invocation.Arguments.SequenceEqual(expected))
            Error("Regression execution invocation differs from its declaration.");
        if (Files.Hash(FileFor(result.RuntimeBinary)) != result.Build.BinarySHA256)
            Error("Executed runtime snapshot differs from the observed build.");
        if (result.Status is "passed" or "rejected")
        {
            RegressionObservations observed = ProjectInputs.Read<RegressionObservations>(FileFor(result.Observations));
            try
            {
                if (Outcome(analysis, result.Build, result.Process, binary, observed) != result.Status ||
                    !observed.Results.SequenceEqual(result.Tests))
                    Error("Regression outcome/test set differs from its observations.");
            }
            catch (InputException error) { Error(error.Message); }
            List<Issue> expectedDiagnostics = observed.Results.Where(t => t.Status == "failed")
                .Select(t => new Issue(ErrorCode.RegressionAssertionFailed, t.Id + ": " + t.Message)).ToList();
            if (!expectedDiagnostics.SequenceEqual(result.Diagnostics.Where(d => d.Is(ErrorCode.RegressionAssertionFailed))))
                Error("Regression assertion diagnostics differ from the observed failures.");
        }
        if (result.Claim != (result.Status == "passed" ? "selected-csharp-regressions" : "not-established"))
            Error("Regression claim contradicts its outcome.");
        return issues;
    }
}
