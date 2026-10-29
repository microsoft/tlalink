# Modeling, abstraction and reusable method summaries

[Index](README.md) | [Sources](sources.md)

## 1. The specific feature being evaluated

A lightweight method summary would let an author:

1. Write a small native model of a C# method's caller-visible behavior.
2. Inspect the implementation against that model, including relevant callees
   and competing accesses.
3. Record the reasoning, assumptions and exact inspected inputs.
4. Let caller models use the summary without expanding the implementation.
5. Detect when changes invalidate that inspection.

This is not necessarily a machine-checked refinement proof. An LLM-reviewed
summary is useful evidence if its status and limitations remain explicit.

## 2. What Specula already provides

### Native TLA+ abstraction

Specula writes ordinary TLA+ variables, predicates, operators and actions.
Helpers and actions can be reused by other operators; no special summary
language is needed. Scope is scenario-driven, with implementation references
attached to modeled logic. [S04](sources.md#s04)

This supplies the language and authoring practice for a method summary.
It does not itself supply implementation-conformance evidence.

### Validation against observed code behavior

An instrumented harness produces traces. Trace models invoke base actions and
check captured state, exposing some mismatches between implementation and
abstraction. This is an executable feedback loop supporting abstraction review,
not general source refinement. [S05](sources.md#s05)

### Explicit abstraction of omitted context

The incremental workflow has a particularly relevant mechanism:

- Keep a complete current reference model as the behavioral source of truth.
- Build an update-focused view by selecting affected and interacting actions.
- Represent remaining relevant context through an explicit environment action.
- Check that omitted concrete context actions satisfy that environment relation,
  including the required frame conditions.

This is genuine rely/discharge machinery at the TLA+ model level. It is the
closest inspected analogue to validating an abstraction on one side and using
it on the other. [S07](sources.md#s07), [S08](sources.md#s08)

## 3. What the modular check does and does not establish

Conceptually:

```text
Concrete context actions in the reference TLA+ model
                         |
                  discharge check
                         |
                Environment abstraction
                         |
              Focused update model check
```

The discharge check asks whether the omitted context transitions are permitted
by the authored environment relation. It does not read a C# method and infer
its effects.

Specula explicitly retains full-model checks. Focused checking is not a
replacement for the complete reference, and environment discharge is additional
to those checks. Its guide also says not to turn this mechanism into a wholesale
decomposition architecture. [S07](sources.md#s07),
[S09](sources.md#s09)

Safety-oriented action relies do not automatically provide liveness guarantees.
The guide treats TLAPS as a possible later option when finite TLC checking
becomes limiting, not a required, established automatic refinement pipeline.

## 4. Important tension: hiding CAS internals

Specula's base-model methodology explicitly warns against combining observable
load/check/CAS steps into one atomic action. It recommends retaining intermediate
state and interleaving windows when those windows are relevant to the defect.
State-space reduction should not silently remove them. [S04](sources.md#s04)

Its included ring-buffer model illustrates this emphasis: it records thread
phases, reservation versus publication, and visibility-related state rather
than describing every operation only as an instantaneous enqueue/dequeue.
That is an example of modeling practice, not evidence that this example's
abstraction is universally adequate. [S20](sources.md#s20)

This does not make method summaries wrong. It distinguishes two questions:

| Question | Appropriate modeling detail |
|---|---|
| Is the retry/publication protocol itself correct? | Preserve relevant internal steps and interference |
| Is a caller correct assuming an already-supported abstract operation? | Use the small summary, retaining its assumptions |

A successful CAS may justify an atomic summary for some methods and properties.
It is not enough merely to find a CAS: failure paths, callbacks, other effects,
competing updates and termination assumptions still need consideration.

That last paragraph is a research recommendation, not an automatic capability
found in Specula.

## 5. Feature assessment

| Desired capability | Evidence in inspected Specula material |
|---|---|
| Write a small behavioral summary in TLA+ | Yes: ordinary native modeling |
| Reuse model operators within a larger model | Yes: normal TLA+ composition |
| Challenge model fidelity against implementation | Yes: source review, trace validation and reproduction workflow |
| Check an environment abstraction against concrete model actions | Yes: incremental open/discharge checks |
| First-class C# method-summary declaration | Not found in inspected sources |
| Source-pinned LLM inspection receipt per method summary | Not found in inspected sources |
| Explicit caller-to-reviewed-summary dependency tracking | Not found in inspected sources |
| Automatically prove arbitrary C# implements the summary | No such capability established by this investigation |

The negative findings are scoped to the listed sources and mechanisms, not an
exhaustive search of every dependency or case study.

## 6. Practical conclusion

There is no need to invent a modeling language. TLA+ already expresses the
summary, and Specula provides valuable methods for challenging it.

The potential TlaLink addition is smaller and different: give an existing
method/model binding an explicitly recorded inspection basis and let caller
checks declare reliance on it. Keep the implementation-review argument distinct
from the checker result for the caller.
