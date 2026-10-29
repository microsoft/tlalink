namespace TlaLink;

public sealed class CliOptions
{
    internal static readonly string[] AnnotationCommands = [
        "links", "inspect-source", "inspect", "check", "review", "instrument", "validate-trace",
        "inspect-integrated", "verify-integrated", "test-integrated"];
    private readonly Dictionary<string, List<string>> values = [];
    public string Command { get; }
    public string? Action { get; }
    public CliOptions(string[] args)
    {
        if (args.Length == 0) throw new InputException("Usage: TlaLink <links|symbols|prepare-sources|check-prepared-sources|report|gc|expand|project-schema|doctor|plan|run|check-stage|accept|explain|inspect-source|inspect|check|review|inputs|instrument|execute|validate-trace|check-regression|read-regression|check-dafny|read-dafny|check-apalache|read-apalache|inspect-integrated|verify-integrated|test-integrated|read-integrated> [--option value ...]");
        Command = args[0];
        if (Command == "symbols")
            Action = args.Length > 1 && args[1] is "find" or "show" or "resolve" or
                "references" or "callers" or "callees" or "describe" ? args[1] :
                throw new InputException(ErrorCode.InvalidSymbolQuery, "Usage: symbols <find|show|resolve|references|callers|callees|describe> --source <file> [--option value ...].");
        if (Command == "links")
            Action = args.Length > 1 && args[1] is "list" or "check" or "inspect" or "set" or "remove" or "migrate" ? args[1] :
                throw new InputException(ErrorCode.InvalidAnnotation, "Usage: links <list|check|inspect|set|remove|migrate> --scope <file> [--target <alias>].");
        Dictionary<string, string[]> allowed = new()
        {
            ["prepare-sources"] = ["manifest", "output", "timeout", "format"],
            ["check-prepared-sources"] = ["result", "format"],
            ["report"] = ["manifest", "project", "run", "source-report", "source-inventory", "output", "format"],
            ["inspect-source"] = ["source", "reference", "define", "output", "format", "nullable", "framework-references", "regression-result"],
            ["symbols"] = ["source", "reference", "define", "output", "format", "nullable", "framework-references", "regression-result", "name", "id", "file", "line", "column"],
            ["links"] = ["scope", "target", "source", "reference", "define", "nullable", "framework-references",
                "regression-result", "jar", "dafny", "p", "binding", "endpoint", "symbol", "syntax", "tokens",
                "match", "expected-matches", "capture", "clear-capture", "reason", "output", "timeout", "format", "apply", "strip-source", "reason-policy",
                "context", "view", "include-unlinked", "calls", "source-file", "kind", "status", "capture-point",
                "search", "section", "offset", "limit"],
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
            ["record-review"] = ["project", "obligation", "reviewer", "rationale", "disposition", "evidence", "origin", "format"],
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
        if (Command is "inspect" or "check" or "review" or "instrument" or "validate-trace")
            options = [.. options, "scope", "target"];
        if (Command == "inspect-source") options = [.. options, "scope"];
        if (Command is "links" or "symbols" or "inspect-source" or "inspect" or "check" or "review" or "instrument" or
            "inspect-integrated" or "verify-integrated" or "test-integrated" or "inspect-p" or "check-p")
            options = [.. options, "language-version"];
        HashSet<string> repeated = ["source", "model", "reference", "define", "arg", "evidence", "capture"];
        if (Command == "report") repeated.Add("run");
        if (Command == "links" && Action == "inspect")
            repeated.UnionWith(["binding", "endpoint", "symbol", "syntax", "source-file", "kind", "status", "capture-point", "section"]);
        for (int i = Command is "symbols" or "links" ? 2 : 1; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length) throw new InputException("Options require --name value.");
            string name = args[i][2..];
            if (!options.Contains(name)) throw new InputException("Unknown option: " + args[i]);
            if (values.ContainsKey(name) && !repeated.Contains(name)) throw new InputException("Repeated option: " + args[i]);
            if (!values.TryGetValue(name, out List<string>? list)) values[name] = list = [];
            list.Add(args[i + 1]);
        }
        if (Get("format", "json") is not ("json" or "text" or "summary") &&
            !(Command == "links" && Action == "inspect" && Get("format", "json") == "markdown"))
            throw new InputException("Format must be json, text or summary; links inspect also accepts markdown.");
        if (Command == "links" && Action != "inspect" && new[] { "context", "view", "include-unlinked", "calls",
            "source-file", "kind", "status", "capture-point", "search", "section", "offset", "limit" }.Any(Has))
            throw new InputException(ErrorCode.InvalidAnnotation, "Inspection filters and presentation options require links inspect.");
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
