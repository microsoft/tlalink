using System.Text.Json;
using System.Text.Json.Serialization;

namespace TlaLink;

internal static class TlaNativeJson
{
    internal static readonly JsonSerializerOptions Options = new(Files.Json)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowOutOfOrderMetadataProperties = true
    };

    internal static List<TlaNativeRow> ReadIndex(string json)
    {
        try
        {
            List<TlaNativeRow> rows = JsonSerializer.Deserialize<List<TlaNativeRow>>(json, Options)
                ?? throw new JsonException("Invalid TLA parser response: expected an array.");
            if (rows.Any(row => row is null))
                throw new JsonException("Invalid null TLA parser entry.");
            return rows;
        }
        catch (NotSupportedException error)
        {
            throw new JsonException("Invalid TLA parser entry kind.", error);
        }
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TlaNativeOperator), "operator")]
[JsonDerivedType(typeof(TlaNativeLabel), "label")]
[JsonDerivedType(typeof(TlaNativeVariable), "variable")]
[JsonDerivedType(typeof(TlaNativeDependency), "dependency")]
[JsonDerivedType(typeof(TlaNativeTranslation), "translation")]
[JsonDerivedType(typeof(TlaNativeAssumptions), "assumptions")]
[JsonDerivedType(typeof(TlaNativeOperatorLevel), "operator-level")]
internal abstract record TlaNativeRow;

internal abstract record TlaNativeEndpoint : TlaNativeRow
{
    public required string Name { get; init; }
    public required string Scope { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
    public required int EndLine { get; init; }
    public required int EndColumn { get; init; }
    [JsonIgnore]
    public string Kind => this switch
    {
        TlaNativeOperator => "operator",
        TlaNativeLabel => "label",
        TlaNativeVariable => "variable",
        _ => throw new InvalidOperationException("Unknown native endpoint kind.")
    };
}

internal sealed record TlaNativeOperator : TlaNativeEndpoint;
internal sealed record TlaNativeLabel : TlaNativeEndpoint;
internal sealed record TlaNativeVariable : TlaNativeEndpoint;
internal sealed record TlaNativeDependency(string Path, string Sha256) : TlaNativeRow;
internal sealed record TlaNativeTranslation(string State, string Detail, int Line) : TlaNativeRow;
internal sealed record TlaNativeAssumptions(string Module, int Count) : TlaNativeRow;
internal sealed record TlaNativeOperatorLevel(string Name, int Level) : TlaNativeRow;
internal sealed record TlaNativeMetadata(string Module, List<string> Variables, Dictionary<string, int> Arities);
