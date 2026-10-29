using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace TlaLink;

public static partial class SourceObligations
{
    private static void CheckReferenceEffect(SourceObligation obligation, IFieldSymbol field,
        SyntaxNode declaration, SemanticModel semantics, ControlFlowGraph? graph)
    {
        Need(field.Type.IsReferenceType && !field.IsReadOnly,
            "Reference CAS authorization requires writable reference storage.");
        Need(SymbolEqualityComparer.Default.Equals(semantics.GetDeclaredSymbol(declaration)?.ContainingType,
            field.ContainingType), "The authorization method must own the selected storage.");
        Support(declaration is MethodDeclarationSyntax { Body: not null } && graph != null,
            "Reference CAS authorization requires a block body and control-flow graph.");
        var body = ((MethodDeclarationSyntax)declaration).Body!;
        List<IInvocationOperation> calls = declaration.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(s => semantics.GetOperation(s)).OfType<IInvocationOperation>().ToList();
        var exchanges = calls.Where(c => ReferenceIntrinsic(c, "Interlocked", "CompareExchange")).ToList();
        var effects = calls.Where(c => Matches(c.TargetMethod, obligation.Effect!)).ToList();
        Need(exchanges.Count == 1 && effects.Count == 1,
            "Exactly one reference CompareExchange and one selected effect call are required. Resolved calls: " +
            string.Join(", ", calls.Select(c => SourceControlFlows.Id(c.TargetMethod))) + ".");
        IInvocationOperation exchange = exchanges[0], effect = effects[0];
        Need(exchange.Arguments[0].Value is IFieldReferenceOperation target &&
            target.Instance is IInstanceReferenceOperation &&
            SymbolEqualityComparer.Default.Equals(target.Field.OriginalDefinition, field.OriginalDefinition) &&
            Null(exchange.Arguments[2].Value),
            "Authorization requires CompareExchange(ref this.storage, nonnull reference, null).");
        Support(KnownNonNull(exchange.Arguments[1].Value),
            "A nonnull candidate must be a fresh construction, this, a nonnull constant, an unchanged fresh local, or the trusted BCL TaskCompletionSource<T>.Task getter on such a local; nullable annotations and arbitrary calls/properties do not establish nonnull.");
        ILocalSymbol? resultLocal = null;
        StatementSyntax? resultStatement = null;
        if (exchange.Syntax.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax variable } &&
            variable.Parent?.Parent is LocalDeclarationStatementSyntax local && local.Parent == body &&
            ((VariableDeclarationSyntax)variable.Parent).Variables.Count == 1)
        {
            resultLocal = (ILocalSymbol)semantics.GetDeclaredSymbol(variable)!;
            resultStatement = local;
        }
        else if (exchange.Parent is ISimpleAssignmentOperation { Target: ILocalReferenceOperation targetLocal } assignment &&
            assignment.Syntax.Parent is ExpressionStatementSyntax assigned && assigned.Parent == body)
        {
            resultLocal = targetLocal.Local;
            resultStatement = assigned;
            Support(resultLocal.RefKind == RefKind.None &&
                resultLocal.DeclaringSyntaxReferences.Single().GetSyntax() is VariableDeclaratorSyntax {
                    Parent: VariableDeclarationSyntax { Variables.Count: 1 } } resultVariable &&
                resultVariable.Parent!.Parent is LocalDeclarationStatementSyntax { Parent: BlockSyntax } declarationStatement &&
                declarationStatement.Parent == body,
                "A predeclared CAS result must be one top-level non-ref local.");
            var resultVariableSyntax = (VariableDeclaratorSyntax)resultLocal.DeclaringSyntaxReferences.Single().GetSyntax();
            if (resultVariableSyntax.Initializer is { } initializer)
            {
                Support(SelectedRead(semantics.GetOperation(initializer.Value)!),
                    "A predeclared CAS result initializer must be a recognized Volatile.Read of this same selected field.");
                int declarationIndex = body.Statements.IndexOf((StatementSyntax)resultVariableSyntax.Parent!.Parent!);
                Support(declarationIndex + 1 < body.Statements.IndexOf(assigned) &&
                    body.Statements[declarationIndex + 1] is IfStatementSyntax { Else: null } fast &&
                    semantics.GetOperation(fast.Condition) is IBinaryOperation {
                        OperatorKind: BinaryOperatorKind.NotEquals, OperatorMethod: null } test &&
                    FastReturn(test, fast.Statement, resultLocal),
                    "A reused Volatile.Read local requires an immediate nonnull return fast path before its CAS assignment.");
            }
        }
        Support(resultLocal != null && resultStatement != null,
            "The CAS result must initialize or directly assign one top-level local.");
        int index = body.Statements.IndexOf(resultStatement!);
        Need(index + 2 < body.Statements.Count && body.Statements[index + 1] is IfStatementSyntax,
            "The CAS result requires an immediate nonnull return guard before the effect.");
        var guard = (IfStatementSyntax)body.Statements[index + 1];
        Need(guard.Else == null &&
            semantics.GetOperation(guard.Condition) is IBinaryOperation {
                OperatorKind: BinaryOperatorKind.NotEquals, OperatorMethod: null } comparison &&
            (LocalNull(comparison.LeftOperand, comparison.RightOperand) ||
             LocalNull(comparison.RightOperand, comparison.LeftOperand)) &&
            GuardReturn(guard.Statement),
            "The CAS guard must return the old local when it is nonnull, using built-in inequality.");
        Need(body.Statements[index + 2] is ExpressionStatementSyntax statement &&
            (semantics.GetOperation(statement.Expression) is IInvocationOperation selected && selected.Syntax == effect.Syntax ||
             semantics.GetOperation(statement.Expression) is ISimpleAssignmentOperation {
                 Target: IDiscardOperation, Value: IInvocationOperation discarded } && discarded.Syntax == effect.Syntax),
            "The selected effect must directly follow the CAS nonnull return guard.");
        Support(body.Statements.Skip(index + 3).All(s => s is ReturnStatementSyntax r &&
            (r.Expression == null || SimpleValue(semantics.GetOperation(r.Expression)!))),
            "Only a simple return is supported after the selected effect.");
        Support(body.Statements.Take(index).All(s =>
            s is LocalDeclarationStatementSyntax { Declaration.Variables.Count: 1 } l &&
                (l.Declaration.Variables[0].Initializer is null &&
                    SymbolEqualityComparer.Default.Equals(semantics.GetDeclaredSymbol(l.Declaration.Variables[0]), resultLocal) ||
                 l.Declaration.Variables[0].Initializer is { } initializer &&
                (KnownNonNull(semantics.GetOperation(initializer.Value)!) ||
                 SelectedRead(semantics.GetOperation(initializer.Value)!))) ||
            s is IfStatementSyntax { Else: null } fast &&
                semantics.GetOperation(fast.Condition) is IBinaryOperation {
                    OperatorKind: BinaryOperatorKind.NotEquals, OperatorMethod: null } test &&
                FastReturn(test, fast.Statement) ||
            s is IfStatementSyntax inputGuard && InputNullThrow(inputGuard)),
            "Only fresh candidate locals, parameter-null throws and a Volatile.Read local/non-null early return fast path are supported before CAS.");
        Support(calls.All(c => c == exchange || c == effect || ReferenceIntrinsic(c, "Volatile", "Read")),
            "Additional calls in reference CAS authorization require an effect summary.");
        foreach (IdentifierNameSyntax name in declaration.DescendantNodes().OfType<IdentifierNameSyntax>())
            if (semantics.GetOperation(name) is ILocalReferenceOperation reference)
                Support((reference.Parent is not IAssignmentOperation and not IIncrementOrDecrementOperation ||
                    reference.Parent is ISimpleAssignmentOperation assignment && assignment.Target == reference &&
                    assignment.Value == exchange && SymbolEqualityComparer.Default.Equals(reference.Local, resultLocal)) &&
                    reference.Parent is not IArgumentOperation { Parameter.RefKind: not RefKind.None },
                    "Local writes and ref aliases in CAS authorization are unsupported.");
        foreach (IOperation operation in SourceControlFlows.Operations(semantics.GetOperation(declaration)!))
            if (operation is IFieldReferenceOperation access)
                Support(CompletionOptions(access) ||
                    access.Parent is IArgumentOperation { Parent: IInvocationOperation call } argument &&
                    argument.Parameter?.Ordinal == 0 &&
                    (call.Syntax == exchange.Syntax || ReferenceIntrinsic(call, "Volatile", "Read")) &&
                    access.Instance is IInstanceReferenceOperation &&
                    SymbolEqualityComparer.Default.Equals(access.Field.OriginalDefinition, field.OriginalDefinition),
                    "Field aliases, writes and additional field accesses in CAS authorization are unsupported.");
        List<BasicBlock> decisions = graph!.Blocks.Where(b =>
            b.BranchValue?.Syntax == guard.Condition).ToList();
        Support(decisions.Count == 1, "The CAS guard requires a direct CFG branch.");
        BasicBlock decision = decisions[0];
        var effectBlocks = graph.Blocks.Where(b => b.Operations.Any(op =>
            SourceControlFlows.Operations(op).Any(n => n is IInvocationOperation c && c.Syntax == effect.Syntax))).ToList();
        Support(effectBlocks.Count == 1, "The effect requires one direct CFG block.");
        int effectBlock = effectBlocks[0].Ordinal;
        BasicBlock? authorized = decision.ConditionKind == ControlFlowConditionKind.WhenTrue
            ? decision.FallThroughSuccessor?.Destination : decision.ConditionalSuccessor?.Destination;
        Need(decision.IsReachable && authorized != null && Reaches(graph, authorized.Ordinal, effectBlock) &&
            !Reaches(graph, 0, effectBlock, (decision.Ordinal, authorized.Ordinal)),
            "An effect path bypasses the null CAS authorization outcome.");

        bool LocalNull(IOperation left, IOperation right) =>
            ReferenceOperand(left) is ILocalReferenceOperation l &&
            SymbolEqualityComparer.Default.Equals(l.Local, resultLocal) && Null(right);
        bool GuardReturn(StatementSyntax s) =>
            Return(s) is { } r && ReferenceOperand(r) is ILocalReferenceOperation l &&
            SymbolEqualityComparer.Default.Equals(l.Local, resultLocal);
        IOperation? Return(StatementSyntax s) => s switch {
            ReturnStatementSyntax { Expression: { } e } => semantics.GetOperation(e),
            BlockSyntax { Statements.Count: 1 } b => Return(b.Statements[0]),
            _ => null
        };
        bool SelectedRead(IOperation value) =>
            value is IInvocationOperation read && ReferenceIntrinsic(read, "Volatile", "Read") &&
            read.Arguments[0].Value is IFieldReferenceOperation f &&
            f.Instance is IInstanceReferenceOperation &&
            SymbolEqualityComparer.Default.Equals(f.Field.OriginalDefinition, field.OriginalDefinition);
        bool InputNullThrow(IfStatementSyntax syntax)
        {
            if (syntax.Else != null || semantics.GetOperation(syntax.Condition) is not IBinaryOperation {
                OperatorKind: BinaryOperatorKind.Equals, OperatorMethod: null } condition) return false;
            IOperation? value = Null(condition.RightOperand) ? ReferenceOperand(condition.LeftOperand) :
                Null(condition.LeftOperand) ? ReferenceOperand(condition.RightOperand) : null;
            if (value is not IParameterReferenceOperation { Parameter.Type.IsReferenceType: true }) return false;
            StatementSyntax statement = syntax.Statement is BlockSyntax { Statements.Count: 1 } block
                ? block.Statements[0] : syntax.Statement;
            return statement is ThrowStatementSyntax { Expression: { } expression } &&
                semantics.GetOperation(expression) is IObjectCreationOperation { Initializer: null, Type: { } type } creation &&
                type.ToDisplayString() == "System.ArgumentNullException" &&
                SymbolEqualityComparer.Default.Equals(type.ContainingAssembly,
                    semantics.Compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly) &&
                creation.Arguments.All(argument => argument.Value.ConstantValue.HasValue);
        }
        bool FastReturn(IBinaryOperation test, StatementSyntax s, ILocalSymbol? expected = null)
        {
            var value = Null(test.RightOperand) ? ReferenceOperand(test.LeftOperand) :
                Null(test.LeftOperand) ? ReferenceOperand(test.RightOperand) : null;
            return value is ILocalReferenceOperation l && Return(s) is { } r &&
                (expected == null || SymbolEqualityComparer.Default.Equals(l.Local, expected)) &&
                ReferenceOperand(r) is ILocalReferenceOperation returned &&
                SymbolEqualityComparer.Default.Equals(l.Local, returned.Local) &&
                l.Local.DeclaringSyntaxReferences.Single().GetSyntax() is VariableDeclaratorSyntax {
                    Initializer.Value: { } initializer } &&
                SelectedRead(semantics.GetOperation(initializer)!);
        }
        bool KnownNonNull(IOperation value)
        {
            value = ReferenceOperand(value);
            if (value is IObjectCreationOperation { Initializer: null } or IInstanceReferenceOperation ||
                value.ConstantValue is { HasValue: true, Value: not null }) return value.Type?.IsReferenceType == true;
            return FreshLocal(value) != null || CompletionTask(value);
        }
        IObjectCreationOperation? FreshLocal(IOperation value) =>
            value is ILocalReferenceOperation { Local.RefKind: RefKind.None } l &&
                l.Local.DeclaringSyntaxReferences.Single().GetSyntax() is VariableDeclaratorSyntax {
                    Initializer.Value: { } initializer } &&
                ReferenceOperand(semantics.GetOperation(initializer)!) is IObjectCreationOperation { Initializer: null } fresh
                    ? fresh : null;
        INamedTypeSymbol? BclType(string metadataName)
        {
            IAssemblySymbol core = semantics.Compilation.GetSpecialType(SpecialType.System_Object).ContainingAssembly;
            return core.GetTypeByMetadataName(metadataName) ?? core.ResolveForwardedType(metadataName);
        }
        bool CompletionSource(IObjectCreationOperation creation) =>
            BclType("System.Threading.Tasks.TaskCompletionSource`1") is { } source &&
            !source.Locations.Any(l => l.IsInSource) &&
            SymbolEqualityComparer.Default.Equals(creation.Type?.OriginalDefinition, source);
        bool CompletionTask(IOperation value) =>
            value is IPropertyReferenceOperation { Instance: { } receiver, Arguments.Length: 0 } property &&
            FreshLocal(ReferenceOperand(receiver)) is { } fresh && CompletionSource(fresh) &&
            property.Property is { Name: "Task", IsStatic: false, IsIndexer: false, RefKind: RefKind.None,
                SetMethod: null, GetMethod: { IsVirtual: false, IsAbstract: false } getter } &&
            !getter.Locations.Any(l => l.IsInSource) &&
            SymbolEqualityComparer.Default.Equals(property.Property.ContainingType.OriginalDefinition,
                fresh.Type!.OriginalDefinition) &&
            SymbolEqualityComparer.Default.Equals(property.Type?.OriginalDefinition,
                BclType("System.Threading.Tasks.Task`1"));
        bool CompletionOptions(IFieldReferenceOperation access) =>
            access.Field.IsConst && !access.Field.Locations.Any(l => l.IsInSource) &&
            SymbolEqualityComparer.Default.Equals(access.Field.ContainingType,
                BclType("System.Threading.Tasks.TaskCreationOptions")) &&
            access.Parent is IArgumentOperation { Parent: IObjectCreationOperation creation } &&
            CompletionSource(creation);
        bool SimpleValue(IOperation value) =>
            ReferenceOperand(value) is ILocalReferenceOperation or IParameterReferenceOperation or IInstanceReferenceOperation ||
            value.ConstantValue.HasValue || CompletionTask(ReferenceOperand(value));
    }

    private static void CheckCallbackEffect(CSharpScope scope, SourceObligation obligation, IFieldSymbol field,
        SyntaxNode declaration, SemanticModel semantics, List<Place> evidence)
    {
        Need(field.Type.IsReferenceType && field.IsReadOnly && field.ContainingType.IsReferenceType,
            "Callback wiring requires a readonly reference helper in a reference-type owner.");
        Need(SymbolEqualityComparer.Default.Equals(semantics.GetDeclaredSymbol(declaration)?.ContainingType,
            field.ContainingType), "The delegating method must own the selected helper.");
        ExpressionSyntax? expression = declaration is MethodDeclarationSyntax {
            Body.Statements: [ExpressionStatementSyntax s] } ? s.Expression : ReturnExpression(declaration);
        Support(expression != null && semantics.GetOperation(expression) is IInvocationOperation,
            "Callback wiring requires a single direct delegation call.");
        var gate = (IInvocationOperation)semantics.GetOperation(expression!)!;
        Need(Matches(gate.TargetMethod, obligation.Gate!) && !gate.TargetMethod.IsStatic &&
            !gate.TargetMethod.IsAsync && gate.TargetMethod.Locations.Any(l => l.IsInSource) &&
            gate.Instance is IFieldReferenceOperation helper && helper.Instance is IInstanceReferenceOperation &&
            SymbolEqualityComparer.Default.Equals(helper.Field.OriginalDefinition, field.OriginalDefinition) &&
            gate.Arguments.Length == 1 && gate.Arguments[0].Parameter?.RefKind == RefKind.None,
            "Expected one selected synchronous helper gate call on this.storage with one callback argument.");
        IOperation argument = ReferenceOperand(gate.Arguments[0].Value);
        if (argument is IDelegateCreationOperation creation) argument = creation.Target;
        Support(argument is IMethodReferenceOperation, "The callback must be a direct instance method group.");
        var reference = (IMethodReferenceOperation)argument;
        IMethodSymbol callback = reference.Method;
        Need(reference.Instance is IInstanceReferenceOperation &&
            SymbolEqualityComparer.Default.Equals(callback.ContainingType, field.ContainingType) &&
            callback.DeclaredAccessibility == Accessibility.Private && !callback.IsStatic && !callback.IsAsync &&
            callback.MethodKind == MethodKind.Ordinary && callback.DeclaringSyntaxReferences.Length == 1,
            "The callback must be a private synchronous instance source method on this owner.");
        SyntaxNode callbackDeclaration = callback.DeclaringSyntaxReferences.Single().GetSyntax();
        SemanticModel callbackSemantics = scope.Compilation.GetSemanticModel(callbackDeclaration.SyntaxTree);
        Support(callbackDeclaration is MethodDeclarationSyntax { Body: not null } method &&
            !method.DescendantNodes().Any(n => n is LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax),
            "The callback requires a direct block or one top-level try/catch.");
        var callbackBody = ((MethodDeclarationSyntax)callbackDeclaration).Body!;
        ExpressionStatementSyntax? first = callbackBody.Statements.FirstOrDefault() is ExpressionStatementSyntax direct &&
            callbackBody.Statements.Skip(1).All(s => s is ReturnStatementSyntax) ? direct :
            callbackBody.Statements.FirstOrDefault() is TryStatementSyntax { Finally: null, Catches.Count: > 0 } attempt &&
                callbackBody.Statements.Skip(1).All(s => s is ReturnStatementSyntax) &&
                attempt.Block.Statements.FirstOrDefault() is ExpressionStatementSyntax effectStatement &&
                attempt.Block.Statements.Skip(1).All(s => s is ReturnStatementSyntax) &&
                attempt.Catches.All(c => c.Filter == null && c.Block.Statements.All(s => s is
                    ExpressionStatementSyntax or ReturnStatementSyntax or ThrowStatementSyntax)) ? effectStatement : null;
        Support(first != null, "The callback supports a first direct effect followed only by returns, in a direct block or one top-level try/catch.");
        Need(callbackSemantics.GetOperation(first!.Expression) is IInvocationOperation effect &&
            Matches(effect.TargetMethod, obligation.Effect!), "The callback must directly invoke the selected effect.");
        int references = 0, effects = 0;
        foreach (SyntaxTree tree in scope.Compilation.SyntaxTrees)
        {
            SemanticModel model = scope.Compilation.GetSemanticModel(tree);
            foreach (SyntaxNode syntax in tree.GetRoot().DescendantNodes())
            {
                if (syntax is SimpleNameSyntax name)
                {
                    SymbolInfo info = model.GetSymbolInfo(name);
                    if (new[] { info.Symbol }.Concat(info.CandidateSymbols).OfType<IMethodSymbol>().Any(named =>
                        SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, callback.OriginalDefinition)))
                        Need(reference.Syntax.SyntaxTree == syntax.SyntaxTree && reference.Syntax.Span.Contains(syntax.Span),
                            "An additional callback reference is unsupported.");
                }
                IOperation? operation = syntax is InvocationExpressionSyntax or IdentifierNameSyntax or MemberAccessExpressionSyntax
                    ? model.GetOperation(syntax) : null;
                if (operation?.Syntax != syntax) continue;
                Support(operation is not IDynamicInvocationOperation and not IFunctionPointerInvocationOperation ||
                    !InOwner(model.GetEnclosingSymbol(syntax.SpanStart)?.ContainingType),
                    "Dynamic and function-pointer calls in the owner require an independent callback reference summary.");
                if (operation is IMethodReferenceOperation methodReference &&
                    SymbolEqualityComparer.Default.Equals(methodReference.Method.OriginalDefinition, callback.OriginalDefinition))
                {
                    references++;
                    Need(syntax.SyntaxTree == reference.Syntax.SyntaxTree && syntax.Span == reference.Syntax.Span,
                        "A callback reference escapes the selected method-group argument.");
                }
                if (operation is IInvocationOperation call)
                {
                    Need(!SymbolEqualityComparer.Default.Equals(call.TargetMethod.OriginalDefinition, callback.OriginalDefinition),
                        "A direct callback invocation bypasses the selected helper.");
                    if (Matches(call.TargetMethod, obligation.Effect!) &&
                        InOwner(model.GetEnclosingSymbol(syntax.SpanStart)?.ContainingType))
                    {
                        effects++;
                        Need(syntax.SyntaxTree == first.SyntaxTree && first.Span.Contains(syntax.Span),
                            "Another direct selected effect invocation exists in the owner.");
                    }
                }
            }
        }
        Need(references == 1 && effects == 1, "Exactly one callback reference and one owner effect invocation are required.");
        evidence.Add(At(callbackDeclaration));

        bool InOwner(INamedTypeSymbol? type)
        {
            for (; type != null; type = type.ContainingType)
                if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, field.ContainingType.OriginalDefinition))
                    return true;
            return false;
        }
    }
}
