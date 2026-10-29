using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TlaLink;

public sealed record InspectionQuery(List<string> Bindings, List<string> Endpoints, List<string> Symbols, List<string> SyntaxKinds,
    List<string> SourceFiles, List<string> Kinds, List<string> Statuses, List<string> CapturePoints,
    string? Search, List<string> Sections, int Offset, int? Limit);

internal static class InspectionFiltering
{
    private static readonly string[] AllSections = ["model", "source", "context", "captures", "observations", "calls"];

    internal static InspectionQuery Read(CliOptions options)
    {
        List<string> Values(string key, string[]? allowed = null)
        {
            List<string> values = options.Many(key);
            if (values.Any(v => string.IsNullOrWhiteSpace(v) || allowed != null && !allowed.Contains(v, StringComparer.Ordinal)))
                throw Invalid("--" + key + (allowed == null ? " requires nonempty values." : " must be one of: " + string.Join(", ", allowed)));
            return values.Distinct(StringComparer.Ordinal).ToList();
        }
        int Number(string key, int fallback, int minimum)
        {
            if (!int.TryParse(options.Get(key, fallback.ToString(CultureInfo.InvariantCulture)), NumberStyles.None,
                CultureInfo.InvariantCulture, out int value) || value < minimum)
                throw Invalid("--" + key + " must be an integer >= " + minimum + ".");
            return value;
        }
        List<string> sections = Values("section", AllSections);
        if (sections.Count == 0) sections = [.. AllSections];
        if (options.Has("section") && options.Get("calls", "false") == "true" && !sections.Contains("calls"))
            throw Invalid("--calls true conflicts with sections that omit calls.");
        if (options.Has("section") && sections.Contains("calls") && options.Get("calls", "true") == "false")
            throw Invalid("--section calls conflicts with --calls false.");
        List<string> symbols = Values("symbol");
        if (symbols.Any(s => s.Length < 3 || s[1] != ':' || !"MFTPEN".Contains(s[0])))
            throw Invalid("--symbol requires a Roslyn declaration documentation ID.");
        List<string> syntax = Values("syntax");
        if (syntax.Any(s => !Enum.TryParse(s, out SyntaxKind kind) || !Enum.IsDefined(kind) || kind.ToString() != s))
            throw Invalid("--syntax requires a Roslyn SyntaxKind name.");
        string? search = options.Optional("search");
        if (search != null && string.IsNullOrWhiteSpace(search)) throw Invalid("--search must be nonempty.");
        return new(Values("binding"), Values("endpoint"), symbols, syntax,
            Values("source-file").Select(FilePaths.Full).Distinct(FilePaths.Comparer).ToList(), Values("kind"),
            Values("status", ["linked", "unlinked", "declared-hidden", "unresolved"]),
            Values("capture-point", ["invoke", "commit", "complete", "fault", "cleanup", "none"]),
            search, sections, Number("offset", 0, 0), options.Has("limit") ? Number("limit", 1, 1) : null);
    }

    internal static bool IncludeCalls(CliOptions options, InspectionQuery query) =>
        options.Get("calls", "false") == "true" || options.Has("section") && query.Sections.Contains("calls");

    internal static string? SourceSymbol(SourceBinding binding)
    {
        if (binding.Declaration.Source != null) return binding.Declaration.Source.Symbol;
        foreach (SyntaxNode node in binding.Node.AncestorsAndSelf())
        {
            if (node is FieldDeclarationSyntax field && field.Declaration.Variables.Count == 1)
                return binding.Semantics.GetDeclaredSymbol(field.Declaration.Variables[0])?.GetDocumentationCommentId();
            if (node is BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or VariableDeclaratorSyntax)
                return binding.Semantics.GetDeclaredSymbol(node)?.GetDocumentationCommentId();
        }
        return null;
    }

    internal static List<InspectedEndpoint> Apply(List<InspectedEndpoint> entries, InspectionQuery query)
    {
        bool Text(string? text) => query.Search == null || text?.Contains(query.Search, StringComparison.OrdinalIgnoreCase) == true;
        bool linkFilters = query.Symbols.Count > 0 || query.SyntaxKinds.Count > 0 ||
            query.SourceFiles.Count > 0 || query.CapturePoints.Count > 0;
        List<InspectedEndpoint> result = [];
        foreach (InspectedEndpoint entry in entries)
        {
            if (query.Kinds.Count > 0 && !query.Kinds.Contains(entry.Model.Kind, StringComparer.Ordinal) ||
                query.Statuses.Count > 0 && !query.Statuses.Contains(entry.Status, StringComparer.Ordinal)) continue;
            bool modelMatches = Text(entry.Model.Name) || Text(entry.Model.Source);
            List<InspectedLink> links = entry.Links.Where(link =>
                (query.Symbols.Count == 0 || query.Symbols.Contains(link.SourceSymbol ?? "", StringComparer.Ordinal)) &&
                (query.SyntaxKinds.Count == 0 || query.SyntaxKinds.Contains(link.SourceSyntax ?? "", StringComparer.Ordinal)) &&
                (query.SourceFiles.Count == 0 || link.Source != null && query.SourceFiles.Contains(link.Source.Location.Path, FilePaths.Comparer)) &&
                (query.CapturePoints.Count == 0 || query.CapturePoints.Any(point => point == "none"
                    ? link.Captures.All(c => c.Point == null)
                    : link.Captures.Any(c => c.Point?.ToString().Equals(point, StringComparison.OrdinalIgnoreCase) == true))) &&
                (modelMatches || Text(link.Binding) || Text(link.SourceSymbol) || Text(link.Source?.SelectedText))).ToList();
            if (entry.Links.Count == 0 ? linkFilters || !modelMatches : links.Count == 0) continue;
            result.Add(entry with { Links = links });
        }
        return result;
    }

    internal static string Json(LinkInspectionResult result)
    {
        bool Includes(string section) => result.Query!.Sections.Contains(section, StringComparer.Ordinal);
        DefaultJsonTypeInfoResolver resolver = new();
        resolver.Modifiers.Add(type =>
        {
            void Section(string section, params string[] members)
            {
                if (Includes(section)) return;
                foreach (JsonPropertyInfo property in type.Properties.Where(p =>
                    members.Any(m => Files.Json.PropertyNamingPolicy!.ConvertName(m) == p.Name)))
                    property.ShouldSerialize = (_, _) => false;
            }
            if (type.Type == typeof(Endpoint))
                Section("model", nameof(Endpoint.Source));
            else if (type.Type == typeof(InspectedLink))
            {
                Section("source", nameof(InspectedLink.Selector), nameof(InspectedLink.Selection));
                Section("captures", nameof(InspectedLink.Captures));
                Section("observations", nameof(InspectedLink.Observations));
            }
            else if (type.Type == typeof(InspectionSource))
            {
                Section("source", nameof(InspectionSource.SelectedText));
                Section("context", nameof(InspectionSource.Lines), nameof(InspectionSource.Context),
                    nameof(InspectionSource.Boundaries), nameof(InspectionSource.Locks));
                Section("calls", nameof(InspectionSource.Calls));
            }
        });
        return JsonSerializer.Serialize(result, new JsonSerializerOptions(Files.Json) { TypeInfoResolver = resolver });
    }

    private static InputException Invalid(string message) => new(ErrorCode.InvalidAnnotation, message);
}
