using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace TlaLink;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Observation(string Id, string Operator, string Type, [property: JsonRequired] int Arity = 0);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EventBinding(string Model, string Step, string Operator, int Arity, string Point,
    string Classification, List<Observation> Observations, [property: JsonRequired] bool Entity = false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InstrumentManifest(int SchemaVersion, string BindingId, Dictionary<string, string> Fingerprints,
    Dictionary<string, string> GeneratedFiles, List<EventBinding> Events, string Boundary)
{
    [JsonRequired]
    public string Kind { get; init; } = "instrumentation-manifest";
    [JsonRequired]
    public int AnnotationVersion { get; init; } = Markup.Version;
}
public sealed record ModelBinding(string Model, Tag Tag, Endpoint Endpoint, int Arity);

public static class Instrumentation
{
    public static InstrumentManifest ReadManifest(string path)
    {
        using JsonDocument document = JsonDocument.Parse(Files.Read(path));
        Files.RequireSchema(document.RootElement, "instrumentation-manifest");
        InstrumentManifest manifest = document.RootElement.Deserialize<InstrumentManifest>(Files.Json)
            ?? throw new InputException(ErrorCode.InvalidTrace, "Invalid binding manifest.");
        Markup.RequireVersion(manifest.AnnotationVersion);
        if (manifest.Events.Count == 0 ||
            manifest.BindingId != Files.HashText(JsonSerializer.Serialize(new { manifest.Fingerprints, manifest.Events }, Files.Json)) ||
            manifest.Events.Select(e => (e.Model, e.Step)).Distinct().Count() != manifest.Events.Count)
            throw new InputException(ErrorCode.InvalidTrace, "Binding manifest identity or event uniqueness is invalid.");
        return manifest;
    }

    public static InstrumentManifest Generate(CSharpScope scope, List<ModelBinding> models, Packet packet, string output)
    {
        scope.RequireCompilable();
        if (packet.LinksStatus != "passed" || packet.Diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.UnboundModelEndpoint, ErrorCode.UncoveredStateWrite, ErrorCode.UnresolvedCompilation)))
            throw new InputException(ErrorCode.UnresolvedInstrumentation, "Instrumentation requires resolved, complete structural/state-write bindings. " +
                string.Join("; ", packet.Diagnostics.Take(8).Select(d => d.Code + ": " + d.Message)));
        output = FilePaths.Full(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new InputException(ErrorCode.UnresolvedInstrumentation, "Instrumentation output must be a new directory.");
        List<SourceBinding> executable = scope.Bindings.Where(b => b.Tag.Kind == "step" && b.Tag.Options.ContainsKey("point")).ToList();
        if (executable.Count == 0) throw new InputException(ErrorCode.UnresolvedInstrumentation, "No executable observation points.");
        List<EventBinding> events = [];
        Dictionary<string, List<TextChange>> changes = [];
        List<(SourceBinding Binding, string Before, string After)> captures = [];
        Dictionary<(string Path, int Start), string> invocationNames = [];
        string InvocationIdentity(SourceBinding binding)
        {
            SyntaxNode? function = binding.Node.Ancestors().FirstOrDefault(n =>
                n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax);
            BlockSyntax? body = function switch {
                BaseMethodDeclarationSyntax method => method.Body,
                LocalFunctionStatementSyntax localFunction => localFunction.Body,
                AnonymousFunctionExpressionSyntax lambda => lambda.Body as BlockSyntax,
                AccessorDeclarationSyntax accessor => accessor.Body,
                _ => null
            };
            if (body == null) throw new InputException(ErrorCode.InvalidInstrumentation, "operation=\"$call\" requires an enclosing block-bodied function.");
            if (body.DescendantNodes(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)).Any(n => n is YieldStatementSyntax))
                throw new InputException(ErrorCode.InvalidInstrumentation, "operation=\"$call\" does not treat iterator enumeration as a method invocation.");
            var key = (binding.Path, body.SpanStart);
            if (invocationNames.TryGetValue(key, out string? known)) return known;
            string name = "__tlalinkOperation" + invocationNames.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            HashSet<string> identifiers = body.SyntaxTree.GetRoot().DescendantTokens()
                .Where(t => t.IsKind(SyntaxKind.IdentifierToken)).Select(t => t.ValueText).ToHashSet(StringComparer.Ordinal);
            while (identifiers.Contains(name)) name += "_";
            invocationNames.Add(key, name);
            if (!changes.TryGetValue(binding.Path, out List<TextChange>? edits)) changes[binding.Path] = edits = [];
            edits.Add(new(new TextSpan(body.OpenBraceToken.Span.End, 0),
                "\nstring " + name + " = global::TlaLink.Runtime.TraceRuntime.NewOperationIdentity();\n"));
            return name;
        }
        foreach (SourceBinding binding in executable)
        {
            Tag tag = binding.Tag;
            string point = tag.Options["point"];
            string classification = tag.Options.GetValueOrDefault("classification", "unresolved");
            if (classification == "unresolved" || point == "fault")
                throw new InputException(ErrorCode.InvalidInstrumentation, "Unresolved or fault observation requires a reviewed capture implementation; not generated.");
            bool controlTransfer = binding.Node is ReturnStatementSyntax or ThrowStatementSyntax or SwitchStatementSyntax;
            if (binding.Node is not (ExpressionStatementSyntax or LocalDeclarationStatementSyntax) && !controlTransfer ||
                controlTransfer && point != "invoke" ||
                binding.Node is LocalDeclarationStatementSyntax local && !local.UsingKeyword.IsKind(SyntaxKind.None) ||
                binding.Node.Parent is not (BlockSyntax or SwitchSectionSyntax))
                throw new InputException(ErrorCode.InvalidInstrumentation, "Capture requires expression statements, ordinary local declarations, or invoke-only return/throw/switch statements in a block/switch section; using declarations are not completion points.");
            if (binding.Node.DescendantNodes().Any(n => n is AnonymousFunctionExpressionSyntax))
                throw new InputException(ErrorCode.InvalidInstrumentation, "Nested lambda capture is unsupported.");
            string actor = RequiredIdentity(binding, "actor");
            string operation = tag.Options.GetValueOrDefault("operation") == "$call"
                ? InvocationIdentity(binding) : RequiredIdentity(binding, "operation");
            string? entity = tag.Options.TryGetValue("entity", out string? identity) ? Scalar(binding, identity, identity: true) : null;
            string connection = tag.Options.TryGetValue("connection", out string? c) ? Scalar(binding, c, identity: true) : "0L";
            ModelBinding? action = models.SingleOrDefault(m => m.Model == binding.Model && m.Tag.Kind == "step" && m.Tag.Identity == tag.Identity);
            if (classification == "transition" && action is null) throw new InputException(ErrorCode.InvalidInstrumentation, "Observable transition has no model action.");
            if (action is not null && (action.Arity is < 0 or > 3 || action.Arity == 3 && entity == null))
                throw new InputException(ErrorCode.InvalidInstrumentation, "Trace actions take 0, actor, actor+operation, or actor+operation+explicit entity.");
            List<Observation> observed = [];
            List<string> values = [];
            ISymbol? guardSymbol = null;
            string guard = "null";
            string[] names = tag.Options.GetValueOrDefault("observe", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (names.Distinct().Count() != names.Length) throw new InputException(ErrorCode.InvalidInstrumentation, "Duplicate observation.");
            foreach (string name in names)
            {
                SourceBinding state = scope.Bindings.SingleOrDefault(b => b.Model == binding.Model && b.Tag.Kind == "state" &&
                    b.Tag.Identity == name && SymbolEqualityComparer.Default.Equals(b.Symbol?.ContainingType,
                        binding.Semantics.GetEnclosingSymbol(binding.Node.SpanStart)?.ContainingType))
                    ?? throw new InputException(ErrorCode.InvalidInstrumentation, $"Observation {name} must resolve to a field in this type.");
                IFieldSymbol field = state.Symbol as IFieldSymbol ?? throw new InputException(ErrorCode.InvalidInstrumentation, "Unresolved observation field.");
                string type = TypeName(field.Type);
                if (state.Tag.Options.TryGetValue("type", out string? declared) && declared != type)
                    throw new InputException(ErrorCode.InvalidInstrumentation, "Declared observation type disagrees with Roslyn.");
                ModelBinding observation = models.SingleOrDefault(m => m.Model == binding.Model && m.Tag.Kind == "state" && m.Tag.Identity == name)
                    ?? throw new InputException(ErrorCode.InvalidInstrumentation, "Missing model observation.");
                if (observation.Arity != 0 && (observation.Arity != 1 || entity is null))
                    throw new InputException(ErrorCode.InvalidInstrumentation, "Model observations require zero arguments, or one entity argument with explicit entity capture.");
                if (observation.Tag.Options.TryGetValue("type", out string? modelType) && modelType != type)
                    throw new InputException(ErrorCode.InvalidInstrumentation, "Model/source observation type declarations disagree.");
                string expression = field.IsStatic ? field.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ".@" + field.Name : "this.@" + field.Name;
                string claimedGuard = state.Tag.Options.GetValueOrDefault("guard")
                    ?? throw new InputException(ErrorCode.InvalidInstrumentation, "Shared-field observations require an explicit held-lock guard.");
                IFieldSymbol resolvedGuard = ResolveGuard(binding, claimedGuard);
                if (!Held(binding, resolvedGuard) || guardSymbol is not null && !SymbolEqualityComparer.Default.Equals(guardSymbol, resolvedGuard))
                    throw new InputException(ErrorCode.InvalidInstrumentation, "All observed fields must use the same actually held lock; no cross-lock snapshot.");
                guardSymbol = resolvedGuard;
                guard = resolvedGuard.IsStatic ? resolvedGuard.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ".@" + resolvedGuard.Name : "this.@" + resolvedGuard.Name;
                observed.Add(new(name, observation.Endpoint.Name, type, observation.Arity));
                values.Add($"[{Quote(name)}] = {expression}");
            }
            if (tag.Options.ContainsKey("hook") && binding.Locks.Count != 0)
                throw new InputException(ErrorCode.InvalidInstrumentation, "Scheduling hooks inside held source locks are not generated.");
            EventBinding eventBinding = new(binding.Model, tag.Identity, action?.Endpoint.Name ?? "", action?.Arity ?? 0,
                point, classification, observed, entity is not null);
            events.Add(eventBinding);
            string emit = $"global::TlaLink.Runtime.TraceRuntime.{(entity is null ? "Emit" : "EmitEntity")}(BINDING_ID, {Quote(binding.Model)}, {Quote(tag.Identity)}, {Quote(point)}, {Quote(classification)}, {connection}, {actor}, {operation}, {(entity is null ? "" : entity + ", ")}{guard}, new global::System.Collections.Generic.Dictionary<string, object> {{ {string.Join(", ", values)} }});";
            string before = point == "invoke" ? emit : "";
            string after = point != "invoke" ? emit : "";
            if (tag.Options.TryGetValue("hook", out string? hook))
            {
                string checkpoint = $"global::TlaLink.Runtime.TraceRuntime.Checkpoint({Quote(tag.Identity)}, {Quote(hook)});";
                if (hook == "before") before = checkpoint + "\n" + before;
                else after += "\n" + checkpoint;
            }
            captures.Add((binding, before, after));
        }
        if (events.Select(e => (e.Model, e.Step)).Distinct().Count() != events.Count)
            throw new InputException(ErrorCode.InvalidInstrumentation, "Observable step IDs must be unique; use distinct invocation/completion bindings.");
        string bindingId = Files.HashText(JsonSerializer.Serialize(new { packet.Fingerprints, Events = events }, Files.Json));
        foreach (var capture in captures)
        {
            if (!changes.TryGetValue(capture.Binding.Path, out List<TextChange>? edits))
                changes[capture.Binding.Path] = edits = [];
            if (capture.Before.Length != 0) edits.Add(new(new TextSpan(capture.Binding.Node.SpanStart, 0), capture.Before.Replace("BINDING_ID", Quote(bindingId), StringComparison.Ordinal) + "\n"));
            if (capture.After.Length != 0) edits.Add(new(new TextSpan(capture.Binding.Node.Span.End, 0), "\n" + capture.After.Replace("BINDING_ID", Quote(bindingId), StringComparison.Ordinal) + "\n"));
        }
        Dictionary<string, string> generated = [];
        // Validate every rewrite before creating the output directory.
        Dictionary<string, string> rewritten = [];
        HashSet<string> namesUsed = new(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in scope.Texts)
        {
            string name = Path.GetFileName(pair.Key);
            if (!namesUsed.Add(name)) throw new InputException(ErrorCode.InvalidInstrumentation, "Duplicate source basenames; supply an unambiguous explicit scope.");
            string text = SourceText.From(pair.Value).WithChanges(changes.GetValueOrDefault(pair.Key, [])).ToString();
            rewritten[name] = text;
        }
        string runtime = Path.Combine(AppContext.BaseDirectory, "TraceRuntime.source.cs");
        if (rewritten.ContainsKey("TlaLink.TraceRuntime.cs") || rewritten.ContainsKey("TlaLink.Schema.cs"))
            throw new InputException(ErrorCode.InvalidInstrumentation, "Runtime/schema source basenames are reserved.");
        rewritten["TlaLink.TraceRuntime.cs"] = Files.Read(runtime);
        rewritten["TlaLink.Schema.cs"] = $"namespace TlaLink;\npublic static class Schema {{ public const int Version = {Schema.Version}; }}\n";
        List<SyntaxTree> generatedTrees = rewritten.Select(p => CSharpSyntaxTree.ParseText(p.Value,
            (CSharpParseOptions)scope.Compilation.SyntaxTrees.First().Options, p.Key)).Cast<SyntaxTree>().ToList();
        var rewrittenCompilation = scope.Compilation.RemoveAllSyntaxTrees().AddSyntaxTrees(generatedTrees);
        var errors = rewrittenCompilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count != 0) throw new InputException(ErrorCode.InvalidInstrumentation, "Generated compilation rejected: " + string.Join("; ", errors.Take(8)));
        Directory.CreateDirectory(output);
        foreach (var pair in rewritten)
        {
            string path = Path.Combine(output, pair.Key);
            File.WriteAllText(path, pair.Value, new System.Text.UTF8Encoding(false));
            generated[path] = Files.Hash(path);
        }
        InstrumentManifest manifest = new(Schema.Version, bindingId, new(packet.Fingerprints), generated, events,
            "Test-only Roslyn instrumentation; lock-field capture checked, but atomicity, full source coverage, host authenticity and general refinement are not proved.");
        Files.WriteNew(Path.Combine(output, "bindings.json"), manifest);
        return manifest;
    }

    private static string RequiredIdentity(SourceBinding binding, string name) =>
        Scalar(binding, binding.Tag.Options.GetValueOrDefault(name) ?? throw new InputException(ErrorCode.InvalidInstrumentation, $"Executable step needs {name}."), identity: true);

    private static string Scalar(SourceBinding binding, string expression, bool identity)
    {
        ExpressionSyntax syntax = SyntaxFactory.ParseExpression(expression);
        if (syntax.ContainsDiagnostics || syntax.DescendantNodesAndSelf().Any(n => n is not (IdentifierNameSyntax or MemberAccessExpressionSyntax or ThisExpressionSyntax or LiteralExpressionSyntax or PrefixUnaryExpressionSyntax)))
            throw new InputException(ErrorCode.InvalidInstrumentation, "Capture expressions must be simple scalar fields, parameters, locals or literals; no calls/getters/indexers.");
        if (syntax is PrefixUnaryExpressionSyntax unary &&
            (unary.Kind() is not (SyntaxKind.UnaryMinusExpression or SyntaxKind.UnaryPlusExpression) ||
             unary.Operand is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.NumericLiteralExpression)))
            throw new InputException(ErrorCode.InvalidInstrumentation, "Only signed numeric literals may use prefix operators.");
        ITypeSymbol? type = binding.Semantics.GetSpeculativeTypeInfo(binding.Node.SpanStart, syntax, SpeculativeBindingOption.BindAsExpression).Type;
        ISymbol? symbol = binding.Semantics.GetSpeculativeSymbolInfo(binding.Node.SpanStart, syntax, SpeculativeBindingOption.BindAsExpression).Symbol;
        if (identity && syntax is ThisExpressionSyntax)
        {
            if (type is not INamedTypeSymbol { TypeKind: TypeKind.Class, IsReferenceType: true })
                throw new InputException(ErrorCode.InvalidInstrumentation, "this identities require a reference-type class receiver.");
            return "global::TlaLink.Runtime.TraceRuntime.ObjectIdentity(this)";
        }
        if (syntax is not LiteralExpressionSyntax && syntax is not PrefixUnaryExpressionSyntax && symbol is not (IFieldSymbol or ILocalSymbol or IParameterSymbol))
            throw new InputException(ErrorCode.InvalidInstrumentation, "Unresolved or effectful capture symbol.");
        if (syntax is MemberAccessExpressionSyntax member && member.Expression is not ThisExpressionSyntax &&
            (symbol is not IFieldSymbol { IsStatic: true } ||
             binding.Semantics.GetSpeculativeSymbolInfo(binding.Node.SpanStart, member.Expression, SpeculativeBindingOption.BindAsExpression).Symbol is not INamedTypeSymbol))
            throw new InputException(ErrorCode.InvalidInstrumentation, "Identity receivers must be this or a static type; getter/instance chains are not captured.");
        if (symbol is IFieldSymbol { IsReadOnly: false } mutable &&
            (!binding.Tag.Options.TryGetValue("guard", out string? guard) || !Held(binding, ResolveGuard(binding, guard))))
            throw new InputException(ErrorCode.InvalidInstrumentation, $"Mutable identity field {mutable.Name} requires an explicit held guard.");
        if (identity && type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
        {
            if (underlying.SpecialType == SpecialType.System_UInt64)
                throw new InputException(ErrorCode.InvalidInstrumentation, "UInt64-backed enum identities cannot be represented losslessly as signed trace integers.");
            _ = TypeName(underlying);
            return "((long)(" + expression + "))";
        }
        if (identity && type is INamedTypeSymbol { TypeKind: TypeKind.Class } && type.SpecialType != SpecialType.System_String)
            return "global::TlaLink.Runtime.TraceRuntime.ObjectIdentity(" + expression + ")";
        if (type is null || identity && TypeName(type) == "bool") throw new InputException(ErrorCode.InvalidInstrumentation, "Identity requires int or string.");
        _ = TypeName(type);
        return expression;
    }

    private static string TypeName(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
        SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 => "int",
        SpecialType.System_Boolean => "bool",
        SpecialType.System_String => "string",
        _ => throw new InputException(ErrorCode.InvalidInstrumentation, $"Unsupported observation type: {type}")
    };

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static IFieldSymbol ResolveGuard(SourceBinding binding, string expression)
    {
        ExpressionSyntax syntax = SyntaxFactory.ParseExpression(expression);
        ISymbol? symbol = binding.Semantics.GetSpeculativeSymbolInfo(binding.Node.SpanStart, syntax, SpeculativeBindingOption.BindAsExpression).Symbol;
        if (symbol is not IFieldSymbol { Type.IsReferenceType: true, IsReadOnly: true } field ||
            !field.IsStatic && syntax is not (IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }) ||
            field.IsStatic && syntax is MemberAccessExpressionSyntax member &&
                binding.Semantics.GetSpeculativeSymbolInfo(binding.Node.SpanStart, member.Expression, SpeculativeBindingOption.BindAsExpression).Symbol is not INamedTypeSymbol)
            throw new InputException(ErrorCode.InvalidInstrumentation, "Guard must be a direct this-field or static readonly reference field, without getter/instance chains.");
        return field;
    }

    private static bool Held(SourceBinding binding, IFieldSymbol guard) =>
        binding.Node.Ancestors()
        .TakeWhile(n => n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
        .OfType<LockStatementSyntax>().Any(l =>
        {
            ExpressionSyntax expression = l.Expression;
            while (expression is ParenthesizedExpressionSyntax parentheses) expression = parentheses.Expression;
            return SymbolEqualityComparer.Default.Equals(binding.Semantics.GetSymbolInfo(expression).Symbol, guard) &&
                (guard.IsStatic || expression is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax });
        });
}
