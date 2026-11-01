namespace TlaLink;

internal static class WorkflowInputRoles
{
    internal static HashSet<string> ReviewOnly(string path, AnalysisProject project, AcceptancePolicy policy,
        WorkflowRun run, string directory, IReadOnlyDictionary<string, IAnalysisResult> evidence,
        List<SelectedMethodSummary> summaries, HashSet<string>? policyInputs = null)
    {
        HashSet<string> result = new(FilePaths.Comparer);
        if (run.ExecutionStatus != "completed" || evidence.Count != run.Stages.Count) return result;
        result.UnionWith(policyInputs ?? []);
        HashSet<string> reviewIds = policy.Requirements.Where(r => r.Scenario == run.Scenario)
            .SelectMany(r => r.Reviews ?? []).Concat(summaries.Select(s => s.Summary.Inspection))
            .ToHashSet(StringComparer.Ordinal);
        foreach (ReviewObligation review in project.Obligations.Where(o => reviewIds.Contains(o.Id)))
            foreach (string input in review.Inputs) result.Add(ProjectInputs.Resolve(path, input));

        HashSet<string> execution = new(FilePaths.Comparer) {
            path, ProjectInputs.Resolve(path, project.Requirements)
        };
        execution.UnionWith(project.Authoring!.Fingerprints.Keys);
        execution.ExceptWith(policyInputs ?? []);
        execution.UnionWith(project.Compiler.References.Select(p => ProjectInputs.Resolve(path, p)));
        AnalysisScenario scenario = project.Scenarios.Single(s => s.Id == run.Scenario);
        foreach (AnalysisStage stage in scenario.Stages)
        {
            foreach (string input in (stage.Sources ?? []).Concat(stage.Models ?? [])
                .Concat(new[] { stage.Config, stage.Bindings, stage.Scope }.OfType<string>()))
                execution.Add(ProjectInputs.Resolve(path, input));
        }
        foreach (SelectedMethodSummary summary in summaries)
        {
            execution.Add(summary.Scope);
            execution.UnionWith(summary.Summary.Sources.Select(p => ProjectInputs.Resolve(summary.Scope, p)));
            execution.UnionWith(CorrespondenceInputs.Target(summary.Configuration,
                Markup.Name(summary.Summary.Binding).Alias, "tla").Inputs);
        }
        foreach (WorkflowStageResult stage in run.Stages)
        {
            if (evidence[stage.Id] is IInputEvidence captured && captured.Fingerprints.Count > 0)
            {
                execution.UnionWith(captured.Fingerprints.Keys.Where(FilePaths.IsAbsolute)
                    .Select(p => CurrentInput(path, run, p)));
                continue;
            }
            if (evidence[stage.Id] is not TlcResult || stage.StoredResults is not { Count: > 0 } stored)
                return new(FilePaths.Comparer);
            foreach (StoredStageResult reference in stored)
            {
                string entryPath = ProjectAcceptance.SafePath(directory,
                    Path.Combine(stage.Id, reference.Directory, "entry.json"));
                TlcStoredResult entry = ProjectInputs.Read<TlcStoredResult>(entryPath);
                execution.UnionWith(entry.Material.Inputs.Select(p => CurrentInput(path, run, p.Path)));
                execution.Add(CurrentInput(path, run, entry.Material.Model));
                execution.Add(CurrentInput(path, run, entry.Material.Config));
                execution.Add(CurrentInput(path, run, entry.Material.Jar));
            }
        }
        result.ExceptWith(execution);
        return result;
    }

    internal static string CurrentInput(string path, WorkflowRun run, string input)
    {
        if (!FilePaths.IsNativeAbsolute(input)) return input;
        string relative = Path.GetRelativePath(run.ProjectDirectory, input);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? ProjectInputs.Resolve(path, relative) : input;
    }
}
