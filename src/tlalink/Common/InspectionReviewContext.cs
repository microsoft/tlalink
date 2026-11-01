using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TlaLink;

public sealed record InspectionMemberAttachment(string Binding, string Endpoint, Place Location,
    List<CapturePoint> CapturePoints);
public sealed record InspectionMemberGroup(InspectionMember Member, List<InspectionLine> Lines,
    List<InspectionMemberAttachment> Attachments, int MatchingAttachments);
public sealed record InspectionCalleeSource(string Key, string Symbol, Place Location, string Status,
    List<InspectionLine> Lines, int TotalLines, int OmittedLines);

internal static class InspectionReviewContext
{
    internal static string? MemberSymbol(SourceBinding binding, SyntaxNode member)
    {
        SyntaxNode declaration = member is FieldDeclarationSyntax field && field.Declaration.Variables.Count == 1
            ? field.Declaration.Variables[0] : member;
        return binding.Semantics.GetDeclaredSymbol(declaration)?.GetDocumentationCommentId();
    }

    internal static List<InspectionMemberGroup> GroupMembers(List<InspectedEndpoint> entries,
        List<InspectedEndpoint> matching)
    {
        bool Same(InspectionMember? left, InspectionMember? right) => left != null && right != null &&
            left.Symbol == right.Symbol && FilePaths.Comparer.Equals(left.Location.Path, right.Location.Path) &&
            left.Location.Line == right.Location.Line && left.Location.Column == right.Location.Column;
        List<InspectionMember> members = [];
        foreach (InspectionMember member in entries.SelectMany(e => e.Links).Select(l => l.Source?.Member).OfType<InspectionMember>())
            if (!members.Any(m => Same(m, member))) members.Add(member);
        List<InspectionMemberGroup> groups = [];
        foreach (InspectionMember member in members.OrderBy(m => m.Location.Path, FilePaths.Comparer)
            .ThenBy(m => m.Location.Line).ThenBy(m => m.Location.Column))
        {
            var links = entries.SelectMany(e => e.Links.Select(l => (Endpoint: e.Model.Name, Link: l)))
                .Where(e => Same(e.Link.Source?.Member, member))
                .OrderBy(e => e.Link.Source!.Location.Line).ThenBy(e => e.Link.Source!.Location.Column)
                .ThenBy(e => e.Link.Binding, StringComparer.Ordinal).ToList();
            List<InspectionLine> lines = links.SelectMany(e => e.Link.Source!.Lines)
                .Where(l => l.Number >= member.Location.Line && l.Number <= member.Location.EndLine)
                .GroupBy(l => l.Number).OrderBy(g => g.Key)
                .Select(g => new InspectionLine(g.Key, g.First().Text, g.Any(l => l.Selected))).ToList();
            groups.Add(new(member, lines, links.Select(e => new InspectionMemberAttachment(e.Link.Binding, e.Endpoint,
                e.Link.Source!.Location, e.Link.Captures.Where(c => c.Point != null).Select(c => c.Point!.Value).ToList())).ToList(),
                matching.SelectMany(e => e.Links).Count(l => Same(l.Source?.Member, member))));
        }
        return groups;
    }

    internal static List<InspectionCalleeSource> ExpandCallees(CSharpScope source, List<InspectedEndpoint> entries,
        InspectionQuery query)
    {
        List<InspectionCalleeSource> catalogue = [];
        Dictionary<string, List<InspectionCalleeSource>> byTarget = new(StringComparer.Ordinal);
        int expanded = 0;
        List<InspectionCalleeSource> Read(string id)
        {
            if (byTarget.TryGetValue(id, out var cached)) return cached;
            List<InspectionCalleeSource> result = [];
            foreach (ISymbol symbol in SourceSymbols.ResolveId(id, source.Compilation))
            {
                IEnumerable<SyntaxReference> references = symbol.DeclaringSyntaxReferences;
                if (symbol is IMethodSymbol { PartialImplementationPart: { } implementation })
                    references = references.Concat(implementation.DeclaringSyntaxReferences);
                foreach (SyntaxReference reference in references.OrderBy(r => r.SyntaxTree.FilePath, FilePaths.Comparer)
                    .ThenBy(r => r.Span.Start))
                {
                    SyntaxNode node = reference.GetSyntax();
                    string path = node.SyntaxTree.FilePath;
                    if (!source.Texts.TryGetValue(path, out string? text)) continue;
                    Place location = Files.At(path, text, node.SpanStart, node.Span.End);
                    if (result.Any(c => c.Location == location)) continue;
                    var lines = node.SyntaxTree.GetText().Lines;
                    int first = lines.GetLineFromPosition(node.SpanStart).LineNumber;
                    int last = lines.GetLineFromPosition(Math.Max(node.SpanStart, node.Span.End - 1)).LineNumber;
                    int total = last - first + 1;
                    bool omitted = expanded >= query.CalleeLimit;
                    List<InspectionLine> excerpt = omitted ? [] : Enumerable.Range(first, Math.Min(total, query.CalleeMaxLines))
                        .Select(i => new InspectionLine(i + 1, lines[i].ToString(), false)).ToList();
                    bool body = node is BaseMethodDeclarationSyntax { Body: not null } or
                        BaseMethodDeclarationSyntax { ExpressionBody: not null } or
                        LocalFunctionStatementSyntax { Body: not null } or LocalFunctionStatementSyntax { ExpressionBody: not null };
                    string status = omitted ? "budget-omitted" : excerpt.Count < total ? "truncated" :
                        body ? "included" : "declaration-only";
                    InspectionCalleeSource entry = new("callee-" + (catalogue.Count + 1), id, location, status,
                        excerpt, total, total - excerpt.Count);
                    catalogue.Add(entry);
                    result.Add(entry);
                    if (!omitted) expanded++;
                }
            }
            byTarget[id] = result;
            return result;
        }
        foreach (InspectedEndpoint endpoint in entries)
        {
            for (int index = 0; index < endpoint.Links.Count; index++)
            {
                InspectedLink link = endpoint.Links[index];
                if (link.Source?.Calls == null) continue;
                List<InspectionCall> calls = [];
                foreach (InspectionCall call in link.Source.Calls)
                {
                    List<InspectionCalleeSource> targets = call.Site.Target == null ? [] : Read(call.Site.Target);
                    calls.Add(call with
                    {
                        ExpansionStatus = call.Site.Target == null ? "no-declaration-id" : targets.Count == 0 ? "no-selected-source" :
                            targets.Any(t => t.Status == "budget-omitted") ? "budget-omitted" :
                            targets.Any(t => t.Status == "truncated") ? "truncated" :
                            targets.All(t => t.Status == "declaration-only") ? "declaration-only" : "included",
                        CalleeKeys = targets.Select(t => t.Key).ToList()
                    });
                }
                endpoint.Links[index] = link with { Source = link.Source with { Calls = calls } };
            }
        }
        return catalogue;
    }
}
