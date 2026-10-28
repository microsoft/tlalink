using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TlaLink;

public sealed record EvidenceReportManifest(int SchemaVersion, string Kind, string Title, string Scope,
    Dictionary<string, string> Fingerprints, List<EvidenceReportNode> Nodes,
    List<EvidenceReportRegion> CodeRegions, List<string> Assumptions, List<string> Gaps,
    List<ReportSourceInventory>? SourceInventories = null);
public sealed record ReportSourceInventory(string? Inventory, List<ReportSourceDisposition> Dispositions);
public sealed record ReportSourceDisposition(string File, string Profile, string Disposition,
    List<string> Contracts, string Reason, string? SymbolPrefix = null);
public sealed record EvidenceReportNode(string Id, string Kind, string Title, string Claim,
    string RecordedStatus, string? ExpectedStatus, List<string> DependsOn, List<string> Evidence,
    List<string> Assumptions, List<string> Gaps);
public sealed record EvidenceReportRegion(string File, int Line, int EndLine, string Symbol,
    string Profile, string Disposition, List<string> Contracts, string Reason);

public static partial class EvidenceReport
{
    public const string Boundary = "Presentation of supplied recorded claims and source dispositions, not independent native verification, acceptance, authentication, or general C# refinement. Matching input bytes do not validate a code region. Dependencies do not compose a theorem. Retention requires separate explicit roots.";

    public static int Run(CliOptions options)
    {
        if (options.Has("project") || options.Has("run")) return ProjectReport.Run(options);
        if (options.Has("source-report") || options.Has("source-inventory"))
            throw new InputException(ErrorCode.InvalidReportInput, "--source-report/--source-inventory accompany --project and explicit --run inputs.");
        string manifestPath = FilePaths.Full(options.Required("manifest"));
        string output = FilePaths.Full(options.Required("output"));
        if (File.Exists(output) || Directory.Exists(output))
            throw new InputException(ErrorCode.InvalidReportInput, "Report output must be new.");
        Dictionary<string, string> snapshot = new(FilePaths.Comparer);
        string text = Files.ReadPinned(manifestPath, snapshot);
        EvidenceReportManifest manifest = JsonSerializer.Deserialize<EvidenceReportManifest>(text, ProjectInputs.Json)
            ?? throw new InputException(ErrorCode.InvalidReportInput, "Empty evidence report manifest.");
        string directory = Path.GetDirectoryName(manifestPath)!;
        Dictionary<string, string> pins = Validate(manifest, directory);
        Dictionary<string, string?> observed = new(FilePaths.Comparer);
        List<Issue> issues = [];
        foreach (var pin in pins)
        {
            string? actual = CurrentHash(pin.Key);
            observed.Add(pin.Key, actual);
            if (!string.Equals(actual, pin.Value, StringComparison.OrdinalIgnoreCase))
                issues.Add(new(ErrorCode.ReportInputStale, "Changed or missing report input: " + pin.Key));
        }
        manifest = ExpandInventories(manifest, directory, pins, observed);
        Validate(manifest, directory);
        string markdown = Markdown(manifest, pins, issues, directory, manifestPath, output);
        foreach (var pin in observed)
            if (!string.Equals(CurrentHash(pin.Key), pin.Value, StringComparison.Ordinal))
                throw new InputException(ErrorCode.ReportSnapshotChanged, "Input changed during report generation: " + pin.Key);
        if (Files.Hash(manifestPath) != snapshot[manifestPath])
            throw new InputException(ErrorCode.ReportSnapshotChanged, "Report manifest changed during generation.");
        using (FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(new UTF8Encoding(false).GetBytes(markdown));
        ProjectWorkflow.Render(new {
            SchemaVersion = Schema.Version, Kind = "report-result", Status = issues.Count == 0 ? "generated" : "stale-inputs",
            CurrentApplicability = issues.Count == 0 ? "current" : "stale", Output = output,
            Nodes = manifest.Nodes.Count, CodeRegions = manifest.CodeRegions.Count, InputFiles = pins.Count,
            Diagnostics = issues, Boundary
        }, options);
        return issues.Count == 0 ? 0 : 2;
    }

    private static string? CurrentHash(string path)
    {
        try { return Files.Hash(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static Dictionary<string, string> Validate(EvidenceReportManifest manifest, string directory)
    {
        void Require(bool condition, string message)
        {
            if (!condition) throw new InputException(ErrorCode.InvalidReportInput, message);
        }
        Require(manifest.SchemaVersion == Schema.Version && manifest.Kind == "evidence-report",
            "Expected the current evidence-report schema.");
        Require(!string.IsNullOrWhiteSpace(manifest.Title) && !string.IsNullOrWhiteSpace(manifest.Scope),
            "Report title and scope are required.");
        Require(manifest.Nodes.Count is > 0 and <= 10000 && manifest.CodeRegions.Count <= 100000 &&
            manifest.Fingerprints.Count > 0, "Report needs nodes and pinned inputs.");
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        foreach (var pin in manifest.Fingerprints)
        {
            Require(!string.IsNullOrWhiteSpace(pin.Key) && pin.Value is { Length: 64 } &&
                pin.Value.All(char.IsAsciiHexDigit), "Invalid report input path/SHA256.");
            Require(pins.TryAdd(FilePaths.Full(pin.Key, directory), pin.Value), "Duplicate canonical report input.");
        }
        foreach (ReportSourceInventory inventory in manifest.SourceInventories ?? [])
        {
            Require(inventory != null && !string.IsNullOrWhiteSpace(inventory.Inventory) &&
                pins.ContainsKey(FilePaths.Full(inventory.Inventory, directory)) && inventory.Dispositions.Count > 0,
                "Source inventories need pinned input and explicit dispositions.");
            foreach (ReportSourceDisposition rule in inventory!.Dispositions)
                Require(rule != null && !string.IsNullOrWhiteSpace(rule.File) &&
                    pins.ContainsKey(FilePaths.Full(rule.File, directory)) &&
                    !string.IsNullOrWhiteSpace(rule.Profile) && !string.IsNullOrWhiteSpace(rule.Disposition) &&
                    !string.IsNullOrWhiteSpace(rule.Reason) && (rule.SymbolPrefix == null || rule.SymbolPrefix.Length > 0) &&
                    rule.Contracts.All(c => !string.IsNullOrWhiteSpace(c)), "Invalid source disposition.");
        }
        HashSet<string> ids = new(StringComparer.Ordinal);
        string[] kinds = ["code", "check", "proof", "test", "trace", "review", "requirement", "milestone", "audit"];
        foreach (EvidenceReportNode node in manifest.Nodes)
        {
            if (node is null) throw new InputException(ErrorCode.InvalidReportInput, "Null report node.");
            Require(!string.IsNullOrWhiteSpace(node.Id) && Schema.IsStableId(node.Id) && ids.Add(node.Id), "Invalid or duplicate report node ID.");
            Require(kinds.Contains(node.Kind) && !string.IsNullOrWhiteSpace(node.Title) &&
                !string.IsNullOrWhiteSpace(node.Claim) && !string.IsNullOrWhiteSpace(node.RecordedStatus) &&
                (node.ExpectedStatus is null || !string.IsNullOrWhiteSpace(node.ExpectedStatus)), "Incomplete report node.");
            Require(node.Evidence.Count > 0 && node.Evidence.All(p => !string.IsNullOrWhiteSpace(p) && pins.ContainsKey(FilePaths.Full(p, directory))),
                "Every node needs pinned evidence; an unpinned claim cannot be reported as evidence.");
            Require(node.DependsOn.Distinct(StringComparer.Ordinal).Count() == node.DependsOn.Count,
                "Duplicate report dependency.");
        }
        foreach (EvidenceReportNode node in manifest.Nodes)
            foreach (string dependency in node.DependsOn)
                Require(dependency != node.Id && ids.Contains(dependency),
                    $"Missing/self report dependency: {node.Id} -> {dependency ?? "<null>"}.");
        Dictionary<string, int> indegree = manifest.Nodes.ToDictionary(n => n.Id, n => n.DependsOn.Count, StringComparer.Ordinal);
        Dictionary<string, List<string>> dependents = ids.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (EvidenceReportNode node in manifest.Nodes)
            foreach (string dependency in node.DependsOn) dependents[dependency].Add(node.Id);
        Queue<string> ready = new(indegree.Where(p => p.Value == 0).Select(p => p.Key));
        int visited = 0;
        while (ready.TryDequeue(out string? id))
        {
            visited++;
            foreach (string next in dependents[id])
                if (--indegree[next] == 0) ready.Enqueue(next);
        }
        Require(visited == manifest.Nodes.Count, "Circular report dependencies cannot establish a proof tree.");
        foreach (EvidenceReportRegion region in manifest.CodeRegions)
        {
            if (region is null) throw new InputException(ErrorCode.InvalidReportInput, "Null code region.");
            Require(!string.IsNullOrWhiteSpace(region.File) && pins.ContainsKey(FilePaths.Full(region.File, directory)) && region.Line > 0 &&
                region.EndLine >= region.Line && !string.IsNullOrWhiteSpace(region.Symbol) &&
                !string.IsNullOrWhiteSpace(region.Profile) && !string.IsNullOrWhiteSpace(region.Disposition) &&
                !string.IsNullOrWhiteSpace(region.Reason), "Invalid or unpinned code region.");
        }
        foreach (string value in manifest.Assumptions.Concat(manifest.Gaps)
            .Concat(manifest.Nodes.SelectMany(n => n.Assumptions.Concat(n.Gaps)))
            .Concat(manifest.CodeRegions.SelectMany(r => r.Contracts)))
            Require(!string.IsNullOrWhiteSpace(value), "Report lists cannot contain empty statements.");
        return pins;
    }

    private static string Markdown(EvidenceReportManifest manifest, Dictionary<string, string> pins,
        List<Issue> issues, string directory, string manifestPath, string output)
    {
        StringBuilder doc = new();
        void Line(string text = "") => doc.AppendLine(text);
        string Reference(string path, string? label = null)
        {
            string full = FilePaths.Full(path, directory);
            Uri baseUri = new(Path.GetDirectoryName(output)! + Path.DirectorySeparatorChar);
            string target = baseUri.MakeRelativeUri(new Uri(full)).ToString();
            return $"[{Md(label ?? Path.GetFileName(full))}](<{target}>)";
        }
        bool current = issues.Count == 0;
        string Applicability() => current ? "current snapshot" : "STALE snapshot";
        string Style(EvidenceReportNode node) => !current || node.RecordedStatus == "stale" ? "stale" :
            node.ExpectedStatus is not null && node.ExpectedStatus == node.RecordedStatus && node.Gaps.Count == 0 ? "matched" :
            node.RecordedStatus is "failed" or "rejected" ? "rejected" : "open";
        Line("# " + Md(manifest.Title));
        Line();
        Line(Md(manifest.Scope));
        Line();
        Line("**Input applicability: " + Applicability() + ".** " +
            $"{manifest.Nodes.Count} evidence nodes; {manifest.CodeRegions.Count} code regions; {pins.Count} pinned files.");
        Line();
        Line("**Not a proof certificate.** " + Boundary);
        Line();
        Line("Manifest: " + Reference(manifestPath) + ". Generated with TlaLink schema " + Schema.Version + ".");
        Line();
        Line("## Reading the diagrams");
        Line();
        Line("- Arrows show declared prerequisites, not automatic cross-language refinement.");
        Line("- Green means a supplied recorded status matches its declared expectation in a byte-current snapshot; it does not mean the linked C# is proved.");
        Line("- Amber means recorded coverage, historical scope or an open obligation. Red means rejection or stale inputs.");
        Line("- Expected negative controls may match successfully while their native outcome remains **rejected**.");
        Line("- Source dispositions are coverage assignments, not per-method validation. All counts concern only the selected input scope.");
        Line();
        var groups = manifest.Nodes.GroupBy(n => n.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        Dictionary<string, string> groupIds = groups.Select((g, i) => (g.Key, Id: "g" + i))
            .ToDictionary(p => p.Key, p => p.Id, StringComparer.Ordinal);
        Dictionary<string, EvidenceReportNode> byId = manifest.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        Line("## Overview");
        Line();
        Line("```mermaid");
        Line("flowchart LR");
        foreach (var group in groups)
            Line($"  {groupIds[group.Key]}[\"{Mermaid(group.Key + ": " + group.Count())}\"]");
        foreach (var edge in manifest.Nodes.SelectMany(n => n.DependsOn.Select(d => (From: byId[d].Kind, To: n.Kind)))
            .Where(e => e.From != e.To).Distinct().OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal))
            Line($"  {groupIds[edge.From]} --> {groupIds[edge.To]}");
        Line("```");
        Line();
        Line("## Complete evidence dependency diagram");
        Line();
        Dictionary<string, string> nodeIds = manifest.Nodes.Select((n, i) => (n.Id, Diagram: "n" + i))
            .ToDictionary(p => p.Id, p => p.Diagram, StringComparer.Ordinal);
        Line("```mermaid");
        Line("flowchart TD");
        foreach (var group in groups)
        {
            Line($"  subgraph {groupIds[group.Key]}[\"{Mermaid(group.Key)}\"]");
            foreach (EvidenceReportNode node in group)
                Line($"    {nodeIds[node.Id]}[\"{Mermaid(node.Title + " | recorded: " + node.RecordedStatus)}\"]:::{Style(node)}");
            Line("  end");
        }
        foreach (EvidenceReportNode node in manifest.Nodes)
            foreach (string dependency in node.DependsOn) Line($"  {nodeIds[dependency]} --> {nodeIds[node.Id]}");
        Line("  classDef matched fill:#e8f5e9,stroke:#2e7d32,color:#111;");
        Line("  classDef open fill:#fff8e1,stroke:#946200,color:#111;");
        Line("  classDef rejected fill:#ffebee,stroke:#b71c1c,color:#111;");
        Line("  classDef stale fill:#ffebee,stroke:#b71c1c,color:#111,stroke-dasharray:5 5;");
        Line("```");
        Line();
        foreach (var group in groups)
        {
            Line("## " + Md(group.Key) + " evidence");
            Line();
            Line("| ID / piece | Recorded / expected | Claim and scope | Evidence |");
            Line("|---|---|---|---|");
            foreach (EvidenceReportNode node in group)
                Line($"| {Md(node.Id)}: {Md(node.Title)} | {Md(node.RecordedStatus)} / {Md(node.ExpectedStatus ?? "not asserted")} | {Md(node.Claim)} | {string.Join(", ", node.Evidence.Select(p => Reference(p)))} |");
            Line();
        }
        Line("## Code coverage inventory");
        Line();
        Line("The following rows retain supplied source dispositions and contract names. They do not assert that every listed branch, method, interleaving or environment has been validated.");
        Line();
        foreach (var file in manifest.CodeRegions.GroupBy(r => FilePaths.Full(r.File, directory), FilePaths.Comparer)
            .OrderBy(g => g.Key, FilePaths.Comparer))
        {
            Line("<details>");
            Line("<summary>" + WebUtility.HtmlEncode(Path.GetFileName(file.Key)) + $" ({file.Count()} regions)</summary>");
            Line();
            Line("Source: " + Reference(file.Key) + ".");
            Line();
            Line("| Symbol | Lines | Profile | Disposition / contracts | Boundary / rationale |");
            Line("|---|---|---|---|---|");
            foreach (EvidenceReportRegion region in file.OrderBy(r => r.Line).ThenBy(r => r.Symbol, StringComparer.Ordinal))
                Line($"| {Md(region.Symbol)} | {region.Line}-{region.EndLine} | {Md(region.Profile)} | {Md(region.Disposition)}; {Md(string.Join("; ", region.Contracts))} | {Md(region.Reason)} |");
            Line();
            Line("</details>");
            Line();
        }
        Line("## Assumptions and outstanding gaps");
        Line();
        foreach (string assumption in manifest.Assumptions) Line("- Assumption: " + Md(assumption));
        foreach (string gap in manifest.Gaps) Line("- **Open:** " + Md(gap));
        foreach (EvidenceReportNode node in manifest.Nodes)
        {
            foreach (string assumption in node.Assumptions) Line("- " + Md(node.Id) + " assumption: " + Md(assumption));
            foreach (string gap in node.Gaps) Line("- **Open (" + Md(node.Id) + "):** " + Md(gap));
        }
        Line();
        Line("## Input applicability and reproduction");
        Line();
        Line("All pinned files were read and checked again before publication. This checks bytes, not symlink/hard-link distinctness or native claim semantics. Missing/changed files make the whole snapshot stale; no green nodes survive that condition.");
        Line();
        if (issues.Count == 0) Line("No pinned input differences.");
        foreach (Issue issue in issues) Line("- " + Md(issue.Code + ": " + issue.Message));
        Line();
        Line("Regenerate with `TlaLink report --manifest <manifest.json> --output <new-report.md>`. Outputs are exclusive; no proof, test, approval or retention decision is created by reporting.");
        return doc.ToString();
    }

    private static string Md(string value)
    {
        StringBuilder result = new();
        foreach (char c in value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal))
        {
            if (c is '&' or '<' or '>' or '|' or '`' or '[' or ']' or '\\' or '*' or '_' or '#' or '%' or '{' or '}')
                result.Append("&#").Append(((int)c).ToString(CultureInfo.InvariantCulture)).Append(';');
            else result.Append(c);
        }
        return result.ToString();
    }

    private static string Mermaid(string value)
    {
        StringBuilder result = new();
        foreach (char c in value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal))
            if (char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or ':' or '-' or '/')
                result.Append(c);
            else result.Append('#').Append(((int)c).ToString(CultureInfo.InvariantCulture)).Append(';');
        return result.ToString();
    }
}
