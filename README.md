# TlaLink

**An agent-first tool for connecting C# implementations to formal evidence.**

Your coding agent can help reason about a concurrency bug, write a model or
propose an invariant. TlaLink gives that work a checkable foundation: explicit
links to your code, results from established formal-methods tools, and retained
evidence of exactly what was checked.

You work with the agent to define the question and review the assumptions.
The agent operates TlaLink to prepare and run checks, investigate failures and
explain the results. The goal is to move from **"the agent says this looks
correct"** to **"here is the evidence, its scope and what remains unproven."**

TlaLink is a local .NET 10 command-line tool for an agent to operate, not a
hosted AI service or a bundled coding agent. It uses upstream TLA+, Dafny and P
tools rather than replacing their parsers, model checkers or verifiers.
It is designed specifically for the **C#/.NET toolchain**: Roslyn resolves your
C# sources and bindings, and execution evidence records the compiler inputs
and loaded .NET binary. TLA+, Dafny and P complement that implementation
workflow; TlaLink is not a language-agnostic implementation verifier.
TlaLink itself does not upload your code; your coding agent's data handling
depends on its own configuration.

## Supported tools at a glance

| Tool | Main strengths in TlaLink |
|---|---|
| [**Roslyn**](https://github.com/dotnet/roslyn) | Resolve C# symbols and source/model bindings; validate capture sites and compilation context. |
| [**SANY / PlusCal**](https://github.com/tlaplus/tlaplus) | Parse and resolve TLA+ models; support labeled algorithms and check translation freshness. |
| [**TLC**](https://github.com/tlaplus/tlaplus) | Explore configured finite models for safety, deadlocks and temporal properties; match recorded C# executions against TLA+. |
| [**Apalache**](https://apalache-mc.org/) | Use SMT-based TLA+ typechecking, bounded safety checks and supplied-invariant induction. |
| [**Dafny**](https://dafny.org/) | Verify algorithmic contracts and invariants; generate executable oracles for selected C# comparisons. |
| [**P: PChecker, PEx, PSym, PVerifier**](https://p-org.github.io/P/) | Model asynchronous state machines; select schedule exploration, finite/symbolic exhaustion or invariant proofs. |
| [**PObserve**](https://p-org.github.io/P/advanced/pobserve/pobserve/) | Check observed execution logs against P monitors. |

Each backend retains its own scope and guarantees. The native P suite exercises
PChecker; the other P engines have adapter/evidence controls, not end-to-end
native coverage. See the [selection guide](REFERENCE.md#choosing-tools-and-checkers)
for prerequisites, limits and test coverage.

## What can I use it for?

| Your question | TlaLink's approach |
|---|---|
| Which code corresponds to which model action or contract? | Resolve source/model bindings and report gaps for review. |
| Does this recorded C# execution fit my TLA+ model? | Instrument a separate source copy, record its build and execution, then use TLC to find a whole-trace witness. |
| Does this C# operation agree with a verified algorithm? | Compare selected C# adapter calls with an executable Dafny oracle. |
| Does my model satisfy its properties? | Run explicitly selected TLC, Apalache, Dafny or P analyses, keeping their different guarantees separate. |
| Is yesterday's evidence still relevant after a change? | Check archived evidence, current inputs and independent acceptance requirements. |
| Which evidence must I keep, and what needs rechecking? | Use the read-only [retention planner](REFERENCE.md#evidence-retention-and-recheck-planning) to follow explicit pins, review histories and recorded dependencies. |
| Can I share the whole evidence story with a person? | Generate a [Markdown report with Mermaid diagrams](REFERENCE.md#human-readable-evidence-reports), linked code regions, scoped claims, assumptions and open gates. |

The core value is the evidence chain: **source bindings ->
build/execution provenance -> scoped results -> change-aware acceptance**.

**This is not an automatic proof of C# correctness.** A matching trace covers
one finite observed execution; oracle comparisons cover selected cases.
Verification of a Dafny or P model does not automatically verify the linked C#.
Coverage, assumptions and cross-model correspondence still need review.

## Getting started

Open this repository alongside your code in an agent-enabled development
environment. Your agent needs access to the relevant sources and a shell to
run the tool. [AGENTS.md](AGENTS.md) contains the repository guidance;
the [reference](REFERENCE.md) contains the commands and formats. You do not
need to learn the CLI first.

For a first look, have the agent check setup and walk you through the
[small C# example](examples/Demo.cs) and its [TLA+ model](examples/Demo.tla).
This demonstrates source/model review without executing the C#.
For an end-to-end execution check, use the `lanes` scenario in the
[workflow example](examples/Workflow/project.json).

When you bring your own code, start with a concrete concern: "Can cancellation
leak a credit?" is more useful than "Verify this subsystem." Share the relevant
entry points, dependencies and any existing models. You and the agent can then
agree on the smallest useful scope and the evidence needed.

## How you work with it

1. **Define the claim.** Identify the behavior, bounds and assumptions that
   matter. The agent can recommend a model and checker; you review whether
   that abstraction captures the real concern.
2. **Prepare the checks.** The agent links source symbols to model declarations
   and sets up the workflow. Missing context and coverage gaps stay explicit.
   Instrumentation uses separate source copies, not edits to product code.
3. **Run and investigate.** Native tools produce model results, trace witnesses,
   counterexamples or oracle comparisons. The agent helps explain failures
   without treating tool errors or incomplete searches as passes.
4. **Review the evidence.** You get the result, the exact inputs and tools,
   remaining assumptions and an assessment against independently declared
   requirements. Changing code can make earlier evidence inapplicable.

For repeatable work, the project workflow is
**`doctor` -> `plan` -> `run` -> `accept` -> `explain`**.
The agent handles the mechanics; you should expect an explanation of the
claim, the evidence supporting it and its limitations. It must not weaken
requirements or replace a baseline merely to get a pass.

## What needs to be installed?

The host needs **Windows x64 or Linux x64**, the **.NET 10 SDK** selected by
[global.json](global.json), and **PowerShell 7** for repository scripts.
Your agent should consult [setup instructions](REFERENCE.md#requirements-and-build)
and install only the backends needed for the chosen workflow.

Tools live in the ignored local `.tools` cache. Java-backed commands require
the pinned JDK, not whichever Java is on `PATH`. Provisioning is an explicit
setup step: analysis commands do not automatically download or upgrade tools.
The [launcher](TlaLink.ps1) builds before invoking the CLI to avoid stale binaries.

An initial build and core test run need no formal-methods backends.
The full test suite additionally requires the external backend distributions.
Exact commands, versions and checksums are in the reference.

## Workflow guide

Use these references when choosing or asking your agent to extend a workflow.

| Goal | Start with |
|---|---|
| Check a complete C# execution against TLA+ | [Project workflow](REFERENCE.md#project-workflow), especially the `lanes` scenario in the [workflow example](examples/Workflow/project.json). |
| Understand or customize capture and trace matching | [Instrument, execute, validate](REFERENCE.md#instrument-execute-validate) and the [annotation grammar](REFERENCE.md#one-maintained-markup-tla-2). |
| Verify Dafny contracts and compare C# calls | [Integrated workflow](REFERENCE.md#integrated-tla-and-dafny-workflow) and the [binding example](examples/Integrated/bindings.json). Requires Dafny 4.11.0. |
| Check typed TLA+ safety with SMT | [Apalache setup and analysis](REFERENCE.md#using-apalache-with-tla). Requires Apalache 0.62.4 plus the TLA+ and Java tools. |
| Analyze an asynchronous P model or monitor logs | [P setup](REFERENCE.md#optional-p-prerequisites) and [P workflow](REFERENCE.md#p-analysis-workflow). Requires P 3.1.0 and engine-specific dependencies. |
| Decide which checker fits your problem | [Tool selection guide](REFERENCE.md#choosing-tools-and-checkers), including limits and native test coverage. |

The examples are synthetic demonstrations, not acceptance policies for your system.

## Understand the result

- **Readiness is not verification.** Resolved bindings and a valid plan only
  establish that the requested work is structurally ready.
- **A completed run is not acceptance.** Project `run` can complete successfully
  even when a stage deliberately rejects a negative control. Use `accept` to
  evaluate the requirements.
- **Incomplete is not passed.** Missing tools, unsupported inputs, timeouts
  and partial searches remain explicit.
- **Old evidence is not automatically current.** Archive consistency, current
  applicability and acceptance are separate results.

An agent's summary should make these distinctions visible, not compress
everything into a "verified" badge. In particular, review uncaptured calls,
Task/queue handoffs, cancellation and external effects before interpreting
an execution trace. See [the evidence boundaries](DESIGN.md) for details.

## Documentation and repository layout

- [Reference](REFERENCE.md): detailed setup, commands, annotations, JSON formats,
  evidence and testing.
- [Design](DESIGN.md): architecture and the precise boundaries of each claim.
- [Agent guidance](AGENTS.md): operating workflow and maintenance safeguards.
- [Security](SECURITY.md): vulnerability reporting.
- [Examples](examples): small source/model pairs and complete workflows.
- [Source](src/tlalink) and [tests](tests/TlaLink.Tests): the tool and console suite.

Local backend distributions belong in the ignored `.tools` cache. Private
consumer sources and evidence belong under ignored `local`, which the public
solution does not depend on. To share the public source, use the
[allowlisted export script](scripts/Export-Source.ps1), not a recursive copy
of the working directory.
