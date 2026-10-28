using System.Text.Json;

namespace TlaLink;

public static class Markup
{
    public const int Version = 4;

    public static void RequireVersion(int version)
    {
        if (version != Version)
            throw new InputException(ErrorCode.InvalidAnnotation,
                $"Declare annotation_version {Version} in the root project, or --annotation-version {Version} on standalone annotation commands.");
    }

    public static readonly JsonSerializerOptions AnnotationJson = CreateAnnotationJson();

    private static JsonSerializerOptions CreateAnnotationJson()
    {
        JsonSerializerOptions options = new(ProjectInputs.Json) { MaxDepth = 16 };
        options.Converters.Clear();
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }

    public static List<LocatedAnnotation> Parse(string content, int start, int end)
    {
        string payload = content.Trim();
        if (payload.StartsWith("@tla", StringComparison.Ordinal))
            throw new InputException(ErrorCode.InvalidAnnotation,
                "Replace @tla version headers with @link and declare annotation_version in the root project.");
        if (!payload.StartsWith("@link", StringComparison.Ordinal) ||
            payload.Length > 5 && (char.IsLetterOrDigit(payload[5]) || payload[5] == '_')) return [];
        if (payload.Length == 5 || !char.IsWhiteSpace(payload[5]))
            throw new InputException(ErrorCode.InvalidAnnotation, "Use @link followed by a JSON object.");
        List<LocatedAnnotation> result = [];
        try
        {
            AnnotationPayload document = JsonSerializer.Deserialize<AnnotationPayload>(payload[5..], AnnotationJson)
                ?? throw new JsonException("Annotation payload must be an object.");
            if (document.Bind.Any(b => b is null) || document.Capture.Any(c => c is null) || document.Exclude.Any(e => e is null))
                throw new JsonException("Annotation entries cannot be null.");
            foreach (BindAnnotation binding in document.Bind)
            {
                Name(binding.Id);
                if (binding.Reason != null && string.IsNullOrWhiteSpace(binding.Reason)) throw new JsonException("Empty binding reason.");
                result.Add(new(binding, start, end));
            }
            foreach (CaptureAnnotation capture in document.Capture)
            {
                Name(capture.Binding);
                ValidateList(capture.Observe, identifiers: true);
                ValidateList(capture.Arguments, identifiers: false);
                if (new[] { capture.Actor, capture.Operation, capture.Connection, capture.Entity, capture.Guard, capture.Units, capture.Reason }
                    .Any(value => value != null && string.IsNullOrWhiteSpace(value))) throw new JsonException("Empty capture option.");
                result.Add(new(capture, start, end));
            }
            foreach (ExcludeAnnotation exclusion in document.Exclude)
            {
                RequireId(exclusion.Id);
                ValidateList(exclusion.From, identifiers: true, qualified: false);
                if (exclusion.From.Count == 0 || string.IsNullOrWhiteSpace(exclusion.Reason)) throw new JsonException("Exclusion needs targets and a reason.");
                result.Add(new(exclusion, start, end));
            }
        }
        catch (JsonException error)
        {
            throw new InputException(ErrorCode.InvalidAnnotation, "Invalid annotation JSON: " + error.Message);
        }
        if (result.Count == 0) throw new InputException(ErrorCode.InvalidAnnotation, "Empty annotation.");
        return result;
    }

    private static void RequireId(string value)
    {
        if (!Schema.IsStableId(value)) throw new JsonException("Expected a stable annotation ID.");
    }

    private static void ValidateList(List<string>? values, bool identifiers, bool qualified = true)
    {
        if (values == null) return;
        if (values.Count == 0 || values.Any(string.IsNullOrWhiteSpace) ||
            identifiers && (values.Any(v => qualified ? !IsCorrespondenceId(v) : !Schema.IsStableId(v)) ||
                values.Distinct(StringComparer.Ordinal).Count() != values.Count))
            throw new JsonException("Expected a nonempty string array; ID arrays must contain unique stable IDs.");
    }

    public static string Encode(AnnotationPayload payload)
    {
        JsonSerializerOptions options = new(AnnotationJson) {
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        string json = JsonSerializer.Serialize(payload, options).Replace("*/", "*\\/", StringComparison.Ordinal);
        Parse("@link " + json, 0, 0);
        return json;
    }

    public static bool IsCorrespondenceId(string value)
    {
        int separator = value.IndexOf("::", StringComparison.Ordinal);
        return separator > 0 && Schema.IsStableId(value[..separator]) && Schema.IsStableId(value[(separator + 2)..]);
    }

    public static NativeReference Name(string value)
    {
        int separator = value.IndexOf("::", StringComparison.Ordinal);
        if (!IsCorrespondenceId(value))
            throw new InputException(ErrorCode.InvalidAnnotation, "A correspondence ID must be scope::name, with two stable names.");
        return new(value[..separator], value[(separator + 2)..]);
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
