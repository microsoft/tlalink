using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TlaLink;

public sealed record DafnyProofPosition(int Line, int Character);
public sealed record DafnyProofRange(DafnyProofPosition Start, DafnyProofPosition End);
public sealed record DafnyProofLocation(string? Filename = null, string? FilePath = null,
    string? Uri = null, DafnyProofRange? Range = null);
public sealed record DafnyProofRelated(string Message, DafnyProofLocation? Location);
public sealed record DafnyProofGoal(int Severity, string? Source, string? ErrorId, string Message,
    DafnyProofLocation? Location, int OmittedRelatedInformation, List<DafnyProofRelated> RelatedInformation);
public sealed record DafnyProofDiagnostics(int? VerifiedObligations, int? Errors,
    int ObservedGoals, int OmittedGoals, List<DafnyProofGoal> Goals);

public static class DafnyProofOutput
{
    public const int GoalLimit = 100;
    public const int RelatedLimit = 10;
    public const int MessageLimit = 2048;

    public static DafnyProofDiagnostics Read(string log)
    {
        List<DafnyProofGoal> goals = [];
        int observed = 0, summaries = 0;
        int? verified = null, errors = null;
        foreach (string line in log.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            DafnyNativeMessage? message;
            try { message = JsonSerializer.Deserialize<DafnyNativeMessage>(line, DafnyNativeJson.Options); }
            catch (JsonException) { continue; }
            if (message is DafnyNativeStatus status)
            {
                Match summary = Regex.Match(status.Value?.Trim() ?? "",
                    @"\ADafny program verifier finished with (\d+) verified, (\d+) errors?\z");
                if (summary.Success && int.TryParse(summary.Groups[1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out int v) && int.TryParse(summary.Groups[2].Value,
                    NumberStyles.None, CultureInfo.InvariantCulture, out int e))
                {
                    summaries++;
                    verified = v;
                    errors = e;
                }
            }
            if (message is not DafnyNativeDiagnostic { Value.Severity: 1 } diagnostic) continue;
            observed++;
            if (goals.Count == GoalLimit) continue;
            DafnyNativeDiagnosticValue value = diagnostic.Value;
            List<DafnyNativeRelatedInformation> related = value.RelatedInformation ?? [];
            goals.Add(new(value.Severity, value.Source, value.ErrorId, Bounded(value.DefaultFormatMessage),
                value.Location, Math.Max(0, related.Count - RelatedLimit),
                related.Take(RelatedLimit).Select(r => new DafnyProofRelated(Bounded(r.DefaultFormatMessage), r.Location)).ToList()));
        }
        return new(summaries == 1 ? verified : null, summaries == 1 ? errors : null,
            observed, observed - goals.Count, goals);
    }

    private static string Bounded(string? message) =>
        message is null ? "" : message[..Math.Min(message.Length, MessageLimit)];
}
