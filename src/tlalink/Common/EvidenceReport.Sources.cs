namespace TlaLink;

public static partial class EvidenceReport
{
    private static EvidenceReportManifest ExpandInventories(EvidenceReportManifest manifest, string directory,
        Dictionary<string, string> pins, Dictionary<string, string?> observed)
    {
        List<EvidenceReportRegion> regions = [.. manifest.CodeRegions];
        foreach (ReportSourceInventory selected in manifest.SourceInventories ?? [])
        {
            string path = FilePaths.Full(selected.Inventory!, directory);
            if (!string.Equals(observed[path], pins[path], StringComparison.OrdinalIgnoreCase)) continue;
            SourceInventoryResult inventory = ProjectInputs.Read<SourceInventoryResult>(path);
            Require(inventory.SchemaVersion == Schema.Version && inventory.Kind == "source-inventory" &&
                inventory.Status == "ready" && inventory.AnnotationVersion == Markup.Version &&
                inventory.Sources.Count == inventory.SourceCount && inventory.Members.Count == inventory.MemberCount &&
                inventory.Diagnostics.All(d => d.Severity != "error"),
                "Source presentation requires a ready, consistent current-format inventory.");
            Dictionary<string, string> sourcePins = StageEvidence.PathPins(inventory.Fingerprints);
            HashSet<string> sources = new(FilePaths.Comparer);
            foreach (string source in inventory.Sources)
            {
                Require(FilePaths.IsNativeAbsolute(source) && sources.Add(FilePaths.Full(source)),
                    "Inventory sources must be distinct native absolute paths.");
                Require(sourcePins.TryGetValue(source, out string? hash) && pins.TryGetValue(source, out string? expected) &&
                    string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase),
                    "Report and inventory must pin the same selected source bytes: " + source);
            }
            List<ReportSourceDisposition> rules = selected.Dispositions
                .Select(r => r with { File = FilePaths.Full(r.File, directory) }).ToList();
            Require(rules.All(r => sources.Contains(r.File)), "Disposition selects a file outside the inventory.");
            Require(inventory.Members.All(m => m != null && sources.Contains(FilePaths.Full(m.Location.Path))),
                "Inventory member lies outside its declared source scope.");
            foreach (string source in sources)
            {
                List<ReportSourceDisposition> defaults = rules.Where(r => FilePaths.Comparer.Equals(r.File, source) &&
                    r.SymbolPrefix == null).ToList();
                Require(defaults.Count == 1, "Each inventoried file needs exactly one explicit default disposition: " + source);
                ReportSourceDisposition fallback = defaults[0];
                List<SourceMember> members = inventory.Members.Where(m => FilePaths.Comparer.Equals(m.Location.Path, source)).ToList();
                foreach (ReportSourceDisposition rule in rules.Where(r => FilePaths.Comparer.Equals(r.File, source) && r.SymbolPrefix != null))
                    Require(members.Any(m => m.Symbol.StartsWith(rule.SymbolPrefix!, StringComparison.Ordinal)),
                        "Symbol-prefix disposition matched no inventoried member: " + rule.SymbolPrefix);
                foreach (SourceMember member in members)
                {
                    List<ReportSourceDisposition> overrides = rules.Where(r => FilePaths.Comparer.Equals(r.File, source) &&
                        r.SymbolPrefix != null && member.Symbol.StartsWith(r.SymbolPrefix, StringComparison.Ordinal)).ToList();
                    Require(overrides.Count <= 1, "Overlapping symbol-prefix dispositions: " + member.Symbol);
                    ReportSourceDisposition rule = overrides.SingleOrDefault() ?? fallback;
                    regions.Add(new(source, member.Location.Line, member.Location.EndLine, member.Symbol,
                        rule.Profile, rule.Disposition, rule.Contracts, rule.Reason));
                }
                if (members.Count == 0)
                    regions.Add(new(source, 1, 1, Path.GetFileName(source), fallback.Profile,
                        fallback.Disposition, fallback.Contracts, fallback.Reason));
            }
        }
        return manifest with { CodeRegions = regions };
    }

    internal static EvidenceReportManifest Rebase(EvidenceReportManifest manifest, string directory)
    {
        Require(manifest.SchemaVersion == Schema.Version && manifest.Kind == "evidence-report",
            "Source report must use the current evidence-report manifest.");
        Require(manifest.Nodes.All(n => n != null) && manifest.CodeRegions.All(r => r != null) &&
            (manifest.SourceInventories ?? []).All(s => s != null && s.Dispositions.All(r => r != null)),
            "Source reports cannot contain null presentation entries.");
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        foreach (var pin in manifest.Fingerprints)
        {
            StageEvidence.RequireHash(pin.Value);
            Require(pins.TryAdd(FilePaths.Full(pin.Key, directory), pin.Value.ToLowerInvariant()),
                "Duplicate canonical source-report input.");
        }
        return manifest with {
            Fingerprints = pins,
            Nodes = manifest.Nodes.Select(n => n with {
                Evidence = n.Evidence.Select(p => FilePaths.Full(p, directory)).ToList()
            }).ToList(),
            CodeRegions = manifest.CodeRegions.Select(r => r with { File = FilePaths.Full(r.File, directory) }).ToList(),
            SourceInventories = manifest.SourceInventories?.Select(s => s with {
                Inventory = s.Inventory == null ? null : FilePaths.Full(s.Inventory, directory),
                Dispositions = s.Dispositions.Select(r => r with { File = FilePaths.Full(r.File, directory) }).ToList()
            }).ToList()
        };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InputException(ErrorCode.InvalidReportInput, message);
    }
}
