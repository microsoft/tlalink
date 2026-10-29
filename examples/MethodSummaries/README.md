# Reviewed method summaries

This independently synthetic example gives a CAS-based C# helper one small
TLA+ relation. Two caller models import it without retry locals or helper
program-counter state. No implementation-to-summary machine proof is claimed.
The [formal summary](verification/PermitSummary.tla) owns the Roslyn source
selector; the C# helper needs no binding comment. Use the documented
[symbols and links CLI](../../REFERENCE.md#maintaining-model-owned-links)
to discover, validate and maintain that connection.

- [C# helper](src/PermitGate.cs)
- [Stateless native summary](verification/PermitSummary.tla)
- [Summary declaration](verification/scope.json)
- [First caller](verification/CallerA.tla) and [second caller](verification/CallerB.tla)
- [Agent inspection argument](verification/inspection.md)

The [project](tlalink.project.json) selects both callers and the pinned TLA+
tool. After the separately documented tool setup, run from the repository root:

```powershell
.\TlaLink.ps1 doctor --project .\examples\MethodSummaries\tlalink.project.json --scenario callers
.\TlaLink.ps1 plan --project .\examples\MethodSummaries\tlalink.project.json --scenario callers --format summary
.\TlaLink.ps1 run --project .\examples\MethodSummaries\tlalink.project.json --scenario callers --output .\Results\method-summaries-new
```

Acceptance initially leaves the inspection pending. After inspecting the source,
relation and assumptions, an agent can explicitly record its own decision:

```powershell
.\TlaLink.ps1 record-review --project .\examples\MethodSummaries\tlalink.project.json --obligation permit-summary --origin agent --reviewer "agent:self-review" --rationale "See the scoped linearization argument and limitations." --evidence "verification\inspection.md"
.\TlaLink.ps1 accept --project .\examples\MethodSummaries\tlalink.project.json --run .\Results\method-summaries-new --format summary
```

This is agent inspection, not human approval. A second accepted run can reuse
the declared TLC result store while independently rechecking inspection freshness.

The native regression tests construct isolated projects and exercise
`doctor`, `run`, `record-review`, `accept` and `report`. They do not install
tools. The focused selection requires only the selected TLA+ JAR and pinned JDK:

```powershell
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . --summaries
```

In an authored project, each TLC/trace stage declares its dependencies:

```json
"method_summaries": [{
  "scope": "verification\\scope.json",
  "bindings": ["permits::try-take"]
}]
```

The project also declares the `permit-summary` review obligation with
`"origin": "agent"`, an explicit claim, assumptions and input paths. An
additional human obligation remains separate. `record-review --origin agent`
records a reasoned inspection and automatically pins the resolved summary,
source/compiler context, imports and inspection tools.

The caller configurations explicitly check `CallerPrecondition`. Its predicate
is not an action guard that silently removes misuse from the model. The CLI
resolves the selected precondition but does not prove that every caller checks
or establishes it; that remains part of the authored model and its review.

Changing the implementation makes its inspection stale. An unchanged caller
model can still use the same relation, but acceptance needs current inspection
support. Changing the summary also changes the caller's imported model closure.
