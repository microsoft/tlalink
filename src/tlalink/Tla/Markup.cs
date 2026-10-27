using System.Text.Json;
using System.Text.RegularExpressions;

namespace TlaLink;

public static class Markup
{
    public const int Version = 2;

    public static void RequireVersion(int version)
    {
        if (version != Version)
            throw new InputException(ErrorCode.InvalidAnnotation,
                $"Declare annotation_version {Version} in the root project, or --annotation-version {Version} on standalone annotation commands.");
    }

    private const string Id = @"[A-Za-z_][A-Za-z0-9_.-]*";
    private static readonly Regex Token = new($@"\G\s*(""(?:[^""\\]|\\.)*""|{Id}|[=;])", RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, HashSet<string>> Options = new()
    {
        ["model"] = [],
        ["state"] = ["guard", "type", "units"],
        ["step"] = ["actor", "operation", "connection", "entity", "point", "classification", "observe", "guard", "justification", "hook", "visibility"]
    };

    public static List<Tag> Parse(string content, int start, int end)
    {
        string payload = content.Trim();
        if (payload.StartsWith("@tla", StringComparison.Ordinal))
            throw new InputException(ErrorCode.InvalidAnnotation,
                "Replace @tla version headers with @link and declare annotation_version in the root project.");
        if (!Regex.IsMatch(payload, @"^@link\b")) return [];
        Match header = Regex.Match(payload, @"^@link(?=\s|$)");
        if (!header.Success)
            throw new InputException(ErrorCode.InvalidAnnotation, "Use @link with semicolon-terminated model/state/step directives.");
        List<string> tokens = [];
        int offset = header.Length;
        while (payload[offset..].Trim().Length != 0)
        {
            Match token = Token.Match(payload, offset);
            if (!token.Success) throw new InputException(ErrorCode.InvalidAnnotation, $"Invalid annotation near {payload[offset..]}");
            tokens.Add(token.Groups[1].Value);
            offset = token.Index + token.Length;
        }
        List<Tag> result = [];
        int cursor = 0;
        while (cursor < tokens.Count)
        {
            int semicolon = tokens.IndexOf(";", cursor);
            if (semicolon < cursor + 2) throw new InputException(ErrorCode.InvalidAnnotation, "Each model/state/step needs an ID and terminating semicolon.");
            string kind = tokens[cursor++], identity = tokens[cursor++];
            if (!Options.TryGetValue(kind, out HashSet<string>? allowed) || !Regex.IsMatch(identity, "^" + Id + "$"))
                throw new InputException(ErrorCode.InvalidAnnotation, "Expected model, state or step followed by a stable ID.");
            Dictionary<string, string> options = new();
            while (cursor < semicolon)
            {
                if (cursor + 2 >= semicolon || tokens[cursor + 1] != "=")
                    throw new InputException(ErrorCode.InvalidAnnotation, "Options must have the form key=value.");
                string key = tokens[cursor], raw = tokens[cursor + 2];
                if (!allowed.Contains(key) || options.ContainsKey(key))
                    throw new InputException(ErrorCode.InvalidAnnotation, $"Unknown or duplicate {kind} option: {key}");
                string value;
                try { value = raw.StartsWith('"') ? JsonSerializer.Deserialize<string>(raw)! : raw; }
                catch (JsonException error) { throw new InputException(ErrorCode.InvalidAnnotation, "Invalid quoted annotation option: " + error.Message); }
                if (string.IsNullOrWhiteSpace(value)) throw new InputException(ErrorCode.InvalidAnnotation, "Empty annotation option.");
                options.Add(key, value);
                cursor += 3;
            }
            cursor++;
            if (options.TryGetValue("point", out string? point) && point is not ("invoke" or "commit" or "complete" or "fault" or "cleanup"))
                throw new InputException(ErrorCode.InvalidAnnotation, "Unknown observation point.");
            if (options.TryGetValue("classification", out string? classification) && classification is not ("transition" or "stutter" or "unresolved"))
                throw new InputException(ErrorCode.InvalidAnnotation, "Unknown abstraction classification.");
            if (options.TryGetValue("type", out string? type) && type is not ("int" or "bool" or "string"))
                throw new InputException(ErrorCode.InvalidAnnotation, "Observation type must be int, bool or string.");
            if (options.TryGetValue("hook", out string? hook) && hook is not ("before" or "after"))
                throw new InputException(ErrorCode.InvalidAnnotation, "Hook must be before or after.");
            if (options.TryGetValue("visibility", out string? visibility) &&
                (visibility is not ("observed" or "hidden") || visibility == "hidden" && !options.ContainsKey("justification")))
                throw new InputException(ErrorCode.InvalidAnnotation, "Model visibility must be observed or hidden; hidden actions need a justification.");
            result.Add(new(kind, identity, options, start, end));
        }
        if (result.Count == 0) throw new InputException(ErrorCode.InvalidAnnotation, "Empty annotation.");
        return result;
    }

    public static List<(int Start, int End, string Content)> TlaComments(string text)
    {
        List<(int, int, string)> result = [];
        for (int i = 0; i < text.Length;)
        {
            if (text[i] == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"') i += text[i] == '\\' ? 2 : 1;
                i++;
            }
            else if (text.AsSpan(i).StartsWith("\\*"))
            {
                int end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                result.Add((i, end, text[(i + 2)..end]));
                i = end;
            }
            else if (text.AsSpan(i).StartsWith("(*"))
            {
                int start = i, depth = 1;
                i += 2;
                while (i < text.Length && depth > 0)
                {
                    if (text.AsSpan(i).StartsWith("(*")) { depth++; i += 2; }
                    else if (text.AsSpan(i).StartsWith("*)")) { depth--; i += 2; }
                    else i++;
                }
                if (depth != 0) throw new InputException(ErrorCode.InvalidAnnotation, "Unterminated TLA comment.");
                result.Add((start, i, text[(start + 2)..(i - 2)]));
            }
            else i++;
        }
        return result;
    }

    public static bool OnlyTrivia(string text, int start, int end, IEnumerable<(int Start, int End, string Content)> comments)
    {
        int cursor = start;
        foreach (var comment in comments.Where(c => c.Start >= start && c.End <= end).OrderBy(c => c.Start))
        {
            if (!string.IsNullOrWhiteSpace(text[cursor..comment.Start])) return false;
            cursor = comment.End;
        }
        return string.IsNullOrWhiteSpace(text[cursor..end]);
    }
}
