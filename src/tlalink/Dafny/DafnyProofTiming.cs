using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace TlaLink;

public sealed record DafnyProofEffort(string Name, string Outcome, double DurationSeconds,
    long ResourceCount, int RandomSeed);
public sealed record DafnyProofTimings(int ObservedEfforts, int OmittedEfforts, List<DafnyProofEffort> Efforts);

public static class DafnyProofTiming
{
    public const int EffortLimit = 100;

    public static DafnyProofTimings Read(string csv)
    {
        using TextFieldParser parser = new(new StringReader(csv)) {
            TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true
        };
        parser.SetDelimiters(",");
        string[] header = ["TestResult.DisplayName", "TestResult.Outcome", "TestResult.Duration",
            "TestResult.ResourceCount", "RandomSeed"];
        if (parser.ReadFields() is not { } columns || !columns.SequenceEqual(header))
            throw new InputException(ErrorCode.WorkflowStageInconsistent, "Unrecognized native Dafny timing CSV header.");
        List<DafnyProofEffort> efforts = [];
        int observed = 0;
        while (!parser.EndOfData)
        {
            string[]? row = parser.ReadFields();
            if (row is not { Length: 5 } || string.IsNullOrWhiteSpace(row[0]) ||
                string.IsNullOrWhiteSpace(row[1]) ||
                !TimeSpan.TryParseExact(row[2], "c", CultureInfo.InvariantCulture, out TimeSpan duration) ||
                duration < TimeSpan.Zero ||
                !long.TryParse(row[3], NumberStyles.None, CultureInfo.InvariantCulture, out long resources) ||
                !int.TryParse(row[4], NumberStyles.None, CultureInfo.InvariantCulture, out int seed))
                throw new InputException(ErrorCode.WorkflowStageInconsistent, "Malformed native Dafny timing CSV row.");
            observed++;
            if (efforts.Count < EffortLimit)
                efforts.Add(new(row[0], row[1], duration.TotalSeconds, resources, seed));
        }
        return new(observed, observed - efforts.Count, efforts);
    }
}
