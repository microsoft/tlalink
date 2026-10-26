# TlaLink

**An agent-first tool for connecting C# implementations to formal evidence.**

## Overview

TlaLink is a local .NET 10 command-line tool for reviewing C# code against
explicit formal models, running checks, and keeping reproducible evidence.
It brings source/model review, model checking, algorithm verification and
selected C# execution checks into one workflow.

Your coding agent writes the models and checks needed to investigate your
question, then challenges its own work. TlaLink gives that work a checkable foundation: explicit
links to your code, results from established formal-methods tools, and retained
evidence of exactly what was checked.

You work with the agent to define the question and review the assumptions.
The agent operates TlaLink to prepare and run checks, investigate failures and
explain the results in plain English; you do not need to read TLA+, Dafny or P
to use this workflow. The goal is to move from **"the agent says this looks
correct"** to **"here is the evidence, its scope and what remains unproven."**

You can also operate the CLI directly. TlaLink does not include a coding agent
or hosted AI service. Its implementation-facing integrations are designed for
the **C#/.NET toolchain**, with TLA+, Dafny and P as complementary formal backends.

## Features

- **Source/model review:** resolve links between C# symbols and formal
  declarations, and report gaps for review.
- **Model and contract checks:** run established checkers against explicitly
  selected models, properties, bounds and assumptions.
- **Execution checks:** match recorded C# executions against TLA+ models,
  compare selected C# calls with verified Dafny oracles, and run selected
  console-harness regressions.
- **Repeatable projects:** combine checks under independent requirements,
  with component manifests that keep verification inputs beside their code.
- **Reviewable results:** retain exact inputs and tool versions, generate
  [human-readable reports](REFERENCE.md#human-readable-evidence-reports), and
  check whether earlier evidence still applies after changes.

TlaLink complements tests and code review; it does not replace them or
automatically prove that a C# implementation is correct.

## Getting started

### Prerequisites

- Windows x64 or Linux x64.
- The .NET 10 SDK selected by [global.json](global.json).
- PowerShell 7 for repository scripts.

### Build and test

From the repository root:

```powershell
dotnet build .\TlaLink.sln
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . --core
```

The build and core tests need no formal-methods backends. To run an analysis,
follow the [setup instructions](REFERENCE.md#requirements-and-build) for the
checkers it needs. Tools are pinned in the local `.tools` cache, including
the JDK used by Java-backed commands. Setup is explicit: analysis commands
do not automatically download or upgrade tools.

### First steps

If you use a coding agent, open this repository alongside your code and give
it access to the relevant sources and a shell. [AGENTS.md](AGENTS.md) contains
its operating guidance. You do not need to learn the CLI before asking the
agent to help scope a question.

For a first look, have the agent check setup and walk you through the
[small C# example](examples/Demo.cs) and its [TLA+ model](examples/Demo.tla).
This demonstrates source/model review without executing the C#.
For an end-to-end execution check, use the `lanes` scenario in the
[workflow example](examples/Workflow/project.json).

When you bring your own code, start with a concrete concern: "Can cancellation
leak a credit?" is more useful than "Verify this subsystem." Share the relevant
entry points, dependencies and any existing models. You and the agent can then
agree on the smallest useful scope and the evidence needed.

## Usage

### Develop the models with your agent

Start with the behavior you care about and the relevant C# code. The agent
inspects that scope, recommends a suitable checker, and writes or adapts the
models, properties, configurations, source bindings and project manifests.
TlaLink does not generate a complete model automatically from arbitrary C#.

The agent must then check more than whether its model passes. It should
revisit the C# to justify the modeled steps and interleavings, check that
important behavior is reachable, and use meaningful negative controls to
show that the checks detect the failure being investigated. Selected C#
execution checks can provide additional evidence where supported.

You should receive a plain-English account of what is being checked, what
was simplified, which interface guarantees are assumed, and what remains
outside the model. Your role is to clarify the intended behavior and decide
whether those assumptions and limits are acceptable, not to author the formal
model. The [agent instructions](AGENTS.md#model-authoring-and-self-checks)
and [authoring workflow](REFERENCE.md#model-authoring-and-self-checks) define
the agent's responsibilities. When working from another repository, explicitly
point the agent to this guidance; running the CLI does not load it.

### Run and assess the checks

Once the agent has prepared the models and project, it uses the project workflow:

1. **`doctor`: check readiness.** Check the selected scenario's inputs,
   pinned tools and declared check coverage before attempting a run.
2. **`plan`: inspect the work.** Show the checks, their dependencies, execution
   budgets and associated requirements. Review this plan before execution.
3. **`run`: execute the checks.** Run the selected scenario and save its
   results with the exact inputs and tools used. A completed run can include
   expected rejections; it does not mean the requirements have been accepted.
4. **`accept`: evaluate the evidence.** Assess the run against the independent
   requirements and current human review decisions, keeping archive consistency
   and current applicability separate from acceptance.
5. **`explain`: understand the assessment.** Inspect remaining work, such as
   incomplete checks, pending reviews, unmet requirements or stale inputs.

Counterexamples start another development iteration: the agent investigates
whether they expose an implementation defect, a model defect or an unsupported
assumption. It must not weaken a property or hide an interleaving simply to
make the checks pass. Where requirements call for human review, `record-review`
records an actual reviewed decision; agent self-checks do not satisfy that gate.

The [evidence-stage example](examples/Evidence/project.json) combines C#
regressions and standalone Dafny proofs. Acceptance summaries group remaining
work into incomplete stages, review gates, unmet requirements and validation
diagnostics; full receipts retain the complete evidence assessment.
For larger projects, [component manifests](examples/Components/README.md) keep
checks beside their code, share explicit input sets and execution budgets,
and assemble into one project through a small root manifest.
For example, after setting up the pinned TLA+ and Java tools:

```powershell
.\TlaLink.ps1 doctor --project .\examples\Components\tlalink.project.json --scenario complete
.\TlaLink.ps1 plan --project .\examples\Components\tlalink.project.json --scenario complete
```

These commands check readiness and show the plan; they do not execute the
checks or accept their results. See the [project workflow](REFERENCE.md#project-workflow)
for execution, acceptance and reporting commands.

## Examples

| Example | Demonstrates |
|---|---|
| [Demo](examples/Demo.cs) and its [model](examples/Demo.tla) | Reviewing links between C# source and a TLA+ model. |
| [Components](examples/Components/README.md) | A small root project with code-adjacent checks and shared execution profiles. |
| [Workflow](examples/Workflow/project.json) | Repeatable checks, including a recorded-execution check in the `lanes` scenario. |
| [Evidence stages](examples/Evidence/project.json) | C# console regressions and standalone Dafny proofs in one project. |
| [Integrated bindings](examples/Integrated/bindings.json) | Comparing selected C# calls with verified Dafny oracles. |

The examples are synthetic demonstrations, not acceptance policies for your system.

## Supported tools

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

## Limitations

TlaLink does not automatically translate arbitrary C# into a complete formal
model. A model or proof result applies to the supplied formal program; its
correspondence to the C# implementation still needs review. A matching trace
covers one finite observed execution, and oracle comparisons cover selected
cases, not all possible C# executions.

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

## Documentation

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

## License

TlaLink is licensed under the [MIT License](LICENSE).
