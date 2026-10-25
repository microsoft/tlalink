namespace TlaLink;

[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class DiagnosticInfoAttribute(string id, IssueSeverity severity) : Attribute
{
    public string Id { get; } = id;
    public IssueSeverity Severity { get; } = severity;
}
