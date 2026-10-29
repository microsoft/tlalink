using System.Diagnostics;
using System.Text.Json;

namespace TlaLink;

public sealed record ObservedBuild(string Project, Dictionary<string, string> CompileInputs,
    Dictionary<string, string> References, Dictionary<string, string> BuildInputs, string Binary,
    string BinarySHA256, string Sdk, Dictionary<string, string> CompilerContext, List<string> BuildArguments,
    List<string> MissingBuildInputs);
public sealed record ObservedProcess(string Executable, List<string> Arguments, string WorkingDirectory,
    int TimeoutSeconds, int? ExitCode, bool TimedOut, string Log);

public static class ObservedExecution
{
    public static ObservedBuild Build(string project, string output, int timeout, string? configuration = null)
    {
        project = FilePaths.Full(project);
        output = FilePaths.Full(output);
        if (Directory.Exists(output) || File.Exists(output))
            throw new InputException(ErrorCode.InvalidExecution, "Observed build output must be new.");
        Directory.CreateDirectory(output);
        string target = Path.Combine(output, "Evidence.targets");
        string sources = Path.Combine(output, "compile.txt"), references = Path.Combine(output, "references.txt");
        string binaries = Path.Combine(output, "binary.txt"), context = Path.Combine(output, "context.txt");
        string inputs = Path.Combine(output, "build-inputs.txt");
        string missing = Path.Combine(output, "missing-build-inputs.txt");
        string Escape(string value) => System.Security.SecurityElement.Escape(value);
        File.WriteAllText(target, $"""
            <Project>
              <Target Name="TlaLinkCaptureInputs" BeforeTargets="CoreCompile" Condition="'$(MSBuildProjectFullPath)' == '{Escape(project)}'">
                <GetFileHash Files="@(Compile)" Algorithm="SHA256"><Output TaskParameter="Items" ItemName="_TlaSources" /></GetFileHash>
                <GetFileHash Files="@(ReferencePath)" Algorithm="SHA256"><Output TaskParameter="Items" ItemName="_TlaRefs" /></GetFileHash>
                <ItemGroup>
                  <_TlaConfigCandidates Include="@(EditorConfigFiles);@(GlobalAnalyzerConfigFiles)" />
                  <_TlaPresentConfigs Include="@(_TlaConfigCandidates)" Condition="Exists('%(_TlaConfigCandidates.Identity)')" />
                  <_TlaMissingConfigs Include="@(_TlaConfigCandidates)" Exclude="@(_TlaPresentConfigs)" />
                  <_TlaBuildInputs Include="$(MSBuildAllProjects);$(MSBuildProjectFullPath);$(ProjectAssetsFile);@(Analyzer);@(AdditionalFiles);@(_TlaPresentConfigs)" />
                  <_TlaBuildInputs Include="$(RoslynTargetsPath)\bincore\csc.dll" Condition="Exists('$(RoslynTargetsPath)\bincore\csc.dll')" />
                </ItemGroup>
                <GetFileHash Files="@(_TlaBuildInputs)" Algorithm="SHA256"><Output TaskParameter="Items" ItemName="_TlaBuildHashes" /></GetFileHash>
                <WriteLinesToFile File="{Escape(sources)}" Lines="@(_TlaSources->'%(FullPath)|%(FileHash)')" Overwrite="true" />
                <WriteLinesToFile File="{Escape(references)}" Lines="@(_TlaRefs->'%(FullPath)|%(FileHash)')" Overwrite="true" />
                <WriteLinesToFile File="{Escape(inputs)}" Lines="@(_TlaBuildHashes->'%(FullPath)|%(FileHash)')" Overwrite="true" />
                <WriteLinesToFile File="{Escape(missing)}" Lines="@(_TlaMissingConfigs->'%(FullPath)')" Overwrite="true" />
                <WriteLinesToFile File="{Escape(context)}" Lines="Configuration|$(Configuration);TargetFramework|$(TargetFramework);LangVersion|$(LangVersion);Nullable|$(Nullable);AllowUnsafeBlocks|$(AllowUnsafeBlocks);CheckForOverflowUnderflow|$(CheckForOverflowUnderflow);Defines|$([System.String]::Copy('$(DefineConstants)').Replace(';', ','));GeneratedAssemblyInfoFile|$(GeneratedAssemblyInfoFile);TargetFrameworkMonikerAssemblyAttributesPath|$(TargetFrameworkMonikerAssemblyAttributesPath);GeneratedGlobalUsingsFile|$(GeneratedGlobalUsingsFile)" Overwrite="true" />
              </Target>
              <Target Name="TlaLinkCaptureBinary" AfterTargets="Build" Condition="'$(MSBuildProjectFullPath)' == '{Escape(project)}'">
                <GetFileHash Files="$(TargetPath)" Algorithm="SHA256"><Output TaskParameter="Items" ItemName="_TlaBinary" /></GetFileHash>
                <WriteLinesToFile File="{Escape(binaries)}" Lines="@(_TlaBinary->'%(FullPath)|%(FileHash)')" Overwrite="true" />
              </Target>
            </Project>
            """);
        ObservedProcess sdkProcess = Invoke(["--version"], output, timeout, "sdk");
        RequireSuccess(sdkProcess);
        List<string> arguments = BuildArguments(project, output, configuration);
        RequireSuccess(Invoke(arguments, output, timeout, "build"));
        Dictionary<string, string> compiled = ReadHashes(sources), refs = ReadHashes(references), binary = ReadHashes(binaries);
        Dictionary<string, string> buildInputs = ReadHashes(inputs);
        string host = ProjectWorkflow.Executable("dotnet");
        Files.Pin(buildInputs, host, Files.Hash(host));
        if (binary.Count != 1)
            throw new InputException(ErrorCode.InvalidExecution, "Expected exactly one target binary; multi-target projects are not supported.");
        Dictionary<string, string> settings = ReadContext(context);
        ObservedBuild result = new(project, compiled, refs, buildInputs, binary.Single().Key, binary.Single().Value,
            Files.Read(sdkProcess.Log).Trim(), settings, arguments,
            File.ReadAllLines(missing).Select(p => FilePaths.Full(p)).Distinct(FilePaths.Comparer).ToList());
        Check(result);
        return result;
    }

    internal static List<string> BuildArguments(string project, string output, string? configuration = null)
    {
        List<string> arguments = ["build", project, "--no-restore", "--no-incremental", "--nologo",
            "-p:CustomAfterMicrosoftCommonTargets=" + Path.Combine(output, "Evidence.targets")];
        if (configuration != null) arguments.AddRange(["--configuration", configuration]);
        return arguments;
    }

    internal static Dictionary<string, string> ReadContext(string path) => File.ReadAllLines(path)
        .Select(line => line.Split('|', 2)).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

    internal static Dictionary<string, string> ReadHashes(string path)
    {
        if (!File.Exists(path)) throw new InputException(ErrorCode.InvalidExecution, "Build did not produce compiler evidence: " + path);
        Dictionary<string, string> hashes = new(FilePaths.Comparer);
        foreach (string row in File.ReadAllLines(path))
        {
            string[] fields = row.Split('|');
            if (fields.Length != 2 || fields[1].Length != 64 || fields[1].Any(c => !char.IsAsciiHexDigit(c)))
                throw new InputException(ErrorCode.InvalidExecution, "Malformed compiler evidence: " + path);
            Files.Pin(hashes, FilePaths.Full(fields[0]), fields[1].ToLowerInvariant());
        }
        if (hashes.Count == 0) throw new InputException(ErrorCode.InvalidExecution, "Empty compiler evidence: " + path);
        return hashes;
    }

    public static void Check(ObservedBuild build)
    {
        Check(build.CompileInputs);
        Check(build.References);
        Check(build.BuildInputs);
        if (build.MissingBuildInputs.Any(p => File.Exists(p) || Directory.Exists(p)))
            throw new InputException(ErrorCode.InvalidExecution, "An absent optional compiler configuration appeared during execution.");
        Check(new Dictionary<string, string>(FilePaths.Comparer) { [build.Binary] = build.BinarySHA256 });
    }

    internal static void Check(Dictionary<string, string> hashes)
    {
        foreach (var file in hashes)
            if (!File.Exists(file.Key) || Files.Hash(file.Key) != file.Value)
                throw new InputException(ErrorCode.InvalidExecution, "Build input changed: " + file.Key);
    }

    public static ObservedProcess Invoke(List<string> arguments, string output, int timeout, string label)
    {
        string executable = ProjectWorkflow.Executable("dotnet");
        ProcessStartInfo start = new(executable) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = output };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        Files.WriteNew(Path.Combine(output, label + ".invocation.json"), new NativeProcessInvocation(
            Schema.Version, "process-invocation", executable, arguments, output, timeout));
        using Process process = Process.Start(start) ?? throw new InputException(ErrorCode.InvalidExecution, "Could not start dotnet.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        bool timedOut = !process.WaitForExit(checked(timeout * 1000));
        if (timedOut) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        string log = Path.Combine(output, label + ".log");
        File.WriteAllText(log, stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult());
        return new(executable, arguments, output, timeout, timedOut ? null : process.ExitCode, timedOut, log);
    }

    public static void RequireSuccess(ObservedProcess process)
    {
        if (process.TimedOut || process.ExitCode != 0)
            throw new InputException(ErrorCode.InvalidExecution,
                $"dotnet {(process.TimedOut ? "timed out" : "failed (" + process.ExitCode + ")")}; see {process.Log}");
    }
}
