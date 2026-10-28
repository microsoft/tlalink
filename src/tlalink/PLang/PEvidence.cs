using System.Text.Json;

namespace TlaLink;

public static class PEvidence
{
    public static void Archive(string output, PPacket packet)
    {
        string archive = Path.Combine(output, "inputs");
        Directory.CreateDirectory(archive);
        foreach (var pin in packet.Fingerprints.Where(p => FilePaths.IsAbsolute(p.Key)))
        {
            string copy = Path.Combine(archive, pin.Value);
            if (!File.Exists(copy)) File.Copy(pin.Key, copy);
            if (Files.Hash(copy) != pin.Value)
            {
                packet.Status = "incomplete";
                packet.ProvenanceStatus = "incomplete";
                packet.Diagnostics.Add(new(ErrorCode.PInputChanged, "Input changed before archiving: " + pin.Key));
            }
        }
        foreach (string file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(output, file);
            if (!relative.Split(Path.DirectorySeparatorChar).Contains("inputs"))
                packet.Artifacts.Add(relative, Files.Hash(file));
        }
    }

    public static int Read(CliOptions options)
    {
        string resultPath = FilePaths.Full(options.Required("result"));
        using JsonDocument document = JsonDocument.Parse(Files.Read(resultPath));
        List<Issue> issues = Validate(document.RootElement, Path.GetDirectoryName(resultPath)!, options.Get("current", "false") switch {
            "true" => true, "false" => false,
            _ => throw new InputException(ErrorCode.InvalidPInput, "--current must be true or false.")
        });
        bool consistent = issues.All(i => i.Severity != "error");
        bool stale = issues.Any(i => i.Is(ErrorCode.PEvidenceStale));
        ProjectWorkflow.Render(new {
            SchemaVersion = Schema.Version, Kind = "p-evidence",
            ArchiveStatus = consistent ? "consistent" : "inconsistent",
            CurrentApplicability = options.Get("current", "false") == "false" ? "not-checked" : stale ? "stale" : "current",
            Status = !consistent ? "rejected" : stale ? "incomplete" : "passed", Diagnostics = issues
        }, options);
        return !consistent ? 1 : stale ? 2 : 0;
    }

    public static List<Issue> Validate(JsonElement root, string output, bool current = false)
    {
        List<Issue> issues = [];
        void Error(string message) => issues.Add(new(ErrorCode.InvalidPEvidence, message));
        try
        {
            Files.RequireSchema(root, "p-result");
            PPacket packet = JsonSerializer.Deserialize<PPacket>(root.GetRawText(), ProjectInputs.Json)
                ?? throw new InputException(ErrorCode.InvalidPEvidence, "Missing P packet.");
            if (!PInputs.Engines.Contains(packet.Engine, StringComparer.Ordinal) ||
                packet.Command is not ("inspect-p" or "check-p") || packet.Fingerprints.Count == 0 || packet.Artifacts.Count == 0)
                Error("Missing engine/command/provenance.");
            foreach (var pin in packet.Fingerprints.Where(p => FilePaths.IsAbsolute(p.Key)))
            {
                string archived = Safe(output, Path.Combine("inputs", pin.Value));
                if (!File.Exists(archived) || Files.Hash(archived) != pin.Value)
                    Error("Missing/changed archived input: " + pin.Key);
                if (current && (!FilePaths.IsNativeAbsolute(pin.Key) || !File.Exists(pin.Key) || Files.Hash(pin.Key) != pin.Value))
                    issues.Add(new(ErrorCode.PEvidenceStale, "Changed current input: " + pin.Key));
            }
            foreach (var artifact in packet.Artifacts)
            {
                string file = Safe(output, artifact.Key);
                if (!File.Exists(file) || Files.Hash(file) != artifact.Value) Error("Missing/changed artifact: " + artifact.Key);
            }
            if (!packet.Fingerprints.TryGetValue(packet.Config, out string? configHash) || configHash != packet.ConfigSha256)
                Error("Configuration identity disagrees with its input pins.");
            string configCopy = Safe(output, Path.Combine("inputs", packet.ConfigSha256));
            PAnalysis config = PInputs.Parse(Files.Read(configCopy));
            if (config.Engine != packet.Engine) Error("Analysis engine differs from archived configuration.");
            if (packet.Status is not ("passed" or "ready" or "rejected" or "incomplete")) Error("Unknown P outcome.");
            if (packet.Status != "incomplete")
            {
                if (packet.ProvenanceStatus != "consistent" || packet.Diagnostics.Any(i => i.Severity == "error") && packet.Status != "rejected")
                    Error("Completed P evidence has inconsistent provenance or error diagnostics.");
                if (packet.Inventory.Count == 0) Error("Completed evidence lacks native inventory.");
                using JsonDocument index = JsonDocument.Parse(Files.Read(Safe(output, Path.Combine("frontend", "p-index.json"))));
                Files.RequireSchema(index.RootElement, "p-source-index");
                List<PDeclaration> inventory = JsonSerializer.Deserialize<List<PDeclaration>>(
                    index.RootElement.GetProperty("declarations").GetRawText(), ProjectInputs.Json)!;
                if (!packet.Inventory.SequenceEqual(inventory) || packet.Inventory.Any(d => !packet.Fingerprints.ContainsKey(d.Path)) ||
                    index.RootElement.GetProperty("profile").GetString() != (packet.Engine == "pverifier" ? "pverifier" : "pchecker"))
                    Error("P inventory differs from the authoritative frontend result or source pins.");
                if (packet.Links.Select(l => l.Id).Distinct(StringComparer.Ordinal).Count() != packet.Links.Count ||
                    !packet.Links.Select(l => l.Id).Order().SequenceEqual(config.Bindings.Select(b => b.Id).Order()))
                    Error("Source binding coverage differs from configuration.");
                string Recorded(string file)
                {
                    if (!packet.Fingerprints.TryGetValue(file, out string? hash))
                        throw new InputException(ErrorCode.InvalidPEvidence, "Missing archived correspondence pin: " + file);
                    string copy = Safe(output, Path.Combine("inputs", hash));
                    if (!File.Exists(copy) || Files.Hash(copy) != hash)
                        throw new InputException(ErrorCode.InvalidPEvidence, "Changed archived correspondence input: " + file);
                    return Files.Read(copy);
                }
                Dictionary<string, ArchivedSourceBinding> inline = config.Bindings.Count == 0 ? [] :
                    CorrespondenceEvidence.Bindings(config.Sources.Select(p => ProjectInputs.Resolve(packet.Config, p)),
                        packet.Fingerprints, Recorded);
                CorrespondenceConfiguration? scope = config.Bindings.Count == 0 ? null :
                    CorrespondenceEvidence.Scope(ProjectInputs.Resolve(packet.Config, config.Scope ??
                        throw new InputException(ErrorCode.InvalidPEvidence, "Missing correspondence scope.")), Recorded);
                List<FormalLinkDeclaration> formal = config.Bindings.Count == 0 ? [] :
                    FormalCorrespondence.P(packet.Inventory, packet.Fingerprints, Recorded);
                foreach (PSourceLink link in packet.Links)
                {
                    if (!packet.Inventory.Contains(link.Specification) ||
                        !packet.Fingerprints.ContainsKey(link.Implementation.Location.Path))
                        Error("Link endpoints are not in the pinned source/native inventory.");
                    PBinding binding = config.Bindings.Single(b => b.Id == link.Id);
                    if (!inline.TryGetValue(link.Id, out ArchivedSourceBinding? declaration) ||
                        formal.Count(f => f.Binding.Id == link.Id && f.Endpoint.Name == link.Specification.Name) != 1 ||
                        declaration.Location != link.Implementation.Location ||
                        !scope!.Targets.Any(t => t.Id == Markup.Name(link.Id).Alias && t.Language == "p") ||
                        link.Relation != binding.Relation ||
                        !link.Assumptions.SequenceEqual(binding.Assumptions))
                        Error("P source relation differs from the archived binding.");
                }
                if (packet.Processes.Select(p => p.Label).Distinct().Count() != packet.Processes.Count)
                    Error("Duplicate native process identities.");
                foreach (PProcessResult process in packet.Processes)
                {
                    using JsonDocument invocation = JsonDocument.Parse(Files.Read(Safe(output, process.Label + ".invocation.json")));
                    JsonElement native = invocation.RootElement;
                    Files.RequireSchema(native, "process-invocation");
                    if (native.GetProperty("executable").GetString() != process.Executable ||
                        native.GetProperty("working_directory").GetString() != process.WorkingDirectory ||
                        native.GetProperty("timeout_seconds").GetInt32() != packet.TimeoutSeconds ||
                        !native.GetProperty("arguments").EnumerateArray().Select(a => a.GetString()).SequenceEqual(process.Arguments) ||
                        !packet.Fingerprints.ContainsKey(process.Executable))
                        Error("Native invocation differs from its receipt, deadline or tool pins.");
                }
                string Text(PProcessResult process)
                {
                    foreach (string suffix in new[] { ".stdout.log", ".stderr.log" })
                        if (!packet.Artifacts.ContainsKey(process.Label + suffix)) Error("Missing native log artifact.");
                    return Files.Read(Safe(output, process.Label + ".stdout.log")) + "\n" +
                        Files.Read(Safe(output, process.Label + ".stderr.log"));
                }
                PProcessResult version = packet.Processes.Single(p => p.Label == "p-version");
                if (version.TimedOut || version.ExitCode != 0 || !Text(version).Contains("P version 3.1.0.0", StringComparison.Ordinal))
                    Error("Missing native version completion.");
                if (packet.Command == "inspect-p" || packet.Engine == "inspect")
                {
                    if (packet.Status != "ready" || packet.Claim != "structural-links") Error("Inspection is not a proof/exploration result.");
                }
                else
                {
                    string label = packet.Engine switch { "pverifier" => "p-compile", "pex" or "psym" or "pobserve" => "p-external", _ => "p-check" };
                    PProcessResult process = packet.Processes.Single(p => p.Label == label);
                    if (packet.Engine is "pchecker" or "pex" or "psym")
                    {
                        void Option(string option, string expected)
                        {
                            int position = process.Arguments.IndexOf(option);
                            if (position < 0 || position + 1 >= process.Arguments.Count || process.Arguments[position + 1] != expected)
                                Error("Native invocation disagrees with approved option " + option);
                        }
                        Option("--testcase", config.TestCase!);
                        if (packet.Engine == "pchecker") Option("--mode", "bugfinding");
                        else Option("--strategy", packet.Engine == "pex" ? "dfs" : "symbolic");
                        Option("--schedules", config.Schedules.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        Option("--max-steps", config.MaxSteps.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        Option("--seed", config.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    PAssessment assessment = packet.Engine == "pobserve"
                        ? PResults.Observe(process.ExitCode, process.TimedOut, Text(process),
                            Files.Read(Safe(output, Path.Combine("observations", "PObserveMetrics.txt"))),
                            Directory.GetFiles(Safe(output, "observations"), "*Error_*.txt"))
                        : PResults.Classify(packet.Engine, process.ExitCode, process.TimedOut,
                            PResults.NativeText(output, process, packet.Engine), config.Schedules);
                    if (assessment.Status != packet.Status || assessment.Claim != packet.Claim ||
                        assessment.Diagnostic is ErrorCode code && !packet.Diagnostics.Any(i => i.Is(code)))
                        Error("P outcome contradicts native logs or diagnostics.");
                }
            }
        }
        catch (Exception error) when (error is InputException or JsonException or IOException or UnauthorizedAccessException or
            InvalidOperationException or KeyNotFoundException or FormatException)
        {
            Error("Invalid P evidence: " + error.Message);
        }
        return issues;
    }

    private static string Safe(string root, string relative)
    {
        string file = FilePaths.Full(relative, root);
        if (Path.IsPathRooted(FilePaths.Native(relative)) ||
            !file.StartsWith(FilePaths.Full(root) + Path.DirectorySeparatorChar, FilePaths.Comparison))
            throw new InputException(ErrorCode.InvalidPEvidence, "Artifact escapes the P evidence directory.");
        return file;
    }
}
