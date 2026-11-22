using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace TlaLink;

/// <summary>
/// Static discharge checks for primitive guarantees over the primitive's own type declarations. They are syntactic
/// Roslyn checks over explicit sources: no alias, data-flow or runtime analysis. Guarantees without a check stay outstanding.
/// </summary>
internal static class SynchronizationGuarantees
{
    internal const string SignalsAsyncCheck = "static: every TaskCompletionSource construction passes RunContinuationsAsynchronously " +
        "and every ManualResetValueTaskSourceCore field sets RunContinuationsAsynchronously = true on every construction path";
    internal const string NonBlockingCheck = "static: no Wait, Result, GetResult, Thread.Sleep/Join, Monitor.Wait, WaitHandle or " +
        "SpinWait.SpinUntil call, and await only inside wait operations";
    private const int RunContinuationsAsynchronously = 64;

    internal static List<GuaranteeStatus> Check(CSharpScope source, PrimitiveManifest manifest) =>
        Primitives.IsLibrary(manifest) ? Check(source.Compilation, manifest, null)
            : Check(source.Compilation, manifest, Primitives.Type(source.Compilation, manifest, source.Texts.Keys));

    internal static List<GuaranteeStatus> Check(Compilation compilation, PrimitiveManifest manifest, INamedTypeSymbol? type)
    {
        List<GuaranteeStatus> result = [];
        foreach (string guarantee in manifest.Guarantees ?? [])
        {
            if (type == null || Primitives.IsLibrary(manifest))
                result.Add(new(guarantee, "outstanding", "inspection: a library type has no sources to check", []));
            else if (guarantee == "signals-async")
                result.Add(Status(guarantee, SignalsAsyncCheck, SignalsAsync(compilation, type)));
            else if (guarantee == "non-blocking")
                result.Add(Status(guarantee, NonBlockingCheck, NonBlocking(compilation, manifest, type)));
            else result.Add(new(guarantee, "outstanding", "declaration and inspection; no static check", []));
        }
        return result;
    }

    private static GuaranteeStatus Status(string guarantee, string check, List<string> findings) =>
        new(guarantee, findings.Count == 0 ? "discharged" : "failed", check, findings);

    private static IEnumerable<(SyntaxNode Node, SemanticModel Model)> Nodes(Compilation compilation, INamedTypeSymbol type) =>
        type.DeclaringSyntaxReferences.SelectMany(reference =>
        {
            SyntaxNode declaration = reference.GetSyntax();
            SemanticModel model = compilation.GetSemanticModel(declaration.SyntaxTree);
            return declaration.DescendantNodes().Select(n => (n, model));
        });

    private static List<string> SignalsAsync(Compilation compilation, INamedTypeSymbol type)
    {
        List<string> findings = [];
        foreach (var (node, model) in Nodes(compilation, type))
        {
            if (node is not BaseObjectCreationExpressionSyntax creation ||
                model.GetOperation(creation) is not IObjectCreationOperation operation ||
                !IsType(operation.Type, "System.Threading.Tasks.TaskCompletionSource", "System.Threading.Tasks.TaskCompletionSource`1"))
                continue;
            IArgumentOperation? options = operation.Arguments.FirstOrDefault(a =>
                a.Parameter?.Type.ToDisplayString() == "System.Threading.Tasks.TaskCreationOptions");
            if (options?.Value.ConstantValue is not { HasValue: true, Value: int value } || (value & RunContinuationsAsynchronously) == 0)
                findings.Add(Locate(node, model, "TaskCompletionSource is constructed without a constant TaskCreationOptions.RunContinuationsAsynchronously."));
        }
        foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic &&
            IsType(f.Type, "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1")))
            findings.AddRange(ValueTaskSourceCore(compilation, type, field));
        return findings;
    }

    /// <summary>Either the field initializer or a top-level statement of every constructor sets the flag to true.</summary>
    private static List<string> ValueTaskSourceCore(Compilation compilation, INamedTypeSymbol type, IFieldSymbol field)
    {
        List<string> findings = [];
        bool Sets(SyntaxNode node, SemanticModel model) =>
            node is AssignmentExpressionSyntax { Left: var left, Right: var right } &&
            model.GetSymbolInfo(left).Symbol is IPropertySymbol { Name: "RunContinuationsAsynchronously" } &&
            (left is MemberAccessExpressionSyntax access && SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(access.Expression).Symbol, field) ||
                left is IdentifierNameSyntax && node.Parent is InitializerExpressionSyntax) &&
            model.GetConstantValue(right) is { HasValue: true, Value: true };
        bool initialized = field.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VariableDeclaratorSyntax>()
            .Any(d => d.Initializer?.Value is BaseObjectCreationExpressionSyntax { Initializer: { } init } &&
                init.Expressions.Any(e => Sets(e, compilation.GetSemanticModel(d.SyntaxTree))));
        List<ConstructorDeclarationSyntax> constructors = type.InstanceConstructors.SelectMany(c => c.DeclaringSyntaxReferences)
            .Select(r => r.GetSyntax()).OfType<ConstructorDeclarationSyntax>().ToList();
        Dictionary<IMethodSymbol, bool> known = new(SymbolEqualityComparer.Default);
        bool Constructs(ConstructorDeclarationSyntax constructor)
        {
            SemanticModel model = compilation.GetSemanticModel(constructor.SyntaxTree);
            IMethodSymbol symbol = model.GetDeclaredSymbol(constructor)!;
            if (known.TryGetValue(symbol, out bool done)) return done;
            known[symbol] = false;
            bool result = constructor.Body?.Statements.OfType<ExpressionStatementSyntax>().Any(s => Sets(s.Expression, model)) == true;
            if (!result && constructor.Initializer is { ThisOrBaseKeyword.RawKind: (int)SyntaxKind.ThisKeyword } chained &&
                model.GetSymbolInfo(chained).Symbol is IMethodSymbol target &&
                target.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<ConstructorDeclarationSyntax>().FirstOrDefault() is { } next)
                result = Constructs(next);
            known[symbol] = result;
            return result;
        }
        if (!initialized)
        {
            if (constructors.Count == 0)
                findings.Add(Locate(field.DeclaringSyntaxReferences[0].GetSyntax(), compilation.GetSemanticModel(field.DeclaringSyntaxReferences[0].SyntaxTree),
                    "ManualResetValueTaskSourceCore field " + field.Name + " does not set RunContinuationsAsynchronously = true; the type has only an implicit constructor."));
            foreach (ConstructorDeclarationSyntax constructor in constructors.Where(c => !Constructs(c)))
                findings.Add(Locate(constructor, compilation.GetSemanticModel(constructor.SyntaxTree),
                    "Constructor does not set " + field.Name + ".RunContinuationsAsynchronously = true at its top level."));
        }
        foreach (var (node, model) in Nodes(compilation, type))
        {
            if (node is not AssignmentExpressionSyntax assignment) continue;
            if (SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(assignment.Left).Symbol, field))
                findings.Add(Locate(node, model, "ManualResetValueTaskSourceCore field " + field.Name + " is reassigned."));
            else if (assignment.Left is MemberAccessExpressionSyntax access &&
                SymbolEqualityComparer.Default.Equals(model.GetSymbolInfo(access.Expression).Symbol, field) &&
                model.GetSymbolInfo(access).Symbol is IPropertySymbol { Name: "RunContinuationsAsynchronously" } && !Sets(node, model))
                findings.Add(Locate(node, model, field.Name + ".RunContinuationsAsynchronously is set to a value other than constant true."));
        }
        return findings;
    }

    private static List<string> NonBlocking(Compilation compilation, PrimitiveManifest manifest, INamedTypeSymbol type)
    {
        List<string> findings = [];
        HashSet<string> waits = manifest.Operations.Where(o => o.Role == "wait").Select(o => o.Member).ToHashSet(StringComparer.Ordinal);
        foreach (var (node, model) in Nodes(compilation, type))
        {
            if (node is InvocationExpressionSyntax invocation && model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method &&
                Blocking(method) is string call)
                findings.Add(Locate(node, model, "Blocking call " + call + "."));
            else if (node is SimpleNameSyntax name && model.GetSymbolInfo(name).Symbol is IPropertySymbol { Name: "Result" } property &&
                IsType(property.ContainingType, "System.Threading.Tasks.Task`1", "System.Threading.Tasks.ValueTask`1"))
                findings.Add(Locate(node, model, "Blocking read of " + property.ContainingType.Name + ".Result."));
            else if (IsAwait(node) && !waits.Contains(Member(node, model)))
                findings.Add(Locate(node, model, "await outside an operation with role wait."));
        }
        return findings;
    }

    private static string? Blocking(IMethodSymbol method)
    {
        string type = method.ContainingType.OriginalDefinition.ToDisplayString(), ns = method.ContainingNamespace?.ToDisplayString() ?? "";
        if (method.Name is "Wait" or "WaitAll" or "WaitAny" or "SignalAndWait" && ns is "System.Threading" or "System.Threading.Tasks" ||
            method.Name is "WaitOne" or "WaitAll" or "WaitAny" && DerivesFrom(method.ContainingType, "System.Threading.WaitHandle") ||
            type == "System.Threading.Thread" && method.Name is "Sleep" or "Join" ||
            type == "System.Threading.SpinWait" && method.Name == "SpinUntil" ||
            method.Name == "GetResult" && ns == "System.Runtime.CompilerServices" && method.ContainingType.Name.EndsWith("Awaiter", StringComparison.Ordinal))
            return method.ContainingType.Name + "." + method.Name;
        return null;
    }

    internal static bool IsAwait(SyntaxNode node) => node is AwaitExpressionSyntax ||
        node is ForEachStatementSyntax { AwaitKeyword.RawKind: (int)SyntaxKind.AwaitKeyword } ||
        node is UsingStatementSyntax { AwaitKeyword.RawKind: (int)SyntaxKind.AwaitKeyword } ||
        node is LocalDeclarationStatementSyntax { AwaitKeyword.RawKind: (int)SyntaxKind.AwaitKeyword };

    internal static bool IsType(ITypeSymbol? type, params string[] metadataNames) =>
        type is INamedTypeSymbol named && metadataNames.Contains(MetadataName(named.OriginalDefinition), StringComparer.Ordinal);

    internal static string MetadataName(INamedTypeSymbol type) =>
        (type.ContainingType != null ? MetadataName(type.ContainingType) + "+" :
            type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." : "") + type.MetadataName;

    internal static bool DerivesFrom(ITypeSymbol? type, string metadataName)
    {
        for (ITypeSymbol? current = type; current != null; current = current.BaseType)
            if (current is INamedTypeSymbol named && MetadataName(named.OriginalDefinition) == metadataName) return true;
        return false;
    }

    /// <summary>The documentation ID of the declared member that textually contains a node (lambdas and local functions included).</summary>
    internal static string Member(SyntaxNode node, SemanticModel model)
    {
        foreach (SyntaxNode ancestor in node.AncestorsAndSelf())
        {
            ISymbol? symbol = ancestor switch {
                BaseMethodDeclarationSyntax or BasePropertyDeclarationSyntax or BaseTypeDeclarationSyntax => model.GetDeclaredSymbol(ancestor),
                VariableDeclaratorSyntax { Parent.Parent: BaseFieldDeclarationSyntax } => model.GetDeclaredSymbol(ancestor),
                BaseFieldDeclarationSyntax field => model.GetDeclaredSymbol(field.Declaration.Variables[0]),
                _ => null
            };
            if (symbol?.GetDocumentationCommentId() is string id) return id;
        }
        return "(file scope)";
    }

    internal static string Locate(SyntaxNode node, SemanticModel model, string message)
    {
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        return Path.GetFileName(span.Path) + ":" + (span.StartLinePosition.Line + 1) + ": " + Member(node, model) + ": " + message;
    }

    internal static Place PlaceOf(SyntaxNode node)
    {
        FileLinePositionSpan span = node.GetLocation().GetLineSpan();
        return new(span.Path, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }
}
