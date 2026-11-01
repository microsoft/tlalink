using System.Globalization;
using System.Text.RegularExpressions;

namespace TlaLink;

public sealed record PProofLocation(string File, int Line, int Column);
public sealed record PProofGoal(string Status, string Message, PProofLocation? Location);
public sealed record PProofDiagnostics(int? VerifiedInvariants, int? FailedProperties,
    int ObservedGoals, int OmittedGoals, List<PProofGoal> Goals);

public static class PProofOutput
{
    public const int GoalLimit = 100;
    public const int MessageLimit = 2048;

    public static PProofDiagnostics Read(string text)
    {
        text = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        int? Count(string pattern)
        {
            MatchCollection matches = Regex.Matches(text, pattern, RegexOptions.CultureInvariant);
            return matches.Count == 1 && int.TryParse(matches[0].Groups[1].Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out int count) ? count : null;
        }
        List<PProofGoal> goals = [];
        int observed = 0;
        bool remaining = false;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Contains("Remaining Goals:", StringComparison.Ordinal)) { remaining = true; continue; }
            Match detail = Regex.Match(line, @"(?:❌|❓)\s+(?<message>(?:Failed to |assert\b).*)\z");
            string? status = detail.Success ? line.Contains('❓') ? "unknown" : "failed" : null;
            string message = detail.Success ? detail.Groups["message"].Value : "";
            if (!detail.Success && remaining && line.StartsWith("- ", StringComparison.Ordinal))
            {
                status = "remaining";
                message = line[2..];
            }
            else if (!detail.Success && line.Length > 0) remaining = false;
            if (status == null || message.StartsWith("Failed to verify ", StringComparison.Ordinal) &&
                Regex.IsMatch(message, @"\AFailed to verify \d+ propert")) continue;
            observed++;
            if (goals.Count == GoalLimit) continue;
            Match location = Regex.Match(message, @"(?: at | in )(?<file>.+):(?<line>\d+):(?<column>\d+)(?: failed)?\z");
            PProofLocation? source = null;
            if (location.Success && location.Groups["file"].Value != "<no source>" &&
                int.TryParse(location.Groups["line"].Value, out int lineNumber) &&
                int.TryParse(location.Groups["column"].Value, out int column))
                source = new(location.Groups["file"].Value, lineNumber, column);
            goals.Add(new(status, message[..Math.Min(message.Length, MessageLimit)], source));
        }
        return new(Count(@"Verified (\d+) invariants!"), Count(@"Failed to verify (\d+) propert"),
            observed, observed - goals.Count, goals);
    }
}
