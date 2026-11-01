using System.Globalization;
using System.Text.RegularExpressions;

namespace TlaLink;

public sealed record PNativeMetrics(long? DistinctStates = null, long? Timelines = null,
    long? Schedules = null, long? SymbolicExecutions = null, long? MaximumReportedDepth = null,
    long? EventsRead = null, long? VerifiedEvents = null, long? VerifiedKeys = null,
    long? PartitionKeys = null, long? ParserErrors = null, long? SpecErrors = null,
    long? OutOfOrderErrors = null, long? UnknownErrors = null);

public static class PNativeOutput
{
    public static PNativeMetrics Read(string engine, string text, string observationMetrics = "")
    {
        text = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        long? Number(string input, string pattern, int group = 1)
        {
            MatchCollection matches = Regex.Matches(input, pattern, RegexOptions.CultureInvariant);
            return matches.Count == 1 &&
                Regex.IsMatch(matches[0].Groups[group].Value, @"\A(?:\d+|\d{1,3}(?:,\d{3})+)\z") &&
                long.TryParse(matches[0].Groups[group].Value,
                NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out long number) ? number : null;
        }
        const string totals = @"Total Events Read\s+Total Verified Events\s+Total Verified Keys\s+Total Partition Keys\s+-+\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)";
        const string errors = @"Total Parser Errors\s+Total Spec Errors\s+Total Out of Order Errors\s+Total Unknown Errors\s+-+\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)";
        return engine switch {
            "pex" => new(
                DistinctStates: Number(text, @"Explored ([\d,]+) distinct states over ([\d,]+) timelines"),
                Timelines: Number(text, @"Explored ([\d,]+) distinct states over ([\d,]+) timelines", 2),
                Schedules: Number(text, @"Explored ([\d,]+) distinct schedules"),
                MaximumReportedDepth: Number(text, @"Number of steps explored: [\d,]+ \(min\), [\d,]+ \(avg\), ([\d,]+) \(max\)\.")),
            "psym" => new(Schedules: Number(text, @"Explored ([\d,]+) schedules"),
                SymbolicExecutions: Number(text, @"Explored ([\d,]+) symbolic executions?"),
                MaximumReportedDepth: Maximum(text)),
            "pchecker" => new(Schedules: Number(text, @"Explored ([\d,]+) schedules?")),
            "pobserve" => new(EventsRead: Number(observationMetrics, totals), VerifiedEvents: Number(observationMetrics, totals, 2),
                VerifiedKeys: Number(observationMetrics, totals, 3), PartitionKeys: Number(observationMetrics, totals, 4),
                ParserErrors: Number(observationMetrics, errors), SpecErrors: Number(observationMetrics, errors, 2),
                OutOfOrderErrors: Number(observationMetrics, errors, 3), UnknownErrors: Number(observationMetrics, errors, 4)),
            _ => new()
        };
    }

    private static long? Maximum(string text)
    {
        List<long> depths = [];
        foreach (Match match in Regex.Matches(text, @"(?:Execution finished at depth |Max Depth: )(\d+)"))
            if (long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long depth))
                depths.Add(depth);
        return depths.Count > 0 ? depths.Max() : null;
    }

    public static PNativeMetrics? FromPacket(PPacket packet, string output)
    {
        string label = packet.Engine switch {
            "pex" or "psym" or "pobserve" => "p-external", "pchecker" => "p-check", _ => ""
        };
        if (!packet.Processes.Any(p => p.Label == label)) return null;
        string Read(string file) => Files.Read(ProjectAcceptance.SafePath(output, file));
        string metricsFile = Path.Combine("observations", "PObserveMetrics.txt");
        string metrics = packet.Engine == "pobserve" && File.Exists(ProjectAcceptance.SafePath(output, metricsFile))
            ? Read(metricsFile) : "";
        return PNativeOutput.Read(packet.Engine, Read(label + ".stdout.log") + "\n" + Read(label + ".stderr.log"), metrics);
    }
}
