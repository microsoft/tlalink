# Specula workflow and architecture

[Index](README.md) | [Sources](sources.md)

## 1. What Specula is

Specula combines coding-agent skills, a Python CLI/orchestrator and tools for
TLA+ analysis and TLC execution. Its principal goal is finding bugs in
concurrent and distributed implementations, then establishing whether model
counterexamples correspond to real implementation failures.

It is not a compiler that automatically proves arbitrary implementation code
correct. Agents choose scope, infer properties, author abstractions, build
harnesses and investigate discrepancies. [S01](sources.md#s01),
[S02](sources.md#s02), [S15](sources.md#s15)

## 2. End-to-end artifact flow

```text
Target source + optional modeling guidance
                     |
              Code analysis
                     |
            Modeling brief / scenarios
                     |
             Spec generation
                     |
       base.tla + MC.tla + Trace.tla
          + instrumentation specification
                     |
            Harness generation
                     |
          Instrumented executions / traces
                     |
      Trace validation + model-checking campaigns
                     |
         Counterexamples / candidate findings
                     |
       Source investigation + reproduction
                     |
        Findings, dispositions and reports
```

The phase structure and artifacts are documented in the
[workflow overview](sources.md#s02). The
[Python orchestrator](sources.md#s15) describes and exposes the corresponding
phase sequence, optional review steps, repair-loop controls and agent routing.
The presence of orchestration does not mechanically enforce every semantic
instruction inside a phase's skill.

## 3. Analysis produces scenarios, not a whole-program translation

The analysis phase maps important implementation paths, concurrency boundaries,
history and potential failure mechanisms. It organizes the result into
scenarios: for example, a publication window or recovery interaction.

The modeling brief is the handoff explaining what to model and why. Scenario
selection limits scope; behavior inside the selected scope is supposed to
remain faithful to the implementation. This separates deciding what matters
from deciding how it behaves. [S02](sources.md#s02),
[S04](sources.md#s04)

## 4. Three related model surfaces

| Artifact | Responsibility |
|---|---|
| `base.tla` | Authored implementation abstraction: state, guards, effects and relevant interleavings |
| `MC.tla` and configurations | Model-checking setup, bounds, fault budgets and scenario campaigns |
| `Trace.tla` and configuration | Match recorded implementation events against base-model behavior |

An instrumentation specification maps model actions to implementation event
locations and fields. These mappings are authored guidance, not equivalent to
Roslyn-resolved C# bindings. [S02](sources.md#s02),
[S04](sources.md#s04), [S05](sources.md#s05)

The same base behavior is intended to support both broader exploration and
matching observed executions. Trace wrappers should invoke base actions,
not substitute easier assignments when matching becomes difficult.

## 5. Trace validation and model checking answer different questions

Trace validation asks whether a recorded execution can be explained by the
selected abstraction and observations. Specula's instructions require
post-state checks as well as action/event matching.

Model checking explores additional model behaviors under the configured
domains, enabled actions and assumptions. Exhaustive finite exploration,
simulation and budget-limited search have different coverage.

Neither matching sampled executions nor checking a model establishes that all
implementation executions refine that model. [S05](sources.md#s05),
[S09](sources.md#s09)

## 6. Counterexamples are investigated, not automatically filed as bugs

The workflow distinguishes incorrect properties, model-fidelity defects and
implementation-defect candidates. The fidelity checklist sends the agent back
to the source to look for missing guards, suppression, leases and other
mechanisms that invalidate a purported execution. [S06](sources.md#s06)

The detailed confirmation guide requires reachability and a consequence for a
real caller/consumer. It distinguishes reproduced findings, environment-limited
arguments, masked defects, false positives and unresolved work. A model or
fault-model artifact can trigger a repair request back to validation rather
than become a reported implementation bug. [S16](sources.md#s16)

The top-level overview uses simpler confirmation/tier descriptions. For the
specific disposition rules, this research follows the detailed confirmation
guide and the inspected CI verdict code rather than treating the overview as
a complete result schema. [S02](sources.md#s02),
[S18](sources.md#s18)

## 7. Operating modes and adoption cost

- **Interactive:** invoke the skills phase by phase.
- **Automatic:** let the pipeline coordinate agents and artifacts.
- **Incremental:** retain a model/harness baseline and inspect subsequent changes.
- **Bring your own model:** import existing model assets rather than start with
  generated ones.

Setup involves Python tooling, Java/Maven, agent CLIs and target build/test
dependencies. The documented native platforms are Linux and macOS; Windows
uses WSL2. This is a material integration difference from TlaLink's native
Windows C#/.NET workflow. [S01](sources.md#s01), [S03](sources.md#s03)

Specula also depends on the selected coding agent and its provider access.
Running TLC locally is not a claim about how the surrounding agent handles
source data. No Specula setup or agent execution was performed here.
