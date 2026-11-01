# Native P translation controls

These independently synthetic fixtures are copied to fresh test work directories.
`PTranslationTests` is registered by the existing `--p` console suite. No selected
tool is mutated, installed or upgraded.

| Construct / motivating defect | Healthy native control | Named defective control |
|---|---|---|
| Inline field assignment, integer event payload, self-send and later handler state | `InlineState.p`: PChecker completes ten schedules | `ValueCorruption.p`: completed native assertion `inline-event-state-persistence` |
| Transitive procedural helper field/message effects | Native PChecker frontend resolves both helpers in `HelperSend.p` | PVerifier frontend refuses the same model before proof; this is an unsupported-translation guard, **not** a rejected invariant |
| Supported inline PVerifier assignment, seeded send and unchanged state across another handler | `InlineVerifier.p`: native invariant and default obligations complete | `VerifierValueCorruption.p`: completed inductive counterexample, not timeout/unknown/tool failure |
| PObserve generated nested monitor type, field storage and event handlers | Native generation, Java compilation and nonempty `healthy.log` monitoring | `MonitorTypeMismatch.java`: exactly two unresolved-type diagnostics; `wrong-state.log`: completed native specification violation |
| PEx generated runtime imports and event-handler code | Native generation and Java compilation against the selected runtime | `UnknownUtilityImport.java`: exactly one missing `pex.utils` package diagnostic |
| Monitoring prerequisite versus property failure | Nonempty healthy monitoring | `parser-error.log` remains incomplete, never a successful rejection control |

## Explicit optional selections

The ordinary PCompiler prerequisite is selected by `TLALINK_P_COMPILER`.
Extended controls use existing schema-4 `p-analysis` configurations solely to
select their already-pinned dependency/runtime closures:

```powershell
$env:TLALINK_P_VERIFIER_CONFIG = "<existing pverifier healthy.json>"
$env:TLALINK_P_OBSERVE_CONFIG = "<existing pobserve healthy.json>"
$env:TLALINK_P_EX_CONFIG = "<existing pex healthy.json>"
$env:TLALINK_P_TRANSLATION_COMPILER = "<separately built P 3.1.0 generation-only p.dll>"
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . --p
```

Paths are caller-relative or absolute. Configuration-relative dependencies keep
their original declaring base. The test host leases the explicitly selected
compiler closure, dependency files and runtime/artifact JARs before running.
The generation-only compiler is the separately built native compiler from
[extended setup](../../../../examples/Dispatcher/setup/README.md), not an edit to
the selected official installation. Its PEx compilation stage must remain
disabled; tests never invoke the upstream latest-version Maven build.

PObserve/PEx configurations must pin `java`, `javac` and `jar` executables and
their complete JDK closure. PEx Java compilation also needs the selected Lombok
annotation JAR as an explicit dependency (the maintained PEx POM selects
1.18.24); `-proc:none` disables annotation
processing, not annotation typechecking. The selected artifact/runtime and
explicit dependency JARs form the compilation classpath. No cache discovery or
dependency provisioning occurs during tests. Missing optional environment
selections print `SKIP`; missing/mismatched explicitly selected inputs fail
preflight. Neither case counts as a native rejection.
PObserve generation explicitly selects the unique
`translation.controls.generated` package so the parser must compile against
these generated events, not similarly named event classes in selected artifacts.

## Scope and gaps

These controls exercise particular source constructs and defect signatures,
not general native translation attestation or C# refinement. Java type failures
are compilation controls, not P safety counterexamples. The named Java defects
are independently authored shapes, not mutations of selected generators.
PEx exhaustion and PSym generation are not covered here. The verifier pair
checks one supplied invariant; it does not establish message delivery,
termination or asynchronous progress. The PChecker pair separately exercises
the seeded event chain, avoiding a proof-only reachability claim.
PObserve checks two serial records in one partition, not log completeness,
causal ordering, concurrent monitor equivalence or arbitrary payload types.
Collections, aliases, helper translation, machine creation semantics, exceptions
and the full P language remain outside this focused coverage.
