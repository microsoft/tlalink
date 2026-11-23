using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TlaLink;

/// <summary>A model instance of a target whose primitive declares a state_mapping, resolved in the trace sources.</summary>
internal sealed record CapturedInstance(IFieldSymbol Field, InstanceAnnotation Instance, PrimitiveManifest Primitive,
    INamedTypeSymbol Type, List<MappedStateField> Mapping, IFieldSymbol? Lock, HashSet<string> SiteMembers);

/// <summary>
/// Trace capture through state_mapping: for a trace target, the instance fields whose primitive maps its abstract state,
/// and the operation sites a bound statement contains. Instrumentation snapshots each such instance after the statement.
/// </summary>
internal sealed class PrimitiveStateCapture
{
    internal const string Accessor = "__TlaLinkState";

    private PrimitiveStateCapture(Dictionary<IFieldSymbol, CapturedInstance> instances, Dictionary<string, string> pins) =>
        (Instances, Pins) = (instances, pins);

    internal Dictionary<IFieldSymbol, CapturedInstance> Instances { get; }
    internal Dictionary<string, string> Pins { get; }

    /// <summary>Null when the scope declares no primitive with a state_mapping or the target declares none of its instances.</summary>
    internal static PrimitiveStateCapture? Load(string scopePath, CorrespondenceConfiguration config, IReadOnlyList<TlaDocument> documents,
        CSharpScope source)
    {
        Dictionary<string, string> pins = new(FilePaths.Comparer);
        List<PrimitiveManifest> mapped = (config.Synchronization?.Primitives ?? [])
            .Select(p => Primitives.Load(FilePaths.Full(p, Path.GetDirectoryName(scopePath)!), pins))
            .Where(m => m.StateMapping != null).ToList();
        if (mapped.Count == 0) return null;
        Dictionary<IFieldSymbol, CapturedInstance> instances = new(SymbolEqualityComparer.Default);
        foreach (TlaDocument document in documents)
            foreach (FormalInstanceDeclaration declaration in FormalCorrespondence.Instances(document.Path, document.Text, document.Instances,
                ModelScope.FormalComments(document.Text)))
            {
                InstanceAnnotation instance = declaration.Instance;
                if (DocumentationCommentId.GetSymbolsForDeclarationId(instance.Field, source.Compilation).OfType<IFieldSymbol>().ToList()
                    is not [IFieldSymbol field] || field.Type is not INamedTypeSymbol fieldType)
                    continue;
                string typeId = fieldType.OriginalDefinition.GetDocumentationCommentId()!;
                if (mapped.FirstOrDefault(m => m.Type == typeId) is not { } primitive) continue;
                INamedTypeSymbol type = fieldType.OriginalDefinition;
                if (!type.DeclaringSyntaxReferences.Any() || type.DeclaringSyntaxReferences.Any(r => !source.Texts.ContainsKey(r.SyntaxTree.FilePath)))
                    throw new InputException(ErrorCode.InvalidInstrumentation, "State capture of instance " + instance.Id +
                        " requires the sources of primitive " + primitive.Id + " among the instrumented sources.");
                List<MappedStateField> mapping = PrimitiveStateMapping.Check(source.Compilation, type, primitive);
                IFieldSymbol? lockField = primitive.StateLock == null ? null :
                    DocumentationCommentId.GetSymbolsForDeclarationId(primitive.StateLock, source.Compilation).OfType<IFieldSymbol>().Single();
                HashSet<string> sites = primitive.Operations.Where(o => SynchronizationRegion.SiteRoles.Contains(o.Role, StringComparer.Ordinal))
                    .Select(o => o.Member).ToHashSet(StringComparer.Ordinal);
                if (!instances.TryAdd(field.OriginalDefinition, new(field.OriginalDefinition, instance, primitive, type, mapping, lockField, sites)))
                    throw new InputException(ErrorCode.InvalidInstrumentation, "One trace target declares two instances of " + instance.Field + ".");
            }
        return instances.Count == 0 ? null : new(instances, pins);
    }

    /// <summary>Instances with an operation site inside the bound syntax: a declared site-role member applied to the instance field.</summary>
    internal List<CapturedInstance> Sites(SyntaxNode node, SemanticModel model)
    {
        List<CapturedInstance> result = [];
        foreach (SimpleNameSyntax name in node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
        {
            ExpressionSyntax? receiver = name.Parent switch {
                MemberAccessExpressionSyntax access when access.Name == name => access.Expression,
                MemberBindingExpressionSyntax => name.Parent.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault()?.Expression,
                _ => null
            };
            if (receiver == null || model.GetSymbolInfo(name).Symbol is not { } member ||
                member.OriginalDefinition.GetDocumentationCommentId() is not string id)
                continue;
            while (receiver is ParenthesizedExpressionSyntax parenthesized) receiver = parenthesized.Expression;
            if (model.GetSymbolInfo(receiver).Symbol is IFieldSymbol field && Instances.TryGetValue(field.OriginalDefinition, out CapturedInstance? captured) &&
                captured.SiteMembers.Contains(id) && !result.Contains(captured))
                result.Add(captured);
        }
        return result.OrderBy(c => c.Instance.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>The snapshot method inserted into the primitive type: one evaluation of every mapped expression, under state_lock if declared.</summary>
    internal static string AccessorText(CapturedInstance instance)
    {
        string values = "return new global::System.Collections.Generic.Dictionary<string, object> { " +
            string.Join(", ", instance.Mapping.Select(m => "[" + System.Text.Json.JsonSerializer.Serialize(m.Field) + "] = (" + m.Expression + ")")) + " };";
        string body = instance.Lock == null ? values : "lock (this.@" + instance.Lock.Name + ")\n{\n" + values + "\n}";
        return "\ninternal global::System.Collections.Generic.Dictionary<string, object> " + Accessor + "()\n{\n" + body + "\n}\n";
    }
}
