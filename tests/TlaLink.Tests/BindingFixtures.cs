using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using TlaLink;

namespace TlaLink.Tests;

internal static class BindingFixtures
{
    public static void WriteIntegrated(string path, IntegratedManifest manifest)
    {
        string directory = Path.GetDirectoryName(path)!;
        List<string> sources = manifest.Sources.Select(p => FilePaths.Full(p, directory)).ToList();
        CSharpScope original = CSharpScope.Parse(sources);
        HashSet<string> proofIds = manifest.Bindings.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        List<string> modelAliases = original.Bindings.Where(b => b.Kind == SourceBindingKind.Step)
            .Select(b => b.Model).Distinct(StringComparer.Ordinal).ToList();
        foreach (SyntaxTree tree in original.Compilation.SyntaxTrees)
        {
            List<TextChange> changes = [];
            foreach (SyntaxTrivia trivia in tree.GetRoot().DescendantTrivia().Where(t => t.IsKind(SyntaxKind.MultiLineCommentTrivia)))
            {
                string text = trivia.ToString();
                List<LocatedAnnotation> parsed = Markup.Parse(text[2..^2], 0, 0);
                if (parsed.Count == 0) continue;
                List<BindAnnotation> keep = parsed.Select(a => a.Declaration).OfType<BindAnnotation>()
                    .Where(b => modelAliases.Contains(b.Target.Alias, StringComparer.Ordinal) &&
                        manifest.Models.Count > 0 && !proofIds.Contains(b.Id)).ToList();
                List<CaptureAnnotation> captures = parsed.Select(a => a.Declaration).OfType<CaptureAnnotation>()
                    .Where(c => keep.Any(b => b.Id == c.Binding)).ToList();
                List<ExcludeAnnotation> exclusions = parsed.Select(a => a.Declaration).OfType<ExcludeAnnotation>().ToList();
                string replacement = keep.Count + captures.Count + exclusions.Count == 0 ? "" :
                    "/* @link " + Markup.Encode(new() { Bind = keep, Capture = captures, Exclude = exclusions }) + " */";
                changes.Add(new(trivia.Span, replacement));
            }
            File.WriteAllText(tree.FilePath, tree.GetText().WithChanges(changes).ToString());
        }
        CSharpScope source = CSharpScope.Parse(sources);
        var symbols = IntegratedReview.SourceSymbols(source);
        Dictionary<string, List<TextChange>> additions = new(FilePaths.Comparer);
        foreach (DafnyBinding binding in manifest.Bindings)
        {
            var resolved = IntegratedReview.Resolve(symbols, binding.Source);
            SyntaxNode node = resolved.Symbol.DeclaringSyntaxReferences.Single().GetSyntax();
            if (node is VariableDeclaratorSyntax variable) node = variable.Parent!.Parent!;
            if (!additions.TryGetValue(node.SyntaxTree.FilePath, out List<TextChange>? edits))
                additions.Add(node.SyntaxTree.FilePath, edits = []);
            AnnotationPayload payload = new() { Bind = [new(binding.Id, new("proof", binding.Dafny))] };
            edits.Add(new(new TextSpan(node.SpanStart, 0), "/* @link " + Markup.Encode(payload) + " */ "));
        }
        foreach (var addition in additions)
        {
            SyntaxTree tree = source.Compilation.SyntaxTrees.Single(t => FilePaths.Comparer.Equals(t.FilePath, addition.Key));
            File.WriteAllText(addition.Key, tree.GetText().WithChanges(addition.Value).ToString());
        }
        List<FormalTarget> targets = [new("proof", "dafny", manifest.Proofs.Select(p => FilePaths.Full(p, directory)).ToList())];
        foreach (string alias in modelAliases.Where(_ => manifest.Models.Count > 0))
            targets.Add(new(alias, "tla", manifest.Models.Select(p => FilePaths.Full(p, directory)).ToList(),
                RequiredBindings: original.Bindings.Where(b => b.Model == alias).Select(b => b.BindingId).Distinct(StringComparer.Ordinal).ToList()));
        string scope = Path.Combine(directory, "scope-fixture.json");
        Files.WriteNew(scope, new CorrespondenceConfiguration(Schema.Version, "correspondence-scope", Markup.Version, targets, []));
        Files.WriteNew(path, manifest with { Scope = scope,
            TlaTargets = manifest.Models.Count == 0 ? [] : modelAliases });
    }
}
