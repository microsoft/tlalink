# TlaLink overlap and a narrow method-summary feature

[Index](README.md) | [Sources](sources.md)

This is a recommendation based on the investigation, not an implemented design
or a new acceptance policy.
It records the comparison before implementation; current TlaLink behavior is
documented in the [reference](../../../REFERENCE.md#reviewed-method-summaries).

## 1. What overlaps

| Concern | Specula, in inspected sources | TlaLink surface at investigation time |
|---|---|---|
| Agent-authored abstraction | Scenario-driven TLA+ models | Agent-first C# models with TLA+, Dafny and P backends |
| Connection to implementation | Authored source references, instrumentation maps and execution traces | Paired resolved bindings, explicit C# compiler context and execution evidence |
| Model-fidelity feedback | Trace matching, source review, reproduction and repair loops | Source premises, trace matching, controls and correspondence review |
| Repeatable execution | Pipeline and persistent incremental workflow | Project composition and `doctor` / `plan` / `run` |
| Evidence decisions | Finding dispositions and CI verdicts | Separate requirements, review decisions, applicability and `accept` |
| Focused abstraction | Open update/environment discharge at the model level | No general automatic composition proof claimed |
| Proposed reviewed method summaries | No first-class mechanism found in the inspected sources | Would be a small new relationship over existing bindings/evidence |

TlaLink basis: [README](../../../README.md),
[design](../../../DESIGN.md) and [reference](../../../REFERENCE.md).
Specula basis: [S02](sources.md#s02), [S07](sources.md#s07),
[S15](sources.md#s15), [S18](sources.md#s18).

These are different emphases, not a ranking. Specula supplies agent
orchestration and a TLA+ bug-finding process. TlaLink's distinctive maintained
surface is C#/.NET source resolution, compiler/build context and independently
inspectable evidence.

## 2. Do not rebuild these ideas

- A new language for summary behavior: use the caller's native model language.
- A second TLA+ interpreter or ad hoc parser: retain authoritative frontends.
- A complete agent pipeline solely to add summaries: use existing project stages.
- A new report format: extend the existing evidence report.
- An automatic generic composition engine as the first increment.

Specula's best immediately reusable ideas are methodological: scenario-driven
scope, independent source-fidelity review, executable mismatch investigation,
non-vacuity witnesses and repair loops.

## 3. The smallest useful addition

Treat a model summary as an existing C# method/native-model binding plus:

| Information | Purpose |
|---|---|
| Summary binding | Identify the method and native behavior declaration |
| Inspected inputs | Pin implementation, relevant context, dependencies and model bytes |
| Inspection rationale | Explain why the implementation admits this abstraction |
| Assumptions and gaps | State interference, effects, exceptions and progress limits |
| Inspection origin | Distinguish LLM self-review from any required human decision |
| Explicit consumers | Name checks relying on this summary |

Use the summary as the inspection target internally and as the operation used
by caller models externally. Internal model details need not appear in callers.
Reuse paired annotations for endpoint identity; avoid a new annotation per
statement and avoid treating caller uses as duplicate provider endpoints.

The formal declaration remains in ordinary TLA+, Dafny or P, selected explicitly.
This does not imply one backend's summary can be consumed automatically by
another backend.

## 4. Keep evidence claims small

For the lightweight version:

```text
Implementation matches summary:
    LLM inspection argument, current for pinned inputs

Caller property under that summary:
    Native checker result, with its exact scope and assumptions

Overall interpretation:
    Conditional on the inspected implementation abstraction
```

An authored rationale is not human approval or a machine proof. A consumer's
acceptance policy decides whether that evidence is sufficient. Unsupported
claims and required human reviews remain outstanding rather than being
silently discharged.

If implementation bytes change, the inspection becomes stale. If the summary
remains unchanged, an existing caller model result may still apply to that
same abstraction, but current implementation support must be re-established.
Use existing applicability/evidence mechanisms rather than introducing a
general-purpose result cache.

## 5. When not to summarize the method as one atomic action

Retain internal behavior when the requested claim depends on:

- Publication, reclamation or other observable intermediate states.
- Callbacks, reentrancy, suspension or externally visible partial effects.
- Failure paths that do not fit the proposed summary.
- Progress, starvation or fairness hidden by removing retries.

A summary can still be useful, but may need several abstract steps or a weaker
claim. Specula's warning against collapsing relevant interleavings is directly
applicable here. [S04](sources.md#s04)

## 6. Suggested implementation sequence

1. Choose one independently synthetic helper and one caller.
2. Give the helper a small native summary with a paired method binding.
3. Record an explicit source-pinned LLM inspection, assumptions and omissions.
4. Declare the caller check's reliance on the summary.
5. Demonstrate stale inspection after a source change.
6. Exercise a defective helper and a caller that violates an assumption.
7. Present conditional results through the existing report and policy surfaces.

Do not claim automatic source refinement or require model-level discharge
machinery to ship this initial feature. The latter is a possible future,
stronger evidence path, not a prerequisite for useful reviewed summaries.

## Decision

Adding lightweight reviewed method summaries does not appear to duplicate a
first-class feature established by this Specula investigation. Building another
end-to-end agentic TLA+ bug finder would duplicate much more.

Proceed with a small evidence relationship over TlaLink's current C# bindings
and workflow, while borrowing Specula's fidelity safeguards.
