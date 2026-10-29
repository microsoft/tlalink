using System.Text.Json;
using System.Text.Json.Serialization;

namespace TlaLink;

internal static class DafnyNativeJson
{
    internal static readonly JsonSerializerOptions Options = new(Files.Json)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowOutOfOrderMetadataProperties = true
    };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DafnyNativeStatus), "status")]
[JsonDerivedType(typeof(DafnyNativeDiagnostic), "diagnostic")]
internal abstract record DafnyNativeMessage;

internal sealed record DafnyNativeStatus(string? Value) : DafnyNativeMessage;
internal sealed record DafnyNativeDiagnostic(DafnyNativeDiagnosticValue Value) : DafnyNativeMessage;
internal sealed record DafnyNativeDiagnosticValue(int Severity, string? Source = null)
{
    private string? defaultFormatMessage;
    public string? DefaultFormatMessage
    {
        get => defaultFormatMessage;
        init
        {
            defaultFormatMessage = value;
            HasDefaultFormatMessage = true;
        }
    }
    [JsonIgnore]
    public bool HasDefaultFormatMessage { get; private init; }
}
