using System.Text.Json;
using System.Text.Json.Serialization;

namespace TlaLink;

public enum CapturePoint { Invoke, Commit, Complete, Fault, Cleanup }
public enum CaptureClassification { Transition, Stutter, Unresolved }
public enum CapturedType { Int, Bool, String }
public enum SchedulingHook { Before, After }
public enum SourceBindingKind { State, Step, Declaration }

[JsonConverter(typeof(NativeReferenceConverter))]
public sealed record NativeReference(string Alias, string Endpoint)
{
    public override string ToString() => Alias + "::" + Endpoint;
}

public sealed class NativeReferenceConverter : JsonConverter<NativeReference>
{
    public override NativeReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("target requires a qualified string.");
        (string alias, string endpoint) = Markup.Target(reader.GetString()!);
        return new(alias, endpoint);
    }

    public override void Write(Utf8JsonWriter writer, NativeReference value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public abstract record SourceAnnotation
{
    [JsonIgnore]
    public abstract string Identity { get; }
}

public sealed record BindAnnotation(string Id, NativeReference Target, string? Reason = null) : SourceAnnotation
{
    public override string Identity => Id;
}

public sealed record CaptureAnnotation(string Binding, CapturePoint? Point = null,
    CaptureClassification? Classification = null, string? Actor = null, string? Operation = null,
    string? Connection = null, string? Entity = null, List<string>? Arguments = null,
    List<string>? Observe = null, string? Guard = null, CapturedType? Type = null,
    string? Units = null, string? Reason = null, SchedulingHook? Hook = null) : SourceAnnotation
{
    public override string Identity => Binding;

    internal Dictionary<string, string> BackendOptions()
    {
        Dictionary<string, string> options = new(StringComparer.Ordinal);
        void Text(string key, string? value) { if (value != null) options.Add(key, value); }
        void Choice<T>(string key, T? value) where T : struct, Enum =>
            Text(key, value == null ? null : JsonNamingPolicy.SnakeCaseLower.ConvertName(value.Value.ToString()));
        Choice("point", Point);
        Choice("classification", Classification);
        Choice("type", Type);
        Choice("hook", Hook);
        Text("actor", Actor);
        Text("operation", Operation);
        Text("connection", Connection);
        Text("entity", Entity);
        Text("guard", Guard);
        Text("units", Units);
        Text("justification", Reason);
        Text("arguments", Arguments == null ? null : string.Join(",", Arguments));
        Text("observe", Observe == null ? null : string.Join(",", Observe));
        return options;
    }
}

public sealed record ExcludeAnnotation(string Id, List<string> From, string Reason) : SourceAnnotation
{
    public override string Identity => Id;
}

public sealed record AnnotationPayload
{
    public List<BindAnnotation> Bind { get; init; } = [];
    public List<CaptureAnnotation> Capture { get; init; } = [];
    public List<ExcludeAnnotation> Exclude { get; init; } = [];
}

public sealed record LocatedAnnotation(SourceAnnotation Declaration, int Start, int End);
public sealed record SourceLinkDeclaration(BindAnnotation Binding, CaptureAnnotation? Capture,
    SourceBindingKind SourceKind, Place Location);
