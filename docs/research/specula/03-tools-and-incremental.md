# Tools, trace matching and incremental verification

[Index](README.md) | [Sources](sources.md)

## 1. Tools are not all implementation analyzers

| Tool or subsystem | Actual role | Boundary |
|---|---|---|
| Python pipeline | Coordinate phases, agents, optional reviews and repair loops | Orchestration is not proof of model adequacy |
| CFA / `spec_analyzer` | Analyze TLA+ action variable assignments and control flow | Its CFG is a TLA+ CFG, not a C# implementation CFG |
| TLC output reader | Summarize counterexamples, navigate states and compare values | Interprets native output; does not establish source correspondence |
| Trace debugger | Debug model/trace mismatches with TLC/DAP breakpoints and expressions | Debugs TLA+ execution and matching, not CLR execution |
| TLC task tools | Start and wait for long-running checking/simulation tasks | Job completion is not exhaustive safety verification |

Sources: [S11](sources.md#s11), [S12](sources.md#s12),
[S13](sources.md#s13), [S14](sources.md#s14), [S15](sources.md#s15).

The inspected pipeline exposes review steps as optional and disabled by default.
Do not equate those agent reviews with independent human approval.

## 2. CFA's especially easy-to-misread role

The CFA documentation describes a SANY/ANTLR-based TLA+ analysis and
transformation tool. The current pipeline uses its variable-assignment
validation entry point to detect missing or duplicate assignments across action
branches.

Other supported transformations insert frame conditions, adjust primed-state
reads and split actions using auxiliary control state. The documentation
connects that broader transformation pipeline to an earlier statement-oriented
Specula approach. It is not evidence of a current automatic
C#-to-summary verifier. [S11](sources.md#s11)

## 3. Two trace-ordering strategies

### Distributed/message-oriented targets

The documented pattern uses a single trace cursor. Event wrappers invoke base
actions, validate post-state fields and advance through the trace. Constrained
silent actions account for selected unobserved model behavior.

### Concurrent/lock-free targets

The documented pattern uses per-thread event arrays, per-thread cursors and
operation start/end intervals. A viability predicate prevents choosing an event
when another pending event is known to have completed before it started.
Overlapping intervals leave alternatives for TLC to search.

The purpose is to avoid making a global logging mutex or sequence counter
serialize nanosecond-scale operations and hide interleavings.
[S05](sources.md#s05), [S19](sources.md#s19)

### Limits relevant to reuse

The guides make strong claims about finding consistent interleavings, including
state captured outside the measured interval. Those claims should not be
imported as unconditional guarantees.

Timestamp comparability, instrumentation placement, memory visibility and the
meaning of multi-field observations require target-specific justification.
Independent shared-field reads do not automatically form one coherent snapshot.
This investigation did not validate the proposed timing or capture scheme.

TlaLink should not adopt timestamp ordering or unguarded state snapshots merely
because a Specula example uses them.

## 4. Incremental verification keeps a complete reference

The incremental instructions reuse prior models and harnesses but require the
new reference suite to represent the current source revision. Changes include
guards, state meanings, initialization, recovery, fairness, trace wrappers and
other affected callers, not just changed function bodies.

The update-specific model is a checking view over that reference. It must not
become a competing definition of the new implementation.
[S08](sources.md#s08), [S10](sources.md#s10)

Campaigns can include:

1. Update properties over the full reference behavior.
2. Concrete focused checking.
3. Open focused checking and environment discharge.
4. Scenario hunts with reachability witnesses.
5. Final affected full checks after repairs stabilize.

This is incremental allocation of modeling/checking effort, not simply a cache
of individual method summaries. [S09](sources.md#s09)

## 5. Completion and CI verdicts require careful interpretation

The incremental checking guide permits planned budget-limited exploration to
satisfy campaign execution requirements. It explicitly requires reporting that
as limited exploration, not exhaustive safety verification.
[S09](sources.md#s09)

The inspected `ci_verdict.py` computes a disposition-based conclusion:

- Reproduced or environment-limited confirmed bugs yield `FAIL`.
- Masked findings yield `WARNING` when no confirmed bug forces failure.
- Otherwise the conclusion function returns `PASS`.

The surrounding reader checks valid dispositions, evidence files, applicable
reuse and prior findings. Nevertheless, `PASS` is not a theorem that no bug
exists, and it does not mean every TLC campaign exhausted its state space.
[S18](sources.md#s18)

The inspected identity code hashes workflow source, skills and launcher
material and includes agent/model tuning, guidance and other execution settings
in a reuse identity. It declines to produce that check key without an explicit
resolved model. Its function named `method_digest` refers to the verification
method/workflow, not an implementation-method behavioral summary.
[S17](sources.md#s17)
