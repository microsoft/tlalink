namespace TlaLink;

public sealed class CliOptions
{
    internal static readonly string[] AnnotationCommands = [
        "inspect-source", "inspect", "check", "review", "instrument", "validate-trace",
        "inspect-integrated", "verify-integrated", "test-integrated"];
    private readonly Dictionary<string, List<string>> values = [];
    public string Command { get; }
    public CliOptions(string[] args)
    {
        if (args.Length == 0) throw new InputException("Usage: TlaLink <report|gc|expand|project-schema|doctor|plan|run|check-stage|accept|explain|inspect-source|inspect|check|review|inputs|instrument|execute|validate-trace|check-regression|read-regression|check-dafny|read-dafny|check-apalache|read-apalache|inspect-integrated|verify-integrated|test-integrated|read-integrated> [--option value ...]");
        Command = args[0];
        Dictionary<string, string[]> allowed = new()
        {
            ["report"] = ["manifest", "project", "run", "output", "format"],
            ["inspect-source"] = ["source", "reference", "define", "output", "format"],
            ["check-dafny"] = ["config", "dafny", "output", "timeout", "format"],
            ["read-dafny"] = ["result", "current", "format"],
            ["check-regression"] = ["config", "output", "timeout", "format"],
            ["read-regression"] = ["result", "current", "format"],
            ["check-apalache"] = ["config", "apalache", "jar", "output", "timeout", "format"],
            ["read-apalache"] = ["result", "current", "format"],
            ["inspect-p"] = ["config", "p", "output", "reference", "define", "timeout", "format"],
            ["check-p"] = ["config", "p", "output", "reference", "define", "timeout", "format"],
            ["read-p"] = ["result", "current", "format"],
            ["gc"] = ["store", "pins", "grace-hours", "apply", "exclusive", "output", "format"],
            ["expand"] = ["project", "output", "format"],
            ["project-schema"] = ["output", "format"],
            ["doctor"] = ["project", "scenario", "format"],
            ["plan"] = ["project", "scenario", "format"],
            ["run"] = ["project", "scenario", "output", "format", "fresh"],
            ["check-stage"] = ["project", "scenario", "stage", "fingerprint", "workers", "max-heap-mib", "timeout", "format"],
            ["accept"] = ["project", "run", "output", "format"],
            ["explain"] = ["project", "run", "output", "format"],
            ["record-review"] = ["project", "obligation", "reviewer", "rationale", "disposition", "evidence", "format"],
            ["inspect"] = ["source", "model", "reference", "define", "jar", "timeout", "format"],
            ["check"] = ["source", "model", "reference", "define", "jar", "timeout", "format", "baseline", "snapshot"],
            ["review"] = ["source", "model", "reference", "define", "jar", "timeout", "format", "step", "baseline", "snapshot"],
            ["inputs"] = ["model", "jar", "timeout", "format"],
            ["instrument"] = ["source", "model", "reference", "define", "jar", "timeout", "format", "output"],
            ["execute"] = ["manifest", "project", "trace", "output", "timeout", "format", "arg"],
            ["validate-trace"] = ["model", "manifest", "trace", "config", "execution", "jar", "output", "timeout", "format", "init", "next", "workers", "max-heap-mib"],
            ["inspect-integrated"] = ["bindings", "dafny", "jar", "reference", "define", "output", "baseline", "timeout", "format"],
            ["verify-integrated"] = ["bindings", "dafny", "jar", "reference", "define", "output", "baseline", "timeout", "format"],
            ["test-integrated"] = ["bindings", "dafny", "jar", "reference", "define", "output", "baseline", "timeout", "format"],
            ["read-integrated"] = ["result", "current", "format", "timeout"]
        };
        if (!allowed.TryGetValue(Command, out string[]? options)) throw new InputException("Unknown command: " + Command);
        if (AnnotationCommands.Contains(Command, StringComparer.Ordinal))
            options = [.. options, "annotation-version"];
        HashSet<string> repeated = ["source", "model", "reference", "define", "arg", "evidence"];
        if (Command == "report") repeated.Add("run");
        for (int i = 1; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) throw new InputException("Options require --name value.");
            string name = args[i][2..];
            if (!options.Contains(name)) throw new InputException("Unknown option: " + args[i]);
            if (values.ContainsKey(name) && !repeated.Contains(name)) throw new InputException("Repeated option: " + args[i]);
            if (!values.TryGetValue(name, out List<string>? list)) values[name] = list = [];
            list.Add(args[i + 1]);
        }
        if (Get("format", "json") is not ("json" or "text" or "summary")) throw new InputException("Format must be json, text or summary.");
        int maximum = Command == "check-stage" ? 172800 : 7200;
        if (!int.TryParse(Get("timeout", "30"), out int timeout) || timeout < 1 || timeout > maximum) throw new InputException($"Timeout must be 1..{maximum} seconds.");
    }

    public string Get(string name, string fallback) => values.TryGetValue(name, out List<string>? list) ? list.Single() : fallback;
    public string Required(string name) => values.TryGetValue(name, out List<string>? list) ? list.Single() : throw new InputException("Required option: --" + name);
    public string? Optional(string name) => values.TryGetValue(name, out List<string>? list) ? list.Single() : null;
    public List<string> Many(string name) => values.GetValueOrDefault(name, []);
    public bool Has(string name) => values.ContainsKey(name);
    public int Timeout => int.Parse(Get("timeout", "30"), System.Globalization.CultureInfo.InvariantCulture);
    public int AnnotationVersion => int.TryParse(Optional("annotation-version"), out int version) ? version : 0;
}
