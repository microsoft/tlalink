using System.Text.Json;

namespace TlaLink;

public static class StageEvidence
{
    internal static Dictionary<string, string> PathPins(Dictionary<string, string> values)
    {
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        foreach (var pin in values) Files.Pin(pins, pin.Key, pin.Value);
        return pins;
    }

    public static Dictionary<string, string> Seal(string directory, Dictionary<string, string> fingerprints)
    {
        IntegratedInputs.CheckPins(fingerprints);
        string inputs = Directory.CreateDirectory(Path.Combine(directory, "inputs")).FullName;
        foreach (var pin in fingerprints.Where(p => FilePaths.IsAbsolute(p.Key)))
        {
            RequireHash(pin.Value);
            string copy = Path.Combine(inputs, pin.Value);
            if (!File.Exists(copy)) File.Copy(pin.Key, copy);
            if (Files.Hash(copy) != pin.Value)
                throw new InputException(ErrorCode.WorkflowInputChanged, "Input changed during archival: " + pin.Key);
        }
        Dictionary<string, string> artifacts = new(StringComparer.Ordinal);
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(directory, file);
            if (!relative.StartsWith("inputs" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && relative != "result.json")
                artifacts.Add(relative, Files.Hash(file));
        }
        IntegratedInputs.CheckPins(fingerprints);
        return artifacts;
    }

    public static List<Issue> Validate(JsonElement result, string directory, bool current)
    {
        Dictionary<string, string> fingerprints = result.GetProperty("fingerprints").Deserialize<Dictionary<string, string>>(ProjectInputs.Json)!;
        Dictionary<string, string> artifacts = result.GetProperty("artifacts").Deserialize<Dictionary<string, string>>(ProjectInputs.Json)!;
        List<Issue> issues = [];
        if (fingerprints.Count == 0 || artifacts.Count == 0)
            issues.Add(new(ErrorCode.WorkflowProvenanceInconsistent, "Stage evidence requires input and artifact closures."));
        foreach (var pin in fingerprints)
        {
            if (!FilePaths.IsAbsolute(pin.Key))
            {
                if (!pin.Key.StartsWith("runtime:", StringComparison.Ordinal) && !pin.Key.StartsWith("tool:", StringComparison.Ordinal))
                    issues.Add(new(ErrorCode.WorkflowProvenanceInconsistent, "Unknown stage fingerprint identity: " + pin.Key));
                continue;
            }
            string copy = Archived(directory, pin.Value);
            if (!File.Exists(copy) || Files.Hash(copy) != pin.Value)
                issues.Add(new(ErrorCode.WorkflowProvenanceInconsistent, "Missing/changed archived input: " + pin.Key));
            if (current && (!FilePaths.IsNativeAbsolute(pin.Key) || !File.Exists(pin.Key) || Files.Hash(pin.Key) != pin.Value))
                issues.Add(new(ErrorCode.WorkflowEvidenceStale, "Missing/changed current input: " + pin.Key));
        }
        foreach (var artifact in artifacts)
        {
            RequireHash(artifact.Value);
            string file = ProjectAcceptance.SafePath(directory, artifact.Key);
            if (!File.Exists(file) || Files.Hash(file) != artifact.Value)
                issues.Add(new(ErrorCode.WorkflowProvenanceInconsistent, "Missing/changed stage artifact: " + artifact.Key));
        }
        return issues;
    }

    public static string Archived(string directory, string hash)
    {
        RequireHash(hash);
        return Path.Combine(directory, "inputs", hash);
    }

    public static void RequireHash(string hash)
    {
        if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InputException(ErrorCode.WorkflowProvenanceInconsistent, "Invalid evidence SHA256.");
    }

    public static int Read(CliOptions options, string kind, Func<JsonElement, string, List<Issue>> validate)
    {
        bool current = options.Get("current", "false") switch {
            "true" => true, "false" => false,
            _ => throw new InputException(ErrorCode.InvalidWorkflowInput, "--current must be true or false.")
        };
        string file = FilePaths.Full(options.Required("result")), directory = Path.GetDirectoryName(file)!;
        JsonElement result = ProjectInputs.Read<JsonElement>(file);
        Files.RequireSchema(result, kind);
        List<Issue> issues = Validate(result, directory, current);
        if (!issues.Any(i => i.Is(ErrorCode.WorkflowProvenanceInconsistent)))
            issues.AddRange(kind == "csharp-regression-result" ? CSharpRegression.Validate(result, directory, current) : validate(result, directory));
        ProjectWorkflow.Render(new {
            SchemaVersion = Schema.Version, Kind = "stage-evidence-inspection",
            ArchiveStatus = issues.Any(i => i.Is(ErrorCode.WorkflowProvenanceInconsistent)) ? "inconsistent" : "consistent",
            SemanticStatus = issues.Any(i => i.Is(ErrorCode.WorkflowStageInconsistent)) ? "inconsistent" : "consistent",
            CurrentApplicability = !current ? "not-checked" : issues.Any(i => i.Is(ErrorCode.WorkflowEvidenceStale)) ? "stale" : "current",
            RecordedStatus = result.GetProperty("status"), AcceptanceStatus = "not-evaluated", Diagnostics = issues,
            Boundary = "Independent archive, native-result consistency and optional applicability checks; no re-execution, acceptance, authentication or C# refinement."
        }, options);
        return issues.Any(i => i.Severity == "error" && !i.Is(ErrorCode.WorkflowEvidenceStale)) ? 1 : issues.Count > 0 ? 2 : 0;
    }
}
