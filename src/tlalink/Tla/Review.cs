using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TlaLink;

public static class Review
{
    public static Packet Build(string command, CSharpScope source, List<ModelBinding> model,
        IEnumerable<Issue> modelDiagnostics, Dictionary<string, string> modelPins, string? selected = null, IEnumerable<Endpoint>? inventory = null)
    {
        Packet packet = new(command);
        packet.Diagnostics.AddRange(source.Diagnostics);
        packet.Diagnostics.AddRange(modelDiagnostics);
        packet.Dispositions.AddRange(source.Dispositions);
        packet.OmissionMatches.AddRange(source.OmissionMatches);
        packet.SourceBindings.AddRange(source.DeclaredLinks);
        packet.SelectedTargets.AddRange(source.SelectedTargets ?? source.Bindings.Select(b => b.Model).ToHashSet(StringComparer.Ordinal));
        packet.Inventory.AddRange(inventory ?? model.Select(m => m.Endpoint));
        foreach (var pair in source.Fingerprints.Concat(modelPins)) Files.Pin(packet.Fingerprints, pair.Key, pair.Value);
        foreach (var group in source.Bindings.GroupBy(b => b.Key).Where(g => g.Count() > 1))
            packet.Diagnostics.Add(new(ErrorCode.DuplicateBinding, "Duplicate C# binding: " + group.Key, group.First().Endpoint.Location));
        foreach (var group in model.GroupBy(b => $"{b.Model}:{b.Tag.Kind}:{b.Tag.Identity}").Where(g => g.Count() > 1))
            packet.Diagnostics.Add(new(ErrorCode.DuplicateBinding, "Duplicate model binding: " + group.Key, group.First().Endpoint.Location));
        foreach (SourceBinding binding in source.Bindings.Where(b => source.SelectedTargets == null || source.SelectedTargets.Contains(b.Model)))
        {
            List<ModelBinding> targets = model.Where(m => m.Model == binding.Model && m.Tag.Kind == binding.Tag.Kind && m.Tag.Identity == binding.Tag.Identity).ToList();
            if (targets.Count == 0)
            {
                packet.Diagnostics.Add(new(ErrorCode.MissingModelEndpoint, "Missing model endpoint: " + binding.Key, binding.Endpoint.Location));
                continue;
            }
            if (targets.Count != 1) continue;
            if (targets[0].Tag.Options.GetValueOrDefault("visibility") == "hidden")
                packet.Diagnostics.Add(new(ErrorCode.MissingModelEndpoint, "A hidden model action cannot be bound to an observed C# step.", binding.Endpoint.Location));
            List<SourceBinding> states = source.Bindings.Where(s => s.Model == binding.Model && s.Tag.Kind == "state" && s.Symbol is not null).ToList();
            HashSet<string> stateSymbols = states.Select(s => CSharpScope.SymbolId(s.Symbol!)).ToHashSet();
            List<Access> accesses = source.Accesses.Where(a => stateSymbols.Contains(a.Symbol)).ToList();
            List<string> questions = [
                "Do source branches, exceptional cleanup and external effects implement exactly this model action?",
                "Can a competing operation observe an intermediate state? Follow calls beyond this explicit compilation scope.",
                "Are actor/operation identities stable, and is every relevant publication represented?",
                "Do guard claims match all accesses, including those outside supplied files?"
            ];
            packet.Links.Add(new(binding.Key, binding.Tag.Identity, binding.Tag.Kind, binding.Model, binding.Endpoint,
                targets[0].Endpoint, binding.Declaration, binding.Capture, binding.Locks, CSharpScope.Boundaries(binding), accesses, questions));
        }
        foreach (ModelBinding binding in model.Where(m => m.Tag.Options.GetValueOrDefault("visibility") != "hidden" && !source.Bindings.Any(s =>
            s.Model == m.Model && s.Tag.Kind == m.Tag.Kind && s.Tag.Identity == m.Tag.Identity)))
            packet.Diagnostics.Add(new(ErrorCode.UnboundModelEndpoint, $"Model endpoint has no C# binding: {binding.Model}:{binding.Tag.Kind}:{binding.Tag.Identity}", binding.Endpoint.Location));
        foreach (SourceBinding state in source.Bindings.Where(b => b.Tag.Kind == "state" && b.Symbol is not null &&
            (source.SelectedTargets == null || source.SelectedTargets.Contains(b.Model))))
        {
            foreach (SyntaxTree tree in source.Compilation.SyntaxTrees)
            {
                SemanticModel semantics = source.Compilation.GetSemanticModel(tree);
                foreach (IdentifierNameSyntax identifier in tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (!SymbolEqualityComparer.Default.Equals(semantics.GetSymbolInfo(identifier).Symbol?.OriginalDefinition, state.Symbol?.OriginalDefinition)) continue;
                    SyntaxNode expression = identifier.Parent is MemberAccessExpressionSyntax member && member.Name == identifier ? member : identifier;
                    if (!CSharpScope.IsWrite(expression)) continue;
                    bool covered = source.Bindings.Any(b => b.Tag.Kind == "step" && b.Model == state.Model && b.Path == tree.FilePath &&
                        b.Node.Span.Contains(expression.Span));
                    if (!covered) packet.Diagnostics.Add(new(ErrorCode.UncoveredStateWrite, $"Mapped state write outside a linked step: {state.Key}",
                        Files.At(tree.FilePath, source.Texts[tree.FilePath], expression.SpanStart, expression.Span.End)));
                }
            }
        }
        if (packet.Links.Count == 0) packet.Diagnostics.Add(new(ErrorCode.NoLinks, "No C#/model links in explicit scope."));
        packet.LinksStatus = packet.Diagnostics.Any(d => d.Is(ErrorCode.StaleTranslation, ErrorCode.ToolFailure)) ? "incomplete" :
            packet.Diagnostics.Any(d => d.Severity == "error") ? "failed" :
            packet.Diagnostics.Any(d => d.Is(ErrorCode.UnboundModelEndpoint, ErrorCode.NoLinks, ErrorCode.UncoveredStateWrite, ErrorCode.UnresolvedCompilation)) ? "incomplete" : "passed";
        if (selected is not null)
        {
            List<Link> matches = packet.Links.Where(l => l.Kind == "step" && (l.Identity == selected || l.Key == selected)).ToList();
            if (matches.Count != 1) throw new InputException(ErrorCode.ReviewInputChanged, "Step selection must resolve to exactly one linked step.");
            packet.Links = matches;
        }
        return packet;
    }
}
