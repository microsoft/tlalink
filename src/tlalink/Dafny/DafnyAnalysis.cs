using System.Text.Json;

namespace TlaLink;

public sealed record DafnyAnalysis(int SchemaVersion, string Kind, List<string> Proofs);
public sealed record DafnyResult(int SchemaVersion, string Kind, string Status, string Claim, string Config,
    string ConfigSha256, string Dafny, string Directory, int TimeoutSeconds, List<string> Roots, string ProofStatus,
    string AuditStatus, List<DafnyDeclaration> Inventory, Dictionary<string, StageResult> Stages,
    Dictionary<string, string> Fingerprints, Dictionary<string, string> Artifacts, List<Issue> Diagnostics, string Boundary)
    : IAnalysisResult, IArtifactEvidence;

public static class DafnyAnalysisBackend
{
    public static DafnyAnalysis Load(string path, Dictionary<string, string> pins)
    {
        path = FilePaths.Full(path);
        DafnyAnalysis analysis = JsonSerializer.Deserialize<DafnyAnalysis>(Files.ReadPinned(path, pins), ProjectInputs.Json)
            ?? throw new InputException(ErrorCode.InvalidIntegratedInput, "Empty Dafny analysis.");
        if (analysis.SchemaVersion != Schema.Version || analysis.Kind != "dafny-analysis" ||
            analysis.Proofs.Count is < 1 or > 1000 || analysis.Proofs.Any(string.IsNullOrWhiteSpace))
            throw new InputException(ErrorCode.InvalidIntegratedInput, "Expected schema-4 dafny-analysis with explicit proof files.");
        List<string> roots = analysis.Proofs.Select(p => ProjectInputs.Resolve(path, p)).ToList();
        if (roots.Distinct(FilePaths.Comparer).Count() != roots.Count ||
            roots.Any(p => !File.Exists(p) || Path.GetExtension(p) != ".dfy"))
            throw new InputException(ErrorCode.InvalidIntegratedInput, "Dafny roots must be distinct existing .dfy files, not directories.");
        foreach (string root in roots) Files.Pin(pins, root, Files.Hash(root));
        return analysis with { Proofs = roots };
    }

    public static int Run(CliOptions options)
    {
        if (options.Command == "read-dafny") return StageEvidence.Read<DafnyResult>(options, "dafny-result",
            (result, directory, _) => Validate(result, directory));
        string config = FilePaths.Full(options.Required("config")), executable = FilePaths.Full(options.Required("dafny"));
        string output = FilePaths.Full(options.Required("output"));
        if (File.Exists(output) || System.IO.Directory.Exists(output))
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Dafny output must be a new directory.");
        DafnyEvidenceState state = new();
        DafnyAnalysis analysis = Load(config, state.Fingerprints);
        Program.PinTool(state.Fingerprints);
        DafnyBackend.PinDistribution(executable, state.Fingerprints);
        System.IO.Directory.CreateDirectory(output);
        DafnyBackend backend = new(executable, output, options.Timeout, state);
        List<DafnyDeclaration> inventory = [];
        try
        {
            backend.Initialize();
            inventory = backend.Index(analysis.Proofs).Declarations;
            backend.Verify(analysis.Proofs);
            IntegratedInputs.CheckPins(state.Fingerprints);
            IntegratedInputs.CheckPins(state.GeneratedFiles);
        }
        catch (InputException error)
        {
            state.ProofStatus = "incomplete";
            state.Diagnostics.Add(new(error.Code ?? ErrorCode.IntegratedToolFailure, error.Message));
        }
        string status = state.ProofStatus == "verified" && state.AuditStatus == "passed" ? "passed" :
            state.ProofStatus == "rejected" ? "rejected" : "incomplete";
        if (state.Stages.TryGetValue("dafny-verify", out StageResult? proof) && state.ProofStatus == "rejected")
        {
            state.Diagnostics.RemoveAll(i => i.Is(ErrorCode.DafnyVerificationFailed));
            state.Diagnostics.AddRange(VerificationDiagnostics(Files.Read(proof.Log)));
        }
        Dictionary<string, StageResult> stages = state.Stages.ToDictionary(p => p.Key,
            p => p.Value with { Log = Path.GetRelativePath(output, p.Value.Log) }, StringComparer.Ordinal);
        DafnyResult result = new(Schema.Version, "dafny-result", status,
            status == "passed" ? "dafny-contracts" : "not-established", config, Files.Hash(config), executable,
            output, options.Timeout, analysis.Proofs, state.ProofStatus, state.AuditStatus, inventory, stages,
            state.Fingerprints, StageEvidence.Seal(output, state.Fingerprints), state.Diagnostics,
            "Native verification of the selected Dafny roots and transitive includes, with assumption audit; no C# equivalence, oracle execution or cross-model refinement.");
        Files.WriteNew(Path.Combine(output, "result.json"), result);
        ProjectWorkflow.Render(result, options);
        return status == "passed" ? 0 : status == "rejected" ? 1 : 2;
    }

    private static List<Issue> VerificationDiagnostics(string log)
    {
        List<Issue> diagnostics = [];
        foreach (string line in log.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            DafnyNativeMessage? message = JsonSerializer.Deserialize<DafnyNativeMessage>(line, DafnyNativeJson.Options);
            if (message is DafnyNativeDiagnostic { Value.Severity: 1 } diagnostic)
            {
                if (!diagnostic.Value.HasDefaultFormatMessage || diagnostic.Value.DefaultFormatMessage == null)
                    throw new InputException(ErrorCode.WorkflowStageInconsistent, "Native Dafny error diagnostic is missing its message.");
                diagnostics.Add(new(ErrorCode.DafnyVerificationFailed, diagnostic.Value.DefaultFormatMessage));
            }
        }
        return diagnostics;
    }

    public static List<Issue> Validate(JsonElement document, string directory)
    {
        Files.RequireSchema(document, "dafny-result");
        DafnyResult result = document.Deserialize<DafnyResult>(ProjectInputs.Json)!;
        return Validate(result, directory);
    }

    public static List<Issue> Validate(DafnyResult result, string directory)
    {
        if (result.SchemaVersion != Schema.Version || result.Kind != "dafny-result")
            throw new InputException(ErrorCode.WorkflowStageInconsistent, "Expected current dafny-result.");
        result = result with { Fingerprints = StageEvidence.PathPins(result.Fingerprints) };
        List<Issue> issues = [];
        void Error(string text) => issues.Add(new(ErrorCode.WorkflowStageInconsistent, text));
        string FileFor(string relative)
        {
            if (!result.Artifacts.ContainsKey(relative)) throw new InputException(ErrorCode.WorkflowProvenanceInconsistent, "Unpinned Dafny artifact: " + relative);
            return ProjectAcceptance.SafePath(directory, relative);
        }
        if (!result.Fingerprints.TryGetValue(result.Config, out string? configHash) || configHash != result.ConfigSha256 ||
            !result.Fingerprints.ContainsKey(result.Dafny))
            Error("Dafny config/tool are absent from the input closure.");
        else
        {
            DafnyAnalysis analysis = ProjectInputs.Read<DafnyAnalysis>(StageEvidence.Archived(directory, configHash));
            if (analysis.Kind != "dafny-analysis" || analysis.SchemaVersion != Schema.Version ||
                analysis.Proofs.Count is < 1 or > 1000 || analysis.Proofs.Any(string.IsNullOrWhiteSpace) ||
                !analysis.Proofs.Select(p => ProjectInputs.Resolve(result.Config, p)).SequenceEqual(result.Roots, FilePaths.Comparer) ||
                result.Roots.Any(p => !result.Fingerprints.ContainsKey(p)))
                Error("Dafny proof roots differ from the archived analysis manifest.");
        }
        string[] names = ["dafny-version", "dafny-bridge", "dafny-discover", "dafny-resolve", "dafny-verify", "dafny-audit"];
        foreach (var stage in result.Stages)
        {
            if (!names.Contains(stage.Key)) Error("Unexpected Dafny stage: " + stage.Key);
            if (stage.Value.Status is not ("passed" or "failed" or "incomplete") ||
                stage.Value.Status == "passed" && stage.Value.ExitCode != 0 ||
                stage.Value.Status == "failed" && stage.Value.ExitCode is null or 0 ||
                stage.Value.Status == "incomplete" && stage.Value.ExitCode != null)
                Error("Dafny process status contradicts its exit code.");
            _ = FileFor(stage.Value.Log);
            if (stage.Key == "dafny-bridge") continue;
            NativeProcessInvocation invocation = ProjectInputs.Read<NativeProcessInvocation>(FileFor(stage.Key + ".invocation.json"));
            if (invocation.SchemaVersion != Schema.Version || invocation.Kind != "process-invocation")
                throw new InputException(ErrorCode.WorkflowStageInconsistent, "Expected current process-invocation.");
            if (invocation.TimeoutSeconds != result.TimeoutSeconds ||
                invocation.WorkingDirectory != result.Directory)
                Error("Dafny stage invocation has a different deadline/directory.");
            string[]? expected = stage.Key switch {
                "dafny-version" => ["--version"],
                "dafny-discover" or "dafny-resolve" => [Path.Combine(result.Directory, "DafnyIndex.dll"),
                    Path.GetDirectoryName(result.Dafny)!, Path.Combine(result.Directory, stage.Key + ".json"), .. result.Roots],
                _ => null };
            if (expected != null && (!invocation.Arguments.SequenceEqual(expected) ||
                stage.Key == "dafny-version" && invocation.Executable != result.Dafny))
                Error("Dafny version/frontend invocation differs from the selected distribution/roots.");
        }
        string proofStatus = "not-run", auditStatus = "not-run";
        if (result.Stages.TryGetValue("dafny-verify", out StageResult? proof))
        {
            string log = Files.Read(FileFor(proof.Log));
            proofStatus = DafnyBackend.ClassifyVerification(proof, log);
            NativeProcessInvocation invocation = ProjectInputs.Read<NativeProcessInvocation>(FileFor("dafny-verify.invocation.json"));
            string[] expected = ["verify", .. result.Roots, "--verify-included-files", "--json-output", "--show-snippets=false",
                "--verification-time-limit", result.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)];
            if (invocation.Executable != result.Dafny ||
                !invocation.Arguments.SequenceEqual(expected))
                Error("Dafny verification invocation differs from the selected obligation.");
            if (proofStatus == "rejected" && !VerificationDiagnostics(log).SequenceEqual(result.Diagnostics.Where(d => d.Is(ErrorCode.DafnyVerificationFailed))))
                Error("Dafny rejection diagnostics differ from the native verifier output.");
        }
        if (result.Stages.TryGetValue("dafny-audit", out StageResult? audit))
        {
            auditStatus = audit.Status != "passed" || audit.ExitCode != 0 || !result.Artifacts.ContainsKey("dafny-audit.txt") ? "incomplete" :
                string.IsNullOrWhiteSpace(Files.Read(FileFor("dafny-audit.txt"))) ? "passed" : "findings";
            DafnyIndexResult index = ProjectInputs.Read<DafnyIndexResult>(FileFor("dafny-resolve.json"));
            string[] expected = ["audit", .. index.Dependencies.Concat(result.Roots).Distinct(FilePaths.Comparer),
                "--report-file", Path.Combine(result.Directory, "dafny-audit.txt"), "--report-format", "txt"];
            NativeProcessInvocation invocation = ProjectInputs.Read<NativeProcessInvocation>(FileFor("dafny-audit.invocation.json"));
            if (invocation.Executable != result.Dafny ||
                !invocation.Arguments.SequenceEqual(expected))
                Error("Dafny assumption audit invocation differs from the resolved proof closure.");
        }
        string expectedStatus = proofStatus == "verified" && auditStatus == "passed" ? "passed" :
            proofStatus == "rejected" && auditStatus == "not-run" ? "rejected" : "incomplete";
        if (result.Status != expectedStatus || result.AuditStatus != auditStatus ||
            result.ProofStatus != proofStatus && !(proofStatus == "not-run" && result.ProofStatus == "incomplete" && result.Status == "incomplete"))
            Error("Dafny outcome contradicts native verification/audit.");
        if (result.Status is "passed" or "rejected")
        {
            foreach (string name in names.Take(4))
                if (!result.Stages.TryGetValue(name, out StageResult? stage) || stage.Status != "passed" || stage.ExitCode != 0)
                    Error("Completed Dafny evidence requires a passed " + name + ".");
            DafnyIndexResult index = ProjectInputs.Read<DafnyIndexResult>(FileFor("dafny-resolve.json"));
            if (index.Dependencies.Any(p => !result.Fingerprints.ContainsKey(FilePaths.Full(p))) ||
                Schema.Hash(result.Inventory) != Schema.Hash(index.Declarations) ||
                !Files.Read(FileFor("dafny-version.log")).Trim().StartsWith("4.11.0+", StringComparison.Ordinal))
                Error("Dafny inventory, include closure or selected native version differs from frontend observations.");
        }
        if (result.Claim != (result.Status == "passed" ? "dafny-contracts" : "not-established"))
            Error("Dafny claim contradicts its outcome.");
        return issues;
    }
}
