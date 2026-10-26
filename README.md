# TlaLink

**Keeps agent-written formal models in sync with your C# code.**

## Overview

Formal methods such as TLA+, Dafny and P can catch concurrency bugs, lost
updates and cleanup mistakes that testing misses. Until recently, writing
those models took specialist skills and a lot of time. Coding agents have
changed that: an agent can now draft a model of the behavior you care about
in a fraction of the time.

The harder problem is keeping a model connected to the code. Code keeps
changing, and a model that passed last month may no longer describe what
the code does today. Without an explicit link, a passing model can give false
confidence.

TlaLink is a local .NET 10 command-line tool that keeps that link explicit and
checkable:

- **Links live in your code.** The agent annotates the C# to show which code
  each model step and variable represents. TlaLink checks that every link
  still resolves to real code and model declarations.
- **Executions are checked against the model.** Where supported, TlaLink runs
  instrumented copies of your code and checks the recorded behavior against
  the model.
- **Evidence knows when it is stale.** TlaLink records exactly which code,
  models and tool versions each result used, and reports when changes mean
  earlier results no longer apply. The agent then updates the model and
  reruns the affected checks.

The goal is to move from **"the agent says this looks correct"** to **"here is
the evidence, its scope and what remains unproven."** You do not need to read
or write TLA+, Dafny or P yourself.

TlaLink does not include an agent or hosted AI service, and it does not upload
your code. It is designed for the **C#/.NET toolchain**.

## How it works

1. **Pick one question.** You describe a concrete concern, such as "Can
   cancellation leak a credit?", and point the agent at the relevant code.
2. **The agent models it and maps it to your code.** It writes a small model
   of just that behavior and annotates the C# to show which code each model
   step and variable represents.
3. **TlaLink checks the links and runs the checkers.** It confirms that the
   annotations resolve to real C# symbols and model declarations, then runs
   the selected checker. Where supported, it also runs instrumented copies of
   your code and checks the recorded executions against the model.
4. **The agent challenges its own work.** It rechecks that the model matches
   the code, that important behavior is actually reachable, and that the
   checks catch the defect being investigated. Counterexamples are
   investigated, not tuned away.
5. **You get a plain-English summary.** It states what was checked, what was
   simplified or assumed, and what is still open. You decide whether that is
   good enough.

The mapping lives in your source as short annotations. For example, this
says the increment under the lock is the model step `Admit`:

```csharp
lock (this.gate)
{
    /* @tla 2
    step Admit justification=lock actor="1" operation="1" point=commit classification=transition observe=active;
    */
    this.active++;
}
```

## What you do, and what the agent does

| You | The agent |
|---|---|
| Choose the behavior worth checking. | Writes the models, properties and code annotations. |
| Explain intended behavior and interface guarantees. | Picks a suitable checker and runs it. |
| Decide whether the stated assumptions and gaps are acceptable. | Checks its model against the code and investigates failures. |
| Record a review decision when a project requires one. | Explains results and limits in plain English. |

You should not need to read the formal models. If a result depends on an
assumption you cannot judge, such as what an interface guarantees, the agent
should ask rather than guess. The agent's own checks never count as your
approval. The [agent guidance](AGENTS.md#model-authoring-and-self-checks)
describes what the agent is expected to do. If your agent runs from another
repository, point it to that guidance explicitly.

## Working with larger code

TlaLink does not model a whole program at once. For a larger system:

- **Use several focused models**, each answering one question.
- **Cut at interfaces.** Model what an interface promises, not the code
  underneath. If the promise is unclear, clarify the interface contract
  rather than relying on implementation details.
- **Keep checks beside their code.** [Component manifests](examples/Components/README.md)
  let each part of the codebase own its checks, combined into one project.

Combining checks in one project is for organization and repeatability. It
does not prove that the separate results add up to a system-wide guarantee.
Larger projects also take real effort: some searches may hit their limits,
and some questions may stay open for review.

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

These need no formal-methods tools. The checkers themselves are set up
separately, only when you need them; see the
[setup instructions](REFERENCE.md#requirements-and-build).

### First steps

Open this repository alongside your code in your coding agent, and give it
access to the relevant sources and a shell. Then ask it to:

1. Check setup and walk you through the [small C# example](examples/Demo.cs)
   and its [TLA+ model](examples/Demo.tla).
2. Run the `lanes` scenario in the [workflow example](examples/Workflow/project.json),
   which checks a recorded C# execution against a model.
3. Help you choose one concrete question about your own code.

## Examples

| Example | Shows |
|---|---|
| [Demo](examples/Demo.cs) and its [model](examples/Demo.tla) | Linking C# code to a TLA+ model. |
| [Workflow](examples/Workflow/project.json) | A repeatable project, including a recorded-execution check. |
| [Components](examples/Components/README.md) | Checks kept beside their code and combined into one project. |
| [Evidence stages](examples/Evidence/project.json) | C# regressions and Dafny proofs in one project. |
| [Integrated bindings](examples/Integrated/bindings.json) | Comparing C# calls with verified Dafny results. |

The examples are synthetic demonstrations, not policies for your system.

## Supported tools

TlaLink runs existing, established tools rather than replacing them. You only
need the ones your chosen checks use.

| Tool | Used for |
|---|---|
| [Roslyn](https://github.com/dotnet/roslyn) | Resolving the C# side of the links. |
| [TLA+ tools](https://github.com/tlaplus/tlaplus) (SANY, PlusCal, TLC) | Parsing TLA+ models, exploring their states and checking recorded executions. |
| [Apalache](https://apalache-mc.org/) | Symbolic checking of TLA+ safety properties. |
| [Dafny](https://dafny.org/) | Verifying algorithms and comparing selected C# calls with verified results. |
| [P](https://p-org.github.io/P/) | Checking asynchronous state-machine models and logs. |

Each checker gives a different kind of guarantee; the
[selection guide](REFERENCE.md#choosing-tools-and-checkers) compares them.

## Limitations

- **A passing model is not proof that the C# is correct.** Checkers verify
  the model. How well the model matches the code depends on the mapping,
  the agent's checks and any reviews.
- **Linked does not mean correct.** TlaLink confirms that annotations point at
  real code and model elements, not that they describe the code accurately.
- **Execution checks cover what ran.** A recorded execution that matches the
  model shows that one run fits; it does not cover every possible run.
- **Incomplete is not passed.** Timeouts, missing tools and partial searches
  are reported as incomplete.
- **Results can go stale.** When code or models change, TlaLink reports
  whether earlier evidence still applies.

A good summary keeps these distinctions visible rather than reporting a
single "verified" badge. See the [design](DESIGN.md) for the precise limits
of each kind of check.

## Documentation

- [Reference](REFERENCE.md): setup, commands, annotations, file formats and testing.
- [Design](DESIGN.md): architecture and what each kind of evidence does and does not show.
- [Agent guidance](AGENTS.md): how agents should build models and operate the tool.
- [Security](SECURITY.md): reporting vulnerabilities.

If you use a coding agent, its own data handling depends on its configuration.

## License

TlaLink is licensed under the [MIT License](LICENSE).
