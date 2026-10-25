using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TlaLink;

public sealed record PAssessment(string Status, string Claim, ErrorCode? Diagnostic = null);

public static class PResults
{
    public static string NativeText(string output, PProcessResult process, string engine)
    {
        string text = Files.Read(Path.Combine(output, process.Label + ".stdout.log")) + "\n" +
            Files.Read(Path.Combine(output, process.Label + ".stderr.log"));
        string traces = Path.Combine(output, "checker", "BugFinding");
        if (engine != "pchecker" || !Directory.Exists(traces)) return text;
        foreach (string trace in Directory.EnumerateFiles(traces, "*.trace.json"))
        {
            using JsonDocument document = JsonDocument.Parse(Files.Read(trace), new JsonDocumentOptions { AllowDuplicateProperties = false });
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
                if (entry.GetProperty("type").GetString() == "AssertionFailure")
                    text += "\n" + entry.GetProperty("details").GetProperty("error").GetString();
        }
        return text;
    }

    public static PAssessment Classify(string engine, int? exitCode, bool timedOut, string text, int schedules)
    {
        text = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        if (timedOut || exitCode == null) return Incomplete();
        if (Regex.IsMatch(text, @"(?:reached|hit|exceeded).*max(?:imum)?.*(?:steps|bound)", RegexOptions.IgnoreCase))
            return Incomplete();
        if (text.Contains("scheduling steps bound", StringComparison.OrdinalIgnoreCase)) return Incomplete();
        if (engine == "pverifier")
        {
            if (text.Contains("UNDEF ->", StringComparison.Ordinal) ||
                Regex.IsMatch(text, @"\u2753 (?:Failed to|assert)")) return Incomplete();
            if (Regex.IsMatch(text, @"Failed to verify [1-9]\d* propert", RegexOptions.CultureInvariant))
                return new("rejected", "inductive-counterexample", ErrorCode.PInvariantFailed);
            if (text.Contains("Remaining Goals:", StringComparison.Ordinal)) return Incomplete();
            return exitCode == 0 && Regex.IsMatch(text, @"Verified [1-9]\d* invariants!") &&
                text.Contains("default P proof obligations", StringComparison.Ordinal) &&
                text.Contains("Compilation succeeded.", StringComparison.Ordinal)
                ? new("passed", "inductive-invariants") : Incomplete();
        }
        if (engine == "pex")
        {
            if (Regex.IsMatch(text, @"Result:\s*correct for any depth\s*(?:\r?\n|$)") && exitCode == 0)
                return new("passed", "exhaustive-finite-model");
            if (Regex.IsMatch(text, @"Found [1-9][\d,]* bugs?\.") &&
                text.Contains("Result:", StringComparison.Ordinal))
                return new("rejected", "model-counterexample", ErrorCode.PAssertionFailed);
            return Incomplete();
        }
        if (engine == "psym")
        {
            // Legacy PSym has a distinct terminal result; partial/depth-bounded safety is not exhaustion.
            if (Regex.IsMatch(text, @"Result:\s*correct for any depth\s*(?:\r?\n|$)") && exitCode == 0)
                return new("passed", "exhaustive-symbolic-model");
            if (Regex.IsMatch(text, @"Result:\s*found cex of (?:symbolic )?length \d+\s*(?:\r?\n|$)"))
                return new("rejected", "model-counterexample", ErrorCode.PAssertionFailed);
            return Incomplete();
        }
        if (engine == "pchecker")
        {
            if (Regex.IsMatch(text, @"Found [1-9][\d,]* bugs?\."))
            {
                if (text.Contains("liveness", StringComparison.OrdinalIgnoreCase))
                    return new("rejected", "scheduled-progress-violation", ErrorCode.PLivenessFailed);
                if (text.Contains("assert", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("deadlock", StringComparison.OrdinalIgnoreCase))
                    return new("rejected", "model-counterexample", ErrorCode.PAssertionFailed);
                return Incomplete();
            }
            Match explored = Regex.Match(text, @"Explored ([\d,]+) schedules?");
            if (exitCode == 0 && text.Contains("Found 0 bugs.", StringComparison.Ordinal) &&
                explored.Success && long.Parse(explored.Groups[1].Value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture) >= schedules &&
                !Regex.IsMatch(text, @"(?:reached|hit|exceeded).*max(?:imum)?.*(?:steps|bound)", RegexOptions.IgnoreCase))
                return new("passed", "explored-schedules");
        }
        return Incomplete();
    }

    public static PAssessment Observe(int? exitCode, bool timedOut, string text, string metrics, IEnumerable<string> errors)
    {
        if (timedOut || exitCode == null) return Incomplete();
        string[] errorFiles = errors.Select(Path.GetFileName).OfType<string>().ToArray();
        if (errorFiles.Any(f => f.StartsWith("ParserError_", StringComparison.Ordinal) ||
            f.StartsWith("UnknownError_", StringComparison.Ordinal))) return Incomplete();
        Match totals = Regex.Match(metrics,
            @"Total Events Read\s+Total Verified Events\s+Total Verified Keys\s+Total Partition Keys\s+-+\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)");
        Match failures = Regex.Match(metrics,
            @"Total Parser Errors\s+Total Spec Errors\s+Total Out of Order Errors\s+Total Unknown Errors\s+-+\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)");
        if (!totals.Success || !failures.Success || !text.Contains("PObserve run completed with ", StringComparison.Ordinal))
            return Incomplete();
        long[] counts = Enumerable.Range(1, 4).Select(i => long.Parse(totals.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
        long[] failed = Enumerable.Range(1, 4).Select(i => long.Parse(failures.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
        if (failed[0] != 0 || failed[2] != 0 || failed[3] != 0) return Incomplete();
        if (failed[1] > 0) return new("rejected", "observed-property-violation", ErrorCode.PMonitorFailed);
        return exitCode == 0 && counts.All(c => c > 0) && counts[2] == counts[3]
            ? new("passed", "observed-properties") : Incomplete();
    }

    private static PAssessment Incomplete() => new("incomplete", "not-established", ErrorCode.PExplorationIncomplete);
}
