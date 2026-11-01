using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using System.Text.Json.Serialization;

namespace TlaLink;

public sealed record SourceObligation(string Id, string Binding, string Kind, string? Gate = null,
    string? Effect = null, string? Storage = null, bool? When = null, List<string>? AllowedCalls = null,
    string? Parameter = null, List<string>? AllowedProperties = null);
public sealed record SourceObligationResult(SourceObligation Obligation, string Status, string Reason,
    List<Place> Evidence, SourceControlFlow? Flow = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AsyncGateLifetime? AsyncGate { get; init; }
}
public sealed record AsyncGateLifetime(Place Acquisition, Place Release, Place ProtectedRegion,
    List<Place> Awaits, string Boundary);

public static partial class SourceObligations
{
    public static string Status(IEnumerable<SourceObligationResult> results)
    {
        List<string> states = results.Select(r => r.Status).ToList();
        return states.Count == 0 ? "not-specified" : states.Contains("incomplete") ? "incomplete" :
            states.Contains("rejected") ? "rejected" : "passed";
    }

    public static void Validate(List<SourceObligation> obligations, IEnumerable<string> bindings)
    {
        if (obligations == null || obligations.Count > 1000)
            throw new InputException(ErrorCode.InvalidSourceObligation, "At most 1000 source obligations are supported.");
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (SourceObligation o in obligations)
        {
            bool Method(string? value) => value != null && value.StartsWith("M:", StringComparison.Ordinal);
            bool Field(string? value) => value != null && value.StartsWith("F:", StringComparison.Ordinal);
            bool Properties() => o.AllowedProperties == null || o.AllowedProperties is { Count: > 0 and <= 100 } &&
                o.AllowedProperties.All(p => p != null && p.StartsWith("P:", StringComparison.Ordinal)) &&
                o.AllowedProperties.Distinct(StringComparer.Ordinal).Count() == o.AllowedProperties.Count;
            if (o == null || !Schema.IsStableId(o.Id) || !ids.Add(o.Id) || !bindings.Contains(o.Binding, StringComparer.Ordinal) ||
                !Field(o.Storage) || o.Kind switch {
                    "guarded-effect" or "guarded-try-effect" => !Method(o.Gate) || !Method(o.Effect) ||
                        o.When == null || o.AllowedCalls != null || o.AllowedProperties != null || o.Parameter != null,
                    "atomic-exchange-gate" or "monotonic-int32" or "reference-cell" or "reference-read" => o.Gate != null || o.Effect != null ||
                        o.When != null || o.AllowedCalls != null || o.AllowedProperties != null || o.Parameter != null,
                    "reference-cas-gate" => o.Gate != null || o.Effect != null || o.When != null ||
                        o.AllowedCalls != null || o.AllowedProperties != null ||
                        string.IsNullOrWhiteSpace(o.Parameter) || !SyntaxFacts.IsValidIdentifier(o.Parameter),
                    "reference-cas-effect" => o.Gate != null || !Method(o.Effect) || o.When != null ||
                        o.AllowedCalls != null || o.AllowedProperties != null || o.Parameter != null,
                    "callback-effect" => !Method(o.Gate) || !Method(o.Effect) || o.When != null ||
                        o.AllowedCalls != null || o.AllowedProperties != null || o.Parameter != null,
                    "balanced-async-gate" => !Method(o.Gate) || !Method(o.Effect) || o.Gate == o.Effect ||
                        o.When != null || o.AllowedCalls != null || o.AllowedProperties != null || o.Parameter != null,
                    "stable-storage" => o.Gate != null || o.Effect != null || o.When != null ||
                        o.Parameter != null || !Properties() ||
                        o.AllowedCalls is not { Count: > 0 and <= 100 } ||
                        o.AllowedCalls.Any(c => !Method(c)) ||
                        o.AllowedCalls.Distinct(StringComparer.Ordinal).Count() != o.AllowedCalls.Count,
                    _ => true
                })
                throw new InputException(ErrorCode.InvalidSourceObligation,
                    "Source obligations require unique IDs, a declared binding, documentation IDs and the exact fields for their supported kind.");
        }
    }

    public static List<SourceObligationResult> Check(CSharpScope scope, IntegratedManifest manifest)
    {
        Validate(manifest.SourceObligations, manifest.Bindings.Select(b => b.Id));
        scope.RequireCompilable();
        var symbols = IntegratedReview.SourceSymbols(scope);
        return manifest.SourceObligations.Select(o =>
        {
            List<Place> evidence = [];
            SourceControlFlow? flow = null;
            AsyncGateLifetime? asyncGate = null;
            try
            {
                var endpoint = IntegratedReview.Resolve(symbols, manifest.Bindings.Single(b => b.Id == o.Binding).Source);
                var storage = IntegratedReview.Resolve(symbols, o.Storage!);
                evidence.Add(endpoint.Endpoint.Location);
                evidence.Add(storage.Endpoint.Location);
                Support(storage.Symbol is IFieldSymbol, "Storage must resolve to a source field.");
                IFieldSymbol field = (IFieldSymbol)storage.Symbol;
                Support(!field.IsStatic && field.DeclaredAccessibility == Accessibility.Private,
                    "Only private instance storage is supported.");
                for (INamedTypeSymbol? type = field.ContainingType; type != null; type = type.ContainingType)
                {
                    Support(!type.DeclaringSyntaxReferences.Any(r =>
                        r.GetSyntax() is TypeDeclarationSyntax t && t.Modifiers.Any(SyntaxKind.PartialKeyword)),
                        "Partial storage containers require a separately established source closure.");
                    Support(!type.GetAttributes().Any(a =>
                        a.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.InlineArrayAttribute" ||
                        a.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.StructLayoutAttribute" &&
                        a.ConstructorArguments is [{ Value: 2 }]),
                        "Overlapping or inline-array storage layouts are unsupported.");
                }
                Support(!field.GetAttributes().Any(a =>
                    a.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.FieldOffsetAttribute"),
                    "Explicit field offsets require a storage-alias argument.");
                if (o.Kind == "balanced-async-gate")
                {
                    asyncGate = CheckAsyncGate(scope, o, endpoint.Symbol, field, evidence, out flow);
                }
                else if (o.Kind is "stable-storage" or "monotonic-int32" or "reference-cell")
                    CheckStorage(scope, o, field, evidence, symbols);
                else if (o.Kind == "reference-read")
                {
                    Support(endpoint.Symbol is IPropertySymbol { IsStatic: false, IsIndexer: false, RefKind: RefKind.None } or
                        IMethodSymbol { IsStatic: false, IsAsync: false },
                        "A reference read must select an instance getter or synchronous method.");
                    SyntaxNode declaration = endpoint.Symbol.DeclaringSyntaxReferences.Single().GetSyntax();
                    SemanticModel semantics = scope.Compilation.GetSemanticModel(declaration.SyntaxTree);
                    ExpressionSyntax? expression = ReturnExpression(declaration);
                    Support(expression != null, "A reference read must consist of one return expression.");
                    Need(semantics.GetOperation(expression!) is IInvocationOperation read &&
                        ReferenceIntrinsic(read, "Volatile", "Read") &&
                        read.Arguments[0].Value is IFieldReferenceOperation target &&
                        SymbolEqualityComparer.Default.Equals(target.Field.OriginalDefinition, field.OriginalDefinition) &&
                        target.Instance is IInstanceReferenceOperation,
                        "Expected a direct Volatile.Read of this instance's selected reference field.");
                }
                else
                {
                    Support(endpoint.Symbol is IMethodSymbol { IsAsync: false, IsStatic: false } method &&
                        method.DeclaringSyntaxReferences.Length == 1, "Select a synchronous instance source method.");
                    SyntaxNode declaration = endpoint.Symbol.DeclaringSyntaxReferences.Single().GetSyntax();
                    SemanticModel semantics = scope.Compilation.GetSemanticModel(declaration.SyntaxTree);
                    Support(!declaration.DescendantNodes().Any(n => n is
                        UsingStatementSyntax or AwaitExpressionSyntax or LockStatementSyntax or
                        ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax or
                        WhileStatementSyntax or DoStatementSyntax or GotoStatementSyntax or YieldStatementSyntax or
                        LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or UnsafeStatementSyntax or FixedStatementSyntax ||
                        n is MethodDeclarationSyntax m && m.Modifiers.Any(SyntaxKind.UnsafeKeyword) ||
                        n is TryStatementSyntax && o.Kind != "guarded-try-effect" ||
                        n is LocalDeclarationStatementSyntax local && !local.UsingKeyword.IsKind(SyntaxKind.None)),
                        "Exception regions, disposal, locks, loops, jumps, suspension and nested functions are unsupported.");
                    Support(!SourceControlFlows.Operations(semantics.GetOperation(declaration)!).Any(op =>
                        op is IDynamicInvocationOperation or IFunctionPointerInvocationOperation),
                        "Dynamic and function-pointer calls are unsupported.");
                    flow = SourceControlFlows.Build(declaration, semantics, At);
                    if (o.Kind == "guarded-try-effect") CheckTryEffect(o, declaration, semantics);
                    if (o.Kind == "reference-cas-effect")
                        CheckReferenceEffect(o, field, declaration, semantics, SourceControlFlows.Graph(declaration, semantics));
                    else if (o.Kind == "callback-effect")
                        CheckCallbackEffect(scope, o, field, declaration, semantics, evidence);
                    else if (o.Kind is "guarded-effect" or "guarded-try-effect")
                        CheckGuard(o, field, SourceControlFlows.Graph(declaration, semantics), flow);
                    else if (o.Kind == "reference-cas-gate") CheckReferenceGate(o, field, declaration, semantics);
                    else CheckAtomic(field, declaration, semantics);
                }
                return new SourceObligationResult(o, "passed",
                    "Selected structural premise holds in the explicit compiler/source scope; language/runtime semantics and source/model correspondence remain review obligations.",
                    evidence, flow) { AsyncGate = asyncGate };
            }
            catch (CheckFailure error) { return new(o, error.Status, error.Message, evidence, flow); }
            catch (InputException error) { return new(o, "incomplete", error.Message, evidence, flow); }
        }).ToList();
    }

    private static void CheckGuard(SourceObligation o, IFieldSymbol field, ControlFlowGraph? graph, SourceControlFlow? flow)
    {
        Support(graph != null && flow != null, "A method control-flow graph is required.");
        Need(field.ContainingType.IsReferenceType, "Gate storage must belong to a reference-type owner.");
        Need(!field.Type.IsValueType || !field.IsReadOnly, "A readonly value-type gate receiver can be defensively copied.");
        var gates = flow!.Calls.Where(c => c.Target == o.Gate || c.Definition == o.Gate).ToList();
        var effects = flow.Calls.Where(c => c.Target == o.Effect || c.Definition == o.Effect).ToList();
        Need(gates.Count == 1 && effects.Count == 1, "Exactly one gate call and one effect call site are required.");
        SourceFlowCall gate = gates[0], effect = effects[0];
        Need(gate.Receiver == o.Storage, "The gate must operate on the selected storage field.");
        BasicBlock decision = graph!.Blocks[gate.Block];
        Need(decision.IsReachable && graph.Blocks[effect.Block].IsReachable, "Gate and effect must be reachable.");
        Need(gate.Block != effect.Block, "Gate and effect share a block without an intervening conditional outcome.");
        Need(decision.ConditionKind != ControlFlowConditionKind.None, "The selected gate call does not decide a branch.");
        IOperation? condition = decision.BranchValue;
        bool negated = false;
        while (condition is IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } unary)
        {
            negated = !negated;
            condition = unary.Operand;
        }
        Support(condition is IInvocationOperation call && Matches(call.TargetMethod, o.Gate!),
            "The branch must directly test the selected Boolean gate call.");
        Need(((IInvocationOperation)condition!).Instance is IFieldReferenceOperation {
            Instance: IInstanceReferenceOperation }, "The gate must use this instance's storage, not another instance or alias.");
        bool conditionalValue = decision.ConditionKind == ControlFlowConditionKind.WhenTrue;
        BasicBlock? authorized = (o.When!.Value != negated) == conditionalValue
            ? decision.ConditionalSuccessor?.Destination : decision.FallThroughSuccessor?.Destination;
        Need(authorized != null && Reaches(graph, authorized.Ordinal, effect.Block),
            "The declared gate outcome must reach the effect.");
        Need(!Reaches(graph, 0, effect.Block, (decision.Ordinal, authorized!.Ordinal)),
            "An effect path bypasses the declared gate outcome.");
        Need(!Successors(graph.Blocks[effect.Block]).Any(b => Reaches(graph, b, effect.Block)),
            "The effect block can repeat within one invocation.");
    }

    private static void CheckTryEffect(SourceObligation obligation, SyntaxNode declaration, SemanticModel semantics)
    {
        List<TryStatementSyntax> tries = declaration.DescendantNodes().OfType<TryStatementSyntax>().ToList();
        Support(declaration is MethodDeclarationSyntax { Body: { } } method &&
            tries.Count == 1 && tries[0].Parent == method.Body,
            "A guarded try effect requires one top-level try statement.");
        Support(tries[0].Block.Statements.FirstOrDefault() is ExpressionStatementSyntax statement &&
            semantics.GetOperation(statement.Expression) is IInvocationOperation effect &&
            Matches(effect.TargetMethod, obligation.Effect!),
            "The selected effect must be the first direct call in the try body.");
        List<InvocationExpressionSyntax> gates = declaration.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(s => semantics.GetOperation(s) is IInvocationOperation call &&
                Matches(call.TargetMethod, obligation.Gate!)).ToList();
        Support(gates.Count == 1 && !gates[0].Ancestors().Contains(tries[0]),
            "The gate must be in the method prefix, outside the selected try region.");
        Need(gates[0].SpanStart < tries[0].SpanStart, "The selected effect precedes its gate.");
    }

    private static void CheckAtomic(IFieldSymbol field, SyntaxNode declaration, SemanticModel semantics)
    {
        Need(field.Type.SpecialType == SpecialType.System_Int32 && !field.IsReadOnly,
            "The exchange gate requires writable Int32 storage.");
        ExpressionSyntax? expression = ReturnExpression(declaration);
        Support(expression != null, "An exchange gate must consist of one return expression.");
        IOperation? operation = semantics.GetOperation(expression!);
        Need(operation is IBinaryOperation { OperatorKind: BinaryOperatorKind.Equals },
            "An exchange gate must compare the old value to zero with equality.");
        IBinaryOperation binary = (IBinaryOperation)operation!;
        IInvocationOperation? call = binary.LeftOperand as IInvocationOperation;
        IOperation zero = binary.RightOperand;
        if (call == null) { call = binary.RightOperand as IInvocationOperation; zero = binary.LeftOperand; }
        Need(call != null && Intrinsic(call, "Interlocked", "Exchange") && Constant(zero, 0) &&
            call.Arguments.Length == 2 && Constant(call.Arguments[1].Value, 1) &&
            call.Arguments[0].Value is IFieldReferenceOperation target &&
            SymbolEqualityComparer.Default.Equals(target.Field, field) && target.Instance is IInstanceReferenceOperation,
            "Expected Interlocked.Exchange(ref this.storage, 1) == 0 on the selected field.");
    }

    private static ExpressionSyntax? ReturnExpression(SyntaxNode declaration)
    {
        static ExpressionSyntax? Body(BlockSyntax? body) =>
            body?.Statements is [ReturnStatementSyntax statement] ? statement.Expression : null;
        return declaration switch {
            MethodDeclarationSyntax method => method.ExpressionBody?.Expression ?? Body(method.Body),
            PropertyDeclarationSyntax property => property.ExpressionBody?.Expression ??
                property.AccessorList?.Accessors.Where(a => a.IsKind(SyntaxKind.GetAccessorDeclaration))
                    .Select(a => a.ExpressionBody?.Expression ?? Body(a.Body)).SingleOrDefault(),
            _ => null
        };
    }

    private static void CheckReferenceGate(SourceObligation obligation, IFieldSymbol field,
        SyntaxNode declaration, SemanticModel semantics)
    {
        Need(field.Type.IsReferenceType && !field.IsReadOnly, "A reference CAS gate requires writable reference storage.");
        ExpressionSyntax? expression = ReturnExpression(declaration);
        Support(expression != null, "A reference CAS gate must consist of one return expression.");
        Need(semantics.GetOperation(expression!) is IBinaryOperation {
            OperatorKind: BinaryOperatorKind.Equals, OperatorMethod: null } binary &&
            (ReferenceGate(binary.LeftOperand, binary.RightOperand) || ReferenceGate(binary.RightOperand, binary.LeftOperand)),
            "Expected reference CompareExchange(ref this.storage, parameter, null) == null, without overloaded equality.");
        bool ReferenceGate(IOperation value, IOperation empty)
        {
            if (ReferenceOperand(value) is not IInvocationOperation call || !ReferenceIntrinsic(call, "Interlocked", "CompareExchange") ||
                !Null(empty) || !Null(call.Arguments[2].Value)) return false;
            return call.Arguments[0].Value is IFieldReferenceOperation target &&
                SymbolEqualityComparer.Default.Equals(target.Field.OriginalDefinition, field.OriginalDefinition) &&
                target.Instance is IInstanceReferenceOperation &&
                ReferenceOperand(call.Arguments[1].Value) is IParameterReferenceOperation parameter &&
                parameter.Parameter.Name == obligation.Parameter;
        }
    }

    private static void CheckStorage(CSharpScope scope, SourceObligation o, IFieldSymbol field, List<Place> evidence,
        List<(ISymbol Symbol, Endpoint Endpoint)> symbols)
    {
        foreach (SyntaxReference reference in field.DeclaringSyntaxReferences)
            if (reference.GetSyntax() is VariableDeclaratorSyntax { Initializer: { } initializer })
            {
                IOperation value = scope.Compilation.GetSemanticModel(initializer.SyntaxTree).GetOperation(initializer.Value)!;
                if (o.Kind == "stable-storage")
                    Support(field.Type.IsReferenceType && value is IObjectCreationOperation { Initializer: null },
                        "Stable reference storage requires a direct fresh constructor initializer; value-type initializers and factories are unsupported.");
                else Need(o.Kind == "reference-cell" ? Null(value) : Constant(value, 0), "Storage must start at its empty value.");
            }
        if (o.Kind == "monotonic-int32")
            Need(field.Type.SpecialType == SpecialType.System_Int32 && !field.IsReadOnly,
                "Monotonic storage must be a writable Int32 field.");
        else if (o.Kind == "reference-cell")
            Need(field.Type.IsReferenceType && !field.IsReadOnly, "Reference-cell storage must be a writable reference field.");
        else
        {
            Need(field.ContainingType.IsReferenceType &&
                (field.Type.IsValueType && !field.IsReadOnly || field.Type.IsReferenceType && field.IsReadOnly),
                "Stable gate storage must be a writable value-type or readonly reference-type field in a reference-type owner.");
            if (field.Type.IsReferenceType)
                Support(field.DeclaringSyntaxReferences.Single().GetSyntax() is VariableDeclaratorSyntax { Initializer: not null },
                    "Stable reference storage requires a direct fresh constructor initializer, not constructor replacement.");
            foreach (string target in o.AllowedCalls!)
                IntegratedReview.Resolve(symbols, target);
            foreach (string target in o.AllowedProperties ?? [])
                Support(IntegratedReview.Resolve(symbols, target).Symbol is IPropertySymbol {
                    IsStatic: false, IsIndexer: false, RefKind: RefKind.None, SetMethod: null },
                    "Allowed properties must be source instance getters without setters or ref returns.");
        }
        int count = 0;
        foreach (SyntaxTree tree in scope.Compilation.SyntaxTrees)
        {
            SemanticModel semantics = scope.Compilation.GetSemanticModel(tree);
            if (o.Kind is "monotonic-int32" or "reference-cell" && field.ContainingType.IsValueType)
            {
                foreach (SyntaxNode syntax in tree.GetRoot().DescendantNodes().Where(n =>
                    n is ThisExpressionSyntax or AssignmentExpressionSyntax or InvocationExpressionSyntax))
                {
                    IOperation? operation = semantics.GetOperation(syntax);
                    bool Container(IOperation? value) => value is IInstanceReferenceOperation &&
                        SymbolEqualityComparer.Default.Equals(value.Type, field.ContainingType);
                    Need(operation is not IAssignmentOperation assignment || !Container(assignment.Target),
                        "The containing value-type instance is replaced, resetting its storage.");
                    Support(operation is not IInvocationOperation invocation || !Container(invocation.Instance),
                        "Calls on the containing value-type instance require an effect summary.");
                    Support(!Container(operation) || operation!.Parent is IFieldReferenceOperation,
                        "The containing value-type instance escapes or is copied.");
                }
            }
            foreach (SyntaxNode syntax in tree.GetRoot().DescendantNodes().Where(n =>
                n is IdentifierNameSyntax or MemberAccessExpressionSyntax))
            {
                if (semantics.GetOperation(syntax) is not IFieldReferenceOperation access ||
                    access.Syntax != syntax ||
                    !SymbolEqualityComparer.Default.Equals(access.Field.OriginalDefinition, field.OriginalDefinition)) continue;
                count++;
                evidence.Add(At(syntax));
                Need(access.Instance is IInstanceReferenceOperation, "Selected storage is accessed through another instance or alias.");
                if (o.Kind == "stable-storage")
                {
                    Need(access.Parent is not IAssignmentOperation and not IIncrementOrDecrementOperation,
                        "Stable storage is assigned or replaced.");
                    if (access.Parent is IInvocationOperation invocation && invocation.Instance == access)
                        Need(o.AllowedCalls!.Any(id => Matches(invocation.TargetMethod, id)),
                            "Storage has a call outside the authored allowed-call set.");
                    else if (access.Parent is IPropertyReferenceOperation property && property.Instance == access)
                        Need((o.AllowedProperties ?? []).Any(id => Matches(property.Property, id)),
                            "Storage has a property access outside the authored getter set.");
                    else Support(false, "Storage copies, ref escapes and non-call accesses are unsupported.");
                }
                else
                {
                    Need(access.Parent is not IAssignmentOperation and not IIncrementOrDecrementOperation,
                        "Monotonic storage has a direct write outside the supported atomic operations.");
                    Support(access.Parent is IArgumentOperation { Parent: IInvocationOperation },
                        "Storage aliases, copies and unrecognized accesses are unsupported.");
                    IArgumentOperation argument = (IArgumentOperation)access.Parent!;
                    IInvocationOperation call = (IInvocationOperation)argument.Parent!;
                    if (o.Kind == "reference-cell")
                    {
                        Support(argument.Parameter?.Ordinal == 0 &&
                            (ReferenceIntrinsic(call, "Volatile", "Read") || ReferenceIntrinsic(call, "Interlocked", "CompareExchange")),
                            "Reference storage escapes to an unrecognized atomic operation.");
                        Need(call.Arguments.Length == 1 || Null(call.Arguments[2].Value),
                            "A reference writer compares against a nonnull value and can replace a published reference.");
                        continue;
                    }
                    Support(argument.Parameter?.Ordinal == 0 &&
                        (Intrinsic(call, "Volatile", "Read") || Intrinsic(call, "Volatile", "Write") ||
                        Intrinsic(call, "Interlocked", "Exchange")), "Storage escapes to an unrecognized ref operation.");
                    Need(call.Arguments.Length == 1 || call.Arguments.Length == 2 && Constant(call.Arguments[1].Value, 1),
                        "A recognized atomic writer resets storage or writes a value other than one.");
                }
            }
        }
        Support(count > 0, "No storage uses were found in the selected source scope.");
    }

    private static bool Matches(ISymbol symbol, string id) => SourceControlFlows.Id(symbol) == id ||
        SourceControlFlows.Id(symbol.OriginalDefinition) == id;
    private static bool Null(IOperation operation) => operation.ConstantValue is { HasValue: true, Value: null };
    private static IOperation ReferenceOperand(IOperation operation)
    {
        while (operation is IConversionOperation { OperatorMethod: null } conversion &&
            (conversion.Conversion.IsIdentity || conversion.Conversion.IsReference))
            operation = conversion.Operand;
        return operation;
    }
    private static bool ReferenceIntrinsic(IInvocationOperation call, string type, string name)
    {
        IMethodSymbol method = call.TargetMethod;
        bool read = type == "Volatile" && name == "Read";
        ITypeSymbol? referenceType = method.IsGenericMethod && method.TypeArguments.Length == 1
            ? method.TypeArguments[0] : !read && method.ReturnType.SpecialType == SpecialType.System_Object
                ? method.ReturnType : null;
        return method.Name == name && method.ContainingType.ToDisplayString() == "System.Threading." + type &&
            !method.Locations.Any(l => l.IsInSource) && method.IsStatic && referenceType?.IsReferenceType == true &&
            method.Parameters.Length == (read ? 1 : 3) &&
            method.Parameters.All(p => SymbolEqualityComparer.Default.Equals(p.Type, referenceType)) &&
            SymbolEqualityComparer.Default.Equals(method.ReturnType, referenceType) &&
            (read ? method.Parameters[0].RefKind is RefKind.Ref or RefKind.In or RefKind.RefReadOnlyParameter :
                method.Parameters[0].RefKind == RefKind.Ref && method.Parameters.Skip(1).All(p => p.RefKind == RefKind.None));
    }

    private static bool Intrinsic(IInvocationOperation call, string type, string name)
    {
        IMethodSymbol method = call.TargetMethod;
        bool read = type == "Volatile" && name == "Read";
        return method.Name == name && method.ContainingType.ToDisplayString() == "System.Threading." + type &&
            !method.Locations.Any(l => l.IsInSource) && !method.IsGenericMethod && method.IsStatic &&
            method.ReturnType.SpecialType == (name == "Write" ? SpecialType.System_Void : SpecialType.System_Int32) &&
            method.Parameters.Length == (read ? 1 : 2) &&
            method.Parameters.All(p => p.Type.SpecialType == SpecialType.System_Int32) &&
            (read ? method.Parameters[0].RefKind is RefKind.Ref or RefKind.In or RefKind.RefReadOnlyParameter :
                method.Parameters[0].RefKind == RefKind.Ref && method.Parameters[1].RefKind == RefKind.None);
    }

    private static bool Constant(IOperation operation, int value) =>
        operation.ConstantValue is { HasValue: true, Value: int actual } && actual == value;

    private static IEnumerable<int> Successors(BasicBlock block) =>
        new[] { block.FallThroughSuccessor?.Destination, block.ConditionalSuccessor?.Destination }
            .OfType<BasicBlock>().Select(b => b.Ordinal).Distinct();

    private static bool Reaches(ControlFlowGraph graph, int start, int end, (int From, int To)? removed = null)
    {
        HashSet<int> visited = [];
        Stack<int> pending = new([start]);
        while (pending.TryPop(out int block))
        {
            if (!visited.Add(block)) continue;
            if (block == end) return true;
            foreach (int next in Successors(graph.Blocks[block]))
                if (removed != (block, next)) pending.Push(next);
        }
        return false;
    }

    private static Place At(SyntaxNode syntax) =>
        Files.At(syntax.SyntaxTree.FilePath, syntax.SyntaxTree.GetText().ToString(), syntax.SpanStart, syntax.Span.End);
    private sealed class CheckFailure(string status, string message) : Exception(message)
    {
        internal string Status { get; } = status;
    }
    private static void Need(bool condition, string message)
    {
        if (!condition) throw new CheckFailure("rejected", message);
    }
    private static void Support(bool condition, string message)
    {
        if (!condition) throw new CheckFailure("incomplete", message);
    }
}
