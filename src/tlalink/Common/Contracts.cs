using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TlaLink;

public sealed class InputException(string message) : Exception(message)
{
    public ErrorCode? Code { get; }

    public InputException(ErrorCode code, string message) : this($"{code.Id()}: {message}")
    {
        Code = code;
    }
}

public sealed record Place(string Path, int Line, int EndLine, int Column = 1);
[method: JsonConstructor]
public sealed record Issue(string Code, string Severity, string Message, Place? Location = null)
{
    public Issue(ErrorCode code, string message, Place? location = null)
        : this(code.Id(), code.Severity(), message, location) { }

    public bool Is(params ErrorCode[] codes) => codes.Any(code => Code == code.Id());
}
public sealed record Tag(string Kind, string Identity, Dictionary<string, string> Options, int Start, int End);
public sealed record Endpoint(string Name, string Scope, string Kind, Place Location, string Source);
public sealed record Boundary(string Kind, string Text, Place Location);
public sealed record Access(string Name, string Symbol, string Expression, string Mode, Place Location);
public sealed record Link(string Key, string Identity, string Kind, string Model, Endpoint Implementation,
    Endpoint Specification, Dictionary<string, string> Binding, List<string> Locks,
    List<Boundary> Boundaries, List<Access> CandidateAccesses, List<string> Questions)
{
    public string Assessment => "unreviewed";
    public string Atomicity => "not-established";
}

public sealed class Packet(string command)
{
    public int SchemaVersion => Schema.Version;
    public string Kind => "source-review";
    public string Command { get; } = command;
    public string LinksStatus { get; set; } = "incomplete";
    public string CoverageStatus => "requires-agent-review";
    public string ConformanceStatus { get; set; } = "not-run";
    public List<Issue> Diagnostics { get; } = [];
    public List<Link> Links { get; set; } = [];
    public List<Endpoint> Inventory { get; } = [];
    public Dictionary<string, string> Fingerprints { get; } = new(FilePaths.Comparer);
    public List<string> Uncertainties { get; } = [
        "Roslyn resolves only the supplied sources, references and preprocessor symbols.",
        "A symbol binding, lock or annotation does not prove atomicity or refinement.",
        "Observed trace inclusion does not prove all C# executions or temporal progress.",
        "External calls, callbacks, reflection and runtime behavior retain explicit proof obligations."
    ];
}

public static class Files
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    public static void RequireSchema(JsonElement root, string kind)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schema_version", out JsonElement version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out int number) || number != Schema.Version ||
            !root.TryGetProperty("kind", out JsonElement discriminator) || discriminator.ValueKind != JsonValueKind.String || discriminator.GetString() != kind)
            throw new InputException($"Unsupported document version or kind; expected schema {Schema.Version} {kind}.");
    }

    public static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return Decode(bytes);
    }

    public static string ReadPinned(string path, Dictionary<string, string> pins)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Pin(pins, path, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        return Decode(bytes);
    }

    private static string Decode(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
            return new UnicodeEncoding(false, true, true).GetString(bytes, 2, bytes.Length - 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            return new UnicodeEncoding(true, true, true).GetString(bytes, 2, bytes.Length - 2);
        int skip = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip);
    }

    public static List<string> Discover(IEnumerable<string> inputs, string extension)
    {
        HashSet<string> result = new(FilePaths.Comparer);
        EnumerationOptions options = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (string input in inputs)
        {
            string path = FilePaths.Full(input);
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.EnumerateFiles(path, "*" + extension, options))
                    result.Add(FilePaths.Full(file));
            }
            else if (File.Exists(path) && Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase))
                result.Add(path);
            else
                throw new InputException($"Expected an existing {extension} file or directory: {path}");
            if (result.Count > 1000) throw new InputException("Scope exceeds 1000 files; supply narrower inputs.");
        }
        if (result.Count == 0) throw new InputException($"No {extension} files in explicit scope.");
        return result.Order(FilePaths.Comparer).ToList();
    }

    public static void Pin(Dictionary<string, string> pins, string key, string value)
    {
        if (pins.TryGetValue(key, out string? previous) && previous != value)
            throw new InputException($"Input changed between parser invocations: {key}");
        pins[key] = value;
    }

    public static void WriteNew(string path, object value)
    {
        using FileStream file = new(FilePaths.Full(path), FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(file, value, Json);
    }

    public static Place At(string path, string text, int start, int end)
    {
        int line = 1, column = 1, endLine = 1;
        for (int i = 0; i < Math.Min(end, text.Length); i++)
        {
            if (text[i] == '\n') endLine++;
            if (i < start)
            {
                if (text[i] == '\n') { line++; column = 1; }
                else column++;
            }
        }
        return new(path, line, endLine, column);
    }
}
