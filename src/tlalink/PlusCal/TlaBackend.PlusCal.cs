using System.Text.RegularExpressions;

namespace TlaLink;

public static partial class TlaBackend
{
    internal static bool HasAlgorithm(string text) => AlgorithmArguments(text).Length != 0;

    private static void AddTranslation(TlaNativeTranslation row, string path,
        List<TlaBackendDiagnostic> diagnostics)
    {
        string state = row.State;
        int line = row.Line;
        if (state is not ("current" or "missing" or "stale") || line < 1)
            throw new BackendFailure("Invalid translation status or source position.");
        if (state != "current")
            diagnostics.Add(new(ErrorCode.StaleTranslation, $"PlusCal translation is {state}. Regenerate with the selected JAR; "
                + "record nondefault translator flags in PlusCal options in the module."
                + row.Detail, path, line));
    }

    private static string[] AlgorithmArguments(string text)
    {
        string[]? result = null;
        foreach (var (bodyStart, bodyEnd) in Comments(text))
        {
            Match marker = Regex.Match(text[bodyStart..bodyEnd],
                @"(?m)^[ \t]*(--algorithm\b|--fair\b[ \t]+algorithm\b)",
                RegexOptions.CultureInvariant);
            if (!marker.Success) continue;
            if (result is not null)
                throw new BackendFailure("Only one PlusCal algorithm per module is supported.");
            bool fair = marker.Groups[1].Value.StartsWith("--fair", StringComparison.Ordinal);
            int offset = bodyStart + marker.Groups[1].Index + (fair ? 6 : 11);
            int line = text.AsSpan(0, offset).Count('\n');
            int column = offset - text.LastIndexOf('\n', offset - 1) - 1;
            result = [line.ToString(System.Globalization.CultureInfo.InvariantCulture),
                column.ToString(System.Globalization.CultureInfo.InvariantCulture), fair ? "true" : "false"];
        }
        return result ?? [];
    }

    private static IEnumerable<(int Start, int End)> Comments(string text)
    {
        int index = 0;
        while (index < text.Length)
        {
            if (text[index] == '"')
            {
                index++;
                while (index < text.Length && text[index] != '"')
                    index += text[index] == '\\' ? 2 : 1;
                index++;
            }
            else if (At(text, index, "\\*"))
            {
                int end = text.IndexOf('\n', index);
                end = end < 0 ? text.Length : end;
                yield return (index + 2, end);
                index = end;
            }
            else if (At(text, index, "(*"))
            {
                int start = index + 2;
                index = CommentEnd(text, start);
                yield return (start, index - 2);
            }
            else index++;
        }
    }

    private static int CommentEnd(string text, int index)
    {
        int depth = 1;
        while (index < text.Length && depth > 0)
        {
            if (At(text, index, "(*")) { depth++; index += 2; }
            else if (At(text, index, "*)")) { depth--; index += 2; }
            else index++;
        }
        if (depth != 0) throw new BackendFailure("Unterminated TLA+ block comment.");
        return index;
    }

    private static bool At(string text, int index, string token) =>
        text.AsSpan(index).StartsWith(token, StringComparison.Ordinal);
}
