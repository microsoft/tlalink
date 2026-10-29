# Specula research: workflow, abstractions and overlap with TlaLink

Investigated: 2026-10-28. Public upstream:
[specula-org/Specula](https://github.com/specula-org/Specula).
Snapshot: [`39b4a0150a2eb313f3d9abbc78fb41a0d756067f`](https://github.com/specula-org/Specula/commit/39b4a0150a2eb313f3d9abbc78fb41a0d756067f),
whose commit date is 2026-10-02.

## Bottom line

Specula already supplies an agent-operated TLA+ bug-finding workflow:
implementation analysis, model generation, execution-trace validation,
model checking, counterexample investigation and implementation-level
reproduction. Rebuilding that orchestration wholesale would duplicate
substantial work. [Sources S01, S02 and S15](sources.md)

For reusable method summaries, the answer is more qualified:

- Native TLA+ operators already express simplified behavior and can be reused
  by other model operators.
- Specula's incremental workflow explicitly abstracts omitted context through
  an environment action and checks that concrete context satisfies it.
- That is **model-to-model discharge**, not automatic validation that a C#
  method implements a summary.
- In the inspected sources, I did **not** find a first-class facility that
  registers an LLM-reviewed method summary, pins its implementation review,
  and tracks caller dependencies on that summary.
- Its concurrent-modeling guidance actually warns against collapsing
  observable load/check/CAS steps merely to reduce the state space.
  [Sources S04 and S07](sources.md)

The useful distinction is therefore:

> Reuse Specula's modeling and validation ideas. Do not mistake its focused
> context abstraction for an existing implementation-backed method-summary
> registry.

This is a scoped research conclusion, not a claim that no similar mechanism
exists anywhere in Specula or its dependencies.

## Reading order

| Document | Question answered |
|---|---|
| [Workflow and architecture](01-workflow.md) | What does Specula do end to end, and what artifacts connect the phases? |
| [Modeling, abstraction and method summaries](02-modeling-and-summaries.md) | How does it simplify behavior, and does it provide the proposed feature? |
| [Tools and incremental verification](03-tools-and-incremental.md) | What is implemented machinery versus agent guidance? |
| [TlaLink overlap and decisions](04-tlalink-decisions.md) | What should TlaLink reuse, avoid duplicating, or add narrowly? |
| [Sources and investigation limits](sources.md) | Which revision and primary sources support these conclusions? |

## Evidence discipline

These documents distinguish:

1. **Documented workflow:** instructions telling agents what to do.
2. **Inspected implementation:** behavior directly visible in selected source.
3. **Research inference:** conclusions drawn from those materials.
4. **Recommendation:** proposed TlaLink work, not an existing feature.

No Specula pipeline, target harness, model checker or reproduction was run
for this research. Its reported bug discoveries and performance were not
independently reproduced. Reading a skill is not evidence that every run
follows it.

The documents are independently written summaries with pinned upstream
citations, not copies of Specula's skills or source. Upstream identifies its
license as Apache-2.0; adopting code or packaged tools needs a separate
license/dependency review. [Source S01](sources.md#s01)

## Implementation status

This research snapshot predates TlaLink's reviewed method-summary implementation.
Its original recommendation and limits are described in
[TlaLink decisions](04-tlalink-decisions.md). For current behavior, use the
[reference](../../../REFERENCE.md#reviewed-method-summaries) and
[runnable example](../../../examples/MethodSummaries/README.md).
