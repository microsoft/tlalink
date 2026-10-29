using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TlaLink;

public sealed record TlcStatistics(long? StatesGenerated, long? DistinctStates, long? StatesLeft,
    long? SearchDepth, string? OptimisticFingerprintOmissionEstimate);

public static partial class ProjectWorkflow
{
    internal sealed record TlcStageRun(TlcResult Result, List<StoredStageResult> Stored, TlcResources? Requested, TlcResources Effective);

    private static (string Model, string Config, string Jar, TlaDocument Document) PrepareTlc(string path, AnalysisProject project,
        AnalysisStage stage, Dictionary<string, string> pins, IReadOnlyDictionary<string, TlaDocument>? parsed)
    {
        string model = ProjectInputs.Resolve(path, stage.Models![0]);
        string jar = ProjectInputs.Resolve(path, project.Tools.Jar!.Path);
        TlaDocument document = parsed != null && parsed.TryGetValue(model, out TlaDocument? known) ? known
            : TlaBackend.Parse(model, jar, stage.TimeoutSeconds);
        foreach (var pin in document.DependencyFingerprints) Files.Pin(pins, pin.Key, pin.Value);
        RequireTlcModel(document);
        // A preflight parse is reused only while its model closure still has the parsed bytes.
        foreach (var pin in document.DependencyFingerprints.Where(p => !p.Key.StartsWith("library:", StringComparison.Ordinal) &&
            p.Key.EndsWith(".tla", StringComparison.OrdinalIgnoreCase)))
            if (Files.Hash(pin.Key) != pin.Value)
                throw new InputException(ErrorCode.WorkflowInputChanged, "Model input changed after preflight: " + pin.Key);
        return (model, ProjectInputs.Resolve(path, stage.Config!), jar, document);
    }

    private static TlcStageRun TlcStage(string path, AnalysisProject project, AnalysisStage stage, string output,
        Dictionary<string, string> pins, bool reuse, IReadOnlyDictionary<string, TlaDocument>? parsed)
    {
        var (model, config, jar, document) = PrepareTlc(path, project, stage, pins, parsed);
        int fingerprint = stage.Fingerprint ?? 0;
        TlcResultKey key = TlcResultStore.Key(model, config, jar, document.DependencyFingerprints, fingerprint);
        string? store = project.ResultStore == null ? null : ProjectInputs.Resolve(path, project.ResultStore);
        List<(TlcStoredResult Entry, string Directory)> entries = store != null && reuse ? TlcResultStore.Find(store, key) : [];
        List<StoredStageResult> stored = [];
        void Attach(TlcStoredResult entry, string source, string origin, bool primary)
        {
            string relative = TlcResultStore.StoredDirectory(entry.Material.Fingerprint);
            TlcResultStore.Copy(source, Path.Combine(output, relative));
            stored.Add(new(entry.Material.Fingerprint, entry.Key, entry.Family, entry.Outcome, origin, primary, relative,
                Files.Hash(Path.Combine(output, relative, "entry.json"))));
        }
        // An exhaustive counterexample is definitive under any fingerprint function, so it takes precedence.
        var primary = entries.Where(e => e.Entry.Outcome == "rejected")
            .OrderBy(e => e.Entry.Material.Fingerprint != fingerprint).ThenBy(e => e.Entry.Material.Fingerprint).FirstOrDefault();
        if (primary.Entry == null) primary = entries.FirstOrDefault(e => e.Entry.Material.Fingerprint == fingerprint);
        TlcResult result;
        TlcResources? requested;
        TlcResources effective;
        if (primary.Entry != null)
        {
            foreach (string file in new[] { "tlc.log", "tlc.invocation.json" })
                File.Copy(Path.Combine(primary.Directory, file), Path.Combine(output, file));
            result = ProjectInputs.Read<TlcResult>(Path.Combine(primary.Directory, "result.json"));
            requested = primary.Entry.RequestedResources;
            effective = primary.Entry.Resources;
            Attach(primary.Entry, primary.Directory, "reused", true);
        }
        else
        {
            Stopwatch clock = Stopwatch.StartNew();
            result = ExecuteTlc(model, config, jar, stage, output, fingerprint);
            requested = stage.Resources;
            effective = TlcResources.Effective(stage.Kind, stage.Resources);
            if (store != null && TlcResultStore.Store(store, key, stage.Id, "run", output, result, requested, effective,
                stage.TimeoutSeconds, clock.Elapsed.TotalSeconds) is string created)
                Attach(ProjectInputs.Read<TlcStoredResult>(Path.Combine(created, "entry.json")), created, "executed", true);
        }
        if (Outcome("tlc", result) == "passed")
            foreach (var other in entries.Where(e => e.Entry.Outcome == "passed" &&
                e.Entry.Material.Fingerprint != result.Fingerprint))
                Attach(other.Entry, other.Directory, "reused", false);
        return new(result, stored, requested, effective);
    }

    private static TlcResult ExecuteTlc(string model, string config, string jar, AnalysisStage stage, string output, int fingerprint)
    {
        TlcResources resources = TlcResources.Effective(stage.Kind, stage.Resources);
        var result = Invoke(Executable("java"), [resources.HeapArgument, "-cp", jar, "tlc2.TLC", .. TlcResultStore.Options,
            "-fp", fingerprint.ToString(System.Globalization.CultureInfo.InvariantCulture), "-workers", resources.WorkerArgument,
            "-metadir", Path.Combine(output, "states"), "-config", config, model],
            output, stage.TimeoutSeconds, "tlc", allowFailure: true);
        List<Issue> diagnostics = TlcDiagnostics(result.Text);
        List<string> codes = diagnostics.Select(d => d.Code).ToList();
        bool rejected = result.ExitCode != 0 && codes.Any(c => c is "TLC2110" or "TLC2111" or "TLC2114" or "TLC2116" or "TLC2120");
        return new(Schema.Version, "tlc-result",
            result.ExitCode == 0 ? "passed" : rejected ? "rejected" : "incomplete", diagnostics,
            model, config, result.ExitCode, fingerprint, stage.Resources, resources,
            ProjectInputs.Read<NativeProcessInvocation>(Path.Combine(output, "tlc.invocation.json")),
            ReadTlcStatistics(result.Text),
            ["Only the configured finite model; source correspondence and fingerprint collision risk require review."]);
    }

    private static List<StoredOutcome>? StoredOutcomes(string path, AnalysisProject project, AnalysisStage stage, IReadOnlyDictionary<string, TlaDocument> parsed)
    {
        if (project.ResultStore == null) return null;
        string model = ProjectInputs.Resolve(path, stage.Models![0]);
        if (!parsed.TryGetValue(model, out TlaDocument? document)) return null;
        TlcResultKey key = TlcResultStore.Key(model, ProjectInputs.Resolve(path, stage.Config!),
            ProjectInputs.Resolve(path, project.Tools.Jar!.Path), document.DependencyFingerprints, stage.Fingerprint ?? 0);
        return TlcResultStore.Find(ProjectInputs.Resolve(path, project.ResultStore), key)
            .Select(e => new StoredOutcome(e.Entry.Material.Fingerprint, e.Entry.Outcome, e.Entry.DurationSeconds, e.Entry.StoredAt)).ToList();
    }

    /// <summary>Runs one TLC stage with an explicit budget directly into the declared result store.</summary>
    private static int CheckStage(string path, AnalysisProject project, AcceptancePolicy policy, CliOptions options)
    {
        string id = options.Required("stage");
        if (project.ResultStore == null)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "check-stage requires the project to declare result_store.");
        List<(AnalysisScenario Scenario, AnalysisStage Stage)> matches = project.Scenarios
            .Where(s => options.Optional("scenario") is not string selected || s.Id == selected)
            .SelectMany(s => s.Stages.Where(stage => stage.Id == id).Select(stage => (s, stage))).ToList();
        if (matches.Count == 0) throw new InputException(ErrorCode.InvalidWorkflowInput, "Unknown stage: " + id);
        if (matches.Select(m => Schema.Hash(m.Stage)).Distinct().Count() > 1)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "Stage declarations differ between scenarios; select --scenario.");
        var (scenario, declared) = matches[0];
        if (declared.Kind != "tlc") throw new InputException(ErrorCode.InvalidWorkflowInput, "check-stage supports tlc stages.");
        int fingerprint = declared.Fingerprint ?? 0;
        if (options.Optional("fingerprint") is string requested &&
            (!int.TryParse(requested, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out fingerprint) ||
             fingerprint > TlcResultStore.MaxFingerprint))
            throw new InputException(ErrorCode.InvalidWorkflowInput, "--fingerprint must be 0.." + TlcResultStore.MaxFingerprint + ".");
        TlcResources? resources = declared.Resources;
        if (options.Has("workers") || options.Has("max-heap-mib"))
        {
            TlcResources current = TlcResources.Effective("tlc", declared.Resources);
            if (!int.TryParse(options.Get("workers", current.WorkerArgument), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int workers) ||
                !int.TryParse(options.Get("max-heap-mib", current.MaxHeapMib.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int heap))
                throw new InputException(ErrorCode.InvalidWorkflowInput, "TLC workers and max-heap-mib must be positive integers.");
            resources = TlcResources.Effective("tlc", new(workers, heap));
        }
        AnalysisStage stage = declared with {
            Fingerprint = fingerprint, Resources = resources,
            TimeoutSeconds = options.Has("timeout") ? options.Timeout : declared.TimeoutSeconds };
        Dictionary<string, TlaDocument> parsed = new(FilePaths.Comparer);
        Dictionary<string, string> pins = Pins(path, project, policy, [new AnalysisScenario(scenario.Id, [declared])], parsed);
        var (model, config, jar, document) = PrepareTlc(path, project, stage, pins, parsed);
        TlcResultKey key = TlcResultStore.Key(model, config, jar, document.DependencyFingerprints, fingerprint);
        string store = ProjectInputs.Resolve(path, project.ResultStore);
        StageCheckResult Report(string status, string outcome, string? entry, double duration, TlcResult? result) =>
            new(Schema.Version, "stage-check", project.Id, scenario.Id, id, status, outcome, fingerprint,
                TlcResultStore.Hash(key), TlcResultStore.Family(key), entry, duration,
                stage.Resources, TlcResources.Effective("tlc", stage.Resources), stage.TimeoutSeconds,
                result?.Statistics, result?.Diagnostics,
                "Stores one completed native TLC outcome for identical inputs; it does not accept requirements or reviews.");
        var existing = TlcResultStore.Find(store, key).FirstOrDefault(e => e.Entry.Material.Fingerprint == fingerprint);
        if (existing.Entry != null)
        {
            Render(Report("already-stored", existing.Entry.Outcome, existing.Directory, existing.Entry.DurationSeconds, null), options);
            return 0;
        }
        string work = Path.Combine(store, "tlc", ".work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            Stopwatch clock = Stopwatch.StartNew();
            TlcResult result = ExecuteTlc(model, config, jar, stage, work, fingerprint);
            string outcome = Outcome("tlc", result);
            string? created = TlcResultStore.Store(store, key, id, "check-stage", work, result, stage.Resources,
                TlcResources.Effective("tlc", stage.Resources), stage.TimeoutSeconds, clock.Elapsed.TotalSeconds);
            created ??= outcome == "incomplete" ? null :
                TlcResultStore.Find(store, key).FirstOrDefault(e => e.Entry.Material.Fingerprint == fingerprint).Directory;
            Render(Report(created == null ? "incomplete" : "stored", outcome, created, clock.Elapsed.TotalSeconds, result), options);
            return created == null ? 2 : 0;
        }
        finally
        {
            Directory.Delete(work, true);
        }
    }

    private static void RequireTlcModel(TlaDocument document)
    {
        if (document.Diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.StaleTranslation, ErrorCode.ToolFailure)))
            throw new InputException(ErrorCode.WorkflowPrerequisiteMissing, "TLC requires a successfully parsed, fresh model: " +
                string.Join("; ", document.Diagnostics.Select(d => d.Message)));
    }

    [GeneratedRegex(@"@!@!@STARTMSG (\d+):1 @!@!@")]
    private static partial Regex TlcErrors();

    [GeneratedRegex(@"@!@!@STARTMSG (\d+):1 @!@!@\s*([\s\S]*?)\s*@!@!@ENDMSG \1 @!@!@")]
    private static partial Regex TlcMessages();

    [GeneratedRegex(@"(?m)^([\d,]+) states generated, ([\d,]+) distinct states found, ([\d,]+) states left on queue\.")]
    private static partial Regex TlcCounts();
    [GeneratedRegex(@"The depth of the complete state graph search is ([\d,]+)\.")]
    private static partial Regex TlcDepth();
    [GeneratedRegex(@"calculated \(optimistic\):\s+val = (\S+)")]
    private static partial Regex TlcFingerprintEstimate();

    internal static TlcStatistics ReadTlcStatistics(string text)
    {
        long? Number(Match match, int group) => !match.Success ? null :
            long.TryParse(match.Groups[group].Value, System.Globalization.NumberStyles.AllowThousands,
                System.Globalization.CultureInfo.InvariantCulture, out long value) ? value :
            throw new InputException(ErrorCode.WorkflowStageFailed, "TLC reported an unsupported state count.");
        Match counts = TlcCounts().Matches(text).LastOrDefault() ?? Match.Empty;
        Match depth = TlcDepth().Match(text);
        Match estimate = TlcFingerprintEstimate().Match(text);
        return new(Number(counts, 1), Number(counts, 2), Number(counts, 3), Number(depth, 1),
            estimate.Success ? estimate.Groups[1].Value : null);
    }

    internal static List<Issue> TlcDiagnostics(string text)
    {
        Dictionary<string, string> messages = TlcMessages().Matches(text)
            .GroupBy(m => "TLC" + m.Groups[1].Value, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Groups[2].Value, StringComparer.Ordinal);
        return TlcErrors().Matches(text).Select(m => "TLC" + m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal).Select(code => {
                string detail = messages.TryGetValue(code, out string? message)
                    ? Regex.Replace(message, @"@!@!@(?:STARTMSG \d+:\d|ENDMSG \d+) @!@!@", "").Trim()
                    : "Native diagnostic body is unavailable.";
                if (detail.Length > 2000) detail = detail[..2000] + "...";
                return new Issue(code, "error", detail + " See tlc.log for the complete native diagnostic.");
            }).ToList();
    }
}
