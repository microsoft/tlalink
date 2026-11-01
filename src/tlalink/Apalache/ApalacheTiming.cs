using System.Globalization;
using System.Text.RegularExpressions;

namespace TlaLink;

public sealed record ApalachePhaseTiming(int Pass, string Phase, string StartTimestamp,
    string EndTimestamp, double DurationSeconds)
{
    public string Basis => "native-log-timestamp-interval";
}

public static class ApalacheTiming
{
    public static List<ApalachePhaseTiming> Read(string text)
    {
        List<ApalachePhaseTiming> timings = [];
        (int Pass, string Phase, string Timestamp, TimeSpan Time)? previous = null;
        foreach (string line in text.Split('\n'))
        {
            Match start = Regex.Match(line,
                @"\APASS #(?<pass>\d+): (?<phase>[A-Za-z][A-Za-z0-9_]*)\s+I@(?<time>\d{2}:\d{2}:\d{2}\.\d{3})\s*\z");
            Match end = Regex.Match(line,
                @"\ATotal time: \d+\.\d+ sec\s+I@(?<time>\d{2}:\d{2}:\d{2}\.\d{3})\s*\z");
            Match marker = start.Success ? start : end;
            if (!marker.Success || !TimeSpan.TryParseExact(marker.Groups["time"].Value,
                @"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture, out TimeSpan time)) continue;
            string timestamp = marker.Groups["time"].Value;
            if (previous is { } p)
            {
                TimeSpan duration = time - p.Time;
                if (duration < TimeSpan.Zero && p.Time.Hours == 23 && time.Hours == 0)
                    duration += TimeSpan.FromDays(1);
                if (duration >= TimeSpan.Zero)
                    timings.Add(new(p.Pass, p.Phase, p.Timestamp, timestamp, duration.TotalSeconds));
            }
            previous = start.Success && int.TryParse(start.Groups["pass"].Value, out int pass)
                ? (pass, start.Groups["phase"].Value, timestamp, time) : null;
        }
        return timings;
    }
}
