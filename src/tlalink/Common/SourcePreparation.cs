using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TlaLink;

public sealed record SourcePreparationManifest(int SchemaVersion, string Kind, int AnnotationVersion,
    CompilerProfile Compiler, List<PreparationFile> Files, ToolPin? Jar = null, List<ToolPin>? ContextSources = null,
    List<string>? ContextImports = null);
public sealed record PreparationFile(string Source, string Sha256, string Destination,
    List<PreparationAnnotation>? Annotations = null, List<PreparationMutation>? Mutations = null);
public sealed record PreparationTarget(string Symbol, string? Syntax = null, string? Tokens = null,
    int Match = 1, int ExpectedMatches = 1);
public sealed record PreparationAnnotation(PreparationTarget Target, AnnotationPayload Payload);
public sealed record PreparationMutation(PreparationTarget Target, string Replacement, string Reason);
public sealed record PreparedFile(string Source, string SourceSha256, string Destination, string Sha256,
    bool CommentOnly, List<PreparationAnnotation> Annotations, List<PreparationMutation> Mutations);
public sealed record PreparedSources(int SchemaVersion, string Kind, string Status, int AnnotationVersion,
    CompilerProfile Compiler, List<PreparedFile> Files, Dictionary<string, string> Fingerprints, string Boundary)
{
    public int FileCount => Files.Count;
    public int MutatedFileCount => Files.Count(f => !f.CommentOnly);
}

public static class SourcePreparation
{
    private sealed record Edit(TextSpan Span, string Text, bool Annotation);
    public const string Boundary = "Explicit byte-pinned copies, comment additions and declared control mutations. " +
        "Resolved targets and compilable copies are not atomicity, source/model refinement, native execution or acceptance evidence.";

    public static int Run(CliOptions options)
    {
        if (options.Command == "check-prepared-sources")
        {
            string path = FilePaths.Full(options.Required("result"));
            string directory = Path.GetDirectoryName(path)!;
            Dictionary<string, string> reading = new(FilePaths.Comparer);
            Files.ReadPinned(path, reading);
            PreparedSources saved = ProjectInputs.Read<PreparedSources>(path);
            Require(saved.SchemaVersion == Schema.Version && saved.Kind == "prepared-sources" && saved.Status == "prepared",
                "Expected schema 4 prepared-sources.");
            Require(saved.Files.Count is > 0 and <= 1000, "Prepared sources require 1..1000 files.");
            Markup.RequireVersion(saved.AnnotationVersion);
            IntegratedInputs.CheckPins(saved.Fingerprints);
            HashSet<string> expected = new(FilePaths.Comparer) { path };
            foreach (PreparedFile file in saved.Files)
            {
                Require(file != null, "Null prepared file.");
                Require(FilePaths.IsNativeAbsolute(file.Source) && saved.Fingerprints.TryGetValue(file.Source, out string? pin) &&
                    pin == file.SourceSha256 && !FilePaths.IsAbsolute(file.Destination),
                    "Prepared file source/destination identities disagree with their recorded pins.");
                string destination = ProjectAcceptance.SafePath(directory, file.Destination);
                Require(expected.Add(destination) && File.Exists(destination) && Files.Hash(destination) == file.Sha256,
                    "Prepared output is missing, duplicated or changed: " + file.Destination);
            }
            Require(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).All(expected.Contains),
                "Unexpected file in prepared output.");
            IntegratedInputs.CheckPins(saved.Fingerprints);
            IntegratedInputs.CheckPins(reading);
            ProjectWorkflow.Render(new PreparedSourcesInspection(Schema.Version, "prepared-sources-inspection",
                "current", saved.FileCount, saved.MutatedFileCount, Boundary), options);
            return 0;
        }
        PreparedSources result = Prepare(FilePaths.Full(options.Required("manifest")),
            FilePaths.Full(options.Required("output")), options.Timeout);
        ProjectWorkflow.Render(result, options);
        return 0;
    }

    public static PreparedSources Prepare(string manifestPath, string output, int timeout)
    {
        manifestPath = FilePaths.Full(manifestPath);
        output = FilePaths.Full(output);
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        Files.ReadPinned(manifestPath, pins);
        SourcePreparationManifest manifest = ProjectInputs.Read<SourcePreparationManifest>(manifestPath);
        Require(manifest.SchemaVersion == Schema.Version && manifest.Kind == "source-preparation",
            "Expected schema 4 source-preparation.");
        Markup.RequireVersion(manifest.AnnotationVersion);
        CompilerProfile compiler = manifest.Compiler;
        Require(compiler.TargetFramework == "net10.0" && CSharpLanguages.Supported(compiler.LanguageVersion) &&
            compiler.Nullable == "enable" && !compiler.Checked && compiler.AllowUnsafe,
            "Preparation requires net10.0, C# 8 or 14, nullable-enable, unchecked, unsafe compiler context.");
        Require(manifest.Files.Count is > 0 and <= 1000, "Declare 1..1000 explicit files.");
        Require(!File.Exists(output) && !Directory.Exists(output), "Output must be a new directory.");
        string declaring = Path.GetDirectoryName(manifestPath)!;
        List<(PreparationFile File, string Source, string Destination)> files = [];
        HashSet<string> destinations = new(FilePaths.Comparer) { Path.Combine(output, "prepared-sources.json") };
        foreach (PreparationFile file in manifest.Files)
        {
            Require(file != null, "Null preparation file.");
            string source = FilePaths.Full(file.Source, declaring);
            StageEvidence.RequireHash(file.Sha256);
            Files.Pin(pins, source, Files.Hash(source));
            Require(string.Equals(pins[source], file.Sha256, StringComparison.OrdinalIgnoreCase),
                "Pinned source changed: " + source);
            Require(!FilePaths.IsAbsolute(file.Destination) && !string.IsNullOrWhiteSpace(file.Destination),
                "Destination must be output-relative.");
            string destination = ProjectAcceptance.SafePath(output, file.Destination);
            Require(!FilePaths.Comparer.Equals(destination, output) &&
                !destinations.Any(previous => Inside(previous, destination) || Inside(destination, previous)) &&
                destinations.Add(destination),
                "Duplicate/reserved output destination: " + file.Destination);
            Require(!Inside(source, output), "Output must not contain input sources.");
            files.Add((file, source, destination));
        }
        Require(!Inside(manifestPath, output), "Output must not contain its manifest.");
        List<string> contexts = [];
        HashSet<string> activeContexts = new(FilePaths.Comparer) { manifestPath };
        HashSet<string> loadedContexts = new(FilePaths.Comparer) { manifestPath };
        CollectContext(manifest, declaring);
        string[] csharp = files.Select(f => f.Source).Where(IsCSharp).Concat(contexts).Distinct(FilePaths.Comparer).ToArray();
        CSharpScope? scope = csharp.Length == 0 ? null : CSharpScope.Parse(csharp.ToList(),
            compiler.References.Select(r => FilePaths.Full(r, declaring)), compiler.Defines,
            languageVersion: compiler.LanguageVersion);
        if (scope != null)
        {
            CompilationReady(scope.Compilation);
            foreach (var pin in scope.Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
        }
        Dictionary<string, TlaDocument> models = new(FilePaths.Comparer);
        List<(string Path, byte[] Bytes)> outputs = [];
        List<PreparedFile> ledger = [];
        foreach (var file in files)
        {
            List<PreparationAnnotation> annotations = file.File.Annotations ?? [];
            List<PreparationMutation> mutations = file.File.Mutations ?? [];
            byte[] original = File.ReadAllBytes(file.Source);
            Require(string.Equals(Convert.ToHexString(SHA256.HashData(original)), pins[file.Source], StringComparison.OrdinalIgnoreCase),
                "Source changed during preparation: " + file.Source);
            byte[] prepared = original;
            if (annotations.Count + mutations.Count > 0)
            {
                string text = Files.ReadPinned(file.Source, pins);
                bool cs = IsCSharp(file.Source);
                Require(cs || Path.GetExtension(file.Source).Equals(".tla", StringComparison.OrdinalIgnoreCase),
                    "Edits require a C# or TLA+ source.");
                Require(cs || mutations.Count == 0, "Control mutations use C# syntax targets only.");
                Require(cs || annotations.Count == 0, "Formal endpoints use native identities; source binding insertion is C# only.");
                SyntaxTree? tree = cs ? scope!.Compilation.SyntaxTrees.Single(t => FilePaths.Comparer.Equals(t.FilePath, file.Source)) : null;
                TlaDocument? model = cs ? null : Model(file.Source);
                TextSpan Resolve(PreparationTarget target) => cs
                    ? CSharpTarget(scope!.Compilation, tree!, target).Span
                    : TlaTarget(model!, target);
                List<Edit> edits = [];
                foreach (PreparationAnnotation annotation in annotations)
                {
                    Require(annotation != null, "Null annotation.");
                    string json = Markup.Encode(annotation.Payload);
                    int start = Resolve(annotation.Target).Start;
                    edits.Add(new(new(start, 0), "/* @link " + json + " */", true));
                }
                foreach (PreparationMutation mutation in mutations)
                {
                    Require(mutation != null, "Null mutation.");
                    Require(!string.IsNullOrWhiteSpace(mutation.Reason) && !string.IsNullOrWhiteSpace(mutation.Replacement),
                        "Each control mutation needs an explicit reason and replacement.");
                    edits.Add(new(Resolve(mutation.Target), mutation.Replacement, false));
                }
                List<Edit> replacements = edits.Where(e => !e.Annotation).OrderBy(e => e.Span.Start).ToList();
                for (int i = 0; i < replacements.Count; i++)
                {
                    Edit current = replacements[i];
                    Require(i == 0 || replacements[i - 1].Span.End <= current.Span.Start, "Control mutations overlap.");
                    Require(!edits.Any(e => e.Annotation && e.Span.Start > current.Span.Start && e.Span.Start < current.Span.End),
                        "An annotation lies inside replaced syntax.");
                }
                string transformed = text;
                foreach (var group in edits.GroupBy(e => e.Span.Start).OrderByDescending(g => g.Key))
                {
                    Edit? replacement = group.SingleOrDefault(e => !e.Annotation);
                    List<string> annotationsAtTarget = group.Where(e => e.Annotation).Select(e => e.Text).ToList();
                    string comments = "";
                    int insertionStart = group.Key;
                    if (annotationsAtTarget.Count != 0)
                    {
                        int lineStart = text.AsSpan(0, group.Key).LastIndexOf('\n') + 1;
                        int indentEnd = lineStart;
                        while (indentEnd < group.Key && text[indentEnd] is ' ' or '\t') indentEnd++;
                        string indent = text[lineStart..indentEnd];
                        int lineEnd = text.IndexOf('\n', group.Key);
                        if (lineEnd < 0) lineEnd = text.IndexOf('\n');
                        string newline = lineEnd > 0 && text[lineEnd - 1] == '\r' ? "\r\n" : "\n";
                        comments = (indentEnd == group.Key ? "" : newline + indent) +
                            string.Join(newline + indent, annotationsAtTarget) + newline + indent;
                        if (indentEnd != group.Key)
                            while (insertionStart > lineStart && text[insertionStart - 1] is ' ' or '\t') insertionStart--;
                    }
                    transformed = transformed.Remove(insertionStart, group.Key - insertionStart + (replacement?.Span.Length ?? 0))
                        .Insert(insertionStart, comments + (replacement?.Text ?? ""));
                }
                if (cs)
                {
                    SyntaxTree changed = CSharpSyntaxTree.ParseText(SourceText.From(transformed),
                        (CSharpParseOptions)tree!.Options, file.Source);
                    CompilationReady(scope!.Compilation.ReplaceSyntaxTree(tree, changed));
                }
                prepared = Files.EncodeLike(transformed, original);
            }
            outputs.Add((file.Destination, prepared));
            ledger.Add(new(file.Source, pins[file.Source], Path.GetRelativePath(output, file.Destination),
                Convert.ToHexString(SHA256.HashData(prepared)).ToLowerInvariant(), mutations.Count == 0, annotations, mutations));
        }
        Program.PinTool(pins);
        IntegratedInputs.CheckPins(pins);
        CompilerProfile actual = compiler with { References = compiler.References.Select(r => FilePaths.Full(r, declaring)).ToList(),
            Defines = scope == null ? compiler.Defines : scope.Fingerprints["compiler:defines"].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() };
        PreparedSources result = new(Schema.Version, "prepared-sources", "prepared", manifest.AnnotationVersion, actual, ledger, pins, Boundary);
        Directory.CreateDirectory(output);
        foreach (var file in outputs)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);
            using FileStream stream = new(file.Path, FileMode.CreateNew, FileAccess.Write);
            stream.Write(file.Bytes);
        }
        IntegratedInputs.CheckPins(pins);
        Files.WriteNew(Path.Combine(output, "prepared-sources.json"), result);
        return result;

        void CollectContext(SourcePreparationManifest definition, string directory)
        {
            foreach (ToolPin context in definition.ContextSources ?? [])
            {
                Require(context != null, "Null compiler context source.");
                string path = FilePaths.Full(context.Path, directory);
                Require(IsCSharp(path), "Compiler context sources must be explicit C# files.");
                StageEvidence.RequireHash(context.Sha256);
                Files.ReadPinned(path, pins);
                Require(string.Equals(pins[path], context.Sha256, StringComparison.OrdinalIgnoreCase),
                    "Pinned compiler context source changed: " + path);
                contexts.Add(path);
                Require(contexts.Count <= 1000, "Compiler context exceeds 1000 declared sources.");
            }
            foreach (string import in definition.ContextImports ?? [])
            {
                Require(!string.IsNullOrWhiteSpace(import), "Empty compiler context import.");
                string path = FilePaths.Full(import, directory);
                Require(!activeContexts.Contains(path), "Cyclic compiler context import: " + path);
                if (!loadedContexts.Add(path)) continue;
                Require(loadedContexts.Count <= 1000 && activeContexts.Count < 100,
                    "Compiler context import scope is too large or deeply nested.");
                Files.ReadPinned(path, pins);
                SourcePreparationManifest shared = ProjectInputs.Read<SourcePreparationManifest>(path);
                Require(shared.SchemaVersion == Schema.Version && shared.Kind == "source-preparation" &&
                    shared.AnnotationVersion == manifest.AnnotationVersion && shared.Files.Count == 0 && shared.Jar == null &&
                    shared.Compiler.TargetFramework == "net10.0" && shared.Compiler.LanguageVersion == compiler.LanguageVersion &&
                    shared.Compiler.Nullable == "enable" && !shared.Compiler.Checked && shared.Compiler.AllowUnsafe &&
                    shared.Compiler.Defines == null && shared.Compiler.References.Count == 0,
                    "Context imports must be context-only source-preparation manifests without compiler overrides.");
                activeContexts.Add(path);
                CollectContext(shared, Path.GetDirectoryName(path)!);
                activeContexts.Remove(path);
            }
        }

        TlaDocument Model(string path)
        {
            if (models.TryGetValue(path, out TlaDocument? existing)) return existing;
            Require(manifest.Jar != null, "TLA+ annotation targets require an explicit pinned SANY JAR.");
            string jar = FilePaths.Full(manifest.Jar.Path, declaring);
            StageEvidence.RequireHash(manifest.Jar.Sha256);
            string hash = Files.Hash(jar);
            Require(string.Equals(hash, manifest.Jar.Sha256, StringComparison.OrdinalIgnoreCase), "Pinned SANY JAR changed.");
            Files.Pin(pins, jar, hash);
            TlaDocument document = TlaBackend.Parse(path, jar, timeout);
            Require(document.ParserSucceeded && document.Diagnostics.All(d => d.Severity != "error" &&
                !d.Is(ErrorCode.StaleTranslation, ErrorCode.ToolFailure)),
                "SANY target resolution incomplete: " + string.Join("; ", document.Diagnostics.Select(d => d.Message)));
            Require(!TlaBackend.HasAlgorithm(document.Text), "Source preparation does not edit PlusCal or generated translations.");
            foreach (var pin in document.DependencyFingerprints) Files.Pin(pins, pin.Key, pin.Value);
            Files.Pin(pins, path, document.Hash);
            models.Add(path, document);
            return document;
        }
    }

    internal static SyntaxNode CSharpTarget(CSharpCompilation compilation, SyntaxTree? tree, PreparationTarget target)
    {
        Require(!string.IsNullOrWhiteSpace(target.Symbol), "C# targets need a Roslyn declaration documentation ID.");
        List<SyntaxNode> declarations = compilation.SyntaxTrees.Where(t => tree == null || t == tree)
            .SelectMany(t => t.GetRoot().DescendantNodes().Where(n =>
                n is BaseMethodDeclarationSyntax or BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or
                    PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax or VariableDeclaratorSyntax)
                .Where(n => compilation.GetSemanticModel(t).GetDeclaredSymbol(n)?.GetDocumentationCommentId() == target.Symbol)).ToList();
        Require(declarations.Count == 1, "Expected one source declaration for " + target.Symbol);
        SyntaxNode declaration = declarations[0];
        if (declaration is VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax field })
        {
            Require(field.Declaration.Variables.Count == 1, "Field selectors require a single declarator.");
            declaration = field;
        }
        if (target.Syntax == null)
        {
            Require(target.Tokens == null && target.Match == 1 && target.ExpectedMatches == 1,
                "A declaration target cannot select tokens or repeated matches without a syntax kind.");
            return declaration;
        }
        Require(Enum.TryParse(target.Syntax, out SyntaxKind kind) && Enum.IsDefined(kind) &&
            kind.ToString() == target.Syntax, "Syntax selectors need a Roslyn SyntaxKind name.");
        List<(int Kind, string Text)>? pattern = target.Tokens == null ? null : Tokens(target.Tokens);
        Require(pattern == null || pattern.Count > 0, "Syntax target token text is empty.");
        List<SyntaxNode> candidates = declaration.DescendantNodesAndSelf()
            .Where(n => n.IsKind(kind) && (pattern == null || Tokens(n.ToString()).SequenceEqual(pattern))).ToList();
        Require(target.ExpectedMatches > 0 && target.Match > 0 && target.Match <= target.ExpectedMatches &&
            candidates.Count == target.ExpectedMatches,
            $"Expected {target.ExpectedMatches} {target.Syntax} matches in {target.Symbol}, found {candidates.Count}.");
        return candidates[target.Match - 1];
    }

    private static TextSpan TlaTarget(TlaDocument document, PreparationTarget target)
    {
        Require(target.Syntax == "operator" && target.Tokens == null &&
            target.Match == 1 && target.ExpectedMatches == 1, "TLA+ targets select one SANY operator by name.");
        List<TlaEndpoint> endpoints = document.Endpoints.Where(e => e.Name == target.Symbol && e.Kind == target.Syntax).ToList();
        Require(endpoints.Count == 1, "Expected one SANY declaration for " + target.Symbol);
        return TextSpan.FromBounds(endpoints[0].Start, endpoints[0].End);
    }

    private static List<(int Kind, string Text)> Tokens(string text) => SyntaxFactory.ParseTokens(text)
        .Where(t => !t.IsKind(SyntaxKind.EndOfFileToken)).Select(t => (t.RawKind, t.Text)).ToList();
    private static bool IsCSharp(string path) => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);
    private static bool Inside(string path, string directory) => FilePaths.Comparer.Equals(path, directory) ||
        path.StartsWith(directory + Path.DirectorySeparatorChar, FilePaths.Comparison);
    private static void CompilationReady(CSharpCompilation compilation)
    {
        List<Diagnostic> errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Require(errors.Count == 0, "Explicit C# compilation is incomplete: " + string.Join("; ", errors.Select(e => e.ToString())));
    }
    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, string message)
    {
        if (!condition) throw new InputException(ErrorCode.InvalidSourcePreparation, message);
    }
}
