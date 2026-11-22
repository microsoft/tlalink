namespace TlaLink;

/// <summary>
/// Project integration of synchronization regions: which scopes a selection analyzes, environment evidence IDs,
/// and the preflight issues that block model stages over a failing region.
/// </summary>
internal static class SynchronizationWorkflow
{
    internal const string StageCapability = "Roslyn region checks over explicit files; no model checker; not alias, data-flow or runtime analysis";

    /// <summary>
    /// Region scopes of synchronization stages (role stage) and of TLC/trace stages that use a primitive they do not
    /// verify (role preflight). A primitive's own verification stages model its harness, not a region, and are not blocked.
    /// </summary>
    internal static List<(string Scope, string Role, List<string> Stages)> Scopes(string path, IEnumerable<AnalysisScenario> scenarios)
    {
        Dictionary<string, (string Role, List<string> Stages)> result = new(FilePaths.Comparer);
        Dictionary<string, CorrespondenceConfiguration> configs = new(FilePaths.Comparer);
        void Add(string scope, string role, string stage)
        {
            if (!result.TryGetValue(scope, out var entry)) result[scope] = entry = (role, []);
            if (role == "preflight") entry.Role = role;
            entry.Stages.Add(stage);
            result[scope] = entry;
        }
        foreach (AnalysisScenario scenario in scenarios)
            foreach (AnalysisStage stage in scenario.Stages)
            {
                if (stage.Kind == "synchronization") Add(ProjectInputs.Resolve(path, stage.Scope!), "stage", scenario.Id + "/" + stage.Id);
                foreach (PrimitiveUse use in stage.Primitives ?? [])
                {
                    string scope = ProjectInputs.Resolve(path, use.Scope);
                    if (!configs.TryGetValue(scope, out CorrespondenceConfiguration? config))
                        configs[scope] = config = CorrespondenceInputs.Load(scope, new(FilePaths.Comparer));
                    if (config.Synchronization?.Region == null) continue;
                    HashSet<string> verifying = (config.Synchronization.Primitives).Select(p => FilePaths.Full(p, Path.GetDirectoryName(scope)!))
                        .Select(p => Primitives.Load(p, new(FilePaths.Comparer)))
                        .Where(p => p.Verification.Scenarios.Contains(scenario.Id, StringComparer.Ordinal))
                        .Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
                    if (!use.Ids.All(verifying.Contains)) Add(scope, "preflight", scenario.Id + "/" + stage.Id);
                }
            }
        return result.Select(p => (p.Key, p.Value.Role, p.Value.Stages)).OrderBy(p => p.Key, FilePaths.Comparer).ToList();
    }

    /// <summary>Environment evidence must name existing scenarios or stages of the project.</summary>
    internal static void ValidateEvidence(string path, AnalysisProject project)
    {
        HashSet<string> ids = project.Scenarios.Select(s => s.Id).Concat(project.Scenarios.SelectMany(s => s.Stages).Select(s => s.Id))
            .ToHashSet(StringComparer.Ordinal);
        IEnumerable<string> scopes = project.Scenarios.SelectMany(s => s.Stages).SelectMany(s =>
            (s.Kind == "synchronization" ? [s.Scope!] : Array.Empty<string>()).Concat((s.Primitives ?? []).Select(u => u.Scope)))
            .Select(s => ProjectInputs.Resolve(path, s)).Distinct(FilePaths.Comparer);
        foreach (string scope in scopes)
            foreach (string file in CorrespondenceInputs.Load(scope, new(FilePaths.Comparer)).Synchronization?.Environments ?? [])
            {
                EnvironmentManifest environment = Environments.Load(FilePaths.Full(file, Path.GetDirectoryName(scope)!), new(FilePaths.Comparer));
                foreach (string evidence in environment.Evidence.Where(e => !ids.Contains(e)))
                    throw new InputException(ErrorCode.InvalidWorkflowInput,
                        "Environment " + environment.Id + " names evidence that is not a project scenario or stage: " + evidence);
            }
    }

    internal static bool NeedsJar(string scope, Dictionary<string, string> pins) =>
        (CorrespondenceInputs.Load(scope, pins).Synchronization?.Environments ?? [])
            .Any(file => Environments.Load(FilePaths.Full(file, Path.GetDirectoryName(scope)!), pins).Module != null);

    /// <summary>Failing preflight regions and failed primitive guarantee checks block runs and acceptance.</summary>
    internal static List<Issue> Blocking(IEnumerable<PlannedRegion> regions, IEnumerable<PrimitiveResolution> primitives) =>
        regions.Where(r => r.Role == "preflight" && r.Region.Status != "passed")
            .SelectMany(r => r.Region.Diagnostics.Count > 0 ? r.Region.Diagnostics
                : [new Issue(ErrorCode.SynchronizationRegionIncomplete, r.Region.Scope + ": region checks did not complete.")])
            .Select(i => i with { Message = "Failing region checks block model stages: " + i.Message })
            .Concat(primitives.SelectMany(p => p.Guarantees.Where(g => g.Status == "failed").SelectMany(g => g.Findings.Select(f =>
                new Issue(ErrorCode.GuaranteeCheckFailed, "Primitive " + p.Primitive.Id + " claims " + g.Guarantee + ": " + f)))))
            .ToList();
}
