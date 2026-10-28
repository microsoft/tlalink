using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using TlaLink;

namespace TlaLink.Tests;

internal static class BindingFixtures
{
    public static void WriteIntegrated(string path, IntegratedManifest manifest, string dafny)
    {
        string directory = Path.GetDirectoryName(path)!;
        List<DafnyBinding> selected = manifest.Bindings.Select(b => b with {
            Id = b.Id.Contains("::", StringComparison.Ordinal) ? b.Id : "proof::" + b.Id
        }).ToList();
        Dictionary<string, string> renamed = manifest.Bindings.Zip(selected).ToDictionary(p => p.First.Id, p => p.Second.Id);
        manifest = manifest with { Bindings = selected, SourceObligations = manifest.SourceObligations
            .Select(o => o with { Binding = renamed[o.Binding] }).ToList() };
        DafnyEvidenceState authoring = new();
        DafnyBackend backend = new(dafny, Path.Combine(directory, "authoring-" + Guid.NewGuid().ToString("N")), 60, authoring);
        Directory.CreateDirectory(backend.Output);
        backend.Initialize();
        DafnyIndexResult index = backend.Index(manifest.Proofs.Select(p => FilePaths.Full(p, directory)).ToList());
        List<FormalLinkDeclaration> existing = FormalCorrespondence.Dafny(index.Declarations, authoring.Fingerprints);
        manifest = manifest with { Bindings = manifest.Bindings.Select(b => b with {
            Dafny = string.IsNullOrEmpty(b.Dafny) ? existing.Single(f => f.Binding.Id == b.Id).Endpoint.Name : b.Dafny
        }).ToList() };
        foreach (var proof in index.Declarations.GroupBy(d => d.Path, FilePaths.Comparer))
        {
            string text = Files.Read(proof.Key);
            List<TextChange> edits = FormalCorrespondence.Comments(text, nested: true)
                .Where(c => c.Content.TrimStart().StartsWith("@link", StringComparison.Ordinal))
                .Select(c => new TextChange(new TextSpan(c.Start, c.End - c.Start), "")).ToList();
            foreach (DafnyBinding binding in manifest.Bindings)
            {
                var matching = proof.Where(d => d.Name == binding.Dafny).ToList();
                if (matching.Count == 0) continue;
                if (matching.Count != 1) throw new InvalidOperationException("Ambiguous native fixture declaration.");
                edits.Add(new(new TextSpan(matching[0].Start, 0), "/* @link " +
                    Markup.Encode(new() { Bind = [new(binding.Id)] }) + " */ "));
            }
            File.WriteAllText(proof.Key, Microsoft.CodeAnalysis.Text.SourceText.From(text).WithChanges(edits).ToString());
        }
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
                    .Where(b => modelAliases.Contains(Markup.Name(b.Id).Alias, StringComparer.Ordinal) &&
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
            string id = binding.Id.Contains("::", StringComparison.Ordinal) ? binding.Id : "proof::" + binding.Id;
            AnnotationPayload payload = new() { Bind = [new(id)] };
            edits.Add(new(new TextSpan(node.SpanStart, 0), "/* @link " + Markup.Encode(payload) + " */ "));
        }
        foreach (var addition in additions)
        {
            SyntaxTree tree = source.Compilation.SyntaxTrees.Single(t => FilePaths.Comparer.Equals(t.FilePath, addition.Key));
            File.WriteAllText(addition.Key, tree.GetText().WithChanges(addition.Value).ToString());
        }
        List<FormalTarget> targets = manifest.Bindings.Select(b => Markup.Name(b.Id).Alias).Distinct(StringComparer.Ordinal)
            .Select(alias => new FormalTarget(alias, "dafny", manifest.Proofs.Select(p => FilePaths.Full(p, directory)).ToList())).ToList();
        foreach (string alias in modelAliases.Where(_ => manifest.Models.Count > 0))
            targets.Add(new(alias, "tla", manifest.Models.Select(p => FilePaths.Full(p, directory)).ToList(),
                RequiredBindings: original.Bindings.Where(b => b.Model == alias).Select(b => b.BindingId).Distinct(StringComparer.Ordinal).ToList()));
        string scope = Path.Combine(directory, "scope-fixture.json");
        Files.WriteNew(scope, new CorrespondenceConfiguration(Schema.Version, "correspondence-scope", Markup.Version, targets, []));
        Files.WriteNew(path, manifest with { Scope = scope,
            TlaTargets = manifest.Models.Count == 0 ? [] : modelAliases });
    }
}
