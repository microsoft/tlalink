using System.Diagnostics;
using TlaLink;

namespace TlaLink.Tests;

public static class TestCli
{
    public static (int Code, string Json) Tla(string[] args, List<HiddenModelAction>? hidden = null)
    {
        string Value(string key) => args[Array.IndexOf(args, key) + 1];
        string model = FilePaths.Full(Value("--model"));
        string alias;
        List<string> bindingIds;
        if (args.Contains("--source", StringComparer.Ordinal))
        {
            List<string> sources = args.Select((arg, index) => (arg, index)).Where(e => e.arg == "--source")
                .Select(e => FilePaths.Full(args[e.index + 1])).ToList();
            List<string> Many(string key) => args.Select((arg, index) => (arg, index)).Where(e => e.arg == key)
                .Select(e => args[e.index + 1]).ToList();
            CSharpScope source = CSharpScope.Parse(sources, Many("--reference"),
                args.Contains("--define", StringComparer.Ordinal) ? Many("--define") : null);
            alias = source.Bindings.Select(b => b.Model).Distinct(StringComparer.Ordinal).Single();
            bindingIds = source.Bindings.Select(b => b.BindingId).Distinct(StringComparer.Ordinal).ToList();
        }
        else
        {
            InstrumentManifest manifest = Instrumentation.ReadManifest(Value("--manifest"));
            alias = manifest.Events.Select(e => e.Model).Distinct(StringComparer.Ordinal).Single();
            string originalScope = manifest.Fingerprints.Keys.Where(FilePaths.IsNativeAbsolute)
                .Single(p => Path.GetFileName(p).StartsWith("scope-", StringComparison.Ordinal) && Path.GetExtension(p) == ".json");
            return Run([.. args, "--scope", originalScope, "--target", alias]);
        }
        string scope = Path.Combine(Path.GetDirectoryName(model)!, "scope-" + Guid.NewGuid().ToString("N") + ".json");
        Files.WriteNew(scope, new CorrespondenceConfiguration(Schema.Version, "correspondence-scope", Markup.Version,
            [new(alias, "tla", [model], hidden, bindingIds)], []));
        return Run([.. args, "--scope", scope, "--target", alias]);
    }

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
