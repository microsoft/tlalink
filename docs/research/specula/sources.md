# Sources, provenance and limits

[Index](README.md)

## Snapshot

- Repository: [specula-org/Specula](https://github.com/specula-org/Specula).
- Investigated: 2026-10-28.
- Resolved `main`:
  [`39b4a0150a2eb313f3d9abbc78fb41a0d756067f`](https://github.com/specula-org/Specula/commit/39b4a0150a2eb313f3d9abbc78fb41a0d756067f).
- Commit timestamp reported by GitHub: 2026-10-02T16:48:13Z.
- Discovery used the GitHub CLI repository search, README, commit and recursive
  tree endpoints. Primary content was read through GitHub/raw GitHub.
- Links below are pinned to that revision, not the moving default branch.

## How to interpret this evidence

Skills and guides show intended methodology. Tool documentation describes
interfaces and roles. Selected Python source provides direct evidence for
orchestration, identity and verdict behavior.

This was not an exhaustive source audit. Long files were read in relevant
sections; the pipeline and CI modules were not traced end to end. No external
backend was executed, no tool was installed and no upstream tests or case-study
reproductions were run.

The linked paper, bug spreadsheet, separate case-study archive, modular TLA+
example repository and trace-method research papers were not independently
assessed. They are not treated as verified evidence for effectiveness,
soundness, performance or general implementation refinement.

The statement "not found" means not established by the sources inspected here,
not proof that no related feature exists elsewhere.

## S01

[Repository README](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/README.md)

Supports project purpose, prerequisites, setup, interactive/automatic/incremental
modes, bring-your-own-model entry point and the Apache-2.0 license declaration.
It is not an independently reproduced evaluation.

## S02

[Workflow overview](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/skills/workflow-overview.md)

Supports phases, scenario-driven analysis, model/harness artifacts, trace
categories and the broad validation/confirmation flow. Detailed confirmation
rules are better read with S16 and S18.

## S03

[Usage guide](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/docs/Usage.md)

Inspected the platform, prerequisites and setup sections. Supports Linux/macOS,
WSL2 rather than native Windows, agent/toolchain requirements and setup costs.

## S04

[Base specification methodology](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/skills/spec_generation/references/base-spec-methodology.md)

Supports code-faithful, scenario-driven modeling; source references; splitting
observable atomic steps; and the warning against collapsing concurrent
load/check/CAS behavior for state-space convenience.

## S05

[Trace specification pattern](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/skills/spec_generation/references/trace-spec-pattern.md)

Supports base-action reuse, post-state validation, constrained silent actions,
single-cursor versus per-thread timebox matching and interval-based viability.
Its strong matching claims were not independently validated.

## S06

[Specification fidelity checklist](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/skills/tla-checking-workflow/references/spec-fidelity-checklist.md)

Supports investigating implementation guards and suppression mechanisms before
classifying an implausible counterexample as a real bug.

## S07

[Open update specifications and discharge checking](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/skills/incremental-modeling/references/generation/modular-verification.md)

The key abstraction source: environment/rely actions, omitted-context discharge,
frame conditions, continued full-model checks and limits on liveness claims.
It explicitly scopes this mechanism to focused incremental checking.

## S08

[Generate the complete new reference suite](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/skills/incremental-modeling/references/generation/02-reference-model-generation.md)

Supports one complete behavioral reference, dependency-ordered updates,
implementation-backed semantic repairs and explicit source/update mappings.

## S09

[Update-focused model checking](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/skills/incremental-modeling/references/model-checking/01-update-focused-checking.md)

Supports full/focused/open/discharge campaigns, reachability canaries, repair
back-edges and the distinction between budgeted campaign completion and
exhaustive verification.

## S10

[Incremental modeling guide](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/skills/incremental-modeling/guide.md)

Inspected the workflow description and generation/validation/checking guidance.
Supports reuse of prior semantic assets and a current reference suite, rather
than a documented method-summary registry.

## S11

[CFA tool README](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/tools/cfa/README.md)

Supports TLA+ CFG/variable-assignment analysis, available transformations, the
current `spec_analyzer` role and the distinction from earlier statement-oriented
translation. Not a C# CFG or source-refinement checker.

## S12

[TLC output reader README](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/tools/inv_checking_tool/README.md)

Supports counterexample summaries, state navigation, comparison, variable
tracking and MCP interfaces.

## S13

[Trace debugger README](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/tools/trace_debugger/README.md)

Inspected overview and tool interfaces. Supports TLC/DAP trace debugging,
conditional breakpoints, expression evaluation and syntax checks.

## S14

[TLC task tools README](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/tools/tlc_tools/README.md)

Supports explicit start/wait task handles and persistent job logs/exit codes.

## S15

[Python pipeline orchestrator](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/src/specula/pipelinelib.py)

Inspected its introduction, imports and CLI option declarations. Supports
phase orchestration, repair-loop options, agent routing and optional reviews
disabled by default. Not an end-to-end implementation audit.

## S16

[Bug confirmation guide](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/skills/bug-confirmation/guide.md)

Inspected confirmation flow, verdicts and repair-request rules. Supports
reachability/consequence criteria and distinguishing reproduction,
environment-limited findings, masks and model artifacts.

## S17

[CI identity implementation](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/src/specula/ci_identity.py)

Supports workflow-content digests and check-key inputs. The function
`method_digest` fingerprints the verification workflow, not a source-method
summary.

## S18

[CI verdict implementation](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/src/specula/ci_verdict.py)

Inspected conclusion/exit-code logic and evidence/prior-finding validation.
Supports the finding-disposition interpretation of PASS/WARNING/FAIL.

## S19

[Concurrent timebox trace guide](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/skills/harness-generation/references/concurrent-timebox-guide.md)

Inspected architecture, instrumentation placement and ordering rationale.
Supports the intended low-probe-effect approach. Timing, memory-observation and
snapshot claims were not established by this research.

## S20

[Included ring-buffer base model](https://github.com/specula-org/Specula/blob/39b4a0150a2eb313f3d9abbc78fb41a0d756067f/skills/spec_generation/examples/dpdk-ring/base.tla)

Inspected state declarations and setup: thread phases, reservation/publication
state and visibility-related variables. Used only as an example of modeling
granularity, not as evidence of model correctness.

## Local comparison basis

TlaLink comparisons use the current working tree's
[README](../../../README.md), [design](../../../DESIGN.md),
[reference](../../../REFERENCE.md), and selected
[annotation](../../../src/tlalink/Common/AnnotationContracts.cs),
[correspondence](../../../src/tlalink/Common/FormalCorrespondence.cs),
[project](../../../src/tlalink/Common/ProjectContracts.cs),
[workflow](../../../src/tlalink/Common/ProjectWorkflow.cs) and
[acceptance](../../../src/tlalink/Common/ProjectAcceptance.cs) implementations.

Those local links describe the inspected working tree, not a separately pinned
TlaLink release. Proposed summary behavior is explicitly marked as a
recommendation, not an existing public contract.
