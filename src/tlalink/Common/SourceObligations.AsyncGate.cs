using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace TlaLink;

public static partial class SourceObligations
{
    private static AsyncGateLifetime CheckAsyncGate(CSharpScope scope, SourceObligation obligation,
        ISymbol endpoint, IFieldSymbol field, List<Place> evidence, out SourceControlFlow? flow)
    {
        flow = null;
        Support(endpoint is IMethodSymbol { IsAsync: true, IsStatic: false, ReturnsByRef: false,
            ReturnsByRefReadonly: false } method && method.DeclaringSyntaxReferences.Length == 1 &&
            TaskType(method.ReturnType), "Select one async Task or ValueTask instance source method.");
        var selected = (IMethodSymbol)endpoint;
        Support(selected.DeclaringSyntaxReferences[0].GetSyntax() is MethodDeclarationSyntax { Body: not null },
            "An async gate lifetime requires a block-bodied method.");
        var declaration = (MethodDeclarationSyntax)selected.DeclaringSyntaxReferences[0].GetSyntax();
        BlockSyntax body = declaration.Body!;
        SemanticModel semantics = scope.Compilation.GetSemanticModel(declaration.SyntaxTree);
        Need(SymbolEqualityComparer.Default.Equals(field.ContainingType, selected.ContainingType),
            "The async method must own the selected gate field.");
        Need(field.IsReadOnly && field.Type.IsReferenceType && field.ContainingType.IsReferenceType,
            "An async gate requires readonly reference storage in a reference-type owner.");
        Support(field.Type is INamedTypeSymbol { IsSealed: true },
            "The initial async gate contract requires a sealed reference-type helper.");
        Support(field.DeclaringSyntaxReferences.Single().GetSyntax() is VariableDeclaratorSyntax {
            Initializer.Value: ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax } variable &&
            semantics.Compilation.GetSemanticModel(variable.SyntaxTree).GetOperation(variable.Initializer!.Value)
                is IObjectCreationOperation { Initializer: null },
            "The async gate field requires a direct fresh constructor initializer.");
        Support(!declaration.DescendantNodes().Any(n => n is
            ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax or WhileStatementSyntax or
            DoStatementSyntax or GotoStatementSyntax or YieldStatementSyntax or LocalFunctionStatementSyntax or
            AnonymousFunctionExpressionSyntax or UnsafeStatementSyntax or FixedStatementSyntax ||
            n is MethodDeclarationSyntax m && m.Modifiers.Any(SyntaxKind.UnsafeKeyword)),
            "Loops, jumps, iterators, nested functions and unsafe code require a separate lifetime analysis.");
        Support(!SourceControlFlows.Operations(semantics.GetOperation(declaration)!).Any(op =>
            op is IDynamicInvocationOperation or IFunctionPointerInvocationOperation or IDelegateCreationOperation),
            "Dynamic, delegate and function-pointer effects require an explicit lifetime contract.");
        Support(selected.ReturnType is not INamedTypeSymbol { TypeArguments: [var resultType] } || !TaskType(resultType),
            "Returning a nested task transfers work beyond this method's awaited lifetime.");

        List<IInvocationOperation> calls = declaration.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(s => semantics.GetOperation(s)).OfType<IInvocationOperation>().ToList();
        List<IInvocationOperation> acquisitions = calls.Where(c => Matches(c.TargetMethod, obligation.Gate!)).ToList();
        List<IInvocationOperation> releases = calls.Where(c => Matches(c.TargetMethod, obligation.Effect!)).ToList();
        Need(acquisitions.Count == 1 && releases.Count == 1,
            "Exactly one acquisition and one release call site are required.");
        IInvocationOperation acquire = acquisitions[0], release = releases[0];
        foreach (IInvocationOperation call in new[] { acquire, release })
        {
            Need(call.Instance is IFieldReferenceOperation receiver &&
                receiver.Instance is IInstanceReferenceOperation &&
                SymbolEqualityComparer.Default.Equals(receiver.Field, field),
                "Acquisition and release must use the same selected this-instance gate field.");
            Support(call.TargetMethod is { IsStatic: false, IsAsync: false, ReturnsVoid: true,
                IsVirtual: false, IsOverride: false, Parameters.Length: 0 } &&
                call.TargetMethod.DeclaringSyntaxReferences.Length == 1 &&
                SymbolEqualityComparer.Default.Equals(call.TargetMethod.ContainingType, field.Type),
                "Acquisition and release require direct synchronous parameterless void source methods on the gate helper.");
        }
        Support(acquire.Syntax.Parent is ExpressionStatementSyntax &&
            release.Syntax.Parent is ExpressionStatementSyntax,
            "Gate operations must be direct expression statements.");
        var acquireStatement = (ExpressionStatementSyntax)acquire.Syntax.Parent!;
        var releaseStatement = (ExpressionStatementSyntax)release.Syntax.Parent!;
        Need(releaseStatement.Parent is BlockSyntax { Parent: FinallyClauseSyntax },
            "The release must execute in a finally clause, not on success alone or before awaited work.");
        var finalizer = (FinallyClauseSyntax)releaseStatement.Parent!.Parent!;
        Need(finalizer.Block.Statements.Count == 1,
            "The selected finally must contain only the release attempt; earlier failing work could bypass it.");
        var protectedTry = (TryStatementSyntax)finalizer.Parent!;
        Support(protectedTry.Parent == body,
            "The initial async gate contract requires one top-level protected try/finally.");
        int index = body.Statements.IndexOf(protectedTry);
        Need(index > 0, "The protected try must immediately follow successful acquisition.");
        StatementSyntax prefix = body.Statements[index - 1];
        Need(prefix == acquireStatement ||
            prefix is LockStatementSyntax { Statement: BlockSyntax locked } &&
                locked.Statements.LastOrDefault() == acquireStatement,
            "Acquisition must immediately precede the protected try, or be the last statement of its immediately preceding lock.");
        if (prefix is LockStatementSyntax sourceLock)
            Support(semantics.GetTypeInfo(sourceLock.Expression).Type is not INamedTypeSymbol {
                Name: "Lock", ContainingNamespace: { } space } || space.ToDisplayString() != "System.Threading",
                "A specialized Lock scope requires a separate exit contract; only Monitor-based lock prefixes are supported.");
        Support(body.Statements.Skip(index + 1).All(s =>
            s is ReturnStatementSyntax { Expression: null } ||
            s is ReturnStatementSyntax { Expression: { } value } && SimpleReturn(value, semantics)),
            "Work after release requires a separately bounded lifetime; only a simple return is supported.");
        Support(!body.Statements.Take(index).SelectMany(s => s.DescendantNodesAndSelf())
            .OfType<LocalDeclarationStatementSyntax>().Any(s => !s.UsingKeyword.IsKind(SyntaxKind.None)),
            "An outer using declaration would dispose after release and requires a separate lifetime contract.");

        foreach (IInvocationOperation call in calls)
        {
            if (call.TargetMethod.MethodKind == MethodKind.DelegateInvoke)
                Support(false, "Delegate effects require a checked lifetime contract.");
            if (TaskType(call.Type))
                Support(DirectlyAwaited(call),
                    "An unawaited task-returning call can transfer work beyond the protected lifetime.");
        }
        foreach (SyntaxTree tree in scope.Compilation.SyntaxTrees)
        {
            SemanticModel model = scope.Compilation.GetSemanticModel(tree);
            foreach (SyntaxNode syntax in tree.GetRoot().DescendantNodes().Where(n =>
                n is IdentifierNameSyntax or MemberAccessExpressionSyntax))
            {
                if (model.GetOperation(syntax) is not IFieldReferenceOperation access ||
                    access.Syntax != syntax || !SymbolEqualityComparer.Default.Equals(access.Field, field)) continue;
                Need(access.Instance is IInstanceReferenceOperation,
                    "The selected gate is accessed through another owner instance.");
                Need(access.Parent is not IAssignmentOperation and not IIncrementOrDecrementOperation,
                    "The selected gate storage is replaced.");
                Support(access.Parent is IInvocationOperation invocation && invocation.Instance == access ||
                    access.Parent is IPropertyReferenceOperation property && property.Instance == access &&
                        property.Property.RefKind == RefKind.None &&
                        !scope.Compilation.ClassifyConversion(field.Type, property.Property.Type).Exists,
                    "Gate copies, argument/ref escapes and alias-returning getters require a separate storage contract.");
                bool inMethod = syntax.SyntaxTree == declaration.SyntaxTree && declaration.Span.Contains(syntax.Span);
                if (access.Parent is IInvocationOperation used)
                {
                    bool selectedCall = Matches(used.TargetMethod, obligation.Gate!) || Matches(used.TargetMethod, obligation.Effect!);
                    Support(!selectedCall || inMethod,
                        "Acquisition or release from another source method requires a composed lifetime contract.");
                    Support(selectedCall || !inMethod,
                        "Additional gate calls inside the protected method require an effect contract.");
                }
                else Support(!inMethod,
                    "Gate getters inside the protected method require an effect contract.");
            }
        }
        SourceControlFlow? facts = SourceControlFlows.Build(declaration, semantics, At);
        flow = facts;
        Support(facts != null, "A compiler control-flow graph is required.");
        Need(facts!.Calls.Any(c => MatchesCall(c, acquire) && facts.Blocks.Single(b => b.Id == c.Block).Reachable) &&
            facts.Calls.Any(c => MatchesCall(c, release) && facts.Blocks.Single(b => b.Id == c.Block).Reachable),
            "Acquisition and release must be reachable; an unreachable lifetime is not evidence.");
        List<Place> awaits = protectedTry.DescendantNodes().OfType<AwaitExpressionSyntax>().Select(At).ToList();
        evidence.AddRange(new[] { At(acquire.Syntax), At(release.Syntax), At(protectedTry) });
        evidence.AddRange(awaits);
        return new(At(acquire.Syntax), At(release.Syntax), At(protectedTry), awaits,
            "Successful acquisition is immediately enclosed by a C# try/finally with one matching release attempt, " +
            "including returns, implicit exceptions and faulted/canceled awaits. Lock-prefix support trusts normal " +
            "Monitor exit after successful entry. This is a source-structure premise, not proof of gate/callee semantics, " +
            "successful release, transitive detached-work completion, eventual completion, draining or C# refinement.");

        static bool MatchesCall(SourceFlowCall fact, IInvocationOperation call) =>
            fact.Location == At(call.Syntax) && fact.Target == SourceControlFlows.Id(call.TargetMethod);
    }

    private static bool TaskType(ITypeSymbol? type) => type is INamedTypeSymbol named &&
        !named.Locations.Any(l => l.IsInSource) &&
        named.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks" &&
        named.Name is "Task" or "ValueTask";

    private static bool DirectlyAwaited(IInvocationOperation call)
    {
        IOperation current = call;
        if (current.Parent is IInvocationOperation configure && configure.Instance == current &&
            configure.TargetMethod.Name == "ConfigureAwait" &&
            TaskType(configure.TargetMethod.ContainingType) &&
            configure.TargetMethod.Parameters is [{ Type.SpecialType: SpecialType.System_Boolean }])
            current = configure;
        return current.Parent is IAwaitOperation;
    }

    private static bool SimpleReturn(ExpressionSyntax expression, SemanticModel semantics) =>
        semantics.GetOperation(expression) is ILiteralOperation or ILocalReferenceOperation or IParameterReferenceOperation or
            IDefaultValueOperation;
}
