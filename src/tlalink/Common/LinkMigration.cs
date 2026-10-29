using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace TlaLink;

public sealed record LinkMigrationFile(string Path, string Kind, string BeforeSha256, string AfterSha256,
    int BindingCount, bool ExecutableTokensUnchanged);
public sealed record LinkMigrationResult(int SchemaVersion, string Kind, string Status, string Scope,
    bool ReadOnly, bool StripSource, int BindingCount, string ReasonPolicy, int ExclusionCount,
    List<LinkMigrationFile> Files, Dictionary<string, string> Fingerprints, List<string> Applied, string Boundary);

public static class LinkMigration
{
    private sealed record Change(string Path, byte[] Before, byte[] After, LinkMigrationFile Description);

    public static int Run(CliOptions options)
    {
        bool Boolean(string key, bool fallback) => options.Get(key, fallback ? "true" : "false") switch {
            "true" => true, "false" => false, _ => throw Invalid("--" + key + " must be true or false.")
        };
        bool apply = Boolean("apply", false), strip = Boolean("strip-source", true);
        string reasonPolicy = options.Get("reason-policy", "require-equal");
        if (reasonPolicy is not ("require-equal" or "preserve-both"))
            throw Invalid("--reason-policy must be require-equal or preserve-both.");
        string scopePath = FilePaths.Full(options.Required("scope"));
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        CorrespondenceConfiguration config = CorrespondenceInputs.Load(scopePath, pins);
        List<FormalTarget> targets = options.Optional("target") is string alias
            ? [config.Targets.SingleOrDefault(t => t.Id == alias) ?? throw Invalid("Unknown target: " + alias)]
            : config.Targets;
        List<string> paths = Files.Discover(options.Many("source"), ".cs");
        SourceInventoryContext context = SourceInventory.LoadContext(paths, options);
        CSharpScope source = context.Scope;
        source.RequireCompilable();
        if (source.Diagnostics.Any(d => d.Severity == "error"))
            throw Invalid("Invalid source annotations; migration requires an unambiguous resolved source scope.");
        foreach (var pin in source.Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
        string work = Directory.CreateTempSubdirectory("tlalink-link-migration-").FullName;
        bool complete = false;
        try
        {
            NativeLinkIndex native = NativeLinkIndex.Load(targets, options, work, pins);
            if (native.Diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.ToolFailure, ErrorCode.StaleTranslation)))
                throw Invalid("Native index is incomplete: " + string.Join("; ", native.Diagnostics.Select(d => d.Message)));
            HashSet<string> aliases = targets.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            List<IGrouping<string, SourceBinding>> groups = source.Bindings
                .Where(b => aliases.Contains(b.Model) && b.Declaration.Source == null)
                .GroupBy(b => b.BindingId, StringComparer.Ordinal).ToList();
            Dictionary<string, List<(int Start, int End, AnnotationPayload Payload)>> payloads = new(FilePaths.Comparer);
            Dictionary<string, int> nativeCounts = new(FilePaths.Comparer);
            foreach (var group in groups)
            {
                if (group.Select(b => (b.Path, b.Node.Span)).Distinct().Count() != 1)
                    throw Invalid("Multiple source attachments for " + group.Key);
                SourceBinding binding = group.First();
                List<FormalLinkDeclaration> formal = native.Declarations.Where(d => d.Binding.Id == group.Key).ToList();
                if (formal.Count != 1) throw Invalid("Expected one native counterpart for " + group.Key + "; found " + formal.Count);
                FormalLinkDeclaration endpoint = formal[0];
                if (endpoint.Binding.Source != null) throw Invalid("Connection already has a formal source owner: " + group.Key);
                string path = endpoint.Endpoint.Location.Path;
                string language = targets.Single(t => t.Id == binding.Model).Language;
                if (!payloads.TryGetValue(path, out var filePayloads))
                {
                    string text = Files.ReadPinned(path, pins);
                    filePayloads = Links.Comments(text, language).Where(c =>
                            c.Content.TrimStart().StartsWith("@link", StringComparison.Ordinal))
                        .Select(c => (c.Start, c.End, JsonSerializer.Deserialize<AnnotationPayload>(
                            c.Content.Trim()[5..], Markup.AnnotationJson) ?? throw Invalid("Missing formal payload."))).ToList();
                    payloads.Add(path, filePayloads);
                }
                var comment = filePayloads.Single(p => p.Payload.Bind.Any(b => b.Id == group.Key));
                BindAnnotation existing = comment.Payload.Bind.Single(b => b.Id == group.Key);
                string? reason = existing.Reason ?? binding.Declaration.Reason;
                if (existing.Reason != null && binding.Declaration.Reason != null &&
                    existing.Reason != binding.Declaration.Reason)
                {
                    if (reasonPolicy != "preserve-both") throw Invalid("Conflicting reasons require explicit review: " + group.Key);
                    reason = "Formal endpoint: " + existing.Reason + "\nSource endpoint: " + binding.Declaration.Reason;
                }
                PreparationTarget selector = Selector(source, binding);
                comment.Payload.Bind[comment.Payload.Bind.FindIndex(b => b.Id == group.Key)] = existing with {
                    Source = selector, Reason = reason
                };
                List<CaptureAnnotation> captures = group.Where(b => b.Capture != null).Select(b => b.Capture!).ToList();
                if (language != "tla" && captures.Count > 0) throw Invalid("No native capture adapter for " + group.Key);
                if (comment.Payload.Capture.Any(c => c.Binding == group.Key))
                    throw Invalid("Existing formal captures require explicit reconciliation: " + group.Key);
                comment.Payload.Capture.AddRange(captures);
                nativeCounts[path] = nativeCounts.GetValueOrDefault(path) + 1;
            }
            List<Change> changes = [];
            foreach (SourceExclusion excluded in source.Exclusions)
            {
                string id = excluded.Semantics.GetDeclaredSymbol(excluded.Node)?.GetDocumentationCommentId()
                    ?? throw Invalid("Exclusion needs a compiler declaration selector: " + excluded.Declaration.Id);
                foreach (string from in excluded.Declaration.From.Where(aliases.Contains))
                {
                    FormalLinkDeclaration anchor = native.Declarations.FirstOrDefault(d => Markup.Name(d.Binding.Id).Alias == from)
                        ?? throw Invalid("Exclusion target has no native binding anchor: " + from);
                    string path = anchor.Endpoint.Location.Path;
                    if (!payloads.TryGetValue(path, out var filePayloads))
                    {
                        string language = targets.Single(t => t.Id == from).Language;
                        string text = Files.ReadPinned(path, pins);
                        filePayloads = Links.Comments(text, language).Where(c => c.Content.TrimStart().StartsWith("@link", StringComparison.Ordinal))
                            .Select(c => (c.Start, c.End, JsonSerializer.Deserialize<AnnotationPayload>(
                                c.Content.Trim()[5..], Markup.AnnotationJson) ?? throw Invalid("Missing formal payload."))).ToList();
                        payloads.Add(path, filePayloads);
                    }
                    var comment = filePayloads.Single(p => p.Payload.Bind.Any(b => b.Id == anchor.Binding.Id));
                    comment.Payload.Exclude.Add(excluded.Declaration with { Source = new(id), From = [from] });
                }
            }
            foreach (var file in payloads)
            {
                string language = targets.First(t => t.Inputs.Contains(file.Key, FilePaths.Comparer) ||
                    native.Declarations.Any(d => d.Endpoint.Location.Path == file.Key && Markup.Name(d.Binding.Id).Alias == t.Id)).Language;
                string text = Files.Read(file.Key);
                List<TextChange> edits = file.Value.Select(p => new TextChange(TextSpan.FromBounds(p.Start, p.End),
                    (language == "tla" ? "(* @link " : "/* @link ") + Markup.Encode(p.Payload) +
                    (language == "tla" ? " *)" : " */"))).ToList();
                Add(file.Key, "formal", SourceText.From(text).WithChanges(edits).ToString(), nativeCounts.GetValueOrDefault(file.Key));
            }
            HashSet<string> moved = groups.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            if (strip)
                foreach (SyntaxTree tree in source.Compilation.SyntaxTrees)
                {
                    List<TextChange> edits = [];
                    foreach (SyntaxTrivia trivia in tree.GetRoot().DescendantTrivia().Where(t => t.IsKind(SyntaxKind.MultiLineCommentTrivia)))
                    {
                        List<LocatedAnnotation> parsed = Markup.Parse(trivia.ToString()[2..^2], trivia.SpanStart, trivia.Span.End);
                        if (!parsed.Any(a => a.Declaration is BindAnnotation b && moved.Contains(b.Id) ||
                            a.Declaration is ExcludeAnnotation e && e.From.Any(aliases.Contains))) continue;
                        AnnotationPayload keep = new() {
                            Bind = parsed.Select(a => a.Declaration).OfType<BindAnnotation>().Where(b => !moved.Contains(b.Id)).ToList(),
                            Capture = parsed.Select(a => a.Declaration).OfType<CaptureAnnotation>().Where(c => !moved.Contains(c.Binding)).ToList(),
                            Exclude = parsed.Select(a => a.Declaration).OfType<ExcludeAnnotation>()
                                .Select(e => e with { From = e.From.Where(f => !aliases.Contains(f)).ToList() })
                                .Where(e => e.From.Count > 0).ToList()
                        };
                        string replacement = keep.Bind.Count + keep.Capture.Count + keep.Exclude.Count == 0 ? "" : "/* @link " + Markup.Encode(keep) + " */";
                        edits.Add(new(trivia.Span, replacement));
                    }
                    if (edits.Count == 0) continue;
                    string text = tree.GetText().WithChanges(edits).ToString();
                    SyntaxTree changed = CSharpSyntaxTree.ParseText(text, (CSharpParseOptions)tree.Options, tree.FilePath);
                    if (!tree.GetRoot().DescendantTokens().Select(t => (t.RawKind, t.Text))
                        .SequenceEqual(changed.GetRoot().DescendantTokens().Select(t => (t.RawKind, t.Text))))
                        throw Invalid("Migration changed executable C# tokens: " + tree.FilePath);
                    Add(tree.FilePath, "source", text, groups.Count(g => FilePaths.Comparer.Equals(g.First().Path, tree.FilePath)));
                }
            IntegratedInputs.CheckPins(pins);
            List<string> applied = [];
            if (apply)
                foreach (Change change in changes)
                {
                    Write(change.Path, change.Before, change.After);
                    applied.Add(change.Path);
                }
            LinkMigrationResult result = new(Schema.Version, "link-migration-result", apply ? "applied" : "planned",
                scopePath, !apply, strip, groups.Count, reasonPolicy,
                source.Exclusions.Count(e => e.Declaration.From.Any(aliases.Contains)),
                changes.Select(c => c.Description).ToList(), pins, applied,
                "Moves explicit annotations using native and Roslyn identities; executable tokens are preserved. Does not approve correspondence, renew historical evidence, update independent requirements or provide multi-file crash transactions.");
            if (options.Optional("output") is string output) Files.WriteNew(FilePaths.Full(output), result);
            ProjectWorkflow.Render(result, options);
            complete = true;
            return 0;

            void Add(string path, string kind, string text, int count)
            {
                byte[] original = File.ReadAllBytes(path);
                byte[] after = Files.EncodeLike(text, original);
                string beforeHash = Hash(original);
                if (!pins.TryGetValue(path, out string? pin) || beforeHash != pin)
                    throw Invalid("Input changed during migration planning: " + path);
                if (!original.AsSpan().SequenceEqual(after))
                    changes.Add(new(path, original, after, new(path, kind, beforeHash, Hash(after), count, true)));
            }
        }
        finally
        {
            if (complete || !Directory.EnumerateFileSystemEntries(work).Any()) Directory.Delete(work, recursive: true);
        }
    }

    private static PreparationTarget Selector(CSharpScope source, SourceBinding binding)
    {
        string id = CSharpScope.DeclarationId(binding);
        PreparationTarget declaration = new(id);
        SyntaxNode owner = SourcePreparation.CSharpTarget(source.Compilation, null, declaration);
        if (binding.Kind != SourceBindingKind.Step)
        {
            if (owner.Span != binding.Node.Span) throw Invalid("Declaration selector does not identify the original attachment: " + binding.BindingId);
            return declaration;
        }
        List<SyntaxNode> candidates = owner.DescendantNodesAndSelf().Where(n => n.RawKind == binding.Node.RawKind).ToList();
        int index = candidates.FindIndex(n => n.Span == binding.Node.Span);
        if (index < 0) throw Invalid("Cannot locate source syntax for " + binding.BindingId);
        PreparationTarget selector = new(id, binding.Node.Kind().ToString(), Match: index + 1, ExpectedMatches: candidates.Count);
        if (SourcePreparation.CSharpTarget(source.Compilation, null, selector).Span != binding.Node.Span)
            throw Invalid("Source selector did not round-trip: " + binding.BindingId);
        return selector;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void Write(string path, byte[] before, byte[] after)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        byte[] current = new byte[stream.Length];
        stream.ReadExactly(current);
        if (!current.AsSpan().SequenceEqual(before)) throw Invalid("Input changed before exclusive migration edit: " + path);
        stream.Position = 0;
        stream.Write(after);
        stream.SetLength(after.Length);
        stream.Flush(flushToDisk: true);
    }

    private static InputException Invalid(string message) => new(ErrorCode.InvalidAnnotation, message);
}
