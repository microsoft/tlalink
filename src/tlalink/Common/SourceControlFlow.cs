using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace TlaLink;

public sealed record SourceFlowArgument(string Parameter, string RefKind, string? Symbol, string Expression);
public sealed record SourceFlowCall(string Target, int Block, Place Location, string? Receiver,
    bool? ReceiverReadOnly, List<SourceFlowArgument> Arguments, string? Definition = null);
public sealed record SourceFlowBlock(int Id, string Kind, bool Reachable, List<int> Predecessors,
    List<int> Dominators, int? Fallthrough, int? Conditional, string ConditionKind, string? Condition);
public sealed record SourceControlFlow(List<SourceFlowBlock> Blocks, List<SourceFlowCall> Calls, string Boundary);

public static class SourceControlFlows
{
    internal static SourceControlFlow? Build(SyntaxNode declaration, SemanticModel semantics, Func<SyntaxNode, Place> at)
    {
        ControlFlowGraph? graph = Graph(declaration, semantics);
        if (graph == null) return null;
        List<BasicBlock> reachable = graph.Blocks.Where(b => b.IsReachable).ToList();
        HashSet<int> all = reachable.Select(b => b.Ordinal).ToHashSet();
        Dictionary<int, HashSet<int>> dominators = reachable.ToDictionary(b => b.Ordinal,
            b => b.Kind == BasicBlockKind.Entry ? new HashSet<int> { b.Ordinal } : new HashSet<int>(all));
        bool changed;
        do
        {
            changed = false;
            foreach (BasicBlock block in reachable.Where(b => b.Kind != BasicBlockKind.Entry))
            {
                List<int> predecessors = block.Predecessors.Where(p => p.Source.IsReachable)
                    .Select(p => p.Source.Ordinal).Distinct().ToList();
                HashSet<int> next = predecessors.Count == 0 ? [] : new(dominators[predecessors[0]]);
                foreach (int predecessor in predecessors.Skip(1)) next.IntersectWith(dominators[predecessor]);
                next.Add(block.Ordinal);
                if (!next.SetEquals(dominators[block.Ordinal])) { dominators[block.Ordinal] = next; changed = true; }
            }
        } while (changed);
        List<SourceFlowCall> calls = [];
        foreach (BasicBlock block in graph.Blocks)
        {
            IEnumerable<IOperation> roots = block.BranchValue == null ? block.Operations : block.Operations.Append(block.BranchValue);
            foreach (IInvocationOperation call in roots.SelectMany(Operations).OfType<IInvocationOperation>())
            {
                ISymbol? receiver = Symbol(call.Instance);
                calls.Add(new(Id(call.TargetMethod), block.Ordinal, at(call.Syntax),
                    receiver == null ? null : Id(receiver), receiver is IFieldSymbol field ? field.IsReadOnly : null,
                    call.Arguments.Select(a => new SourceFlowArgument(a.Parameter?.Name ?? "",
                        a.Parameter?.RefKind.ToString() ?? "", Symbol(a.Value) is ISymbol symbol ? Id(symbol) : null,
                        a.Value.Syntax.ToString())).ToList(), Id(call.TargetMethod.OriginalDefinition)));
            }
        }
        return new(graph.Blocks.Select(b => new SourceFlowBlock(b.Ordinal, b.Kind.ToString(), b.IsReachable,
            b.Predecessors.Select(p => p.Source.Ordinal).Distinct().Order().ToList(),
            dominators.TryGetValue(b.Ordinal, out HashSet<int>? d) ? d.Order().ToList() : [],
            b.FallThroughSuccessor?.Destination?.Ordinal, b.ConditionalSuccessor?.Destination?.Ordinal,
            b.ConditionKind.ToString(), b.BranchValue?.Syntax.ToString())).ToList(), calls,
            "Roslyn explicit control-flow facts and block dominance only. Calls, implicit exception dispatch, " +
            "finally behavior, runtime dispatch, concurrency and language/runtime semantics still require review; not a C# proof.");
    }

    internal static ControlFlowGraph? Graph(SyntaxNode declaration, SemanticModel semantics) =>
        semantics.GetOperation(declaration) switch {
            IMethodBodyOperation method => ControlFlowGraph.Create(method),
            IConstructorBodyOperation constructor => ControlFlowGraph.Create(constructor),
            _ => null
        };

    internal static string Id(ISymbol symbol) => symbol.GetDocumentationCommentId() ??
        symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
    private static ISymbol? Symbol(IOperation? operation) => operation switch {
        IFieldReferenceOperation field => field.Field,
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        IConversionOperation conversion => Symbol(conversion.Operand),
        _ => null
    };
    internal static IEnumerable<IOperation> Operations(IOperation operation)
    {
        yield return operation;
        if (operation is IAnonymousFunctionOperation or ILocalFunctionOperation) yield break;
        foreach (IOperation child in operation.ChildOperations)
            foreach (IOperation nested in Operations(child)) yield return nested;
    }
}
