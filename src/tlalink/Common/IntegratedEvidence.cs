using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TlaLink;

public sealed record IntegratedEvidenceInspection(
    string ArchiveStatus, string SemanticStatus, string CurrentApplicability, string? RecordedStatus,
    List<Issue> Diagnostics, IntegratedResult? RecordedResult)
{
    public int SchemaVersion => Schema.Version;
    public string Kind => "read-integrated";
    public string Command => "read-integrated";
    public string AcceptanceStatus => "not-evaluated";
    public string Boundary => "Checks recorded semantic and byte consistency and optional current applicability only; does not rerun proofs/tests, establish correspondence or authenticate the producing host.";
    [JsonIgnore]
    public int ExitCode => ArchiveStatus != "consistent" || SemanticStatus != "consistent" ? 1 :
        CurrentApplicability == "stale" ? 2 : 0;
}

public static class IntegratedEvidence
{
    public static int Read(CliOptions options)
    {
        bool current = options.Get("current", "false") switch {
            "true" => true, "false" => false, _ => throw new InputException(ErrorCode.InvalidIntegratedEvidence, "--current must be true or false.")
        };
        IntegratedEvidenceInspection inspection = Inspect(options.Required("result"), current);
        ProjectWorkflow.Render(inspection, options);
        return inspection.ExitCode;
    }

    public static IntegratedEvidenceInspection Inspect(string path, bool current)
    {
        path = FilePaths.Full(path);
        using JsonDocument document = JsonDocument.Parse(Files.Read(path), new JsonDocumentOptions { AllowDuplicateProperties = false });
        RequireSchema(document.RootElement);
        List<Issue> issues = [];
        IntegratedResult? result = Decode(document.RootElement, issues);
        if (result == null)
            return new("not-checked", "inconsistent", "not-checked", null, issues, null);
        string archive = Path.Combine(Path.GetDirectoryName(path)!, "inputs");
        issues.AddRange(Validate(result, archive));
        string semantic = issues.Count == 0 ? "consistent" : "inconsistent";
        int count = 0;
        foreach (var pin in result.Fingerprints.Concat(result.GeneratedFiles).Where(p => FilePaths.IsAbsolute(p.Key)))
        {
            if (!IsHash(pin.Value))
                throw new InputException(ErrorCode.InvalidIntegratedEvidence, "Invalid file hash.");
            count++;
            string archived = Path.Combine(archive, pin.Value);
            if (!File.Exists(archived) || Files.Hash(archived) != pin.Value)
                issues.Add(new(ErrorCode.IntegratedArchiveInconsistent, "Missing or changed archived input: " + pin.Key));
        }
        if (count == 0) throw new InputException(ErrorCode.InvalidIntegratedEvidence, "Packet has no archived file pins.");
        if (current)
            foreach (var pin in result.Fingerprints.Where(p => FilePaths.IsAbsolute(p.Key)))
                if (!FilePaths.IsNativeAbsolute(pin.Key) || !File.Exists(pin.Key) || Files.Hash(pin.Key) != pin.Value)
                    issues.Add(new(ErrorCode.IntegratedEvidenceStale, "Current input differs or is missing: " + pin.Key));
        return new(issues.Any(i => i.Code == ErrorCode.IntegratedArchiveInconsistent.Id()) ? "inconsistent" : "consistent", semantic,
            !current ? "not-checked" : issues.Any(i => i.Code == ErrorCode.IntegratedEvidenceStale.Id()) ? "stale" : "current",
            result.Status, issues, result);
    }

    private static bool IsHash(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static void RequireSchema(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("schema_version", out JsonElement schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) ||
            version != Schema.Version || !result.TryGetProperty("kind", out JsonElement kind) ||
            kind.ValueKind != JsonValueKind.String || kind.GetString() != "integrated-result")
            throw new InputException(ErrorCode.InvalidIntegratedEvidence, "Expected current-schema integrated-result packet; unsupported schemas are not upgraded.");
    }

    private static IntegratedResult? Decode(JsonElement result, List<Issue> issues)
    {
        void Error(string message) => issues.Add(new(ErrorCode.IntegratedEvidenceInconsistent, message));
        try { RequireSchema(result); }
        catch (InputException error) { Error(error.Message); return null; }
        foreach (string field in new[] { "command", "status", "links_status", "coverage_status", "proof_status",
            "audit_status", "conformance_status", "correspondence_status", "provenance_status" })
            if (!result.TryGetProperty(field, out JsonElement value) || value.ValueKind != JsonValueKind.String)
                Error("Invalid or missing " + field + ".");
        foreach (string field in new[] { "stages", "generated_files", "fingerprints", "links", "correspondences" })
        {
            JsonValueKind expected = field is "links" or "correspondences" ? JsonValueKind.Array : JsonValueKind.Object;
            if (!result.TryGetProperty(field, out JsonElement value) || value.ValueKind != expected)
                Error("Invalid or missing " + field + ".");
        }
        foreach (string field in new[] { "fingerprints", "generated_files" })
            if (result.TryGetProperty(field, out JsonElement pins) && pins.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty pin in pins.EnumerateObject())
                    if (pin.Value.ValueKind != JsonValueKind.String)
                        Error("Malformed fingerprint: " + pin.Name + ".");
        bool Is(JsonElement value, string name, JsonValueKind kind) =>
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) &&
            property.ValueKind == kind;
        if (result.TryGetProperty("stages", out JsonElement stages) && stages.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty stage in stages.EnumerateObject())
            {
                if (!Is(stage.Value, "status", JsonValueKind.String) ||
                    !stage.Value.TryGetProperty("exit_code", out JsonElement exit) ||
                    exit.ValueKind != JsonValueKind.Null && (exit.ValueKind != JsonValueKind.Number || !exit.TryGetInt32(out _)))
                    Error("Stage status contradicts its native exit code: " + stage.Name + ".");
                if (!Is(stage.Value, "log", JsonValueKind.String))
                    Error("Unpinned stage artifact: " + stage.Name + ".");
            }
        if (result.TryGetProperty("execution", out JsonElement execution) && execution.ValueKind != JsonValueKind.Null)
        {
            if (execution.ValueKind != JsonValueKind.Object) Error("Missing conformance execution receipt.");
            else
            {
                foreach (string field in new[] { "tested_bindings", "untested_bindings" })
                    if (!Is(execution, field, JsonValueKind.Array)) Error("Missing execution " + field + ".");
                    else if (execution.GetProperty(field).EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                        Error("Invalid or duplicate execution " + field + ".");
                foreach (string field in new[] { "compile_inputs", "references" })
                    if (!Is(execution, field, JsonValueKind.Object)) Error("Missing execution " + field + ".");
                    else
                        foreach (JsonProperty pin in execution.GetProperty(field).EnumerateObject())
                            if (pin.Value.ValueKind != JsonValueKind.String)
                                Error("Execution input is not pinned in the recorded closure: " + pin.Name + ".");
                if (!Is(execution, "cases", JsonValueKind.Array)) Error("Conformance requires nonempty case results.");
                else
                    foreach (JsonElement item in execution.GetProperty("cases").EnumerateArray())
                    {
                        if (!Is(item, "binding", JsonValueKind.String) || !Is(item, "id", JsonValueKind.String))
                            Error("Unknown or duplicate case result.");
                        if (!Is(item, "expected", JsonValueKind.Array) || !Is(item, "actual", JsonValueKind.Array) ||
                            !item.TryGetProperty("matches", out JsonElement matches) ||
                            matches.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        { Error("Malformed case comparison."); continue; }
                        if (item.GetProperty("expected").EnumerateArray().Concat(item.GetProperty("actual").EnumerateArray())
                            .Any(v => v.ValueKind != JsonValueKind.String))
                            Error("Case matches flag contradicts compared outputs.");
                    }
                if (!Is(execution, "binary", JsonValueKind.String) || !Is(execution, "binary_sha256", JsonValueKind.String))
                    Error("Execution binary does not match its generated-file pin.");
                if (!Is(execution, "comparison_sha256", JsonValueKind.String))
                    Error("Case comparison receipt does not match its generated-file pin.");
                if (!Is(execution, "compiler_context", JsonValueKind.Object))
                    Error("Missing or malformed execution compiler context.");
                else
                {
                    JsonElement context = execution.GetProperty("compiler_context");
                    if (new[] { "framework", "language_version", "nullable", "compiler" }
                            .Any(field => !Is(context, field, JsonValueKind.String)) ||
                        !Is(context, "defines", JsonValueKind.Array) ||
                        context.GetProperty("defines").EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String))
                        Error("Missing or malformed execution compiler context.");
                }
            }
        }
        if (issues.Count > 0) return null;
        try
        {
            return result.Deserialize<IntegratedResult>(IntegratedInputs.Json)
                ?? throw new JsonException("Expected an integrated-result object.");
        }
        catch (JsonException error)
        {
            Error((error.Path?.StartsWith("$.source_obligations", StringComparison.Ordinal) == true
                ? "Invalid source obligations: " : "Malformed integrated-result: ") + error.Message);
            return null;
        }
    }

    public static List<Issue> Validate(JsonElement result, string? archiveDirectory = null)
    {
        List<Issue> issues = [];
        IntegratedResult? decoded = Decode(result, issues);
        if (decoded != null) issues.AddRange(Validate(decoded, archiveDirectory));
        return issues;
    }

    public static List<Issue> Validate(IntegratedResult result, string? archiveDirectory = null)
    {
        List<Issue> issues = [];
        void Error(string message) => issues.Add(new(ErrorCode.IntegratedEvidenceInconsistent, message));
        if (result.SchemaVersion != Schema.Version || result.Kind != "integrated-result")
        {
            Error("Expected current-schema integrated-result packet; unsupported schemas are not upgraded.");
            return issues;
        }
        void Status(string name, string? value, params string[] allowed)
        {
            if (value == null || !allowed.Contains(value)) Error("Invalid or missing " + name + ".");
        }
        string command = result.Command, status = result.Status, linksStatus = result.LinksStatus,
            proof = result.ProofStatus, audit = result.AuditStatus, conformance = result.ConformanceStatus,
            correspondence = result.CorrespondenceStatus, provenance = result.ProvenanceStatus;
        Status("command", command, "inspect-integrated", "verify-integrated", "test-integrated");
        Status("status", status, "ready", "passed", "rejected", "incomplete");
        Status("links_status", linksStatus, "passed", "incomplete");
        Status("coverage_status", result.CoverageStatus, "requires-review");
        Status("proof_status", proof, "not-run", "verified", "rejected", "incomplete");
        Status("audit_status", audit, "not-run", "passed", "findings", "incomplete");
        Status("conformance_status", conformance, "not-run", "conforms", "rejected", "incomplete");
        Status("correspondence_status", correspondence, "not-specified", "requires-review");
        Status("provenance_status", provenance, "consistent", "incomplete");
        var stages = result.Stages;
        var generated = result.GeneratedFiles;
        var fingerprints = result.Fingerprints;
        foreach (var pin in fingerprints.Concat(generated))
            if (pin.Value == null || FilePaths.IsAbsolute(pin.Key) && !IsHash(pin.Value))
                Error("Malformed fingerprint: " + pin.Key + ".");
        var links = result.Links;
        var claims = result.Correspondences;
        string sourceStatus = "not-specified";
        List<SourceObligationResult> premises = result.SourceObligations.ToList();
        try
        {
            if (premises.Any(p => p == null || p.Obligation == null))
                throw new InputException(ErrorCode.InvalidIntegratedEvidence, "Source obligation records cannot be null.");
            SourceObligations.Validate(premises.Select(p => p.Obligation).ToList(), links.Select(l => l?.Id ?? ""));
            foreach (SourceObligationResult premise in premises)
            {
                if (premise.Status is not ("passed" or "rejected" or "incomplete") ||
                    string.IsNullOrWhiteSpace(premise.Reason) || premise.Evidence == null ||
                    premise.Status == "passed" && premise.Evidence.Count == 0)
                    Error("Source obligations require a supported outcome, explanation and source evidence.");
                if (premise.Status == "passed" && premise.Obligation.Kind is "guarded-effect" or "guarded-try-effect" && premise.Flow == null)
                    Error("Passed guarded effects require recorded source-flow facts.");
            }
            sourceStatus = SourceObligations.Status(premises);
        }
        catch (InputException error)
        {
            Error("Invalid source obligations: " + error.Message);
            sourceStatus = "incomplete";
            premises = [];
        }
        if (result.SourceObligationsStatus != null && result.SourceObligationsStatus != sourceStatus)
            Error("Source obligation status disagrees with recorded premises.");
        if (premises.Count > 0 && result.Bindings == null)
            Error("Source obligations require an explicit pinned binding manifest.");
        if (archiveDirectory != null && result.Bindings is string manifestPath)
        {
            try
            {
                if (!fingerprints.TryGetValue(manifestPath, out string? hash) || !IsHash(hash))
                    throw new InputException(ErrorCode.InvalidIntegratedEvidence, "Binding manifest must have an explicit file pin.");
                string archived = Path.Combine(archiveDirectory, hash);
                if (!File.Exists(archived) || Files.Hash(archived) != hash)
                    throw new InputException(ErrorCode.InvalidIntegratedEvidence, "Missing or changed archived binding manifest.");
                IntegratedManifest policy = IntegratedInputs.Parse(Files.Read(archived));
                if (policy.SchemaVersion != Schema.Version || policy.Kind != "integrated-bindings" ||
                    policy.Bindings == null || policy.Bindings.Any(b => b == null))
                    throw new InputException(ErrorCode.InvalidIntegratedEvidence, "Invalid archived binding manifest.");
                SourceObligations.Validate(policy.SourceObligations, policy.Bindings.Select(b => b.Id));
                if (linksStatus == "passed")
                {
                    Dictionary<string, string> recordedPins = fingerprints.ToDictionary(p => p.Key, p => p.Value);
                    string Recorded(string file)
                    {
                        if (!recordedPins.TryGetValue(file, out string? fileHash))
                            throw new InputException(ErrorCode.InvalidIntegratedEvidence, "Missing archived correspondence pin: " + file);
                        string copy = Path.Combine(archiveDirectory, fileHash);
                        if (!File.Exists(copy) || Files.Hash(copy) != fileHash)
                            throw new InputException(ErrorCode.InvalidIntegratedEvidence, "Changed archived correspondence input: " + file);
                        return Files.Read(copy);
                    }
                    CorrespondenceConfiguration scope = CorrespondenceEvidence.Scope(
                        ProjectInputs.Resolve(manifestPath, policy.Scope), Recorded);
                    List<FormalLinkDeclaration> formal = FormalCorrespondence.Dafny(
                        result.Inventory.ToList(), recordedPins, Recorded);
                    Dictionary<string, ArchivedSourceBinding> inline = CorrespondenceEvidence.Bindings(
                        policy.Sources.Select(p => ProjectInputs.Resolve(manifestPath, p)), policy.Bindings.Select(b => b.Id),
                        recordedPins, Recorded, formal,
                        hash => Path.Combine(archiveDirectory, hash));
                    bool hasTla = result.Tla != null;
                    if (hasTla != (policy.TlaTargets.Count > 0) || hasTla &&
                        !result.Tla!.SelectedTargets.ToHashSet(StringComparer.Ordinal).SetEquals(policy.TlaTargets))
                        Error("Recorded TLA+ selection differs from the pinned binding manifest.");
                    foreach (DafnyLink link in links)
                    {
                        string id = link?.Id ?? "";
                        if (link == null || !inline.TryGetValue(id, out ArchivedSourceBinding? declaration) ||
                            !policy.Bindings.Any(b => b.Id == id) ||
                            formal.Count(f => f.Binding.Id == id && f.Endpoint.Name == link.Specification?.Name) != 1 ||
                            link.Implementation?.Location != declaration.Location ||
                            !scope.Targets.Any(t => t.Id == Markup.Name(id).Alias && t.Language == "dafny"))
                            Error("Recorded link differs from its pinned inline binding: " + id);
                    }
                }
                if ((linksStatus == "passed" || premises.Count > 0) &&
                    JsonSerializer.Serialize(policy.SourceObligations, Files.Json) !=
                    JsonSerializer.Serialize(premises.Select(p => p.Obligation).ToList(), Files.Json))
                    Error("Recorded source obligations differ from the pinned binding manifest.");
            }
            catch (Exception error) when (error is JsonException or InputException or IOException or UnauthorizedAccessException)
            {
                Error("Cannot check source obligation policy: " + error.Message);
            }
        }
        if (sourceStatus is "rejected" or "incomplete" && (proof != "not-run" || audit != "not-run" || conformance != "not-run"))
            Error("Unmet source obligations must block native proof and conformance stages.");
        HashSet<string> linked = [];
        foreach (DafnyLink link in links)
            if (string.IsNullOrWhiteSpace(link?.Id) || !linked.Add(link.Id))
                Error("Link identifiers must be nonempty and unique.");
        if (linksStatus == "passed" && linked.Count == 0) Error("Passed links require resolved bindings.");
        if (correspondence != (claims.Length == 0 ? "not-specified" : "requires-review"))
            Error("Correspondence status disagrees with recorded claims.");
        HashSet<string> ids = [];
        foreach (CorrespondenceResult claim in claims)
        {
            if (claim?.Status != "requires-review" || claim.CompositionStatus != "not-proved")
                Error("Correspondence claims must remain unproved review obligations.");
            if (claim?.Claim == null) { Error("Missing correspondence claim."); continue; }
            if (string.IsNullOrWhiteSpace(claim.Claim.Id) || !ids.Add(claim.Claim.Id) ||
                claim.Claim.DafnyBinding == null || !linked.Contains(claim.Claim.DafnyBinding))
                Error("Invalid correspondence identifiers or binding.");
        }
        bool HasStage(string name, string state, int? code) =>
            stages.TryGetValue(name, out StageResult? stage) && stage?.Status == state && stage.ExitCode == code;
        void NeedStage(string name)
        {
            if (!HasStage(name, "passed", 0)) Error("Outcome requires a passed " + name + " stage.");
        }
        void NeedArtifact(string name)
        {
            if (!generated.Keys.Any(p => Path.GetFileName(p) == name))
                Error("Missing stage artifact: " + name + ".");
        }
        foreach (var entry in stages)
        {
            string? state = entry.Value?.Status, log = entry.Value?.Log;
            if (entry.Key is not ("dafny-version" or "dafny-bridge" or "dafny-discover" or "dafny-resolve" or
                "dafny-verify" or "dafny-audit" or "case-preconditions" or "oracle-translate" or "oracle-compare"))
                Error("Unexpected stage: " + entry.Key + ".");
            if (command == "inspect-integrated" && entry.Key is ("dafny-verify" or "dafny-audit" or
                "case-preconditions" or "oracle-translate" or "oracle-compare") ||
                command == "verify-integrated" && entry.Key is ("case-preconditions" or "oracle-translate" or "oracle-compare"))
                Error("Stage cannot be produced by the recorded command: " + entry.Key + ".");
            int? code = entry.Value?.ExitCode;
            bool exitValid = entry.Value != null && (state == "incomplete" ? code == null :
                code != null && (state == "passed" ? code == 0 : state == "failed" && code != 0));
            if (!exitValid) Error("Stage status contradicts its native exit code: " + entry.Key + ".");
            if (log == null || !generated.ContainsKey(log)) Error("Unpinned stage artifact: " + entry.Key + ".");
            if (entry.Key != "dafny-bridge")
            {
                if (log != null && Path.GetFileName(log) != entry.Key + ".log") Error("Unexpected stage log: " + entry.Key + ".");
                NeedArtifact(entry.Key + ".invocation.json");
            }
        }
        if (linksStatus == "passed")
        {
            foreach (string name in new[] { "dafny-version", "dafny-bridge", "dafny-discover", "dafny-resolve" }) NeedStage(name);
            NeedArtifact("dafny-discover.json");
            NeedArtifact("dafny-resolve.json");
        }
        if (proof == "verified") NeedStage("dafny-verify");
        if (proof == "rejected" && !HasStage("dafny-verify", "failed", 4))
            Error("Rejected proof requires native verification exit 4.");
        if (proof == "not-run" && status != "incomplete" && stages.ContainsKey("dafny-verify"))
            Error("Verification stage contradicts not-run proof.");
        if (proof != "not-run" && linksStatus != "passed") Error("Proof outcome requires passed links.");
        if (audit != "not-run" && proof != "verified") Error("Audit requires verified proof.");
        if (conformance != "not-run" && (proof != "verified" || audit != "passed"))
            Error("Conformance requires verified proof and passed audit.");
        if (audit is "passed" or "findings")
        {
            if (proof != "verified") Error("Audit outcome requires verified proof.");
            NeedStage("dafny-audit");
            NeedArtifact("dafny-audit.txt");
        }
        if (command == "inspect-integrated" && (proof != "not-run" || audit != "not-run" || conformance != "not-run"))
            Error("Inspection cannot claim proof, audit or execution outcomes.");
        if (command == "verify-integrated" && conformance != "not-run")
            Error("Verification command cannot claim conformance.");
        if (conformance is "conforms" or "rejected")
        {
            if (command != "test-integrated" || proof != "verified" || audit != "passed")
                Error("Conformance requires a test command, verified proof and passed audit.");
            foreach (string name in new[] { "case-preconditions", "oracle-translate", "oracle-compare" }) NeedStage(name);
            foreach (string name in new[] { "Cases.dfy", "Oracle.cs", "Conformance.cs", "Conformance.dll",
                "Conformance.runtimeconfig.json", "case-results.json" }) NeedArtifact(name);
            if (result.Execution == null) Error("Missing conformance execution receipt.");
            else ValidateExecution(result.Execution, linked, conformance, generated, fingerprints, Error);
        }
        else if (result.Execution != null) Error("Execution receipt contradicts unexecuted conformance status.");
        if (status is "ready" or "passed" or "rejected")
        {
            if (sourceStatus == "incomplete" || sourceStatus == "rejected" && status != "rejected")
                Error("Completed root outcome contradicts source obligations.");
            if (linksStatus != "passed" || provenance != "consistent") Error("Completed root outcome requires links and provenance.");
            if (status == "ready" && command != "inspect-integrated") Error("Only inspection can be ready.");
            if (status == "passed" && (command == "inspect-integrated" || proof != "verified" || audit != "passed" ||
                command == "test-integrated" && conformance != "conforms"))
                Error("Passed root outcome contradicts required stages.");
            if (status == "rejected" && proof != "rejected" && audit != "findings" && conformance != "rejected" &&
                sourceStatus != "rejected")
                Error("Rejected root outcome has no rejecting evidence.");
        }
        return issues;
    }

    private static void ValidateExecution(IntegratedExecution execution, HashSet<string> linked, string conformance,
        ImmutableDictionary<string, string> generated, ImmutableDictionary<string, string> fingerprints, Action<string> error)
    {
        HashSet<string> ReadIds(ImmutableArray<string> values, string name)
        {
            HashSet<string> ids = [];
            foreach (string value in values)
                if (string.IsNullOrWhiteSpace(value) || !ids.Add(value))
                    error("Invalid or duplicate execution " + name + ".");
            return ids;
        }
        HashSet<string> tested = ReadIds(execution.TestedBindings, "tested_bindings"),
            untested = ReadIds(execution.UntestedBindings, "untested_bindings");
        if (tested.Overlaps(untested) || !tested.Concat(untested).ToHashSet().SetEquals(linked))
            error("Tested and untested identifiers must partition linked bindings.");
        HashSet<(string, string)> keys = [];
        HashSet<string> observed = [];
        bool mismatch = false;
        if (execution.Cases.IsEmpty) error("Conformance requires nonempty case results.");
        foreach (CaseComparison item in execution.Cases)
        {
            string? binding = item?.Binding, id = item?.Id;
            if (binding == null || string.IsNullOrWhiteSpace(id) || !tested.Contains(binding) || !keys.Add((binding, id)))
                error("Unknown or duplicate case result.");
            if (binding != null) observed.Add(binding);
            if (item?.Expected == null || item.Actual == null)
            { error("Malformed case comparison."); continue; }
            bool strings = item.Expected.Concat(item.Actual).All(v => v != null);
            bool equal = strings && item.Expected.SequenceEqual(item.Actual);
            if (!strings || item.Expected.Length == 0 || item.Expected.Length != item.Actual.Length || item.Matches != equal)
                error("Case matches flag contradicts compared outputs.");
            mismatch |= !equal;
        }
        if (!observed.SetEquals(tested)) error("Tested identifiers disagree with case results.");
        if ((conformance == "rejected") != mismatch) error("Conformance status contradicts case results.");
        if (!generated.TryGetValue(execution.Binary, out string? pinned) || pinned != execution.BinarySHA256)
            error("Execution binary does not match its generated-file pin.");
        foreach (var (field, pins) in new[] { ("compile_inputs", execution.CompileInputs), ("references", execution.References) })
        {
            if (pins.Count == 0) error("Missing execution " + field + ".");
            foreach (var pin in pins)
            {
                bool Pinned(ImmutableDictionary<string, string> all) =>
                    pin.Value != null && all.TryGetValue(pin.Key, out string? original) && original == pin.Value;
                if (!Pinned(fingerprints) && (field == "references" || !Pinned(generated)))
                    error("Execution input is not pinned in the recorded closure: " + pin.Key + ".");
            }
        }
        if (!generated.Any(p => Path.GetFileName(p.Key) == "case-results.json" && p.Value == execution.ComparisonSHA256))
            error("Case comparison receipt does not match its generated-file pin.");
        IntegratedCompilerContext context = execution.CompilerContext;
        if (context.Framework != "net10.0" || context.LanguageVersion == null || context.Nullable == null ||
            context.Compiler == null || context.Defines.Any(d => d == null))
            error("Missing or malformed execution compiler context.");
    }
}
