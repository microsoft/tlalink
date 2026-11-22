using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace TlaLink;

/// <summary>A fairness entry of a liveness stage: a kind-module fairness formula of a kind instance, or author text.</summary>
public sealed record FairnessAssumption(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? KindInstance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Operator = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Assumption = null);
public sealed record EnvironmentAssumption(string Scope, string Id, string EnvironmentKind, List<string> Obligations,
    List<string> LockSafe, List<string> Evidence, string Support);
/// <summary>
/// What a TLC/trace stage's result is conditional on: the environment obligations of the regions whose primitives it uses
/// and, when its configuration checks temporal properties, the declared fairness assumptions.
/// </summary>
public sealed record StageAssumptions(string Scenario, string Stage, string Kind, List<string> TemporalProperties,
    bool Symmetry, List<FairnessAssumption> Fairness, List<EnvironmentAssumption> Environments, List<string> Gaps,
    string Boundary);

/// <summary>Resolves and checks the assumptions of model stages during preflight (SR112..SR115).</summary>
internal static class ModelAssumptions
{
    internal const string Boundary = "Assumptions, not checked facts. A kind-module fairness entry is checked to be referenced by " +
        "the specification formula the configuration selects, not to be sufficient or realistic; author fairness entries and " +
        "environment obligations are trusted statements. Temporal results hold only under all of them.";

    /// <summary>Structural rules checked at project load; configuration-dependent rules are preflight diagnostics.</summary>
    internal static void Validate(AnalysisStage stage)
    {
        if (stage.Fairness == null) return;
        static bool Name(string? value) => value != null && Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$");
        if (stage.Kind is not ("tlc" or "trace") || stage.Fairness.Count is < 1 or > 100 || stage.Fairness.Any(f => f == null ||
                (f.Assumption == null ? !Name(f.KindInstance) || !Name(f.Operator) :
                    string.IsNullOrWhiteSpace(f.Assumption) || f.KindInstance != null || f.Operator != null)) ||
            stage.Fairness.Distinct().Count() != stage.Fairness.Count)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "fairness is 1..100 unique entries on TLC/trace stages, each either " +
                "{kind_instance, operator} naming a kind-module fairness formula or {assumption} with nonempty text.");
    }

    internal static (StageAssumptions Assumptions, List<Issue> Issues) Analyze(string path, AnalysisProject project, string scenario,
        AnalysisStage stage, Func<TlaDocument> model, IEnumerable<PlannedRegion> regions)
    {
        string configPath = ProjectInputs.Resolve(path, stage.Config!);
        TlcConfiguration config = TlcConfiguration.Read(configPath);
        List<Issue> issues = [];
        string label = "Stage " + scenario + "/" + stage.Id + " (" + Path.GetFileName(configPath) + "): ";
        void Report(ErrorCode code, string message) => issues.Add(new(code, label + message));
        string properties = string.Join(", ", config.Properties);
        if (config.Temporal && stage.Fairness == null)
            Report(ErrorCode.MissingFairness, "checks temporal properties " + properties + " but declares no fairness; list what the " +
                "liveness result assumes, as kind-module fairness formulas or author assumptions.");
        if (!config.Temporal && stage.Fairness != null)
            Report(ErrorCode.UnusedFairness, "declares fairness but its configuration checks no temporal properties (PROPERTY or PROPERTIES).");
        if (stage.Kind == "tlc" && config.Temporal && config.Symmetry)
            Report(ErrorCode.SymmetryWithLiveness, "declares SYMMETRY and checks temporal properties " + properties + "; TLC symmetry " +
                "reduction is unsound for liveness, so check them in a stage without SYMMETRY.");
        List<FairnessAssumption> kinds = (stage.Fairness ?? []).Where(f => f.KindInstance != null).ToList();
        if (config.Temporal && kinds.Count > 0)
        {
            TlaDocument document = model();
            HashSet<string> modules = Primitives.Select(path, project, [stage]).Select(p => Path.GetFileNameWithoutExtension(p.Module))
                .ToHashSet(StringComparer.Ordinal);
            foreach (FairnessAssumption fairness in kinds)
            {
                string formula = fairness.KindInstance + "!" + fairness.Operator;
                if (document.Instances.SingleOrDefault(i => i.Name == fairness.KindInstance) is not { } instance)
                    Report(ErrorCode.UnresolvedFairness, "fairness " + formula + ": " + fairness.KindInstance +
                        " is not an INSTANCE definition of " + document.Module + ".");
                else if (!modules.Contains(instance.Module))
                    Report(ErrorCode.UnresolvedFairness, "fairness " + formula + ": " + fairness.KindInstance + " instantiates " +
                        instance.Module + ", not the kind module of a primitive this stage uses.");
                else if (config.Specification == null)
                    Report(ErrorCode.UnresolvedFairness, "fairness " + formula + " requires a SPECIFICATION formula that conjoins it; " +
                        "the configuration has none.");
                else if (!SynchronizationRegion.KindReferences(document, config.Specification, fairness.KindInstance!, modules)
                    .Contains(fairness.Operator!))
                    Report(ErrorCode.UnresolvedFairness, "fairness " + formula + " is not referenced by the specification formula " +
                        config.Specification + ".");
            }
        }
        string key = scenario + "/" + stage.Id;
        List<EnvironmentAssumption> environments = regions.Where(r => r.Stages.Contains(key, StringComparer.Ordinal))
            .SelectMany(r => r.Region.Environments.Select(e => new EnvironmentAssumption(r.Region.Scope, e.Id, e.EnvironmentKind,
                e.Obligations, e.LockSafe, e.Evidence, e.Support))).ToList();
        List<string> gaps = environments.Where(e => e.Support == "unsupported-assumption")
            .Select(e => "Environment " + e.Id + " obligations have no declared supporting evidence.").ToList();
        if (stage.Kind == "trace" && config.Temporal)
            gaps.Add("Trace validation does not check the configuration's temporal properties; its fairness entries describe the model only.");
        return (new(scenario, stage.Id, stage.Kind, config.Properties, config.Symmetry, stage.Fairness ?? [], environments, gaps, Boundary),
            issues);
    }

    /// <summary>Report assumption lines: environment obligations, lock_safe operations, fairness, and the conditional claim.</summary>
    internal static List<string> Lines(StageAssumptions assumptions)
    {
        List<string> lines = assumptions.Environments.SelectMany(e => e.Obligations.Select(o => "Environment " + e.Id + " (" +
                e.EnvironmentKind + "): " + o).Concat(e.LockSafe.Select(m => "Environment " + e.Id + " operation " + m +
                " is lock_safe: it does not block, take region locks or run region code inline."))).ToList();
        lines.AddRange(assumptions.Fairness.Select(f => f.Assumption == null
            ? "Fairness: " + f.KindInstance + "!" + f.Operator + " (kind-module fairness of instance " + f.KindInstance + ")."
            : "Fairness (author): " + f.Assumption));
        if (assumptions.TemporalProperties.Count > 0)
            lines.Add("Temporal properties " + string.Join(", ", assumptions.TemporalProperties) +
                " are conditional on the fairness assumptions and environment obligations listed here.");
        return lines;
    }
}
