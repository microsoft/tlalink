using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TlaLink;

/// <summary>A validated state_mapping entry: the kind View field, the authored C# expression and its trace scalar type.</summary>
public sealed record MappedStateField(string Field, string Expression, string Type);

/// <summary>
/// Roslyn validation of a source primitive's state_mapping (SR116 unsupported, SR117 effectful). Each expression binds in
/// the primitive type's instance context and may only read the type's own instance fields (and members of them), constants,
/// allowlisted properties and allowlisted pure methods, combined with literals and a closed operator set.
/// </summary>
internal static class PrimitiveStateMapping
{
    /// <summary>Scalar fields of each standard kind's View(i) projection; custom modules define their own View fields.</summary>
    internal static readonly Dictionary<string, (string Field, string Type)[]> KindFields = new(StringComparer.Ordinal)
    {
        ["register"] = [("value", "scalar")],
        ["latch"] = [("status", "string")],
        ["once"] = [("status", "string")],
        ["admission-gate"] = [("count", "int"), ("closed", "bool"), ("signaled", "bool")],
        ["counting-resource"] = [("available", "int"), ("waiting", "int"), ("granted", "int"), ("closed", "bool")],
        ["channel"] = [("items", "int"), ("completion", "string"), ("waiting", "bool")],
        ["cancellation-source"] = [("requested", "bool"), ("disposed", "bool")]
    };
    internal static readonly string[] Properties = ["Count", "Length", "IsCompleted", "IsCompletedSuccessfully", "IsFaulted",
        "IsCanceled", "IsCancellationRequested", "Task"];
    internal static readonly string[] Methods = ["System.Math.Min", "System.Math.Max", "System.Math.Abs"];
    private static readonly SyntaxKind[] Binary = [SyntaxKind.LogicalAndExpression, SyntaxKind.LogicalOrExpression,
        SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression, SyntaxKind.LessThanExpression, SyntaxKind.LessThanOrEqualExpression,
        SyntaxKind.GreaterThanExpression, SyntaxKind.GreaterThanOrEqualExpression, SyntaxKind.AddExpression, SyntaxKind.SubtractExpression,
        SyntaxKind.MultiplyExpression, SyntaxKind.DivideExpression, SyntaxKind.ModuloExpression, SyntaxKind.BitwiseAndExpression,
        SyntaxKind.BitwiseOrExpression];

    /// <summary>Structural rules checked when the manifest loads.</summary>
    internal static void Validate(PrimitiveManifest manifest)
    {
        if (manifest.StateMapping == null)
        {
            if (manifest.StateLock != null) throw Invalid(manifest, "state_lock requires a state_mapping.");
            return;
        }
        if (Primitives.IsLibrary(manifest)) throw Invalid(manifest, "state_mapping is supported for source primitives only.");
        if (manifest.StateMapping.Count is < 1 or > 100 || manifest.StateMapping.Any(p => !Regex.IsMatch(p.Key, "^[A-Za-z_][A-Za-z0-9_]*$") ||
                string.IsNullOrWhiteSpace(p.Value) || p.Value.Length > 1000 || p.Value.Contains('\n') || p.Value.Contains('\r')))
            throw Invalid(manifest, "state_mapping maps 1..100 field names to one-line C# expressions of at most 1000 characters.");
        if (KindFields.TryGetValue(manifest.PrimitiveKind, out var fields) &&
            manifest.StateMapping.Keys.FirstOrDefault(k => !fields.Any(f => f.Field == k)) is string unknown)
            throw Invalid(manifest, "state_mapping field " + unknown + " is not a View field of kind " + manifest.PrimitiveKind + " (" +
                string.Join(", ", fields.Select(f => f.Field)) + ").");
        if (manifest.StateLock != null && (!manifest.StateLock.StartsWith("F:", StringComparison.Ordinal) || manifest.StateLock.Length < 3))
            throw Invalid(manifest, "state_lock is the F: documentation ID of the primitive's own lock field.");
    }

    /// <summary>Binds every mapped expression in the primitive type's context; throws SR116/SR117 with every finding.</summary>
    internal static List<MappedStateField> Check(Compilation compilation, INamedTypeSymbol type, PrimitiveManifest manifest)
    {
        if (manifest.StateMapping == null) return [];
        List<(ErrorCode Code, string Message)> findings = [];
        List<MappedStateField> result = [];
        if (manifest.StateLock != null && (DocumentationCommentId.GetSymbolsForDeclarationId(manifest.StateLock, compilation)
                .OfType<IFieldSymbol>().ToList() is not [IFieldSymbol lockField] || lockField.IsStatic || !lockField.IsReadOnly ||
                !lockField.Type.IsReferenceType || !SymbolEqualityComparer.Default.Equals(lockField.ContainingType.OriginalDefinition, type.OriginalDefinition)))
            findings.Add((ErrorCode.InvalidStateMapping, "state_lock " + manifest.StateLock + " is not a readonly reference-type instance field of " +
                manifest.Type + "."));
        var (model, position) = Context(compilation, type);
        foreach (var (field, text) in manifest.StateMapping.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string label = "state_mapping " + field + " = " + text + ": ";
            if (model == null)
            {
                findings.Add((ErrorCode.InvalidStateMapping, label + manifest.Type + " has no instance member body to bind the expression in."));
                continue;
            }
            ExpressionSyntax expression = SyntaxFactory.ParseExpression(text);
            if (expression.ContainsDiagnostics || expression.FullSpan.Length != text.Length)
            {
                findings.Add((ErrorCode.InvalidStateMapping, label + "is not one complete C# expression."));
                continue;
            }
            int before = findings.Count;
            Walk(expression, model, position, type, (code, reason) => findings.Add((code, label + reason)));
            if (findings.Count > before) continue;
            ITypeSymbol? resultType = model.GetSpeculativeTypeInfo(position, expression, SpeculativeBindingOption.BindAsExpression).Type;
            string? scalar = resultType == null ? null : Scalar(resultType);
            string expected = KindFields.TryGetValue(manifest.PrimitiveKind, out var fields) ? fields.Single(f => f.Field == field).Type : "scalar";
            if (scalar == null)
                findings.Add((ErrorCode.InvalidStateMapping, label + "has type " + (resultType?.ToDisplayString() ?? "unknown") +
                    "; trace values are int, bool or string."));
            else if (expected != "scalar" && expected != scalar)
                findings.Add((ErrorCode.InvalidStateMapping, label + "has type " + scalar + ", but View field " + field + " of kind " +
                    manifest.PrimitiveKind + " is " + expected + "."));
            else result.Add(new(field, text, scalar));
        }
        if (findings.Count > 0)
            throw new InputException(findings[0].Code, "Primitive " + manifest.Id + ": " + string.Join("; ", findings.Select(f => f.Code.Id() + " " + f.Message)));
        return result;
    }

    private static void Walk(ExpressionSyntax root, SemanticModel model, int position, INamedTypeSymbol type, Action<ErrorCode, string> report)
    {
        ISymbol? Symbol(ExpressionSyntax node) => model.GetSpeculativeSymbolInfo(position, node, SpeculativeBindingOption.BindAsExpression).Symbol;
        bool Own(IFieldSymbol field) => !field.IsStatic && SymbolEqualityComparer.Default.Equals(field.ContainingType.OriginalDefinition, type.OriginalDefinition);
        // A receiver chain must start at this, an own instance field (possibly implicit) or a type for constants and allowlisted methods.
        void Read(ExpressionSyntax node, bool receiver)
        {
            switch (node)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    Read(parenthesized.Expression, receiver);
                    return;
                case ThisExpressionSyntax when receiver:
                    return;
                case PredefinedTypeSyntax when receiver:
                    return;
                case IdentifierNameSyntax identifier:
                    switch (Symbol(identifier))
                    {
                        case IFieldSymbol { IsConst: true }:
                            return;
                        case IFieldSymbol field when Own(field):
                            return;
                        case INamedTypeSymbol or INamespaceSymbol when receiver:
                            return;
                        case IFieldSymbol field:
                            report(ErrorCode.InvalidStateMapping, "reads " + field.ToDisplayString() + ", which is not an instance field of the primitive or a constant.");
                            return;
                        case IPropertySymbol or IMethodSymbol or IEventSymbol:
                            report(ErrorCode.EffectfulStateMapping, "uses member " + identifier + " directly; only fields, allowlisted properties and allowlisted methods are read.");
                            return;
                        case null:
                            report(ErrorCode.InvalidStateMapping, identifier + " does not bind in the context of " + type.ToDisplayString() + ".");
                            return;
                        default:
                            report(ErrorCode.InvalidStateMapping, identifier + " is not a field read.");
                            return;
                    }
                case MemberAccessExpressionSyntax member when member.IsKind(SyntaxKind.SimpleMemberAccessExpression):
                    switch (Symbol(member))
                    {
                        case IFieldSymbol { IsConst: true }:
                            Read(member.Expression, receiver: true);
                            return;
                        case INamedTypeSymbol or INamespaceSymbol when receiver:
                            return;
                        case IFieldSymbol field when !field.IsStatic:
                            if (member.Expression is ThisExpressionSyntax && !Own(field))
                                report(ErrorCode.InvalidStateMapping, "reads " + field.ToDisplayString() + ", which is not an instance field of the primitive.");
                            else if (member.Expression is not ThisExpressionSyntax) Read(member.Expression, receiver: false);
                            return;
                        case IPropertySymbol { IsStatic: false, IsIndexer: false, Parameters.Length: 0 } property
                            when Properties.Contains(property.Name, StringComparer.Ordinal):
                            Read(member.Expression, receiver: false);
                            return;
                        case IPropertySymbol property:
                            report(ErrorCode.EffectfulStateMapping, "reads property " + property.ToDisplayString() + ", whose getter may run code; " +
                                "allowlisted properties are " + string.Join(", ", Properties) + ".");
                            return;
                        case IFieldSymbol field:
                            report(ErrorCode.InvalidStateMapping, "reads static field " + field.ToDisplayString() + ", which is not primitive state.");
                            return;
                        case null:
                            report(ErrorCode.InvalidStateMapping, member + " does not bind in the context of " + type.ToDisplayString() + ".");
                            return;
                        default:
                            report(ErrorCode.EffectfulStateMapping, "uses " + member + " outside an allowlisted method call.");
                            return;
                    }
                default:
                    if (receiver) report(ErrorCode.InvalidStateMapping, "member receiver " + node + " is not this, a field or a type.");
                    else Value(node);
                    return;
            }
        }
        void Value(ExpressionSyntax node)
        {
            switch (node)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    Value(parenthesized.Expression);
                    break;
                case LiteralExpressionSyntax literal when literal.Kind() is SyntaxKind.NumericLiteralExpression or SyntaxKind.StringLiteralExpression or
                    SyntaxKind.TrueLiteralExpression or SyntaxKind.FalseLiteralExpression or SyntaxKind.NullLiteralExpression:
                    break;
                case IdentifierNameSyntax or MemberAccessExpressionSyntax:
                    Read(node, receiver: false);
                    break;
                case PrefixUnaryExpressionSyntax unary when unary.Kind() is SyntaxKind.LogicalNotExpression or SyntaxKind.UnaryMinusExpression or
                    SyntaxKind.UnaryPlusExpression:
                    Value(unary.Operand);
                    break;
                case BinaryExpressionSyntax binary when Binary.Contains(binary.Kind()):
                    Value(binary.Left);
                    Value(binary.Right);
                    break;
                case ConditionalExpressionSyntax conditional:
                    Value(conditional.Condition);
                    Value(conditional.WhenTrue);
                    Value(conditional.WhenFalse);
                    break;
                case InvocationExpressionSyntax invocation:
                    if (Symbol(invocation) is IMethodSymbol { IsStatic: true } method &&
                        Methods.Contains(method.ContainingType.ToDisplayString() + "." + method.Name, StringComparer.Ordinal) &&
                        invocation.Expression is MemberAccessExpressionSyntax target &&
                        invocation.ArgumentList.Arguments.All(a => a.NameColon == null && a.RefKindKeyword.IsKind(SyntaxKind.None)))
                    {
                        Read(target.Expression, receiver: true);
                        foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments) Value(argument.Expression);
                    }
                    else report(ErrorCode.EffectfulStateMapping, "calls " + invocation.Expression + "; only " + string.Join(", ", Methods) +
                        " may be called.");
                    break;
                case AssignmentExpressionSyntax:
                    report(ErrorCode.EffectfulStateMapping, "assigns in " + node + ".");
                    break;
                case PrefixUnaryExpressionSyntax or PostfixUnaryExpressionSyntax when node.Kind() is SyntaxKind.PreIncrementExpression or
                    SyntaxKind.PreDecrementExpression or SyntaxKind.PostIncrementExpression or SyntaxKind.PostDecrementExpression:
                    report(ErrorCode.EffectfulStateMapping, "increments or decrements in " + node + ".");
                    break;
                case AwaitExpressionSyntax or BaseObjectCreationExpressionSyntax or ArrayCreationExpressionSyntax or
                    ImplicitArrayCreationExpressionSyntax or AnonymousFunctionExpressionSyntax or ElementAccessExpressionSyntax or
                    ConditionalAccessExpressionSyntax:
                    report(ErrorCode.EffectfulStateMapping, node.Kind() + " " + node + " can run code or allocate.");
                    break;
                default:
                    report(ErrorCode.InvalidStateMapping, "unsupported " + node.Kind() + " " + node + ".");
                    break;
            }
        }
        Value(root);
    }

    /// <summary>A position inside an instance member body of the type, where this and its private fields bind.</summary>
    private static (SemanticModel? Model, int Position) Context(Compilation compilation, INamedTypeSymbol type)
    {
        foreach (SyntaxReference reference in type.DeclaringSyntaxReferences)
            foreach (SyntaxNode node in reference.GetSyntax().DescendantNodes(n => n is not BaseTypeDeclarationSyntax || n == reference.GetSyntax()))
            {
                bool isStatic = node.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() is { } owner &&
                    owner.Modifiers.Any(SyntaxKind.StaticKeyword);
                int? position = node switch {
                    BlockSyntax block when block.Parent is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax && !isStatic =>
                        block.OpenBraceToken.Span.End,
                    ArrowExpressionClauseSyntax arrow when !isStatic => arrow.Expression.SpanStart,
                    _ => null
                };
                if (position is int found && node.Ancestors().OfType<BaseTypeDeclarationSyntax>().First() == reference.GetSyntax())
                    return (compilation.GetSemanticModel(node.SyntaxTree), found);
            }
        return (null, 0);
    }

    private static string? Scalar(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
        SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 => "int",
        SpecialType.System_Boolean => "bool",
        SpecialType.System_String => "string",
        _ => null
    };

    private static InputException Invalid(PrimitiveManifest manifest, string message) =>
        new(ErrorCode.InvalidStateMapping, "Primitive " + manifest.Id + ": " + message);
}
