namespace TlaLink;

public sealed record NativeDeclarationSpan(Endpoint Endpoint, int Start, int End, int Arity = 0);
public sealed record ResolvedCorrespondence(string Id, SourceBinding Implementation, FormalLinkDeclaration Specification);

public static class FormalCorrespondence
{
    public static List<FormalLinkDeclaration> Attach(string path, string text, IEnumerable<NativeDeclarationSpan> declarations,
        List<(int Start, int End, string Content)> comments)
    {
        List<NativeDeclarationSpan> native = declarations.OrderBy(d => d.Start).ThenByDescending(d => d.End).ToList();
        if (native.Any(d => d.Start < 0 || d.End <= d.Start || d.End > text.Length))
            throw new InputException(ErrorCode.InvalidModelBinding, "Native frontend returned an invalid declaration span: " + path);
        List<FormalLinkDeclaration> result = [];
        foreach (var comment in comments)
        {
            List<LocatedAnnotation> parsed = Markup.Parse(comment.Content, comment.Start, comment.End);
            if (parsed.Count == 0) continue;
            List<ExcludeAnnotation> exclusions = parsed.Select(a => a.Declaration).OfType<ExcludeAnnotation>().ToList();
            if (exclusions.Any(e => e.Source == null))
                throw new InputException(ErrorCode.InvalidAttachment, "Formal exclusions require an explicit source selector.");
            NativeDeclarationSpan endpoint = native.FirstOrDefault(d => d.Start >= comment.End &&
                Markup.OnlyTrivia(text, comment.End, d.Start, comments))
                ?? throw new InputException(ErrorCode.InvalidAttachment, "Formal bind must immediately precede an authoritative native declaration: " + path);
            foreach (BindAnnotation binding in parsed.Select(a => a.Declaration).OfType<BindAnnotation>())
            {
                List<CaptureAnnotation> captures = parsed.Select(a => a.Declaration).OfType<CaptureAnnotation>()
                    .Where(c => c.Binding == binding.Id).ToList();
                if (captures.Count > 0 && binding.Source == null)
                    throw new InputException(ErrorCode.InvalidAttachment, "Formal capture metadata requires a source selector.");
                result.Add(new(binding, endpoint.Endpoint, endpoint.Arity, captures.Count == 0 ? null : captures,
                    exclusions.Count == 0 ? null : exclusions));
            }
            if (parsed.Select(a => a.Declaration).OfType<CaptureAnnotation>()
                .Any(c => !parsed.Any(a => a.Declaration is BindAnnotation b && b.Id == c.Binding)))
                throw new InputException(ErrorCode.InvalidAttachment, "Capture metadata needs a binding in the same formal comment.");
        }
        foreach (var duplicate in result.GroupBy(b => b.Binding.Id, StringComparer.Ordinal).Where(g => g.Count() != 1))
            throw new InputException(ErrorCode.DuplicateBinding, "Duplicate formal correspondence ID: " + duplicate.Key);
        return result;
    }

    public static List<ResolvedCorrespondence> Match(CSharpScope source, IEnumerable<FormalLinkDeclaration> declarations,
        string alias, IEnumerable<string>? selected, List<Issue> diagnostics)
    {
        List<FormalLinkDeclaration> formal = declarations.Where(d => Markup.Name(d.Binding.Id).Alias == alias).ToList();
        HashSet<string>? selection = selected?.ToHashSet(StringComparer.Ordinal);
        foreach (FormalLinkDeclaration declaration in formal.Where(d => d.Binding.Source != null &&
            (selection == null || selection.Contains(d.Binding.Id))))
            source.AttachFormal(declaration);
        List<SourceBinding> implementation = source.Bindings.Where(b => b.Model == alias).ToList();
        HashSet<string> ids = selection ??
            formal.Select(d => d.Binding.Id).Concat(implementation.Select(b => b.BindingId)).ToHashSet(StringComparer.Ordinal);
        List<ResolvedCorrespondence> result = [];
        foreach (string id in ids)
        {
            if (Markup.Name(id).Alias != alias)
                throw new InputException(ErrorCode.InvalidModelBinding, "Selected correspondence belongs to a different scope: " + id);
            List<FormalLinkDeclaration> targets = formal.Where(d => d.Binding.Id == id).ToList();
            List<SourceBinding> sources = implementation.Where(b => b.BindingId == id).ToList();
            if (targets.Count != 1)
                diagnostics.Add(new(targets.Count == 0 ? ErrorCode.MissingModelEndpoint : ErrorCode.DuplicateBinding,
                    $"Correspondence {id} needs exactly one formal endpoint; found {targets.Count}."));
            if (sources.Select(s => (s.Path, s.Start, s.End)).Distinct().Count() != 1)
                diagnostics.Add(new(sources.Count == 0 ? ErrorCode.MissingImplementationEndpoint : ErrorCode.DuplicateBinding,
                    $"Correspondence {id} needs exactly one C# endpoint; found {sources.Count}."));
            if (targets.Count != 1 || sources.Count == 0) continue;
            foreach (SourceBinding binding in sources)
            {
                binding.Formal = targets[0];
                result.Add(new(id, binding, targets[0]));
            }
        }
        return result;
    }

    public static List<(int Start, int End, string Content)> Comments(string text, bool nested)
    {
        List<(int, int, string)> result = [];
        for (int i = 0; i < text.Length;)
        {
            bool verbatim = text[i] == '@' && i + 1 < text.Length && text[i + 1] == '"';
            if (text[i] == '"' || verbatim)
            {
                i += verbatim ? 2 : 1;
                while (i < text.Length)
                {
                    if (!verbatim && text[i] == '\\') { i += 2; continue; }
                    if (text[i++] != '"') continue;
                    if (verbatim && i < text.Length && text[i] == '"') { i++; continue; }
                    break;
                }
            }
            else if (text.AsSpan(i).StartsWith("//"))
            {
                int start = i, end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                result.Add((start, end, text[(start + 2)..end]));
                i = end;
            }
            else if (text.AsSpan(i).StartsWith("/*"))
            {
                int start = i, depth = 1;
                i += 2;
                while (i < text.Length && depth != 0)
                {
                    if (nested && text.AsSpan(i).StartsWith("/*")) { depth++; i += 2; }
                    else if (text.AsSpan(i).StartsWith("*/")) { depth--; i += 2; }
                    else i++;
                }
                if (depth != 0) throw new InputException(ErrorCode.InvalidAnnotation, "Unterminated formal block comment.");
                result.Add((start, i, text[(start + 2)..(i - 2)]));
            }
            else if (text[i] == '\'' && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) && text[i - 1] != '_'))
            {
                int end = i + 1;
                if (end < text.Length && text[end] == '\\')
                {
                    end += 2;
                    while (end < text.Length && text[end] != '\'' && text[end] != '\n') end++;
                }
                else if (end < text.Length) end += char.IsSurrogatePair(text, end) ? 2 : 1;
                i = end < text.Length && text[end] == '\'' ? end + 1 : i + 1;
            }
            else i++;
        }
        return result;
    }

    public static List<FormalLinkDeclaration> Dafny(List<DafnyDeclaration> declarations, Dictionary<string, string> pins,
        Func<string, string>? read = null)
    {
        List<FormalLinkDeclaration> result = [];
        foreach (var file in declarations.GroupBy(d => d.Path, FilePaths.Comparer))
        {
            string text = read == null ? Files.ReadPinned(file.Key, pins) : read(file.Key);
            result.AddRange(Attach(file.Key, text, file.Select(d => Span(d.Name, d.Module, d.Kind,
                new(d.Path, d.Line, d.EndLine, d.Column), text, d.Start, d.End)),
                Comments(text, nested: true)));
        }
        if (result.Any(d => d.Captures is { Count: > 0 }))
            throw new InputException(ErrorCode.InvalidAttachment, "Dafny bindings have no capture adapter.");
        return result;
    }

    public static List<FormalLinkDeclaration> P(List<PDeclaration> declarations, Dictionary<string, string> pins,
        Func<string, string>? read = null)
    {
        List<FormalLinkDeclaration> result = [];
        foreach (var file in declarations.GroupBy(d => d.Path, FilePaths.Comparer))
        {
            string text = read == null ? Files.ReadPinned(file.Key, pins) : read(file.Key);
            result.AddRange(Attach(file.Key, text, file.Select(d => Span(d.Name, "", d.Kind,
                new(d.Path, d.Line, d.EndLine, d.Column), text, d.Start, d.End)),
                Comments(text, nested: false)));
        }
        if (result.Any(d => d.Captures is { Count: > 0 }))
            throw new InputException(ErrorCode.InvalidAttachment, "P bindings have no capture adapter.");
        return result;
    }

    private static NativeDeclarationSpan Span(string name, string scope, string kind, Place location,
        string text, int start, int end)
    {
        if (start < 0 || end <= start || end > text.Length)
            throw new InputException(ErrorCode.InvalidModelBinding, "Native declaration span is invalid: " + name);
        return new(new(name, scope, kind, location, text[start..end]), start, end);
    }
}
