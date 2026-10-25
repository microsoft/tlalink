namespace TlaLink;

public sealed class CliOptions
{
    private readonly Dictionary<string, List<string>> values = [];
    public string Command { get; }
    public CliOptions(string[] args)
    {
        if (args.Length == 0) throw new InputException("Usage: TlaLink <doctor|plan|run|accept|explain|inspect|check|review|inputs|instrument|execute|validate-trace|check-apalache|read-apalache|inspect-integrated|verify-integrated|test-integrated|read-integrated> [--option value ...]");
        Command = args[0];
        Dictionary<string, string[]> allowed = new()
        {
            ["check-apalache"] = ["config", "apalache", "jar", "output", "timeout", "format"],
            ["read-apalache"] = ["result", "current", "format"],
            ["inspect-p"] = ["config", "p", "output", "reference", "define", "timeout", "format"],
            ["check-p"] = ["config", "p", "output", "reference", "define", "timeout", "format"],
            ["read-p"] = ["result", "current", "format"],
            ["doctor"] = ["project", "scenario", "format"],
            ["plan"] = ["project", "scenario", "format"],
            ["run"] = ["project", "scenario", "output", "format"],
            ["accept"] = ["project", "run", "output", "format"],
            ["explain"] = ["project", "run", "output", "format"],
            ["record-review"] = ["project", "obligation", "reviewer", "rationale", "disposition", "evidence", "format"],
            ["inspect"] = ["source", "model", "reference", "define", "jar", "timeout", "format"],
            ["check"] = ["source", "model", "reference", "define", "jar", "timeout", "format", "baseline", "snapshot"],
            ["review"] = ["source", "model", "reference", "define", "jar", "timeout", "format", "step", "baseline", "snapshot"],
            ["inputs"] = ["model", "jar", "timeout", "format"],
            ["instrument"] = ["source", "model", "reference", "define", "jar", "timeout", "format", "output"],
            ["execute"] = ["manifest", "project", "trace", "output", "timeout", "format", "arg"],
            ["validate-trace"] = ["model", "manifest", "trace", "config", "execution", "jar", "output", "timeout", "format", "init", "next"],
            ["inspect-integrated"] = ["bindings", "dafny", "jar", "reference", "define", "output", "baseline", "timeout", "format"],
            ["verify-integrated"] = ["bindings", "dafny", "jar", "reference", "define", "output", "baseline", "timeout", "format"],
            ["test-integrated"] = ["bindings", "dafny", "jar", "reference", "define", "output", "baseline", "timeout", "format"],
            ["read-integrated"] = ["result", "current", "format", "timeout"]
        };
        if (!allowed.TryGetValue(Command, out string[]? options)) throw new InputException("Unknown command: " + Command);
        HashSet<string> repeated = ["source", "model", "reference", "define", "arg", "evidence"];
        for (int i = 1; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) throw new InputException("Options require --name value.");
            string name = args[i][2..];
            if (!options.Contains(name)) throw new InputException("Unknown option: " + args[i]);
            if (values.ContainsKey(name) && !repeated.Contains(name)) throw new InputException("Repeated option: " + args[i]);
            if (!values.TryGetValue(name, out List<string>? list)) values[name] = list = [];
            list.Add(args[i + 1]);
        }
        if (Get("format", "json") is not ("json" or "text")) throw new InputException("Format must be json or text.");
        if (!int.TryParse(Get("timeout", "30"), out int timeout) || timeout is < 1 or > 7200) throw new InputException("Timeout must be 1..7200 seconds.");
    }

    public string Get(string name, string fallback) => values.TryGetValue(name, out List<string>? list) ? list.Single() : fallback;
    public string Required(string name) => values.TryGetValue(name, out List<string>? list) ? list.Single() : throw new InputException("Required option: --" + name);
    public string? Optional(string name) => values.TryGetValue(name, out List<string>? list) ? list.Single() : null;
    public List<string> Many(string name) => values.GetValueOrDefault(name, []);
    public bool Has(string name) => values.ContainsKey(name);
    public int Timeout => int.Parse(Get("timeout", "30"), System.Globalization.CultureInfo.InvariantCulture);
}
