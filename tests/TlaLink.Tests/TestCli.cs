using System.Diagnostics;
using TlaLink;

namespace TlaLink.Tests;

public static class TestCli
{
    public static (int Code, string Json) Run(string[] args)
    {
        TextWriter previous = Console.Out;
        using StringWriter writer = new(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            Console.SetOut(writer);
            int code = TlaLink.Program.Main(args);
            return (code, writer.ToString());
        }
        finally { Console.SetOut(previous); }
    }

    public static void Restore(string project, string output, string config)
    {
        ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = output };
        foreach (string arg in new[] { "restore", project, "--configfile", config, "--nologo" }) start.ArgumentList.Add(arg);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not restore fixture.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new InvalidOperationException("Fixture restore timed out."); }
        string log = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(output, "restore.log"), log);
        if (process.ExitCode != 0) throw new InvalidOperationException(log);
    }
}
