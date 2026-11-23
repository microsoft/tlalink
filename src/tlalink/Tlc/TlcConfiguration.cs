using System.Text.RegularExpressions;

namespace TlaLink;

/// <summary>
/// The directives of a TLC configuration that decide which claims a stage makes: the specification formula, the
/// temporal properties and whether symmetry reduction is declared. Constant values are skipped, not interpreted.
/// </summary>
internal sealed partial record TlcConfiguration(string? Specification, List<string> Properties, bool Symmetry)
{
    private static readonly string[] Keywords = ["SPECIFICATION", "INIT", "NEXT", "INVARIANT", "INVARIANTS", "PROPERTY",
        "PROPERTIES", "CONSTANT", "CONSTANTS", "SYMMETRY", "VIEW", "CONSTRAINT", "CONSTRAINTS", "ACTION_CONSTRAINT",
        "ACTION_CONSTRAINTS", "CHECK_DEADLOCK", "POSTCONDITION", "ALIAS"];

    internal bool Temporal => Properties.Count > 0;

    internal static TlcConfiguration Read(string path)
    {
        string text = BlockComments().Replace(LineComments().Replace(Files.Read(path), ""), " ");
        string? keyword = null, specification = null;
        List<string> properties = [];
        bool symmetry = false;
        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Keywords.Contains(token, StringComparer.Ordinal))
            {
                keyword = token;
                symmetry |= token == "SYMMETRY";
                continue;
            }
            if (keyword == "SPECIFICATION" && specification == null) specification = token;
            else if (keyword is "PROPERTY" or "PROPERTIES" && !properties.Contains(token, StringComparer.Ordinal)) properties.Add(token);
        }
        return new(specification, properties, symmetry);
    }

    [GeneratedRegex(@"\\\*[^\n]*")]
    private static partial Regex LineComments();

    [GeneratedRegex(@"\(\*[\s\S]*?\*\)")]
    private static partial Regex BlockComments();
}
