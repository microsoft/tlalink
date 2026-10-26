# TlaLink

**Check difficult C# behavior, and keep a reproducible record of what you checked.**

A test can pass simply because it never encounters the wrong ordering of
operations. That makes concurrency bugs hard to rule out: two workers claim
the same job, cancellation arrives at the wrong time, or cleanup races with
work still in progress.

TlaLink helps you investigate those questions using **C# source code, explicit
models of the behavior, and established model checkers and verifiers**. It
connects the results to the selected source files, compiler inputs and tools,
so you can see what a result supports and whether it still applies after a change.
It is a local **C#/.NET 10 command-line tool**, not a replacement compiler,
a bundled coding agent or a hosted AI service.

## Why would I use it?

- **Investigate interleavings that tests may miss.** Build a small model of a
  concurrency protocol and ask whether it can reach an unwanted state under
  the stated bounds and assumptions.
- **Check specific rules.** Verify a supplied algorithm's contracts, run selected
  C# regression cases, or compare selected C# calls with a verified Dafny oracle.
- **Connect a model to a real execution.** Record an instrumented copy of your
  C# code and check whether the complete recorded execution fits a TLA+ model.
- **Make results repeatable and reviewable.** Keep the exact inputs, tool versions,
  outcomes, assumptions and open reviews together, then check whether the
  evidence remains applicable when the code or configuration changes.

TlaLink is most useful when you have a concrete behavioral risk, not just a
request to "verify the codebase." It complements ordinary tests and code review;
it does not replace them.

### A concrete example

Suppose a C# job queue promises that a job can be claimed by only one worker.
You model two workers and the steps for finding and claiming a job, then ask
whether both can claim it. A model checker can explore the modeled orderings
and, if the property fails, show the sequence that breaks the rule.

TlaLink helps link those modeled steps to selected C# operations, run the
check reproducibly, and retain the result for review. You still need to establish
whether the model's steps and assumptions reflect the C# synchronization.
**A correct model does not, by itself, prove that the implementation is correct.**

## What do I get?

A run can produce model counterexamples, proof results, selected C# test outcomes
or a match for a recorded execution, depending on the check you choose. TlaLink
records their scope and inputs rather than calling all of them "verified."
You can generate a [human-readable report](REFERENCE.md#human-readable-evidence-reports)
showing the claims, evidence, assumptions and remaining gaps.

The tool is designed for a coding agent to handle the mechanics while you review
the question and the result. You can also operate the CLI directly.
The aim is to move from **"this looks correct"** to
**"here is what was checked, the evidence, and what remains unproven."**

TlaLink does not automatically translate arbitrary C# into a complete formal
model. Source/model correspondence and coverage need review. A matching trace
covers one finite observed execution; oracle comparisons cover selected cases,
not all C# executions.

## Getting started

Start with the supplied synthetic examples before applying the tool to your
system. If you use a coding agent, open this repository alongside your code
and give it access to the relevant sources and a shell. [AGENTS.md](AGENTS.md)
contains its operating guidance; the [reference](REFERENCE.md) contains setup,
commands and formats. You do not need to learn the CLI before asking the agent
to help scope a question.

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
The [evidence-stage example](examples/Evidence/project.json) combines C#
regressions and standalone Dafny proofs. Acceptance summaries group remaining
work into incomplete stages, review gates, unmet requirements and validation
diagnostics; full receipts retain the complete evidence assessment.
For larger projects, [component manifests](examples/Components/README.md) keep
checks beside their code, share explicit input sets and execution budgets,
and assemble into one project through a small root manifest.
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

## Which tools does it use?

TlaLink coordinates existing tools; it does not replace their parsers, model
checkers or verifiers. You only need the backends required for your chosen check.

| Tool | Role |
|---|---|
| [Roslyn](https://github.com/dotnet/roslyn) | Resolve C# symbols and validate source bindings and capture sites. |
| [SANY / PlusCal](https://github.com/tlaplus/tlaplus) | Parse and resolve TLA+ models and check algorithm translation freshness. |
| [TLC](https://github.com/tlaplus/tlaplus) | Explore configured finite models and match recorded C# executions against TLA+. |
| [Apalache](https://apalache-mc.org/) | Check TLA+ types, bounded safety and supplied-invariant induction. |
| [Dafny](https://dafny.org/) | Verify supplied algorithmic contracts and generate oracles for selected C# comparisons. |
| [P tools](https://p-org.github.io/P/) | Analyze asynchronous state-machine models or check observed logs against monitors. |

The checkers have different guarantees and prerequisites. The native P suite
exercises PChecker; other P engines have adapter/evidence controls, not
end-to-end native coverage. See the [selection guide](REFERENCE.md#choosing-tools-and-checkers).

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

TlaLink itself does not upload your code. If you use a coding agent, its data
handling depends on its own configuration; local tool execution does not
establish the agent's data handling policy.
