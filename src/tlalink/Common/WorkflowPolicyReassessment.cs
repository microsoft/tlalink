using System.Text.Json;

namespace TlaLink;

public sealed record WorkflowPolicyIdentity(string? ProjectSha256, string? RequirementsSha256, string? AuthoringSha256);
public sealed record WorkflowPolicyReassessment(string Status, WorkflowPolicyIdentity Recorded,
    WorkflowPolicyIdentity Current, bool? ExecutionDefinitionUnchanged, bool? CheckerExpectationsUnchanged,
    List<string> ChangedAreas, string Boundary);

internal static class WorkflowPolicyComparison
{
    internal sealed record Result(WorkflowPolicyReassessment Report, HashSet<string> ReviewInputs);

    internal static Result Compare(string path, AnalysisProject project, AcceptancePolicy policy,
        WorkflowRun run, string directory, AnalysisProject? archived)
    {
        Dictionary<string, string> currentAuthoring = new(project.Authoring!.Fingerprints, FilePaths.Comparer);
        foreach (var pin in currentAuthoring)
            if (!File.Exists(pin.Key) || Files.Hash(pin.Key) != pin.Value)
                throw new InputException(ErrorCode.WorkflowEvidenceStale, "Current project authoring changed after loading: " + pin.Key);
        string policyPath = ProjectInputs.Resolve(path, project.Requirements);
        Dictionary<string, string> policyPins = new(FilePaths.Comparer);
        AcceptancePolicy currentPolicy;
        try
        {
            currentPolicy = JsonSerializer.Deserialize<AcceptancePolicy>(Files.ReadPinned(policyPath, policyPins), ProjectInputs.Json)
                ?? throw new InputException(ErrorCode.WorkflowEvidenceStale, "Current acceptance policy is empty.");
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InputException(ErrorCode.WorkflowEvidenceStale, "Current acceptance policy could not be reread: " + error.Message);
        }
        if (Schema.Hash(currentPolicy) != Schema.Hash(policy))
            throw new InputException(ErrorCode.WorkflowEvidenceStale, "Current acceptance policy changed after loading: " + policyPath);
        Dictionary<string, string>? recordedAuthoring = archived?.Authoring?.Fingerprints;
        WorkflowPolicyIdentity recorded = new(run.ProjectSha256, run.RequirementsSha256,
            recordedAuthoring == null ? null : Schema.Hash(recordedAuthoring));
        WorkflowPolicyIdentity current = new(currentAuthoring[path], policyPins[policyPath],
            Schema.Hash(currentAuthoring));
        List<string> changes = [];
        HashSet<string> reviewInputs = new(FilePaths.Comparer);
        WorkflowPolicyReassessment Report(string status, bool? execution, bool? expectations) =>
            new(status, recorded, current, execution, expectations, changes,
                "Compares the run's selected archived scenario, shared execution settings and non-review requirements with the explicitly authored current policy. " +
                "Archive integrity, actual execution inputs, evidence and current review decisions are checked independently; " +
                "no run rewriting, execution cache or approval of policy edits is implied.");
        if (run.Project != project.Id)
        {
            changes.Add("project-identity");
            return new(Report("incompatible", false, null), reviewInputs);
        }
        bool authoringChanged = recordedAuthoring != null && !SameAuthoring(path, run, recordedAuthoring, currentAuthoring);
        bool changed = recorded.ProjectSha256 != current.ProjectSha256 ||
            recorded.RequirementsSha256 != current.RequirementsSha256 || authoringChanged;
        if (!changed) return new(Report("unchanged", true, true), reviewInputs);
        if (archived == null)
        {
            changes.Add("archived-project-unavailable");
            return new(Report("unavailable", null, null), reviewInputs);
        }

        string recordedPolicy = ProjectInputs.Resolve(
            FilePaths.Full(Path.GetFileName(path), run.ProjectDirectory), archived.Requirements);
        if (!run.Fingerprints.TryGetValue(recordedPolicy, out string? hash) || hash != run.RequirementsSha256)
            throw new InputException(ErrorCode.WorkflowProvenanceInconsistent, "Archived acceptance policy contradicts its recorded hash.");
        string copy = ProjectAcceptance.SafePath(directory, Path.Combine("inputs", hash));
        if (!File.Exists(copy) || Files.Hash(copy) != hash)
            throw new InputException(ErrorCode.WorkflowProvenanceInconsistent, "Missing/changed archived acceptance policy.");
        AcceptancePolicy previous = ProjectInputs.Read<AcceptancePolicy>(copy);
        if (previous.SchemaVersion != Schema.Version || previous.Kind != "acceptance-policy" || previous.Project != run.Project)
            throw new InputException(ErrorCode.WorkflowProvenanceInconsistent, "Archived acceptance policy identity contradicts the run.");

        bool executionSame = Schema.Hash(Execution(archived, run.Scenario)) == Schema.Hash(Execution(project, run.Scenario));
        bool expectationsSame = Schema.Hash(Expectations(previous, run.Scenario)) == Schema.Hash(Expectations(policy, run.Scenario));
        if (!executionSame) changes.Add("execution-definition");
        if (!expectationsSame) changes.Add("checker-expectations");
        if (Schema.Hash(archived.Obligations) != Schema.Hash(project.Obligations)) changes.Add("review-obligations");
        if (Schema.Hash(previous.Requirements.Select(r => new RequirementReviews(r.Id, r.Reviews)).ToList()) !=
            Schema.Hash(policy.Requirements.Select(r => new RequirementReviews(r.Id, r.Reviews)).ToList()))
            changes.Add("requirement-reviews");
        if (archived.ReviewDirectory != project.ReviewDirectory) changes.Add("review-directory");
        if (archived.Requirements != project.Requirements) changes.Add("requirements-path");
        if (changes.Count == 0) changes.Add("authoring-bytes");
        if (!executionSame || !expectationsSame)
            return new(Report("incompatible", executionSame, expectationsSame), reviewInputs);

        bool otherScenariosChanged = Schema.Hash(Execution(archived)) != Schema.Hash(Execution(project)) ||
            Schema.Hash(Expectations(previous)) != Schema.Hash(Expectations(policy));
        if (otherScenariosChanged) changes.Add("other-scenarios");
        reviewInputs.UnionWith(recordedAuthoring!.Keys.Select(p => WorkflowInputRoles.CurrentInput(path, run, p)));
        reviewInputs.Add(WorkflowInputRoles.CurrentInput(path, run, recordedPolicy));
        reviewInputs.UnionWith(archived.Obligations.SelectMany(o => o.Inputs).Select(p => ProjectInputs.Resolve(path, p)));
        return new(Report(otherScenariosChanged ? "scenario-equivalent" : "review-only", true, true), reviewInputs);
    }

    private sealed record RequirementReviews(string Id, List<string>? Reviews);

    private static AnalysisProject Execution(AnalysisProject project, string? scenario = null) =>
        project with {
            Obligations = [], ReviewDirectory = "", Requirements = "",
            Scenarios = scenario == null ? project.Scenarios : project.Scenarios.Where(s => s.Id == scenario).ToList(),
            CheckObligations = scenario == null ? project.CheckObligations :
                (project.CheckObligations ?? []).Where(o => o.Scenario == scenario).ToList()
        };

    private static AcceptancePolicy Expectations(AcceptancePolicy policy, string? scenario = null) =>
        policy with { Requirements = policy.Requirements.Where(r => scenario == null || r.Scenario == scenario)
            .Select(r => r with { Reviews = null }).ToList() };

    private static bool SameAuthoring(string path, WorkflowRun run, Dictionary<string, string> recorded,
        Dictionary<string, string> current)
    {
        if (recorded.Count != current.Count) return false;
        foreach (var pin in recorded)
            if (!current.TryGetValue(WorkflowInputRoles.CurrentInput(path, run, pin.Key), out string? hash) || hash != pin.Value)
                return false;
        return true;
    }
}
