using System.Text.Json;
using System.Text.Json.Serialization;

namespace TlaLink;

public sealed record IntegratedEvidenceInspection(
    string ArchiveStatus, string SemanticStatus, string CurrentApplicability, string? RecordedStatus,
    List<Issue> Diagnostics, JsonElement RecordedResult)
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
        Console.WriteLine(JsonSerializer.Serialize(inspection, Files.Json));
        return inspection.ExitCode;
    }

    public static IntegratedEvidenceInspection Inspect(string path, bool current)
    {
        path = FilePaths.Full(path);
        using JsonDocument document = JsonDocument.Parse(Files.Read(path), new JsonDocumentOptions { AllowDuplicateProperties = false });
        JsonElement result = document.RootElement;
        RequireSchema(result);
        List<Issue> issues = Validate(result);
        string semantic = issues.Count == 0 ? "consistent" : "inconsistent";
        if (!result.TryGetProperty("fingerprints", out JsonElement inputs) || inputs.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("generated_files", out JsonElement generated) || generated.ValueKind != JsonValueKind.Object)
            throw new InputException(ErrorCode.InvalidIntegratedEvidence, "Expected fingerprint and generated-file objects.");
        string archive = Path.Combine(Path.GetDirectoryName(path)!, "inputs");
        int count = 0;
        foreach (JsonProperty pin in inputs.EnumerateObject().Concat(generated.EnumerateObject()).Where(p => FilePaths.IsAbsolute(p.Name)))
        {
            string hash = pin.Value.ValueKind == JsonValueKind.String ? pin.Value.GetString()! : "";
            if (hash.Length != 64 || hash.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InputException(ErrorCode.InvalidIntegratedEvidence, "Invalid file hash.");
            count++;
            string archived = Path.Combine(archive, hash);
            if (!File.Exists(archived) || Files.Hash(archived) != hash)
                issues.Add(new(ErrorCode.IntegratedArchiveInconsistent, "Missing or changed archived input: " + pin.Name));
        }
        if (count == 0) throw new InputException(ErrorCode.InvalidIntegratedEvidence, "Packet has no archived file pins.");
        if (current)
            foreach (JsonProperty pin in inputs.EnumerateObject().Where(p => FilePaths.IsAbsolute(p.Name)))
                if (!File.Exists(pin.Name) || Files.Hash(pin.Name) != pin.Value.GetString())
                    issues.Add(new(ErrorCode.IntegratedEvidenceStale, "Current input differs or is missing: " + pin.Name));
        return new(issues.Any(i => i.Code == ErrorCode.IntegratedArchiveInconsistent.Id()) ? "inconsistent" : "consistent", semantic,
            !current ? "not-checked" : issues.Any(i => i.Code == ErrorCode.IntegratedEvidenceStale.Id()) ? "stale" : "current",
            Text(result, "status"), issues, result.Clone());
    }

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out JsonElement property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static void RequireSchema(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("schema_version", out JsonElement schema) ||
            schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) ||
            version != Schema.Version || Text(result, "kind") != "integrated-result")
            throw new InputException(ErrorCode.InvalidIntegratedEvidence, "Expected current-schema integrated-result packet; unsupported schemas are not upgraded.");
    }

    public static List<Issue> Validate(JsonElement result)
    {
        List<Issue> issues = [];
        void Error(string message) => issues.Add(new(ErrorCode.IntegratedEvidenceInconsistent, message));
        try { RequireSchema(result); }
        catch (InputException error) { Error(error.Message); return issues; }
        string? Status(string name, params string[] allowed)
        {
            string? value = Text(result, name);
            if (value == null || !allowed.Contains(value)) Error("Invalid or missing " + name + ".");
            return value;
        }
        JsonElement Object(string name, JsonValueKind kind)
        {
            if (result.TryGetProperty(name, out JsonElement value) && value.ValueKind == kind) return value;
            Error("Invalid or missing " + name + ".");
            return default;
        }
        string? command = Status("command", "inspect-integrated", "verify-integrated", "test-integrated");
        string? status = Status("status", "ready", "passed", "rejected", "incomplete");
        string? linksStatus = Status("links_status", "passed", "incomplete");
        Status("coverage_status", "requires-review");
        string? proof = Status("proof_status", "not-run", "verified", "rejected", "incomplete");
        string? audit = Status("audit_status", "not-run", "passed", "findings", "incomplete");
        string? conformance = Status("conformance_status", "not-run", "conforms", "rejected", "incomplete");
        string? correspondence = Status("correspondence_status", "not-specified", "requires-review");
        string? provenance = Status("provenance_status", "consistent", "incomplete");
        JsonElement stages = Object("stages", JsonValueKind.Object);
        JsonElement generated = Object("generated_files", JsonValueKind.Object);
        JsonElement fingerprints = Object("fingerprints", JsonValueKind.Object);
        foreach (JsonElement pins in new[] { fingerprints, generated }.Where(p => p.ValueKind == JsonValueKind.Object))
            foreach (JsonProperty pin in pins.EnumerateObject())
                if (pin.Value.ValueKind != JsonValueKind.String ||
                    FilePaths.IsAbsolute(pin.Name) &&
                    (pin.Value.GetString()!.Length != 64 || pin.Value.GetString()!.Any(c => !char.IsAsciiHexDigit(c))))
                    Error("Malformed fingerprint: " + pin.Name + ".");
        JsonElement links = Object("links", JsonValueKind.Array);
        JsonElement claims = Object("correspondences", JsonValueKind.Array);
        HashSet<string> linked = [];
        if (links.ValueKind == JsonValueKind.Array)
            foreach (JsonElement link in links.EnumerateArray())
                if (Text(link, "id") is not string id || string.IsNullOrWhiteSpace(id) || !linked.Add(id))
                    Error("Link identifiers must be nonempty and unique.");
        if (linksStatus == "passed" && linked.Count == 0) Error("Passed links require resolved bindings.");
        if (claims.ValueKind == JsonValueKind.Array)
        {
            if (correspondence != (claims.GetArrayLength() == 0 ? "not-specified" : "requires-review"))
                Error("Correspondence status disagrees with recorded claims.");
            HashSet<string> ids = [];
            foreach (JsonElement claim in claims.EnumerateArray())
            {
                if (Text(claim, "status") != "requires-review" || Text(claim, "composition_status") != "not-proved")
                    Error("Correspondence claims must remain unproved review obligations.");
                if (claim.ValueKind != JsonValueKind.Object ||
                    !claim.TryGetProperty("claim", out JsonElement body) || body.ValueKind != JsonValueKind.Object)
                { Error("Missing correspondence claim."); continue; }
                if (Text(body, "id") is not string id || string.IsNullOrWhiteSpace(id) || !ids.Add(id) ||
                    Text(body, "dafny_binding") is not string binding || !linked.Contains(binding))
                    Error("Invalid correspondence identifiers or binding.");
            }
        }
        bool HasStage(string name, string state, int? code)
        {
            return stages.ValueKind == JsonValueKind.Object && stages.TryGetProperty(name, out JsonElement stage) &&
                Text(stage, "status") == state && stage.TryGetProperty("exit_code", out JsonElement exit) &&
                (code == null ? exit.ValueKind == JsonValueKind.Null :
                    exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out int actual) && actual == code);
        }
        void NeedStage(string name)
        {
            if (!HasStage(name, "passed", 0)) Error("Outcome requires a passed " + name + " stage.");
        }
        bool HasArtifact(string path) => generated.ValueKind == JsonValueKind.Object && generated.TryGetProperty(path, out _);
        void NeedArtifact(string name)
        {
            if (generated.ValueKind != JsonValueKind.Object ||
                !generated.EnumerateObject().Any(p => Path.GetFileName(p.Name) == name))
                Error("Missing stage artifact: " + name + ".");
        }
        if (stages.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty entry in stages.EnumerateObject())
            {
                string? state = Text(entry.Value, "status"), log = Text(entry.Value, "log");
                if (entry.Name is not ("dafny-version" or "dafny-bridge" or "dafny-discover" or "dafny-resolve" or
                    "dafny-verify" or "dafny-audit" or "case-preconditions" or "oracle-translate" or "oracle-compare"))
                    Error("Unexpected stage: " + entry.Name + ".");
                if (command == "inspect-integrated" && entry.Name is ("dafny-verify" or "dafny-audit" or
                    "case-preconditions" or "oracle-translate" or "oracle-compare") ||
                    command == "verify-integrated" && entry.Name is ("case-preconditions" or "oracle-translate" or "oracle-compare"))
                    Error("Stage cannot be produced by the recorded command: " + entry.Name + ".");
                bool exitValid = entry.Value.ValueKind == JsonValueKind.Object &&
                    entry.Value.TryGetProperty("exit_code", out JsonElement exit) &&
                    (state == "incomplete" ? exit.ValueKind == JsonValueKind.Null :
                        exit.ValueKind == JsonValueKind.Number && exit.TryGetInt32(out int code) &&
                        (state == "passed" ? code == 0 : state == "failed" && code != 0));
                if (!exitValid) Error("Stage status contradicts its native exit code: " + entry.Name + ".");
                if (log == null || !HasArtifact(log)) Error("Unpinned stage artifact: " + entry.Name + ".");
                if (entry.Name != "dafny-bridge")
                {
                    if (log != null && Path.GetFileName(log) != entry.Name + ".log") Error("Unexpected stage log: " + entry.Name + ".");
                    NeedArtifact(entry.Name + ".invocation.json");
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
        if (proof == "not-run" && status != "incomplete" &&
            stages.ValueKind == JsonValueKind.Object && stages.TryGetProperty("dafny-verify", out _))
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
        bool executionPresent = result.TryGetProperty("execution", out JsonElement execution) && execution.ValueKind != JsonValueKind.Null;
        if (conformance is "conforms" or "rejected")
        {
            if (command != "test-integrated" || proof != "verified" || audit != "passed")
                Error("Conformance requires a test command, verified proof and passed audit.");
            foreach (string name in new[] { "case-preconditions", "oracle-translate", "oracle-compare" }) NeedStage(name);
            foreach (string name in new[] { "Cases.dfy", "Oracle.cs", "Conformance.cs", "Conformance.dll",
                "Conformance.runtimeconfig.json", "case-results.json" }) NeedArtifact(name);
            if (!executionPresent || execution.ValueKind != JsonValueKind.Object) Error("Missing conformance execution receipt.");
            else ValidateExecution(execution, linked, conformance, generated, fingerprints, Error);
        }
        else if (executionPresent) Error("Execution receipt contradicts unexecuted conformance status.");
        if (status is "ready" or "passed" or "rejected")
        {
            if (linksStatus != "passed" || provenance != "consistent") Error("Completed root outcome requires links and provenance.");
            if (status == "ready" && command != "inspect-integrated") Error("Only inspection can be ready.");
            if (status == "passed" && (command == "inspect-integrated" || proof != "verified" || audit != "passed" ||
                command == "test-integrated" && conformance != "conforms"))
                Error("Passed root outcome contradicts required stages.");
            if (status == "rejected" && proof != "rejected" && audit != "findings" && conformance != "rejected")
                Error("Rejected root outcome has no rejecting evidence.");
        }
        return issues;
    }

    private static void ValidateExecution(JsonElement execution, HashSet<string> linked, string conformance,
        JsonElement generated, JsonElement fingerprints, Action<string> error)
    {
        HashSet<string> ReadIds(string name)
        {
            HashSet<string> ids = [];
            if (!execution.TryGetProperty(name, out JsonElement values) || values.ValueKind != JsonValueKind.Array)
            { error("Missing execution " + name + "."); return ids; }
            foreach (JsonElement value in values.EnumerateArray())
                if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || !ids.Add(value.GetString()!))
                    error("Invalid or duplicate execution " + name + ".");
            return ids;
        }
        HashSet<string> tested = ReadIds("tested_bindings"), untested = ReadIds("untested_bindings");
        if (tested.Overlaps(untested) || !tested.Concat(untested).ToHashSet().SetEquals(linked))
            error("Tested and untested identifiers must partition linked bindings.");
        HashSet<(string, string)> keys = [];
        HashSet<string> observed = [];
        bool mismatch = false;
        if (!execution.TryGetProperty("cases", out JsonElement cases) || cases.ValueKind != JsonValueKind.Array || cases.GetArrayLength() == 0)
            error("Conformance requires nonempty case results.");
        else
            foreach (JsonElement item in cases.EnumerateArray())
            {
                string? binding = Text(item, "binding"), id = Text(item, "id");
                if (binding == null || string.IsNullOrWhiteSpace(id) || !tested.Contains(binding) || !keys.Add((binding, id)))
                    error("Unknown or duplicate case result.");
                if (binding != null) observed.Add(binding);
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("expected", out JsonElement expected) || expected.ValueKind != JsonValueKind.Array ||
                    !item.TryGetProperty("actual", out JsonElement actual) || actual.ValueKind != JsonValueKind.Array ||
                    !item.TryGetProperty("matches", out JsonElement matches) || matches.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { error("Malformed case comparison."); continue; }
                bool strings = expected.EnumerateArray().Concat(actual.EnumerateArray()).All(v => v.ValueKind == JsonValueKind.String);
                bool equal = strings && expected.EnumerateArray().Select(v => v.GetString()).SequenceEqual(actual.EnumerateArray().Select(v => v.GetString()));
                if (!strings || expected.GetArrayLength() == 0 || expected.GetArrayLength() != actual.GetArrayLength() ||
                    matches.GetBoolean() != equal)
                    error("Case matches flag contradicts compared outputs.");
                mismatch |= !equal;
            }
        if (!observed.SetEquals(tested)) error("Tested identifiers disagree with case results.");
        if ((conformance == "rejected") != mismatch) error("Conformance status contradicts case results.");
        string? binary = Text(execution, "binary"), hash = Text(execution, "binary_sha256");
        if (binary == null || generated.ValueKind != JsonValueKind.Object || !generated.TryGetProperty(binary, out JsonElement pinned) ||
            pinned.ValueKind != JsonValueKind.String || pinned.GetString() != hash)
            error("Execution binary does not match its generated-file pin.");
        foreach (string field in new[] { "compile_inputs", "references" })
        {
            if (!execution.TryGetProperty(field, out JsonElement pins) || pins.ValueKind != JsonValueKind.Object ||
                !pins.EnumerateObject().Any())
            {
                error("Missing execution " + field + ".");
                continue;
            }
            foreach (JsonProperty pin in pins.EnumerateObject())
            {
                bool Pinned(JsonElement all) => all.ValueKind == JsonValueKind.Object &&
                    all.TryGetProperty(pin.Name, out JsonElement original) && original.ValueKind == JsonValueKind.String &&
                    pin.Value.ValueKind == JsonValueKind.String && original.GetString() == pin.Value.GetString();
                if (!Pinned(fingerprints) && (field == "references" || !Pinned(generated)))
                    error("Execution input is not pinned in the recorded closure: " + pin.Name + ".");
            }
        }
        string? comparison = Text(execution, "comparison_sha256");
        if (comparison == null || generated.ValueKind != JsonValueKind.Object ||
            !generated.EnumerateObject().Any(p => Path.GetFileName(p.Name) == "case-results.json" &&
                p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == comparison))
            error("Case comparison receipt does not match its generated-file pin.");
        if (!execution.TryGetProperty("compiler_context", out JsonElement context) || context.ValueKind != JsonValueKind.Object ||
            Text(context, "framework") != "net10.0" || Text(context, "language_version") == null ||
            Text(context, "nullable") == null || Text(context, "compiler") == null ||
            !context.TryGetProperty("defines", out JsonElement defines) || defines.ValueKind != JsonValueKind.Array ||
            defines.EnumerateArray().Any(d => d.ValueKind != JsonValueKind.String))
            error("Missing or malformed execution compiler context.");
    }
}
