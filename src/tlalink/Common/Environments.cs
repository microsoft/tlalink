using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace TlaLink;

/// <summary>
/// Environment manifests: parties outside a synchronization region, their declared operations and obligations.
/// Obligations are assumptions carried into plans and reports; nothing here checks that the environment meets them.
/// </summary>
public static class Environments
{
    internal static readonly string[] Kinds = ["interface", "consumer"];
    internal static readonly string[] Roles = [.. Primitives.Roles, "submit"];

    public static EnvironmentManifest Load(string path, Dictionary<string, string> pins)
    {
        path = FilePaths.Full(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024)
            throw Invalid("Environment manifests must be existing files of at most 16 MiB: " + path);
        string text = Files.ReadPinned(path, pins);
        try
        {
            Files.RequireSchema(JsonSerializer.Deserialize<JsonElement>(text, ProjectInputs.Json), "environment");
            EnvironmentManifest manifest = JsonSerializer.Deserialize<EnvironmentManifest>(text, ProjectInputs.Json)
                ?? throw Invalid("Empty environment manifest: " + path);
            Validate(manifest);
            if (manifest.Module != null && !File.Exists(FilePaths.Full(manifest.Module, Path.GetDirectoryName(path)!)))
                throw Invalid("Environment module does not exist: " + manifest.Module);
            return manifest;
        }
        catch (JsonException error) { throw Invalid(path + ": " + error.Message); }
        catch (InputException error) { throw Invalid(path + ": " + error.Message); }
    }

    public static void Validate(EnvironmentManifest manifest)
    {
        static bool Name(string? value) => value != null && Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_]*$");
        if (manifest.SchemaVersion != Schema.Version || manifest.Kind != "environment" || !Schema.IsStableId(manifest.Id))
            throw Invalid("Environment manifests require schema/kind and a stable id.");
        if (!Kinds.Contains(manifest.EnvironmentKind, StringComparer.Ordinal))
            throw Invalid("environment_kind must be interface or consumer; the scheduler environment is built in: " + manifest.EnvironmentKind);
        if (manifest.Types.Count is < 1 or > 100 || manifest.Types.Any(t => t == null || !t.StartsWith("T:", StringComparison.Ordinal) || t.Length < 3) ||
            manifest.Types.Distinct(StringComparer.Ordinal).Count() != manifest.Types.Count)
            throw Invalid("Environments require 1..100 unique T: type documentation IDs.");
        if (manifest.Module != null && (string.IsNullOrWhiteSpace(manifest.Module) ||
            !Path.GetExtension(manifest.Module).Equals(".tla", StringComparison.OrdinalIgnoreCase)))
            throw Invalid("An environment module is a .tla path relative to the manifest.");
        if (manifest.Operations.Count is < 1 or > 1000 || manifest.Operations.Any(o => o == null))
            throw Invalid("Environments require 1..1000 declared operations.");
        HashSet<string> members = new(StringComparer.Ordinal);
        foreach (EnvironmentOperation operation in manifest.Operations)
        {
            if (!(operation.Member.StartsWith("M:", StringComparison.Ordinal) || operation.Member.StartsWith("P:", StringComparison.Ordinal)) ||
                operation.Member.Length < 3)
                throw Invalid("Operation members are M: or P: documentation IDs: " + operation.Member);
            if (!members.Add(operation.Member)) throw Invalid("Duplicate environment operation member: " + operation.Member);
            if (!Roles.Contains(operation.Role, StringComparer.Ordinal))
                throw Invalid("Unsupported environment operation role: " + operation.Member + ": " + operation.Role);
            if (operation.Actions is { Count: 0 or > 100 } || operation.Actions?.Any(a => !Name(a)) == true ||
                operation.Wait != null && !Name(operation.Wait))
                throw Invalid("Environment actions and waits name module operators: " + operation.Member);
            if (manifest.Module == null && (operation.Actions != null || operation.Wait != null))
                throw Invalid("Environment actions and waits require a module to resolve in: " + operation.Member);
        }
        if (manifest.Obligations.Count is < 1 or > 100 || manifest.Obligations.Any(string.IsNullOrWhiteSpace))
            throw Invalid("Environments require 1..100 nonempty obligations.");
        if (manifest.Evidence.Count > 100 || manifest.Evidence.Any(e => !Schema.IsStableId(e)) ||
            manifest.Evidence.Distinct(StringComparer.Ordinal).Count() != manifest.Evidence.Count)
            throw Invalid("Environment evidence lists unique scenario or stage IDs.");
    }

    /// <summary>Resolves the declared types (source or metadata) and maps every declared member to its symbol.</summary>
    internal static Dictionary<ISymbol, EnvironmentOperation> Resolve(Compilation compilation, EnvironmentManifest manifest,
        out List<INamedTypeSymbol> types)
    {
        types = [];
        foreach (string id in manifest.Types)
        {
            List<INamedTypeSymbol> found = DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation)
                .OfType<INamedTypeSymbol>().ToList();
            if (found.Count != 1)
                throw Invalid("Environment type must resolve to exactly one source or referenced type: " + manifest.Id + ": " + id);
            types.Add(found[0]);
        }
        Dictionary<ISymbol, EnvironmentOperation> result = new(SymbolEqualityComparer.Default);
        foreach (EnvironmentOperation operation in manifest.Operations)
        {
            List<ISymbol> found = DocumentationCommentId.GetSymbolsForDeclarationId(operation.Member, compilation)
                .Where(s => s is IMethodSymbol or IPropertySymbol).ToList();
            List<INamedTypeSymbol> owners = types;
            if (found.Count != 1 || !owners.Any(t => Primitives.IsMemberOf(found[0], t)))
                throw Invalid("Environment operation is not one member of a declared type or of its base types or interfaces: " +
                    manifest.Id + ": " + operation.Member);
            result.Add(found[0], operation);
        }
        return result;
    }

    /// <summary>Resolves operation actions and waits through SANY in the environment's own module.</summary>
    internal static void ResolveModule(string manifestPath, EnvironmentManifest manifest, Dictionary<string, string> pins,
        Func<string, IReadOnlyList<string>, TlaDocument> parse)
    {
        if (manifest.Module == null) return;
        string module = FilePaths.Full(manifest.Module, Path.GetDirectoryName(manifestPath)!);
        TlaDocument document = parse(module, [Path.GetDirectoryName(module)!]);
        if (!document.ParserSucceeded || document.Diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.StaleTranslation)))
            throw Invalid("Environment module did not resolve through SANY: " + manifest.Id + ": " +
                string.Join("; ", document.Diagnostics.Select(d => d.Message)));
        HashSet<string> operators = document.Endpoints.Where(e => e.Kind == "operator").Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        foreach (EnvironmentOperation operation in manifest.Operations)
            foreach (string name in (operation.Actions ?? []).Concat(new[] { operation.Wait }.OfType<string>()))
                if (!operators.Contains(name))
                    throw Invalid("Operator " + name + " of " + operation.Member + " is not defined by " + document.Module + ".");
        Files.Pin(pins, document.Path, document.Hash);
        foreach (var pin in document.DependencyFingerprints) Files.Pin(pins, pin.Key, pin.Value);
    }

    private static InputException Invalid(string message) => new(ErrorCode.InvalidWorkflowInput, message);
}
