using System.Text.Json;
using System.Text.RegularExpressions;

namespace TlaLink;

public sealed record TlcStatistics(long? StatesGenerated, long? DistinctStates, long? StatesLeft,
    long? SearchDepth, string? OptimisticFingerprintOmissionEstimate);

public static partial class ProjectWorkflow
{
    private static JsonElement Tlc(string path, AnalysisProject project, AnalysisStage stage, string output, Dictionary<string, string> pins)
    {
        string model = ProjectInputs.Resolve(path, stage.Models![0]);
        string jar = ProjectInputs.Resolve(path, project.Tools.Jar!.Path);
        TlaDocument document = TlaBackend.Parse(model, jar, stage.TimeoutSeconds);
        foreach (var pin in document.DependencyFingerprints) Files.Pin(pins, pin.Key, pin.Value);
        RequireTlcModel(document);
        TlcResources resources = TlcResources.Effective(stage.Kind, stage.Resources);
        var result = Invoke(Executable("java"), [resources.HeapArgument, "-cp", jar, "tlc2.TLC", "-tool", "-workers", resources.WorkerArgument,
            "-metadir", Path.Combine(output, "states"), "-config", ProjectInputs.Resolve(path, stage.Config!), model],
            output, stage.TimeoutSeconds, "tlc", allowFailure: true);
        List<Issue> diagnostics = TlcDiagnostics(result.Text);
        List<string> codes = diagnostics.Select(d => d.Code).ToList();
        bool rejected = result.ExitCode != 0 && codes.Any(c => c is "TLC2110" or "TLC2111" or "TLC2114" or "TLC2116" or "TLC2120");
        return JsonSerializer.SerializeToElement(new {
            SchemaVersion = Schema.Version, Kind = "tlc-result",
            Status = result.ExitCode == 0 ? "passed" : rejected ? "rejected" : "incomplete",
            Diagnostics = diagnostics,
            Model = model, Config = ProjectInputs.Resolve(path, stage.Config!), ExitCode = result.ExitCode,
            RequestedResources = stage.Resources, Resources = resources,
            Invocation = ProjectInputs.Read<JsonElement>(Path.Combine(output, "tlc.invocation.json")),
            Statistics = ReadTlcStatistics(result.Text),
            Boundaries = new[] { "Only the configured finite model; source correspondence and fingerprint collision risk require review." }
        }, Files.Json);
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
