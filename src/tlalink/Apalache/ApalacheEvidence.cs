using System.Text.Json;
using System.Text.Json.Nodes;

namespace TlaLink;

public static class ApalacheEvidence
{
    public static void Archive(string output, ApalachePacket packet)
    {
        Directory.CreateDirectory(Path.Combine(output, "inputs"));
        foreach (var pin in packet.Fingerprints.Where(p => FilePaths.IsAbsolute(p.Key)))
        {
            string copy = Safe(output, Path.Combine("inputs", pin.Value));
            if (!File.Exists(copy)) File.Copy(pin.Key, copy);
            if (Files.Hash(copy) != pin.Value)
            {
                packet.Status = "incomplete";
                packet.Claim = "not-established";
                packet.Diagnostics.Add(new(ErrorCode.ApalacheToolFailure, "Input changed before archiving: " + pin.Key));
            }
        }
        foreach (string file in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(output, file);
            if (!relative.StartsWith("inputs" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                packet.Artifacts.Add(relative, Files.Hash(file));
        }
    }

    public static int Read(CliOptions options)
    {
        string result = FilePaths.Full(options.Required("result"));
        using JsonDocument document = JsonDocument.Parse(Files.Read(result));
        bool current = options.Get("current", "false") switch {
            "true" => true, "false" => false,
            _ => throw new InputException(ErrorCode.InvalidApalacheInput, "--current must be true or false.")
        };
        List<Issue> issues = Validate(document.RootElement, Path.GetDirectoryName(result)!, current);
        bool consistent = issues.All(i => i.Severity != "error");
        bool stale = issues.Any(i => i.Is(ErrorCode.ApalacheEvidenceStale));
        string? analysisStatus = document.RootElement.TryGetProperty("status", out JsonElement status) ? status.GetString() : null;
        ProjectWorkflow.Render(new {
            SchemaVersion = Schema.Version, Kind = "apalache-evidence",
            ArchiveStatus = consistent ? "consistent" : "inconsistent",
            CurrentApplicability = !current ? "not-checked" : stale ? "stale" : "current",
            AnalysisStatus = analysisStatus, Diagnostics = issues,
            Boundary = "Archive consistency is not property acceptance; use project accept for independent requirements."
        }, options);
        return !consistent ? 1 : stale || analysisStatus == "incomplete" ? 2 : 0;
    }

    public static List<Issue> Validate(JsonElement root, string output, bool current = false)
    {
        List<Issue> issues = [];
        void Error(string message) => issues.Add(new(ErrorCode.InvalidApalacheEvidence, message));
        try
        {
            Files.RequireSchema(root, "apalache-result");
            ApalachePacket packet = JsonSerializer.Deserialize<ApalachePacket>(root, ProjectInputs.Json)
                ?? throw new InputException(ErrorCode.InvalidApalacheEvidence, "Missing Apalache packet.");
            if (packet.Fingerprints.Count == 0 || packet.Artifacts.Count == 0 && packet.Status != "incomplete" ||
                packet.TimeoutSeconds is < 1 or > 7200 || !FilePaths.IsAbsolute(packet.Directory))
                Error("Missing provenance or invalid native deadline.");
            if (packet.Processes.Any(p => p == null))
                throw new InputException(ErrorCode.InvalidApalacheEvidence, "Null native process receipt.");
            foreach (var pin in packet.Fingerprints.Where(p => FilePaths.IsAbsolute(p.Key)))
            {
                if (!Hash(pin.Value)) throw new InputException(ErrorCode.InvalidApalacheEvidence, "Invalid input hash.");
                string copy = Safe(output, Path.Combine("inputs", pin.Value));
                if (!File.Exists(copy) || Files.Hash(copy) != pin.Value) Error("Missing/changed archived input: " + pin.Key);
                if (current && (!FilePaths.IsNativeAbsolute(pin.Key) || !File.Exists(pin.Key) || Files.Hash(pin.Key) != pin.Value))
                    issues.Add(new(ErrorCode.ApalacheEvidenceStale, "Changed current input: " + pin.Key));
            }
            foreach (var artifact in packet.Artifacts)
                if (!Hash(artifact.Value) || !File.Exists(Safe(output, artifact.Key)) ||
                    Files.Hash(Safe(output, artifact.Key)) != artifact.Value)
                    Error("Missing/changed artifact: " + artifact.Key);
            foreach (string file in new[] { packet.Config, packet.Model, packet.Java, packet.Jar })
                if (!packet.Fingerprints.ContainsKey(file)) Error("Missing required input pin: " + file);
            if (!packet.Fingerprints.TryGetValue(packet.Config, out string? configHash) || configHash != packet.ConfigSha256 || !Hash(packet.ConfigSha256))
                throw new InputException(ErrorCode.InvalidApalacheEvidence, "Configuration identity differs from its input pins.");
            ApalacheAnalysis config = ProjectInputs.Read<ApalacheAnalysis>(Safe(output, Path.Combine("inputs", packet.ConfigSha256)));
            ApalacheInputs.Validate(config);
            if (config.Mode != packet.Mode || !FilePaths.Comparer.Equals(ProjectInputs.Resolve(packet.Config, config.Model), packet.Model))
                Error("Analysis mode/model differs from archived configuration.");
            foreach (var model in packet.ModelFiles)
                if (!packet.Fingerprints.TryGetValue(model.Key, out string? hash) ||
                    model.Value != Path.Combine("model", Path.GetFileName(model.Key)) ||
                    !packet.Artifacts.TryGetValue(model.Value, out string? artifactHash) || artifactHash != hash)
                    Error("Staged model differs from source pins: " + model.Key);
            if (packet.Status == "incomplete")
            {
                if (packet.Claim != "not-established" || packet.Diagnostics.Count == 0)
                    Error("Incomplete analysis must retain its failure and cannot establish a claim.");
                return issues;
            }
            if (packet.Status is not ("ready" or "passed" or "rejected") ||
                !packet.ModelFiles.ContainsKey(packet.Model) || !packet.Artifacts.ContainsKey("model-index.json"))
                throw new InputException(ErrorCode.InvalidApalacheEvidence, "Invalid completed outcome or missing model closure.");
            using JsonDocument index = JsonDocument.Parse(Files.Read(Safe(output, "model-index.json")));
            ApalacheBackend.RequireSupportedIndex(index.RootElement, config);
            foreach (JsonElement dependency in index.RootElement.EnumerateArray().Where(r => r.GetProperty("kind").GetString() == "dependency"))
            {
                string path = dependency.GetProperty("path").GetString()!;
                string hash = dependency.GetProperty("sha256").GetString()!;
                if (!packet.Fingerprints.TryGetValue(path, out string? pin) || pin != hash ||
                    FilePaths.IsAbsolute(path) && Path.GetExtension(path).Equals(".tla", StringComparison.OrdinalIgnoreCase) &&
                    !packet.ModelFiles.ContainsKey(path))
                    Error("SANY dependency closure differs from pinned/staged inputs: " + path);
            }
            List<ApalacheQuery> queries = ApalacheInputs.Queries(config);
            if (packet.Processes.Count < 2 || packet.Processes[0].Id != "version" ||
                packet.Processes.Count > queries.Count + 1 ||
                packet.Processes.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != packet.Processes.Count)
                throw new InputException(ErrorCode.InvalidApalacheEvidence, "Missing/duplicate native obligations.");
            foreach (ApalacheProcessResult process in packet.Processes)
            {
                foreach (string suffix in new[] { ".stdout.log", ".stderr.log", ".invocation.json" })
                    if (!packet.Artifacts.ContainsKey(process.Id + suffix)) Error("Missing native receipt/log artifact.");
                using JsonDocument receipt = JsonDocument.Parse(Files.Read(Safe(output, process.Id + ".invocation.json")));
                JsonElement invocation = receipt.RootElement;
                Files.RequireSchema(invocation, "apalache-invocation");
                if (invocation.GetProperty("executable").GetString() != packet.Java ||
                    invocation.GetProperty("working_directory").GetString() != packet.Directory ||
                    invocation.GetProperty("timeout_seconds").GetInt32() != packet.TimeoutSeconds ||
                    invocation.GetProperty("environment").GetString() != "isolated" ||
                    !JsonNode.DeepEquals(JsonNode.Parse(invocation.GetProperty("result").GetRawText()), JsonSerializer.SerializeToNode(process, Files.Json)))
                    Error("Native receipt disagrees with the declared invocation.");
            }
            ApalacheProcessResult version = packet.Processes[0];
            if (!version.Arguments.SequenceEqual(ApalacheBackend.VersionArguments(packet)) ||
                !ApalacheBackend.VersionMatches(version, ApalacheBackend.Text(output, "version")))
                Error("Missing/mismatched native Apalache version.");
            string outcome = "passed";
            for (int i = 1; i < packet.Processes.Count; i++)
            {
                ApalacheProcessResult process = packet.Processes[i];
                ApalacheQuery query = queries[i - 1];
                if (process.Id != query.Id || !process.Arguments.SequenceEqual(ApalacheBackend.Arguments(packet, query)) ||
                    !packet.Artifacts.ContainsKey(query.Id + ".json") ||
                    !JsonNode.DeepEquals(JsonNode.Parse(Files.Read(Safe(output, query.Id + ".json"))), ApalacheBackend.NativeConfig(packet, config, query)))
                    Error("Native obligation/configuration differs from approved analysis: " + query.Id);
                outcome = ApalacheBackend.Assess(process, query, ApalacheBackend.Text(output, process.Id),
                    ApalacheBackend.HasTrace(output, process.Id, "violation", null, packet.Artifacts.Keys),
                    ApalacheBackend.HasTrace(output, process.Id, "example", query.Length + 1, packet.Artifacts.Keys));
                if (outcome != "passed" && i != packet.Processes.Count - 1)
                    Error("Obligations continued after a failed prerequisite.");
            }
            string expectedStatus = outcome == "passed" && config.Mode == "typecheck" ? "ready" : outcome;
            string expectedClaim = outcome == "passed" ? ApalacheInputs.Claim(config.Mode) : "not-established";
            if (outcome == "passed" && packet.Processes.Count != queries.Count + 1 ||
                packet.Status != expectedStatus || packet.Claim != expectedClaim ||
                outcome == "incomplete" ||
                outcome == "rejected" && !packet.Diagnostics.Any(d => d.Is(ApalacheBackend.RejectionCode(config.Mode))) ||
                outcome == "passed" && packet.Diagnostics.Any(d => d.Severity == "error"))
                Error("Outcome/claim contradicts the complete native obligations.");
            if (outcome == "rejected" && !packet.Artifacts.Keys.Any(p => p.EndsWith(".itf.json", StringComparison.Ordinal)))
                Error("Rejection lacks an archived native counterexample.");
        }
        catch (Exception error) when (error is InputException or JsonException or IOException or UnauthorizedAccessException or
            InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {
            Error("Invalid Apalache evidence: " + error.Message);
        }
        return issues;
    }

    private static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static string Safe(string root, string relative)
    {
        string file = FilePaths.Full(relative, root);
        if (Path.IsPathRooted(FilePaths.Native(relative)) ||
            !file.StartsWith(FilePaths.Full(root) + Path.DirectorySeparatorChar, FilePaths.Comparison))
            throw new InputException(ErrorCode.InvalidApalacheEvidence, "Artifact escapes the Apalache evidence directory.");
        return file;
    }
}
