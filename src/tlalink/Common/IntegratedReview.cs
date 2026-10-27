using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TlaLink;

public static class IntegratedReview
{
    public static int Run(CliOptions options)
    {
        string output = FilePaths.Full(options.Required("output"));
        if (Directory.Exists(output) || File.Exists(output))
            throw new InputException(ErrorCode.InvalidIntegratedInput, "Integrated runs require a new output directory.");
        Directory.CreateDirectory(output);
        IntegratedPacket packet = new(options.Command);
        try
        {
            packet.Bindings = FilePaths.Full(options.Required("bindings"));
            IntegratedManifest manifest = IntegratedInputs.Load(packet.Bindings, packet.Fingerprints);
            Program.PinTool(packet.Fingerprints);
            packet.Fingerprints["tool:integrated-schema"] = Schema.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
            CSharpScope source = CSharpScope.Parse(manifest.Sources, options.Many("reference"),
                options.Has("define") ? options.Many("define") : null);
            source.ApplyCorrespondence(manifest.Configuration!);
            packet.Dispositions.AddRange(source.Dispositions);
            packet.OmissionMatches.AddRange(source.OmissionMatches);
            packet.SourceBindings.AddRange(source.DeclaredLinks);
            foreach (var pin in source.Fingerprints) Files.Pin(packet.Fingerprints, pin.Key, pin.Value);
            packet.Diagnostics.AddRange(source.Diagnostics);
            source.RequireCompilable();
            if (source.Diagnostics.Any(d => d.Severity == "error"))
                throw new InputException(ErrorCode.InvalidIntegratedBinding, "Invalid C# annotations; see source diagnostics.");
            manifest = IntegratedInputs.ResolveBindings(manifest, source);
            DafnyBackend backend = new(options.Required("dafny"), output, options.Timeout, packet);
            backend.Initialize();
            DafnyIndexResult index = backend.Index(manifest.Proofs);
            packet.Inventory.AddRange(index.Declarations);
            List<(ISymbol Symbol, Endpoint Endpoint)> symbols = SourceSymbols(source);
            packet.SourceInventory.AddRange(symbols.Select(s => s.Endpoint));
            foreach (DafnyBinding binding in manifest.Bindings)
            {
                List<DafnyDeclaration> declarations = index.Declarations.Where(d => d.Name == binding.Dafny).ToList();
                if (declarations.Count != 1)
                    throw new InputException(ErrorCode.InvalidIntegratedBinding, $"{binding.Id}: expected one resolved Dafny declaration '{binding.Dafny}', found {declarations.Count}.");
                SourceBinding inline = source.Bindings.Single(b => b.BindingId == binding.Id);
                packet.Links.Add(new(binding.Id, inline.Endpoint, declarations[0], binding.Adapter));
                if (binding.Adapter != null) Resolve(symbols, binding.Adapter);
                if (binding.Cases.Count > 0)
                {
                    if (binding.Adapter == null && inline.Kind != SourceBindingKind.Declaration)
                        throw new InputException(ErrorCode.InvalidOracleInput,
                            "Statement/expression bindings require an explicit oracle adapter: " + binding.Id);
                    DafnyConformance.CheckSignature(Resolve(symbols, binding.Adapter ?? binding.Source).Symbol, declarations[0]);
                }
            }
            if (manifest.Models.Count > 0)
            {
                string jar = FilePaths.Full(options.Required("jar"));
                ModelScope models = ModelScope.Load(manifest.Models, jar, options.Timeout);
                source.SelectedTargets = manifest.TlaTargets.ToHashSet(StringComparer.Ordinal);
                foreach (string alias in manifest.TlaTargets)
                    models.BindSources(source, manifest.Configuration!, alias);
                Files.Pin(models.Fingerprints, jar, Files.Hash(jar));
                string adapter = Path.Combine(AppContext.BaseDirectory, "TlaIndex.java");
                Files.Pin(models.Fingerprints, adapter, Files.Hash(adapter));
                packet.Tla = Review.Build(options.Command, source, models.Bindings, models.Diagnostics, models.Fingerprints,
                    null, models.Inventory);
                foreach (var pin in packet.Tla.Fingerprints) Files.Pin(packet.Fingerprints, pin.Key, pin.Value);
                if (packet.Tla.LinksStatus != "passed")
                    throw new InputException(ErrorCode.IntegratedTlaLinksNotReady, "TLA+ source/model links are not ready; see the nested TLA packet.");
            }
            foreach (Correspondence claim in manifest.Correspondences)
            {
                if (!packet.Links.Any(l => l.Id == claim.DafnyBinding) ||
                    packet.Tla == null || !packet.Tla.Links.Any(l => l.Kind == "step" && l.Key == claim.TlaStep))
                    throw new InputException(ErrorCode.InvalidCorrespondence, "Correspondence must reference an existing Dafny binding and linked TLA+ step: " + claim.Id);
                packet.Correspondences.Add(new(claim));
            }
            packet.LinksStatus = "passed";
            packet.SourceObligations.AddRange(SourceObligations.Check(source, manifest));
            foreach (SourceObligationResult premise in packet.SourceObligations.Where(p => p.Status != "passed"))
                packet.Diagnostics.Add(new(premise.Status == "rejected" ? ErrorCode.SourceObligationRejected :
                    ErrorCode.SourceObligationIncomplete, premise.Obligation.Id + ": " + premise.Reason));
            if (options.Optional("baseline") is string baseline) Baseline(baseline, packet);
            if (packet.LinksStatus == "passed" && packet.SourceObligationsStatus is "passed" or "not-specified" &&
                options.Command != "inspect-integrated" && backend.Verify(manifest.Proofs) &&
                options.Command == "test-integrated")
                DafnyConformance.Run(manifest, source, symbols, backend, packet);
            IntegratedInputs.CheckPins(packet.Fingerprints);
            IntegratedInputs.CheckPins(packet.GeneratedFiles);
            packet.ProvenanceStatus = "consistent";
            packet.Status = packet.LinksStatus != "passed" ? "incomplete" :
                packet.SourceObligationsStatus == "incomplete" ? "incomplete" :
                packet.SourceObligationsStatus == "rejected" ? "rejected" :
                packet.ProofStatus == "rejected" || packet.AuditStatus == "findings" || packet.ConformanceStatus == "rejected" ? "rejected" :
                options.Command == "inspect-integrated" ? "ready" :
                packet.ProofStatus == "verified" && packet.AuditStatus == "passed" &&
                (options.Command != "test-integrated" || packet.ConformanceStatus == "conforms") ? "passed" : "incomplete";
        }
        catch (Exception error) when (error is InputException or IOException or UnauthorizedAccessException or JsonException
            or System.ComponentModel.Win32Exception or System.Text.DecoderFallbackException or BadImageFormatException)
        {
            packet.Status = "incomplete";
            packet.Diagnostics.Add(new(ErrorCode.IntegratedToolFailure, error.Message));
        }
        foreach (string generated in Directory.EnumerateFiles(output))
            if (!packet.GeneratedFiles.ContainsKey(generated))
                Files.Pin(packet.GeneratedFiles, generated, Files.Hash(generated));
        Archive(output, packet);
        Files.WriteNew(Path.Combine(output, "result.json"), packet);
        if (options.Get("format", "json") == "text")
        {
            Console.WriteLine($"Status: {packet.Status}; links: {packet.LinksStatus}; proof: {packet.ProofStatus}; audit: {packet.AuditStatus}; conformance: {packet.ConformanceStatus}");
            Console.WriteLine($"Coverage: {packet.CoverageStatus}; correspondence: {packet.CorrespondenceStatus}; provenance: {packet.ProvenanceStatus}");
            Console.WriteLine("Source obligations: " + packet.SourceObligationsStatus);
            foreach (DafnyLink link in packet.Links)
                Console.WriteLine($"{link.Id}: {link.Implementation.Location.Path}:{link.Implementation.Location.Line} -> {link.Specification.Name}");
            foreach (Issue issue in packet.Diagnostics) Console.WriteLine($"{issue.Code} {issue.Severity}: {issue.Message}");
            Console.WriteLine("Evidence: " + Path.Combine(output, "result.json"));
        }
        else ProjectWorkflow.Render(packet, options);
        return packet.Status switch { "passed" or "ready" => 0, "rejected" => 1, _ => 2 };
    }

    public static List<(ISymbol Symbol, Endpoint Endpoint)> SourceSymbols(CSharpScope scope)
    {
        List<(ISymbol, Endpoint)> symbols = [];
        foreach (var tree in scope.Compilation.SyntaxTrees)
        {
            SemanticModel model = scope.Compilation.GetSemanticModel(tree);
            string text = scope.Texts[tree.FilePath];
            foreach (SyntaxNode node in tree.GetRoot().DescendantNodes().Where(n =>
                n is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or VariableDeclaratorSyntax))
            {
                ISymbol? symbol = model.GetDeclaredSymbol(node);
                if (symbol is not (IMethodSymbol or IFieldSymbol or IPropertySymbol)) continue;
                symbols.Add((symbol, new(symbol.Name, symbol.ContainingType.ToDisplayString(), symbol.Kind.ToString(),
                    Files.At(tree.FilePath, text, node.SpanStart, node.Span.End), text[node.SpanStart..node.Span.End])));
            }
        }
        return symbols;
    }

    public static (ISymbol Symbol, Endpoint Endpoint) Resolve(List<(ISymbol Symbol, Endpoint Endpoint)> symbols, string name)
    {
        var matches = symbols.Where(s => s.Symbol.GetDocumentationCommentId() == name ||
            s.Symbol.ToDisplayString() == name || s.Symbol.ContainingType.ToDisplayString() + "." + s.Symbol.Name == name).ToList();
        if (matches.Count != 1)
            throw new InputException(ErrorCode.InvalidIntegratedBinding, $"Expected one C# symbol '{name}', found {matches.Count}; use a documentation ID to disambiguate overloads.");
        return matches[0];
    }

    private static void Baseline(string path, IntegratedPacket packet)
    {
        using JsonDocument document = JsonDocument.Parse(Files.Read(path));
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("schema_version", out JsonElement schema) || schema.GetInt32() != Schema.Version ||
            !root.TryGetProperty("kind", out JsonElement kind) || kind.GetString() != "integrated-result" ||
            !root.TryGetProperty("fingerprints", out JsonElement previous) || previous.ValueKind != JsonValueKind.Object)
            throw new InputException(ErrorCode.IntegratedBaselineChanged, "Integrated baselines require a current integrated-result fingerprint object.");
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        foreach (JsonProperty pair in previous.EnumerateObject())
            if (pair.Value.ValueKind != JsonValueKind.String || !pins.TryAdd(pair.Name, pair.Value.GetString()!))
                throw new InputException(ErrorCode.IntegratedBaselineChanged, "Baseline fingerprints must be unique strings.");
        foreach (string key in pins.Keys.Union(packet.Fingerprints.Keys, FilePaths.Comparer))
            if (pins.GetValueOrDefault(key) != packet.Fingerprints.GetValueOrDefault(key))
                packet.Diagnostics.Add(new(ErrorCode.IntegratedBaselineChanged, "Changed/added/removed input; evidence requires reconsideration: " + key));
        if (pins.Count == 0) throw new InputException(ErrorCode.IntegratedBaselineChanged, "Empty baseline.");
        if (packet.Diagnostics.Any(d => d.Code == ErrorCode.IntegratedBaselineChanged.Id())) packet.LinksStatus = "incomplete";
    }

    private static void Archive(string output, IntegratedPacket packet)
    {
        string archive = Path.Combine(output, "inputs");
        Directory.CreateDirectory(archive);
        foreach (var pin in packet.Fingerprints.Concat(packet.GeneratedFiles).Where(p => FilePaths.IsAbsolute(p.Key)))
        {
            // Proof/source/tool inputs remain addressable even when the working tree changes.
            string destination = Path.Combine(archive, pin.Value);
            if (!File.Exists(destination) && File.Exists(pin.Key))
            {
                File.Copy(pin.Key, destination, overwrite: false);
                if (Files.Hash(destination) != pin.Value)
                {
                    packet.Status = "incomplete";
                    packet.ProvenanceStatus = "incomplete";
                    packet.Diagnostics.Add(new(ErrorCode.IntegratedInputChanged, "Input changed before archive: " + pin.Key));
                }
            }
            else if (!File.Exists(pin.Key))
            {
                packet.Status = "incomplete";
                packet.ProvenanceStatus = "incomplete";
                packet.Diagnostics.Add(new(ErrorCode.IntegratedInputChanged, "Input missing before archive: " + pin.Key));
            }
            if (File.Exists(destination) && Files.Hash(destination) != pin.Value)
            {
                packet.Status = "incomplete";
                packet.ProvenanceStatus = "incomplete";
                packet.Diagnostics.Add(new(ErrorCode.IntegratedInputChanged, "Archived input hash mismatch: " + pin.Key));
            }
        }
    }
}
