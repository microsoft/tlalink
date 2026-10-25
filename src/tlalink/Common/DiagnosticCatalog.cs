using System.Collections.Frozen;
using System.Reflection;

namespace TlaLink;

public static class DiagnosticCatalog
{
    private static readonly FrozenDictionary<ErrorCode, DiagnosticInfoAttribute> Definitions = Load();

    public static DiagnosticInfoAttribute GetInfo(this ErrorCode code) =>
        Definitions.TryGetValue(code, out DiagnosticInfoAttribute? definition) ? definition :
            throw new ArgumentOutOfRangeException(nameof(code), code, "Undefined diagnostic code.");

    public static string Id(this ErrorCode code) => code.GetInfo().Id;

    public static string Severity(this ErrorCode code) => code.GetInfo().Severity switch
    {
        IssueSeverity.Warning => "warning",
        IssueSeverity.Error => "error",
        _ => throw new InvalidOperationException($"Invalid severity for {code}.")
    };

    private static FrozenDictionary<ErrorCode, DiagnosticInfoAttribute> Load()
    {
        Dictionary<ErrorCode, DiagnosticInfoAttribute> definitions = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (FieldInfo field in typeof(ErrorCode).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            DiagnosticInfoAttribute definition = field.GetCustomAttribute<DiagnosticInfoAttribute>()
                ?? throw new InvalidOperationException($"Missing diagnostic metadata for {field.Name}.");
            if (string.IsNullOrWhiteSpace(definition.Id) || !ids.Add(definition.Id) || !Enum.IsDefined(definition.Severity))
                throw new InvalidOperationException($"Invalid or duplicate diagnostic metadata for {field.Name}.");
            if (!definitions.TryAdd((ErrorCode)field.GetValue(null)!, definition))
                throw new InvalidOperationException($"Duplicate diagnostic enum value for {field.Name}.");
        }
        return definitions.ToFrozenDictionary();
    }
}
