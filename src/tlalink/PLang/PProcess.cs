using System.Diagnostics;

namespace TlaLink;

public static class PProcess
{
    public static PProcessResult Run(string executable, List<string> arguments, string output, string label,
        int timeout, IEnumerable<ToolPin> dependencies)
    {
        Directory.CreateDirectory(output);
        ProcessStartInfo start = new(executable) {
            WorkingDirectory = output, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        string[] folders = dependencies.Select(d => Path.GetDirectoryName(d.Path)!)
            .Distinct(FilePaths.Comparer).ToArray();
        if (folders.Length > 0)
            start.Environment["PATH"] = string.Join(Path.PathSeparator, folders) + Path.PathSeparator + start.Environment["PATH"];
        Files.WriteNew(Path.Combine(output, label + ".invocation.json"), new {
            SchemaVersion = Schema.Version, Kind = "process-invocation", Executable = executable,
            Arguments = arguments, WorkingDirectory = output, TimeoutSeconds = timeout, DependencyDirectories = folders
        });
        using Process process = Process.Start(start) ?? throw new InputException(ErrorCode.PToolFailure, "Could not start " + executable);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        bool finished = process.WaitForExit(checked(timeout * 1000));
        if (!finished) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        string outPath = Path.Combine(output, label + ".stdout.log"), errPath = Path.Combine(output, label + ".stderr.log");
        File.WriteAllText(outPath, stdout.GetAwaiter().GetResult());
        File.WriteAllText(errPath, stderr.GetAwaiter().GetResult());
        return new(label, executable, arguments, output, outPath, errPath, finished ? process.ExitCode : null, !finished);
    }

    public static string Executable(string name)
    {
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(folder.Trim('"'), name + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (File.Exists(candidate)) return FilePaths.Full(candidate);
        }
        throw new InputException(ErrorCode.PPrerequisiteMissing, "Required executable is missing: " + name);
    }

    public static string PinCompiler(string compiler, Dictionary<string, string> pins)
    {
        compiler = FilePaths.Full(compiler);
        string home = Path.GetDirectoryName(compiler)!;
        if (Path.GetFileName(compiler) != "p.dll" || !File.Exists(Path.Combine(home, "CompilerCore.dll")) ||
            !File.Exists(Path.Combine(home, "p.runtimeconfig.json")))
            throw new InputException(ErrorCode.PPrerequisiteMissing, "--p must select p.dll in a complete trusted P 3.1.0 distribution.");
        foreach (string file in Directory.EnumerateFiles(home, "*", new EnumerationOptions {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false
        })) Files.Pin(pins, FilePaths.Full(file), Files.Hash(file));
        return home;
    }
}
