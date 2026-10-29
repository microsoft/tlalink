using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;

namespace TlaLink;

public sealed record CSharpCompilerContext(string LanguageVersion, string Nullable, bool IncludeFrameworkReferences);

public static class CSharpLanguages
{
    public const string Default = "14";

    public static bool Supported(string? version) => version is "8" or "14";

    public static LanguageVersion Parse(string version) => version switch {
        "8" => LanguageVersion.CSharp8,
        "14" => LanguageVersion.CSharp14,
        _ => throw new InputException(ErrorCode.UnresolvedCompilation, "C# language version must be 8 or 14.")
    };

    public static bool Observed(string? version, out string canonical)
    {
        canonical = version switch { "8" or "8.0" => "8", "14" or "14.0" => "14", _ => "" };
        return canonical.Length != 0;
    }

    public static bool Matches(string? observed, string requested) =>
        Observed(observed, out string canonical) && canonical == requested;

    public static string Archived(Dictionary<string, string> pins)
    {
        if (!pins.TryGetValue("tool:csharp-context", out string? text))
            throw new InputException(ErrorCode.UnresolvedCompilation, "Missing archived C# compiler context.");
        CSharpCompilerContext context = JsonSerializer.Deserialize<CSharpCompilerContext>(text, ProjectInputs.Json)
            ?? throw new InputException(ErrorCode.UnresolvedCompilation, "Empty archived C# compiler context.");
        if (!Supported(context.LanguageVersion))
            throw new InputException(ErrorCode.UnresolvedCompilation, "Invalid archived C# language version.");
        return context.LanguageVersion;
    }
}
