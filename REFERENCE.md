# TlaLink reference

For an introduction and first run, start with [the README](README.md).
This reference supplies detailed setup, commands, formats and maintenance
guidance for agents operating the tool and people inspecting or running it.
Run shell examples from the repository root; relative Markdown links are also
root-relative because this document lives beside the README.

## Contents

- [Overview](#overview)
- [Human-readable evidence reports](#human-readable-evidence-reports)
- [Requirements and build](#requirements-and-build)
- [Workspace and publication boundary](#workspace-and-publication-boundary)
- [Continuous integration](#continuous-integration)
- [Optional P prerequisites](#optional-p-prerequisites)
- [Source organization](#source-organization)
- [Model authoring and self-checks](#model-authoring-and-self-checks)
- [Choosing tools and checkers](#choosing-tools-and-checkers)
- [Apalache setup and analysis](#using-apalache-with-tla)
- [P analysis workflow](#p-analysis-workflow)
- [Project workflow and acceptance](#project-workflow)
- [Evidence retention and recheck planning](#evidence-retention-and-recheck-planning)
- [Integrated TLA+ and Dafny workflow](#integrated-tla-and-dafny-workflow)
- [Review commands](#review-commands)
- [Current C# source inventory](#current-c-source-inventory)
- [Preparing annotated source copies](#preparing-annotated-source-copies)
- [Source annotation grammar](#one-maintained-markup-link)
- [Instrumentation and trace validation](#instrument-execute-validate)
- [Tests and evidence](#tests-and-evidence)

## Overview

TlaLink is a C#/.NET 10 CLI for turning a scoped behavior question into repeatable
checks and inspectable evidence. It combines source/model correspondence review,
explicit native analyses, selected C# regressions, recorded-execution checks
against TLA+, and C# adapter comparisons with verified Dafny oracles.
You can run it directly or have a coding agent operate it; it is not a bundled
agent or hosted AI service. Its implementation-facing integrations target the C#/.NET toolchain:
Roslyn source resolution, explicit compiler context, compiled adapters and
build/execution provenance. TLA+, Dafny and P are complementary formal backends,
not support for arbitrary implementation languages.
All maintained public JSON documents use **schema 4** and a document
`kind`; older formats are explicitly rejected, not silently upgraded.

- Roslyn supplies C# syntax, symbols, field accesses and capture validation.
- The upstream Java SANY/PlusCal parsers remain authoritative for TLA+,
  explicit labels, imports and translation freshness.
- TLC, not a new interpreter, checks a whole finite recorded execution.
- Explicitly selected Apalache 0.62.4 checks typed TLA+ safety models, through a
  stated transition bound or all three supplied-invariant induction obligations.
- The selected local Dafny 4.11.0 frontend resolves Dafny declarations/includes;
  its verifier, assumption auditor and C# compiler remain authoritative.
- The selected local P 3.1.0 frontend resolves P declarations. PChecker, PEx,
  PSym, PVerifier and PObserve retain separate native evidence claims; P is
  not routed through Dafny or interpreted as TLA+.

**Neither structural links, matching traces nor passing oracle tests prove all
C# executions, atomicity, general refinement or liveness. Dafny verification
proves the supplied Dafny program, not the linked C#.** See [DESIGN.md](DESIGN.md) for the
precise evidence boundary and [AGENTS.md](AGENTS.md) for the agent workflow.

## Human-readable evidence reports

```powershell
.\TlaLink.ps1 report --manifest .\evidence-report.json --output .\assurance.md --format summary
```

`report` generates one local Markdown document with Mermaid overview and
complete dependency diagrams, grouped evidence tables, linked source-region
dispositions, assumptions, gaps and current input applicability. The output
must be new; existing files are never overwritten. Mermaid rendering is done
by the reader's Markdown viewer, not by an online service or the report CLI.
`--format json|text|summary` selects the CLI result presentation, not a second
report format.

The strict manifest uses schema 4 and kind `evidence-report`:

```json
{
  "schema_version": 4,
  "kind": "evidence-report",
  "title": "Counter evidence",
  "scope": "Selected counter tests and source coverage, not general refinement.",
  "fingerprints": {
    "Counter.cs": "<64-hex SHA256 of the approved source bytes>",
    "checks\\result.json": "<64-hex SHA256 of the selected evidence bytes>"
  },
  "nodes": [{
    "id": "counter-tests",
    "kind": "test",
    "title": "Selected counter cases",
    "claim": "Recorded selected test cases; not all executions.",
    "recorded_status": "passed",
    "expected_status": "passed",
    "depends_on": [],
    "evidence": ["Counter.cs", "checks\\result.json"],
    "assumptions": ["The producing test host is trusted."],
    "gaps": ["Concurrent operations remain outside this test scope."]
  }],
  "code_regions": [{
    "file": "Counter.cs", "line": 3, "end_line": 7,
    "symbol": "Counter.Value", "profile": "selected source",
    "disposition": "selected-test-coverage",
    "contracts": ["Counter test cases"],
    "reason": "Authored coverage assignment, not a per-method proof."
  }],
  "assumptions": [],
  "gaps": ["Source/model correspondence remains a separate review."]
}
```

Replace the illustrative hash placeholders with the independently approved
hashes, not whatever hashes happen to clear a stale check. All declared
evidence/source paths must be pinned. Paths are manifest-relative; CLI paths
are caller-relative. IDs follow the shared stable-ID rules. Dependencies must
resolve, be unique and form an acyclic graph. Supported node kinds are `code`,
`check`, `proof`, `test`, `trace`, `review`, `requirement`, `milestone`, `audit`.
`expected_status` may be null for historical or coverage-only statements.
Recorded/expected comparisons are ordinal and exact, not a shared proof flag.

Exit 0 means a structurally valid report of a byte-current snapshot was
published, **not that its claims passed or its milestones were accepted**.
Changed/missing pinned inputs produce an explicitly stale report and exit 2
(`RP002`); no node receives a current green match. Invalid manifests are
`RP001`; a snapshot change during generation is `RP003` and prevents
publication. The CLI emits schema-4 `report-result` with node/region/input
counts. Unknown/duplicate JSON properties are rejected.

Adapters must retain native guarantee boundaries and explicit review status;
the generic renderer does not independently validate backend semantics.
Expected rejection controls stay rejected even when matching expectations.
Historical claims and assigned code coverage are not new acceptance.
Reports/manifests are not automatic GC roots: retain required underlying
evidence through the separate explicit retention/recheck workflow.

### Source inventory presentation

Instead of maintaining a script that enumerates `code_regions`, an
`evidence-report` can declare `source_inventories`:

```json
"source_inventories": [{
  "inventory": "inventory.json",
  "dispositions": [
    {
      "file": "Counter.cs", "profile": "selected C#",
      "disposition": "inventory-only", "contracts": [],
      "reason": "Authored scope, not per-member validation."
    },
    {
      "file": "Counter.cs", "symbol_prefix": "M:Counter.Helper",
      "profile": "selected C#", "disposition": "excluded",
      "contracts": [], "reason": "Explicit authored exclusion."
    }
  ]
}]
```

Pin the inventory and every selected source in `fingerprints`. The inventory
must be ready and list its exact sources; those source hashes must agree with
the report's approved pins. Every source needs exactly one default disposition.
Optional symbol prefixes compare ordinally, must match a member and must not
overlap. The renderer expands recorded member spans; a file with no explicit
members receives a line-1 file disposition. Missing defaults, stale policy
prefixes and ambiguous overrides fail rather than manufacturing coverage.
Changed inventory bytes produce a stale report without expanding untrusted
replacement regions; changed source bytes retain stale presentation status.
Neither expansion nor a disposition establishes tested or validated code.

## Requirements and build

Windows x64 and Linux x64 are supported. Building and running core tests need
the .NET 10 SDK/runtime; repository scripts need PowerShell 7 (`pwsh` on Linux).

```powershell
dotnet build .\TlaLink.sln
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . --core
```

Provision only the backends selected for the workflow. Java-backed commands
require the pinned local Temurin JDK **25.0.4.1+1**; SANY/PlusCal and TLC also
require the pinned TLA tools JAR. Provision Java explicitly before analysis:

```powershell
.\scripts\Initialize-Java.ps1
# Offline alternative: supply the exact pinned distribution archive.
.\scripts\Initialize-Java.ps1 -Archive C:\downloads\OpenJDK25U-jdk_x64_windows_hotspot_25.0.4.1_1.zip
```

[java-toolchain.json](java-toolchain.json) pins the upstream archive URL/SHA256
and per-platform release/launcher hashes. Setup verifies the archive and installs
it into `.tools\java\win-x64\jdk-25.0.4.1+1` or the corresponding
`linux-x64` directory; it never changes machine-wide Java. Repeated setup
checks an existing installation and refuses changed pinned files rather than
overwriting it. The CLI never downloads or updates Java.

SANY/PlusCal, TLC and Java-backed tests select both Java launchers
from this one JDK, independently of the caller's directory, `PATH` and
`JAVA_HOME`. `TLALINK_JAVA_HOME` may select an absolute relocated copy of the
**same pinned distribution**, not another Java version or vendor. A deployed
binary outside the repository requires that explicit override. Missing or
changed pinned files fail explicitly; there is no system-Java fallback.
Selection checks release and launcher bytes, not every installed JDK file;
the archive checksum establishes the distribution at provisioning time.
These inputs are also retained in Java-backed analysis fingerprints.
Explicit native P manifests continue to select their own Java dependencies.

Download the pinned `tla2tools.jar` into `.tools\tla\v1.8.0` and verify its SHA256 before using it:
`D15655EEAFF4B3F7B3D65C46B838BC0F35D131B2EF4B1E473689B62C98D4726F`.
It is the TLA+ 1.8.0 rolling build of upstream commit `2ef2af8`. Upstream replaces its
[v1.8.0 release](https://github.com/tlaplus/tlaplus/releases/tag/v1.8.0) asset in place
on each rolling build, so this repository keeps an unchanging
[mirror of the pinned file](https://github.com/microsoft/tlalink/releases/tag/tla2tools-2ef2af8)
with its MIT license. A different TLA+ build requires a new mirror tag and explicitly
updated pins, never a replaced asset.

```powershell
Invoke-WebRequest https://github.com/microsoft/tlalink/releases/download/tla2tools-2ef2af8/tla2tools.jar -OutFile .\.tools\tla\v1.8.0\tla2tools.jar
Get-FileHash .\.tools\tla\v1.8.0\tla2tools.jar -Algorithm SHA256
```

The full console suite needs the native TLA+, Apalache, Dafny and P prerequisites
described below; do not run it as a backend-free installation check.

[global.json](global.json) selects SDK 10.0.204 with feature-band roll-forward.
There are no third-party NuGet dependencies: Roslyn references come from the
selected SDK. [NuGet.Config](NuGet.Config) retains the approved package proxy.
No product-tree build is involved. TlaLink does not upload code; any surrounding
agent or editor has its own data handling policy.

## Workspace and publication boundary

Consumer repositories should separate maintained product sources, models, proofs,
harness projects, requirements and append-only review decisions from ignored
tool caches, scratch builds and immutable execution archives. Use document-relative
references in maintained inputs; runtime receipts retain observed absolute
identities. A root project manifest can contain source and verification scopes
under one declaring directory. Restoring old archives does not by itself establish
current applicability after relocation; do not rewrite their original provenance.
The tool repository's ignored `local` remains private and is never exported.

Open [TlaLink.sln](TlaLink.sln) for the public tool and standalone console tests,
or [TlaLink.code-workspace](TlaLink.code-workspace) to browse the whole folder
in VS Code, including ignored local work.

- `src`, `tests` and `examples` contain maintained generic code and synthetic
  inputs. They have no dependency on private work packages.
- `local` is a Git-ignored home for private consumer projects, copied sources,
  proofs and evidence. It is not part of the public solution.
- `.tools` contains ignored local backend distributions, separate from work.
- Historical implementations, generated evidence and local environments are
  not public source inputs.

Export public source using the explicit allowlist, not a recursive ZIP of the
working directory:

```powershell
.\scripts\Export-Source.ps1 -Destination C:\temp\TlaLink-source
```

The destination must be new and outside this working tree. The export excludes
private work, tools, build output, evidence, caches and Git history. It is a
source bundle, not legal approval or a binary release.

The current example/cache convention is `.tools\tla\v1.8.0\tla2tools.jar`,
`.tools\dafny\Dafny.exe` and the P local-tool installation below. Tests accept
`TLALINK_TLA_JAR`, `TLALINK_DAFNY`, `TLALINK_P_COMPILER` and
`TLALINK_APALACHE_JAR` overrides.
On Linux the Dafny launcher is `.tools\dafny\dafny`; P's .NET 8 runtime
must also be installed for native P tests.
Missing prerequisites fail explicitly. For backend-independent tests:

```powershell
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . --core
```

Focused selections can also save the same full JSON report as the complete
suite, without running unrelated backends:

```powershell
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . .\Results\workflow-tests-new.json --workflow
```

The report path and selector may appear in either order after the repository
root. Unknown or repeated selectors fail with exit 2 rather than silently
starting the full suite. Report files are exclusive new outputs.

Backend provisioning is explicit; analysis commands do not install tools.
The Java setup script provisions only the pinned Java distribution.

## Continuous integration

[GitHub Actions](.github/workflows/ci.yml) runs on pull requests, pushes to
`main` and manual dispatches using Ubuntu 24.04 x64. It builds Release with the
.NET SDK selected by `global.json`, runs backend-independent controls, then
explicitly provisions the pinned Java, TLC 1.8.0, Dafny 4.11.0 and P 3.1.0 tools
before running the complete console suite. The .NET 8 SDK supplies P's runtime.
Apalache 0.62.4 is also explicitly provisioned for native safety/induction controls.
Archive downloads are checksum-verified; third-party actions use commit pins.
The TLA+ tools JAR comes from this repository's pinned mirror release, because the
upstream rolling release replaces its asset in place.
Missing tools, build errors and failed controls fail the job rather than skip
native tests. Console logs and the full suite's JSON report are retained for
seven days, including on failure when available.

Native P coverage exercises PChecker; the other P engines have classifier and
evidence controls, not automatically provisioned solver/monitor distributions.
CI setup is separate from analysis: analysis commands still never install or
upgrade backend tools.

Paths accept either separator at input boundaries; JSON output and internal
absolute paths use the host's native separator. Relative paths remain relative
to the caller or declaring document, as appropriate. File identity is ordinal
case-insensitive on Windows and ordinal on Linux; foreign drive/UNC roots are
not reinterpreted as local relative paths. Windows-style relative source paths
work on Linux through this input normalization; launcher filenames and tool
hashes remain platform-specific and must be selected explicitly in project
manifests. Shell examples below use PowerShell.
Approved configuration bytes are preserved across checkouts with explicit
`.gitattributes` rules; CI does not rewrite acceptance pins for its host.

## Optional P prerequisites

P commands additionally require a complete local P 3.1.0 distribution.
The tool does not install or update it. To prepare the optional local tool:

```powershell
dotnet tool install P --version 3.1.0 --tool-path .\.tools\p --configfile .\NuGet.Config
```

The native P compiler's generated C# build restores its own dependencies.
Each P run copies and pins the deployed approved NuGet configuration. PEx/PSym JARs,
PObserve runtime/monitor/parser JARs and verifier dependencies are explicit
separately pinned inputs; they are not downloaded during an analysis.
The full suite includes native P tests; set `TLALINK_P_COMPILER` to an absolute
`p.dll` path when not using the example local installation.

[TlaLink.ps1](TlaLink.ps1) builds quietly before invoking the CLI, so it cannot
accidentally use a stale binary. Paths passed to it remain relative to the
caller's directory. Alternatively, after a build, invoke the compiled DLL with
`dotnet` directly.

## Source organization

The single executable uses the public `TlaLink` namespace. Sources under
`src\tlalink` are grouped by responsibility:

| Directory | Responsibility |
|---|---|
| `Common` | C# frontend, diagnostics, capture/execution, integrated evidence and workflow/acceptance |
| `PLang` | Native P frontend, bindings, backend execution, result classification and evidence reader |
| `Tla` | SANY model scope, source annotations, structural review and shared upstream Java adapter |
| `Tlc` | Native TLC workflow and causal whole-execution matching |
| `Apalache` | Native typechecking, bounded/inductive safety and independent evidence reading |
| `PlusCal` | Algorithm discovery and translation-freshness diagnostics in the shared TLA frontend |
| `Dafny` | Native Dafny frontend, contracts, verification/audit and executable oracles |

`Program.cs` is the composition root. Native bridge source assets deploy under
flat names. See [the architecture](DESIGN.md#architecture) for component ownership
and evidence boundaries.
Tool selection follows the obligation, modeling fit, available dependencies and
required evidence, rather than a fixed tool priority or reuse of an existing model.

## Model authoring and self-checks

The agent authors the formal artifacts and performs the technical review.
The user supplies intent and reviews an explanation of the assumptions, scope
and remaining risks; routine use does not require reading the formal language.
Use this procedure alongside the [agent safeguards](AGENTS.md). It is an
authoring discipline, not an additional enforced CLI gate or an automatic
source-to-model translation.

1. **Define the question before writing the model.** Identify the C# source
   scope, the safety/deadlock/progress obligation, the intended evidence and
   independent acceptance requirements. Choose a checker by
   [modeling fit and claim](#choosing-tools-and-checkers), not convenience.
   Inspect existing models for applicability rather than assuming they apply.
2. **Make the abstraction explicit.** Describe state, data domains, initial
   conditions, transitions, environmental behavior and excluded effects.
   Map these to the relevant C# operations, including competing calls, awaits,
   cancellation, exceptions and cleanup. Justify atomic steps using actual
   synchronization or explicit contract guarantees, not statement proximity.
   If the scope ends at an interface, model its specified guarantees rather
   than relying on the concrete/native implementation beneath it. Propose
   clarification of an underspecified contract instead of assuming a guarantee.
3. **Specify properties and limits independently of the result.** State
   bounds, fairness and environmental assumptions, and distinguish safety,
   deadlock and progress. Account for C# arithmetic, ordering and identity
   semantics where relevant. Reduce domains, omit data or use symmetry only
   with a justification that preserves the behavior relevant to the obligation;
   explain what the reduction does not cover. Never eliminate a race by
   serializing operations that can overlap in C#.
4. **Challenge the model before trusting a pass.** Use authoritative native
   frontends and supported checker profiles. Check that initialization is
   feasible and intended operations and checked conditions are reachable;
   a property guarded by an unreachable condition is not useful evidence.
   Include expected-valid behavior and a negative control representing the
   motivating defect, such as a copied model with a relevant guard removed.
   Require the expected native behavioral rejection, not merely any nonzero
   exit. Invalid syntax, unsupported inputs and incomplete searches do not
   demonstrate defect detection. Record which controls actually ran.
5. **Cross-check against the implementation.** Re-read the relevant C# with
   the completed abstraction in view: what behavior does the model accidentally
   forbid, and what required behavior or failure path is missing? Review the
   source/model mapping separately from the passing property. Use supported
   recorded-execution, oracle or regression checks when they address the
   question, and disclose unobserved paths and causal edges. These checks
   supplement correspondence reasoning; none automatically proves refinement.
6. **Run, diagnose and iterate reproducibly.** Keep maintained models,
   configurations, bindings and explicit project declarations under version
   control, near their code where appropriate. Keep requirements independent
   of execution. Use the [project workflow](#project-workflow) to retain exact
   inputs and native outcomes. Classify counterexamples as possible C# defects,
   abstraction/property defects or assumption gaps before making changes.
   Recheck affected controls and correspondence after changes; changed bounds
   or assumptions change the claim. Do not rewrite historical evidence or
   weaken approved requirements just to obtain acceptance.

Keep the abstraction rationale and control findings in existing model comments,
project review material and evidence references, not a parallel manifest format.
The agent's plain-English handoff must identify the claim, source coverage,
assumptions, reductions, actual checker/control outcomes and outstanding gaps.
Separate archive consistency, current applicability and acceptance. A second
reading by an agent is still agent reasoning, not independent human approval;
where requirements call for human or specialist review, leave those gates
pending until the actual review occurs. TlaLink checks supported structures,
pins and native evidence, not the completeness of the agent's abstraction.

## Choosing tools and checkers

Select the engine explicitly for the obligation; the framework never silently
switches engines or weakens requirements when a tool is missing or slow. Start
with the question and the smallest suitable model, not a preferred language.
The following describes the integrations available here, not every upstream
capability:

| Tool / checker | Useful when | Limits and evidence boundary |
|---|---|---|
| Roslyn | Resolve C# symbols, accesses, bindings and capture sites | Requires explicit sources/references/defines. Structural resolution does not establish atomicity or behavior. |
| SANY / PlusCal | Resolve TLA+ modules/operators and author labeled algorithms translated to TLA+ | Authoritative parsing and translation freshness, not a behavioral proof. PlusCal still needs a checker for the generated model. |
| TLC exploration | Explore a configured finite state graph, check invariants, deadlocks and selected temporal properties | Explicit states make debugging concrete but can cause state explosion. Exhaustion is scoped to the configuration and subject to TLC fingerprint omission risk; bounded or incomplete searches are not exhaustion. |
| TLC trace matching | Check whether one instrumented C# execution has a causal whole-model witness | Requires executable bindings, an execution receipt and explicit hidden-step assumptions. A finite observed prefix does not prove all C# executions or liveness. |
| Apalache typechecking | Catch inconsistent model data types before behavioral analysis | Requires native type annotations where inference is insufficient. Successful typing is not safety. |
| Apalache bounded safety | Search typed TLA+ safety models through an explicit transition bound, without enumerating every concrete state | SMT encodings can help with data combinations but solver cost is model-dependent. A pass is only bounded safety; this integration excludes temporal checking, simulation and trace matching. |
| Apalache induction | Establish initiation, preservation and safety implication for a supplied invariant | Can establish safety beyond a search bound, but requires a sufficiently strong typed/domain-constrained invariant and all three feasible obligations. Limited to the selected constants and supported profile, not arbitrary parameters or C# refinement. |
| Dafny verification / assumption audit | Verify supplied algorithmic contracts, loop invariants and lemmas, and inspect trusted assumptions | Deductive reasoning can avoid state enumeration but needs proof annotations and solver support. Verification establishes the scoped Dafny program under its assumptions, not linked C#. |
| Dafny executable oracle comparisons | Compare concrete C# adapter calls with upstream-compiled, verified Dafny behavior | Useful executable examples, but only for selected cases and supported synchronous int/bool adapters. Passing comparisons are not C# verification or exhaustive testing. |
| P frontend | Resolve state machines, events and qualified P/C# correspondence bindings | Native declarations and structural links only; no schedule exploration or automatic execution of the linked C#. |
| PChecker | Explore explicitly budgeted schedules of an asynchronous P model, with reproducible seeds | Often useful for finding interleaving bugs. Schedule sampling is not finite exhaustion; step limits and deadlines leave checks incomplete. |
| PEx | Exhaust a finite P model using an explicitly reviewed compiled JAR | Can establish finite-model exhaustion, but has state-space/resource costs and needs pinned Java/build dependencies. The unpinned upstream Maven update path is not run here. |
| PSym | Exhaust a supported symbolic P model with a compatible legacy runtime/artifact | Symbolic representation can reduce enumeration, but this integration targets the PSym 2.4.0 runtime. P 3.1.0 cannot compile that legacy artifact; an externally reviewed pinned build is required. |
| PVerifier | Establish supplied P invariants by induction with native proof obligations | Avoids a finite schedule sample, but needs UCLID5, a pinned solver/runtime closure and suitable invariants. Unresolved goals are incomplete. |
| PObserve | Check selected properties against an explicit nonempty log using reviewed monitor/parser artifacts | Useful for observations of real executions, but depends on log coverage, parsing and ordering assumptions. Monitoring is not exhaustive exploration or TLC causal trace inclusion. |

Maintained native tests exercise SANY/PlusCal, TLC, Apalache, Dafny and
PChecker. PEx, PSym, PVerifier and PObserve have result-classification and
evidence controls; their external solver/monitor distributions and end-to-end
native runs are not provisioned by the suite or CI. A configured adapter is
not a claim that every native dependency has been exercised.

Compare runtime and modeling/proof effort on the actual obligation when more
than one checker fits. Typechecking, sampled schedules, bounded safety, finite
exhaustion, induction, oracle comparisons and log monitoring are distinct
claims. Keep the selected configuration, assumptions, bounds and independent
acceptance requirements explicit; one claim cannot silently substitute for another.

### Using Apalache with TLA+

Apalache uses the same TLA+ language, not a simpler replacement language.
Variables/constants need upstream `@type:` comments; many operator types are
inferred by Snowcat. These are independent of `@link` source correspondence.
A bounded pass is not TLC exhaustion or an inductive proof. Inductive success
is scoped to the selected constants/domains, assumptions and supported native
encoding, not all C# executions or arbitrary model parameters.

### Local Apalache prerequisite

Provision the complete trusted [Apalache 0.62.4 distribution](https://github.com/apalache-mc/apalache/releases/tag/v0.62.4)
explicitly under `.tools\apalache\0.62.4`, containing `lib\apalache.jar`.
The versioned ZIP SHA256 is
`3B6B7EBF24E778535BF47E0CB3F0799C9AB9048DF41119C7D3091C6CA1760BC1`;
the JAR SHA256 is
`E5223915507B65CC5F1638BD037EDE60F8A4FC1644AFF46355779F9B05DF9262`.
Analysis never downloads or updates tools. The backend uses the existing
pinned Java 25 toolchain directly, not the distribution's shell launcher.
It fingerprints/archives the distribution and selected inputs, isolates native
JSON configuration and child environment, and uses an isolated JVM home so a
user's telemetry opt-in and global configuration are not inherited.
SANY indexing and PlusCal freshness still require the pinned TLA tools JAR.
Tests accept `TLALINK_APALACHE_JAR` for an alternative absolute local
`lib\apalache.jar` location.

```powershell
$apalache = ".\.tools\apalache\0.62.4\lib\apalache.jar"
$tla = ".\.tools\tla\v1.8.0\tla2tools.jar"
.\TlaLink.ps1 check-apalache --config .\examples\Apalache\Credit\inductive.json --apalache $apalache --jar $tla --output .\Results\apalache-induction --timeout 60
.\TlaLink.ps1 read-apalache --result .\Results\apalache-induction\result.json --current true
.\TlaLink.ps1 doctor --project .\examples\Apalache\tlalink.project.json
.\TlaLink.ps1 plan --project .\examples\Apalache\tlalink.project.json --scenario credit
.\TlaLink.ps1 run --project .\examples\Apalache\tlalink.project.json --scenario credit --output .\Results\apalache-credit
.\TlaLink.ps1 accept --project .\examples\Apalache\tlalink.project.json --run .\Results\apalache-credit
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . --apalache
```

### Manifest, claims and limits

The strict schema-4 `apalache-analysis` manifest selects `model`, named nullary
`init`/`next` predicates, `properties`, explicit review `assumptions`, optional
`const_init`, and exactly one mode:

| Manifest mode / project stage | Additional fields | Successful claim |
|---|---|---|
| `typecheck` / `apalache-typecheck` | Empty properties, no length/invariant/constant initializer | `well-typed-model` |
| `bounded` / `apalache-check` | Explicit `length` (0..10000), nonempty properties | `bounded-safety` |
| `inductive` / `apalache-inductive` | Named `invariant`, nonempty properties, no length | `inductive-invariants` |

Induction checks `Init => Inv`, `Inv /\ Next => Inv'`, and `Inv => Safety`,
with the same selected constant initializer in each fresh native process.
Include the necessary type/domain constraints in `Inv`. Failed induction may
show an unreachable state outside an insufficiently strong invariant; it is
not automatically a reachable safety bug.
Because preservation initializes from `Inv`, its domains must also support
native state generation. A bound on `Len(sequence)` alone does not assign the
sequence; use an explicit finite sequence domain for a fixed tiny configuration.
Snowcat typing does not establish that these initialization constraints execute.

The integration uses the local incremental Z3/oopsla19 encoding and
accepts state-level safety/candidate predicates only (SANY checks their levels).
It does not accept arbitrary native arguments, TLC configuration files,
simulation, temporal properties, or Apalache trace matching. SANY-resolved
`ASSUME` declarations, including imports, are rejected rather than silently
reinterpreted. Encode executable constraints in the selected predicates;
the manifest's assumption descriptions are review context, not solver input.
Project `doctor`, `plan` and `run` preflight use the same SANY compatibility
checks as native execution: fresh parsing, imported assumptions, selected
nullary predicate levels and unambiguous module basenames. Assumption errors
identify the declaring modules. Preflight pins the transitive model closure,
but does not run Snowcat or establish safety; use a typecheck stage for native
typing. Apalache-only readiness still makes no deadlock or progress claim;
required deadlock/temporal obligations need explicitly selected TLC checks.
For those TLC stages, a completed native deadlock (`TLC2114`) is a rejection,
not an incomplete tool run; negative requirements can pin that exact diagnostic.
TLC stage diagnostics include bounded excerpts of native error messages and
locations, including nested exception messages; complete output remains in `tlc.log`.
Apalache itself does not check deadlock freedom. A successful safety query must
also produce a
native example with exactly `length + 1` states (including its initial state);
this blocks vacuous success from unsatisfiable initialization. Models whose
executions all terminate before the requested bound are conservatively
incomplete; explicitly model intended terminal stuttering or use TLC when
finite exhaustion is the obligation. This feasibility check also applies to
each induction query, so vacuous preservation is not accepted.
Native failure summaries retain the query, last native pass, process exit/deadline
and recognized native error text (including source locations); complete stdout,
stderr and counterexamples remain archived. Summaries do not change classification.
Unknown output, empty initialization, timeouts and unsupported evaluation
remain incomplete, even if native exit is zero. A property rejection requires
native error completion and a nonempty ITF violation trace, not an example
trace; type errors cannot satisfy a negative property control.

Project stages use `tools.apalache` and `tools.jar`, take their scope only
from `config`, and pin its approved bytes through `config_sha256` requirements.
Requirements select the exact successful claim, or `not-established` plus
`AP100` (safety counterexample) / `AP101` (failed induction) for a deliberate
negative control. Archive consistency, current applicability and independent
acceptance remain separate; `read-apalache` reports the native analysis status
without treating archive validity as a property pass.

The focused native suite shares synthetic model setup and reuses its successful
bounded run for reader controls, not for changed-input analyses. Console tests
report per-test elapsed times; Apalache receipts also retain per-process times.
Native checks remain fresh processes. Large tool fingerprints are streamed
without allocating a tool-sized byte array.

All test selections share one startup hash cache for explicitly selected,
unchanged tool distributions, Java pins and deployed tool/Roslyn assemblies.
Workflow subprocesses use an explicit test host to inherit that snapshot;
the production CLI has no cache flag or environment-variable bypass.
Read leases prevent ordinary writes/replacement on Windows. On Unix the
harness additionally rehashes the originals before reporting success, because
other writers need not honor sharing locks. Treat selected tool installations
as immutable for the whole test session; tests that change tools must use copies.
Sources, configurations, generated output, archived tool copies and other
mutation/drift fixtures are not cached and still use current bytes.
Logs report startup cost and in-process cache hits; this removes repeated
hashing, not native JVM/solver startup or evidence archive copying.
Use `--core` for quick backend-independent feedback; it needs no backend tools.
Every native safety query already includes Snowcat typechecking, so a separate
typecheck stage is optional rather than required setup for checking. Keep it
when its fast, independently reported feedback is useful; omit redundant
typecheck-only stages in a latency-sensitive workflow.

## P analysis workflow

Select the distribution's actual `p.dll`, not a global tool shim. The bridge
checks the exact compiler/frontend version and runs parsing/typechecking in a
fresh process. Models are explicit `.p` files/directories; implicit `.pproj`
discovery, foreign implementation files and handwritten P parsing are not used.

```powershell
$p = ".\.tools\p\.store\p\3.1.0\p\3.1.0\tools\net8.0\any\p.dll"
.\TlaLink.ps1 inspect-p --config .\examples\P\credit.json --p $p --output .\Results\p-links
.\TlaLink.ps1 check-p --config .\examples\P\credit.json --p $p --output .\Results\p-credit --timeout 60
.\TlaLink.ps1 read-p --result .\Results\p-credit\result.json --current true
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . --p
```

The strict `p-analysis` manifest selects an engine, explicit P/C# inputs,
a correspondence `scope`, binding IDs, assumptions and exploration bounds.
Inline C# bindings name qualified native P endpoints such as
`credit-p::Credit.ReturnCredit`; the manifest does not duplicate those endpoints.
A binding is a reviewable
correspondence, not an executable oracle comparison or refinement proof.
Native testcase selection must resolve exactly, avoiding PChecker's
otherwise implicit prefix expansion.

| Engine / project stage | Successful claim | Additional requirements |
|---|---|---|
| `inspect` / `p-inspect` | `structural-links` | Native resolved inventory; no exploration |
| `pchecker` / `p-pchecker` | `explored-schedules` | Explicit testcase, schedule count, step bound and seed |
| `pex` / `p-pex` | `exhaustive-finite-model` | Pinned compiled `artifact` JAR and Java 17+ dependency |
| `psym` / `p-psym` | `exhaustive-symbolic-model` | Pinned PSym 2.4.0-runtime `artifact` JAR and compatible Java dependency |
| `pverifier` / `p-pverifier` | `inductive-invariants` | Pinned UCLID5 and solver/runtime dependency closure |
| `pobserve` / `p-pobserve` | `observed-properties` | Pinned runtime/monitor/parser JARs, one explicit log, specification and parser class |

PEx's upstream compiler invokes Maven's latest-version update goal. This
integration deliberately does **not** invoke that unpinned build path. Build
the JAR separately using an approved pinned/offline toolchain and provide its
hash as `artifact`. P 3.1.0 does not expose a PSym compiler, so PSym
also consumes an explicitly reviewed externally generated JAR. PObserve
consumes a separately built monitor/parser JAR rather than inventing a log
parser or pretending timestamp ordering is causal trace inclusion.
Project acceptance for all three external-artifact engines requires a current
explicit build/source review. The reviewed obligation must cover the actual
JAR, P sources, compiler inputs and parser/log assumptions, not just a name.

All dependency entries are `{ "path": "...", "sha256": "..." }`, resolved
relative to the manifest. Their directories are prepended only to the child
process PATH. Include the complete selected tool/solver runtime closure, not
just launchers. For `pobserve`, `observation` contains `runtime_jar`,
`monitor_jar`, `specification`, `parser`, `logs`, `input_kind` (`text`/`json`)
and `replay`. `artifact` is only valid for PEx/PSym.

PChecker assertion traces are read from native JSON, including failures whose
details appear only in counterexample artifacts rather than stdout. A timeout,
step-limit hit, unknown output, failed build, unresolved proof goal, empty log,
parser failure or out-of-order log is incomplete, not a passing or deliberately
rejected property check. PEx/PSym depth-limited or partial safety is not promoted
to exhaustion. PVerifier requires positive verified invariants and default
proof obligations, not merely process exit 0. PObserve requires nonempty
completed monitoring and keeps observational/liveness limits explicit.

Each P result archives source/tool bytes, native frontend output, invocation,
stdout/stderr, generated binaries and counterexamples. `read-p` independently
checks archived hashes and native outcome semantics; `--current true` also
checks applicability. Archive consistency does not mean the property passed.

To create an executable project example with positive and rejection controls:

```powershell
.\examples\P\Initialize-Project.ps1 -PCompiler $p -Output .\Results\p-project
.\TlaLink.ps1 doctor --project .\Results\p-project\project.json
.\TlaLink.ps1 run --project .\Results\p-project\project.json --scenario credit --output .\Results\p-project-run
.\TlaLink.ps1 accept --project .\Results\p-project\project.json --run .\Results\p-project-run
```

Projects select `tools.p`, a P stage and its `config`; P scope comes entirely
from that manifest. Independent requirements pin `config_sha256`, name the
exact `claim`, optionally require linked binding IDs and retain diagnostic-
specific negative controls. Sampled exploration cannot satisfy an exhaustive
requirement. Existing TLA/Dafny stages and P stages may share a scenario DAG and
review obligations; successful backends do not automatically compose proofs.

### Diagnostic codes

Maintained C# diagnostics use `ErrorCode` enum members with `DiagnosticInfo`
attributes defining their stable external IDs and default severities.
`DiagnosticCatalog` caches and validates the metadata once; missing attributes,
duplicate IDs/enum values and invalid severities fail explicitly.
Use `new InputException(ErrorCode.InvalidWorkflowInput, message)` for coded input
errors and `new Issue(ErrorCode.WorkflowStageFailed, message)` for reports.
Exception messages retain their existing ID prefix. Public JSON still emits
string IDs such as `WF001` and lowercase severity strings, not enum names or
ordinals. Native backend codes remain strings at the evidence boundary.
Top-level command errors preserve a typed input exception's diagnostic ID in
both full and summary output; untyped input/I/O errors retain generic `TL900`.
The incomplete command status and exit 2 do not change.

Run the focused metadata/serialization controls with:

```powershell
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . --diagnostics
```

## Project workflow

### Code-adjacent component authoring

An `analysis-project` can declare inline stages or explicitly import component
files. The maintained example projects all use components: the
[component example](examples/Components/README.md) is the smallest, with a root
project, verification declarations beside the C# code they describe, and shared
input sets and execution profiles.
There is no directory scanning, arbitrary templating or implicit component selection.

| Declaration | Meaning |
|---|---|
| Root `components` | Explicit component file paths, relative to the root document |
| Scenario `components` | IDs of imported components whose stages belong to that scenario |
| Root `input_sets` | Explicit `project-input-sets` document paths |
| Root `execution_profiles` | Explicit `project-execution-profiles` document paths |
| Component `components` | Optional explicit child-component file paths; selection still uses component IDs |

All documents use schema 4 and their explicit kind. A `project-component` declares
`id`, `stages`, optional `obligations` and `check_obligations`. Paths inside each
component resolve relative to that component file, not the root or working directory.
Requirements remain in the independently authored acceptance policy.
Dependencies use the unchanged stage IDs across the assembled scenario.
Reviews and check declarations are project-wide; importing a component does not
grant acceptance or silently narrow its source scope.

An input-set document declares `sets`, each with an `id` and explicit `inputs`.
Reviews reference set IDs through `input_sets`, optionally adding inline `inputs`.
Set paths resolve against the set document; equivalent review input identities are
deduplicated using the host comparer. Input sets do not discover files with globs.

An input set may additionally declare `prepared_sources`, an explicit list of
`prepared-sources.json` ledger paths. Authoring expands each ledger into the
ledger itself and its declared output files, relative to its own directory.
This replaces manually repeated output paths without directory discovery.
The ledger is included in authoring fingerprints and supports archived
composition with the ordinary source reader. This file-list expansion does
not validate preparation semantics, establish acceptance or authorize deletion.

An execution-profile document declares `profiles`, each with `id`, stage `kind`,
`timeout_seconds` and optional TLC/trace `resources`. A stage selects `profile`
instead of inline timeout/resources; mixing them or selecting a different kind is
an error. Omitted `depends_on` means no dependencies. Profiles do not change native
properties, bounds, configuration approvals or acceptance requirements.

```powershell
.\TlaLink.ps1 expand --project .\examples\Components\tlalink.project.json
.\TlaLink.ps1 project-schema --output .\Results\project-schema-new.json
```

`expand` requires no backend installation. It emits a `project-expansion` containing
the normalized `expanded_project`, its declaring `project_directory` and original-byte
fingerprints of every imported authoring document. It is structural inspection,
not checker readiness; use `doctor` and `plan` afterward. Optional `--output` saves
the same expansion exclusively. The expansion is not another editable project format.

`project-schema` generates JSON Schema 2020-12 from the typed authoring contracts.
Use the optional standard `$schema` property to select that schema in an editor.
This property affects editor support only; analyses do not load its URI.

Every new run saves `expanded-project.json` and archives all imported authoring bytes,
including explicitly imported but unselected components and unused shared declarations.
Independent acceptance reconstructs the expansion from those archived bytes and detects
changed, omitted or contradictory composition. Duplicate imports/IDs, missing selections,
unknown references and import cycles fail explicitly. Composition is limited to 1,000
documents, 32 child-import levels and 16 MiB per authoring document.

Start with [the maintained project](examples/Workflow/tlalink.project.json) and its
separate [acceptance policy](examples/Workflow/verification/requirements.json):

```powershell
.\TlaLink.ps1 doctor --project .\examples\Workflow\tlalink.project.json --scenario lanes
.\TlaLink.ps1 plan --project .\examples\Workflow\tlalink.project.json --scenario lanes
.\TlaLink.ps1 run --project .\examples\Workflow\tlalink.project.json --scenario lanes --output .\Results\lanes-new
.\TlaLink.ps1 accept --project .\examples\Workflow\tlalink.project.json --run .\Results\lanes-new --output .\Results\lanes-acceptance-new.json
.\TlaLink.ps1 explain --project .\examples\Workflow\tlalink.project.json --run .\Results\lanes-new --format text
```

`doctor` checks strict configuration, tool hashes, executable availability,
selected compiler settings and input existence. It does not run proofs or
resolve native signatures. `plan` shows dependencies, capability boundaries,
required outcomes and pending/stale reviews. `run` executes fresh subprocesses
in dependency order, retaining independent stages after an unrelated failure.
Dependencies require a successful predecessor; expected rejection controls
should be independent stages. Existing output directories are never reused.

Use `--scenario` on `doctor` and `plan` to limit backend prerequisites to the
selected scenario. Omitting it checks all scenarios; the workflow project's
Dafny scenarios then require Dafny even if the next run will select only `lanes`.
Example project manifests pin platform-specific tool paths/hashes. On another
host, create a separate project with the explicitly selected local distributions;
do not change approved requirements or configuration pins to clear a failure.

The project has three representative scenarios: `credits` (all explicit oracle
cases), `lanes` (native TLC followed by a compiled entity-aware causal trace),
and `cancellation` (cleanup observations and a specifically rejected unsafe
proof). These are synthetic tooling examples, not consumer acceptance policies.
The cancellation scenario deliberately requires an explicit adapter review.

Project paths, binding paths and review evidence paths resolve relative to
their declaring project/manifest, not the shell. Tool paths and SHA256 values
are explicit; there is no installation, credential lookup or implicit upgrade.
Scenario/stage/requirement/obligation IDs are stable, case-sensitive ASCII IDs.
Binding, case and correspondence IDs use the same 1..100-character letters,
digits, dots, underscores and hyphens. Case requirements use the unambiguous
`binding-id/case-id` form; IDs cannot contain its separator.
Dependencies are explicit and cycles fail preflight.

Supported stage kinds are:

| Family | Stage kinds | Scope |
|---|---|---|
| Integrated C#/Dafny | `inspect`, `verify`, `oracle` | An explicit binding manifest, optionally including TLA+ links |
| C# regression | `csharp-regression` | A `csharp-regression` config selecting one SDK console project and exact test IDs |
| Native Dafny | `dafny-verify` | A `dafny-analysis` config selecting explicit `.dfy` roots and transitive includes |
| TLA+ | `inputs`, `tlc`, `trace` | Explicit model inputs; TLC/trace require one root model and config |
| Apalache | `apalache-typecheck`, `apalache-check`, `apalache-inductive` | An `apalache-analysis` config |
| P | `p-inspect`, `p-pchecker`, `p-pex`, `p-psym`, `p-pverifier`, `p-pobserve` | A `p-analysis` config |

Apalache and P scope comes only from their native analysis manifests.
Trace stages
select copied C# sources including their entry point; the generated standalone
project passes the trace path as the first entry-point argument, followed by
the stage's optional `arguments`. No arbitrary shell commands are accepted.
The compiler profile is explicit: net10.0/C#14, nullable enabled, unchecked,
unsafe enabled, explicit references and optional replacement defines.
Unsupported settings fail rather than approximate another build.

TLC and trace stages optionally select `"resources": {"workers": 2,
"max_heap_mib": 2048}`. Workers must be 1..64 and heap 128..65536 MiB.
Omission preserves one worker and 512 MiB for TLC, or one worker and 1024 MiB
for trace matching. `validate-trace` exposes the equivalent `--workers` and
`--max-heap-mib` options. Preflight/plan show requested and effective settings;
run stage records retain those configured settings even when a deadline/failure
interrupts execution or a dependency blocks it. Configuration is not evidence
that a blocked stage launched; actual invocation evidence remains separate.
results retain them and the native invocation, which acceptance cross-checks.
The heap is a JVM allocation limit, not a total process-memory cap or reservation.
Choose resources explicitly for the shared host; there is no automatic sizing,
fallback, arbitrary JVM flag forwarding or retry. Resource experiments use fresh
runs and unchanged logical model/configuration requirements. Timed-out and
resource-exhausted checks remain incomplete.

### Reusing completed TLC results

Every `tlc` stage runs with an explicit TLC fingerprint function, `-fp`, which
selects how TLC hashes states. Stages may set `"fingerprint": 0..130`;
omission means 0. Results and plans record it, and acceptance checks it against
the native invocation.

A project may declare `"result_store": "<directory>"`, relative to the root
project file. `run` then reuses completed TLC outcomes automatically, much like
an incremental build:

- **Key.** Each stage's key covers the bytes of the model and every module it
  imports (as resolved by SANY), the configuration, the TLA+ tools JAR, the
  pinned JDK, the semantic TLC options, the fingerprint function and the
  adapter version. It excludes workers, heap and deadline, which only decide
  whether a search completes.
- **Lookup.** Before running TLC, `run` looks up the key. A matching entry is
  verified byte for byte and copied into the new run under
  `<stage>/stored/fp-<n>`, so runs stay self-contained. Otherwise TLC runs, and
  a completed `passed` or `rejected` outcome is stored.
- **Never stored:** incomplete, timed-out or failed searches.
- **Rejections take precedence.** A stored counterexample is used for any
  fingerprint function, because an exhaustive rejection is definitive.
- **Additional passes are attached.** Passes stored under other fingerprint
  functions with the same inputs are copied in alongside the primary result.
- **`--fresh true`** runs TLC again without lookup. It still stores new
  outcomes; an existing entry is never replaced.

Entries remain valid for as long as their inputs are byte-identical: changing
an input and changing it back makes the old entry match again. Rebuilding
TlaLink does not invalidate them; only an adapter-version change does.

Run expensive searches separately, with their own budget, directly into the
store. The project's routine budget and requirements stay unchanged:

```powershell
.\TlaLink.ps1 check-stage --project .\tlalink.project.json --stage <tlc-stage> --fingerprint 3 --workers 16 --max-heap-mib 16384 --timeout 14400
```

`check-stage` accepts deadlines up to 48 hours. It reports `stored`,
`already-stored` or `incomplete` (exit 2) and never accepts requirements.
`plan` lists the stored outcomes available for each TLC stage. In run
summaries, each stage reports `reused` and the top-level `reuse` reports the
counts.

Acceptance revalidates every copied entry:

- its files, key and location;
- that its key material describes the current declaration;
- that every key input matches the run's recorded inputs.

Reused primary evidence is checked against its own recorded budget, not the
routine one. A reused pass satisfies requirements exactly as a fresh one does,
because it is the same native evidence.

TLC exhaustion can miss states when two states share a fingerprint. Repeating a
search with independent fingerprint functions reduces that risk. A passed
`tlc` requirement may therefore set `"fingerprint_functions": N`, which requires
passes under N distinct fingerprint functions for the same key. Expected
rejections need only one counterexample.

The store is a cache. Runs never depend on it, because reused evidence is copied
into them, so deleting it only forces re-execution. To plan stale-entry cleanup,
use the existing guarded retention workflow with an `evidence-retention`
manifest that declares `result_projects`:

```json
{
  "schema_version": 4,
  "kind": "evidence-retention",
  "pins": [],
  "review_projects": [],
  "result_projects": ["tlalink.project.json"]
}
```

Select `<result_store>/tlc` as `gc --store`, not the containing result store.
The manifest paths are relative to the manifest. Each result project must name
that same result store. Include every project whose current results should be
kept; this is explicit trusted scope, not automatic discovery of other users.
The planner resolves current model closures through SANY and computes their
current family keys. It retains all fingerprint functions in matching families.
An unmatched family may be a candidate, subject to pins, grace and the usual
snapshot/byte/directory-handle guards. No time-based expiry applies to matching
families. Missing projects/tools, parse errors, malformed or changed entries,
unknown folders and active `.work-*` directories block collection.

Planning remains read-only. Actual Windows deletion still requires a saved full
plan, offline writers, `--exclusive true` and a new receipt; see
[guarded application](#explicit-guarded-application-windows). Applying a stale
plan fails rather than weakening the checks. The store is never automatically
pruned by `run`, and result families cannot use protected-consumer catalogs.

### C# regressions and standalone Dafny proofs

Regression `arguments` are opaque strings. Optional `path_arguments` lists
distinct zero-based argument indices that represent input files. Those arguments
resolve relative to the regression configuration, are included in its pinned
input closure, and are passed as absolute paths only at execution. For example,
`"arguments": ["selection", "scope.json"], "path_arguments": [1]` leaves
`selection` unchanged and pins the config-relative `scope.json`. The independent
reader checks the same resolved invocation and file closure.

[The evidence-stage project](examples/Evidence/tlalink.project.json) is an independently
authored arithmetic example. Its correspondence review deliberately starts
pending: successful tests and a Dafny proof do not supply that review.

```powershell
dotnet restore .\examples\Evidence\src\Increment\Regression.csproj --configfile .\NuGet.Config
.\TlaLink.ps1 doctor --project .\examples\Evidence\tlalink.project.json --scenario arithmetic
.\TlaLink.ps1 run --project .\examples\Evidence\tlalink.project.json --scenario arithmetic --output .\Results\arithmetic-new
.\TlaLink.ps1 accept --project .\examples\Evidence\tlalink.project.json --run .\Results\arithmetic-new
.\TlaLink.ps1 report --project .\examples\Evidence\tlalink.project.json --run .\Results\arithmetic-new --output .\Results\arithmetic-report.md
```

Restore is a separate setup step. Regression execution builds with `--no-restore`
and `--no-incremental`; it does not install packages or formal backends.
A `csharp-regression` config declares `schema_version`, `kind`, `project`,
explicit `sources`, `compiler`, exact `tests`, additional pinned `inputs`,
entry-point `arguments`, and optional `configuration` (`Debug`, the default, or
`Release`). Paths resolve from the config. The regression compiler profile is
independent of the trace/Roslyn profile: net10.0 or net10.0-windows, C# 14,
nullable enable/disable, explicit checked/unsafe flags, exact `defines`
(including SDK/configuration symbols) and explicit additional references.
Actual sources must match the declared sources plus SDK-generated assembly-info,
framework-attributes and global-usings files. Single-target SDK console projects
are supported; arbitrary shell commands and testhost/TRX guessing are not.

The harness entry point receives `<new-observations.json> <selection.json>`
followed by the config's arguments. Selection is schema-4
`regression-selection` with an ordered `tests` array. The harness writes one
schema-4 `regression-observations` document with:

- `complete`: all selected cases completed.
- `binary` and lowercase `binary_sha256`: the actual loaded entry assembly.
- `selection`: the exact selection array.
- `results`: one `{id, status, message}` per selected test. Status is `passed`
  or `failed`; a failure requires a nonempty message.

Complete passing observations require exit 0; complete assertion failures
require exit 1 and emit `CS100`. Missing/duplicate/skipped/partial results,
wrong binary, unknown output, deadlines and contradictory exits are incomplete.
Requirements independently pin the config, exact `tests` and the claim
`selected-csharp-regressions` (or `not-established` with `CS100` for controls).
The build project, harness, compiler/source generators and OS/runtime are
trusted. Compiler-time inputs, references, context, imports, analyzer/config
files and the frozen ordinary runtime-output files are captured, not a full
MSBuild workspace or authenticated loaded dependency closure. Absent optional
configuration probes are retained as `missing_build_inputs`; later appearance
is applicability drift. Retention uses a null `expected_sha256` for such
expected absences rather than inventing an input hash.

A `dafny-analysis` config contains current schema/kind and explicit `proofs`
(`.dfy` files, not directories). Native `dafny-verify` stages use the selected
Dafny 4.11.0 distribution, authoritative transitive-include resolution,
verification with included files, and assumption audit. No C# binding or oracle
is required. Requirements pin the config and claim `dafny-contracts` (or
`not-established` with `DF100` for verifier controls). Audit findings, parser/tool
failures and budgets cannot satisfy verification or assertion rejection.
For compound controls, optional requirement `diagnostic_count` pins the exact
number (1..1000) of matching code/text diagnostics. One failed assertion cannot
stand in for several required negative controls.

Direct commands are `check-regression --config ... --output ... --timeout ...`
and `check-dafny --config ... --dafny ... --output ... --timeout ...`.
`read-regression --result ... --current true|false` and
`read-dafny --result ... --current true|false` independently reread the native
evidence without rerunning it. They report archive consistency, semantic
consistency and optional current applicability; acceptance remains separate.
Artifact paths in these receipts are native, stage-relative paths.

Project `report` accepts repeated explicit `--run` directories. It independently
evaluates each against the selected project/policy and saves normal acceptance
snapshots, one `evidence-report` manifest (`<output>.json`) and Markdown/Mermaid.
Command-error summaries retain the single `run` path or ordered `runs` list for
multiple explicit inputs, without hiding the underlying diagnostic.
No run is selected implicitly, merged into another, or treated as a theorem
composition. Presentation freshness does not replace the separately recorded
per-run archive/applicability/acceptance results. Source dispositions are not
invented, and reports are not automatic retention roots. Custom source-region
presentation continues to use the ordinary explicit report manifest.

Use `--source-report authored-report.json` to compose an ordinary
`evidence-report` presentation manifest with those project-run nodes. Its title,
scope, nodes, source dispositions, assumptions and gaps remain explicitly
authored; all paths resolve against the declaring presentation file.
For a fresh runtime-selected inventory, supply `--source-inventory inventory.json`
and declare exactly one `source_inventories` entry with `"inventory": null`.
The tool pins and binds that one slot, but never replaces an existing declared
inventory or updates approved source hashes to clear drift.

### Declared check coverage

Unused tool pins may be omitted from `tools`; selected stage kinds still require
their own pinned tools. Optional compiler `defines` may be omitted (no additional
symbols); compiler `references` and the supported compiler profile remain explicit.

Projects may declare `check_obligations` separately from human review obligations:

```json
"check_obligations": [
  {"id": "ingress-safety", "kind": "safety", "scenario": "ingress", "requirements": ["inductive-safety"]},
  {"id": "ingress-progress", "kind": "progress", "scenario": "ingress", "requirements": ["conditional-progress"]}
]
```

Each declaration names `safety`, `deadlock` or `progress`, one scenario and
acceptance requirement IDs in that scenario. This single representation is used
by preflight and acceptance; obligations are never inferred from stage names.
An empty requirement list explicitly records an unmapped obligation.
`doctor`/`plan` report `check_coverage` as `mapped` or `missing`; missing mappings
return 2 and block `run` before its output directory is created. Acceptance also
rejects missing mappings. Scenario selection limits reporting to that scenario.
Projects without declarations acquire no implicit temporal/deadlock obligations.

The coverage profile maps positive TLC requirements to all three kinds
and positive Apalache bounded/inductive requirements to safety only. Typechecking,
trace matching and rejection controls cannot discharge those positive obligations;
other native backends retain their separate claims rather than being guessed
equivalent. `mapped` means a compatible required check is selected, **not** that
the property is proved. Review the pinned TLC configuration's exact properties,
deadlock setting, bounds and fairness: this report does not infer their semantics
or compose a theorem across checks. Acceptance still requires their actual evidence.

### Execution is not acceptance

Acceptance rejects undeclared or duplicate run stages with structured workflow
diagnostics, including when a receipt is paired with the wrong project. A
project mismatch is not permission to reinterpret its stages or accept its
results.

Use `--format summary` for the common agent/operator status view without filtering
large provenance objects. It emits schema-4 `command-summary` JSON with the
original `result_kind`, distinct readiness/execution/archive/applicability/
acceptance statuses, native claims, stage outcomes and timings, required-check
coverage, requirement results, diagnostics and receipt locations. TLC run stages
include native state counts, search depth and the optimistic fingerprint omission
estimate when reported; missing native statistics stay null, never invented zeros.
Statistics do not establish exhaustion or collision freedom.

For `accept` and `explain`, summary `worklist.groups` separates incomplete/missing
stage evidence, shared nonaccepted reviews, other unmet requirements and remaining
diagnostics grouped by code/severity. Each group has a `next_action`, exact `count`,
up to ten `items` and an exact `omitted` count. Review items count distinct affected
stages and requirements; they never infer approval from passing checks.
Remaining diagnostic groups include one original example per code/severity;
warnings remain warnings and do not change an accepted verdict.
Expected completed rejection controls are not work items merely because they
rejected. Incomplete-stage items do not infer a timeout or its cause.

Acceptance summaries preview at most three diagnostics, requirements, review
statuses and check-coverage rows; top-level `counts`/`omitted` describe these lists.
Nested missing-reason and check-reference previews also stop at three and report
omissions. Requirement/review status counts and diagnostic severity counts retain
the full totals. `next_actions` contains the grouped worklist actions, not one
repetition per diagnostic. Use default full JSON or `--output` for every detail.
These limits apply to acceptance summaries, not to the saved acceptance receipt.

TLC `doctor`/`plan` and `run` preflight use the same authoritative SANY
parse/freshness gate, pin transitive imports, and reject invalid models before
creating a run directory. A ready plan does not execute TLC or prove that native
configuration evaluation will succeed.

```powershell
.\TlaLink.ps1 plan --project .\examples\Workflow\tlalink.project.json --scenario lanes --format summary
.\TlaLink.ps1 run --project .\examples\Workflow\tlalink.project.json --scenario lanes --output .\Results\lanes-new --format summary
.\TlaLink.ps1 accept --project .\examples\Workflow\tlalink.project.json --run .\Results\lanes-new --format summary
```

This is presentation only, not an evidence document: `run.json`, native results
and any explicit `--output` receipt retain their full formats. Independent readers
do not accept a summary in place of evidence. Exit codes and classification are
unchanged, including errors. Full JSON remains the default for automation needing
fingerprints, inventories, invocations or complete recorded packets.

`run` exit 0 means all declared stages **completed**, including any completed
rejections; it never means the requirements were accepted. Incomplete/blocked
stages, changed inputs and process deadlines return 2. Each stage records its
own outcome. `accept`/`explain` recheck archived input bytes, current inputs,
artifact hashes, stage consistency, exact required bindings/case IDs,
diagnostic-specific negative controls and current review decisions.
TLC/trace requirements independently pin `config_sha256`, covering the exact
approved properties, bounds, fairness selection and reductions. Removing a
property or weakening a bound cannot silently become acceptable on a new run;
it requires an explicit policy change.

Negative requirements can additionally set `diagnostic_text` to a nonempty
literal, case-sensitive message fragment, for example
`"diagnostic": "TLC2110", "diagnostic_text": "Invariant Ownership is violated."`.
The code and text must match the **same** diagnostic. This distinguishes a
targeted mutant from an unrelated invariant failure with the same native code.
It does not parse model expressions or replace approved configuration pins.
Text selectors are valid only for rejected outcomes with an explicit code.
Use the actual native diagnostic: a named temporal rejection can report
`TLC2116` and `Temporal property Settles was violated.`, rather than the grouped
`TLC2114` temporal message. Do not substitute a generic rejection or change
properties/configuration pins to make a negative control pass.
Summary acceptance output includes current review status separately from the
recorded disposition/reviewer/rationale; a stale accepted decision stays stale.

Acceptance exit 0 means the declared requirements are satisfied, 1 means
rejected/inconsistent evidence or unsatisfied requirements, and 2 means stale
or incomplete evidence. Expected build failures and timeouts cannot satisfy
rejection controls. Archive consistency, current applicability, execution,
individual outcomes and acceptance remain separate fields.
Here `current_applicability` describes the run inputs. A pending, rejected or
stale required review is separately reported in the review status and leaves a
requirement unsatisfied (`WF015`, exit 1) even when run inputs are current.
If run inputs also changed, their stale-evidence diagnostic instead makes the
exit 2. Do not infer review approval from current run applicability.

### Persistent review decisions

Review decisions live outside the project/run files so review after execution
does not itself invalidate the run. For example, after inspecting the actual
cleanup adapter and evidence:

```powershell
.\TlaLink.ps1 record-review --project .\examples\Workflow\tlalink.project.json --obligation cleanup-adapter --reviewer "<alias>" --rationale "<review findings>" --evidence "..\..\Results\cancellation-new\cleanup\result.json"
```

Evidence paths above are project-relative. New decisions serialize evidence
and fingerprint keys relative to the project, canonicalizing and deduplicating
equivalent paths with the host path comparer. References on another Windows
drive cannot be serialized relatively and are rejected. Existing absolute
schema-4 records remain readable at their recorded identities; relocation
does not rewrite those records or make them portable.
Decisions are append-only under the configured review directory. The latest
decision is authoritative; its
claim/assumption/input identity and supporting evidence hashes must still
match. Changed claims or inputs make reviews stale. Acceptance reports retain
the decisions used. Reviewer names are explicit assertions, not authenticated
identities, and accepted review is not an automatic refinement proof.

### One current public format

All public manifests, receipts, traces, packets, plans, reviews and reports use
`schema_version: 4` plus a `kind` discriminator. Each kind has a specific shape;
tool-specific evidence is not flattened into a generic proof claim. Unknown
versions, kinds, duplicate fields and unknown project options are errors.
Source annotations use `@link` with root `annotation_version: 4`; that grammar number is not a JSON
schema version. Native backend output and .NET runtime/project configuration
retain their upstream formats.

Maintained examples/producers/readers/tests are updated together. There are no
legacy JSON acceptance branches. Frozen source/model profiles and historical
evidence remain untouched and are not relabeled as current evidence.

## Evidence retention and recheck planning

`gc` defaults to a **read-only planner**. It reports which completed
workflow archives are retained, protected or unreferenced within an explicitly
declared store. The same dependency graph identifies recorded file inputs that
changed or disappeared and runs/reviews that conservatively need rechecking.
No backend tools are required.

Create an explicit retention manifest, for example:

```json
{
  "schema_version": 4,
  "kind": "evidence-retention",
  "pins": [
    { "path": "Results\\milestone-run", "reason": "Retained milestone and its dependencies" },
    { "path": "Results\\defect-control", "reason": "Representative counterexample" }
  ],
  "review_projects": ["project.json"]
}
```

Both arrays are required; use an empty array when intentionally selecting no
roots of that type. Pin paths/reasons must be nonblank. Pins identify an existing
immediate-child run directory, or a file/directory inside one, in the selected
store. Selecting a file retains its entire owning run. Paths inside the manifest
are relative to the manifest, while CLI paths are relative to the caller:

```powershell
.\TlaLink.ps1 gc --store .\Results --pins .\retention.json --format summary
.\TlaLink.ps1 gc --store .\Results --pins .\retention.json --grace-hours 24 --output .\gc-plan-new.json --format summary
```

For native/historical archives or loose sidecars, an optional manifest
`"catalog": "protected-consumers.json"` selects a schema-4
`protected-consumer-catalog` **outside the store**. It has:

- `store`: catalog-relative store path.
- `producer_fingerprints`: catalog-relative producer-source paths and SHA-256
  pins. Changed producer code requires a fresh export.
- `consumers`: entries with catalog-relative `path`, nonblank `reason`, `files`
  (all owned relative file paths and SHA-256 values), `inputs` (recorded absolute
  `path`/`sha256` pairs), and absolute `references`.

Each consumer is an immediate store child directory or root sidecar file.
Directory file paths are consumer-relative; a sidecar's single file path is
store-relative. The file set and every byte must still match. These consumers
are **protected roots**, not native deletion candidates; their references retain
ordinary workflow dependencies transitively. Extra files, changed bytes,
missing consumers, unmapped loose JSON or unsupported archives still block
collection. A catalog is not an ignore list and does not certify native proofs.

The native exporter is trusted to interpret its recorded path fields and
declaring bases. Historical formats stay at that explicit integration boundary,
never accepted as current public evidence or rewritten. Exports must preserve
original expected hashes, keep model symbols/URIs distinct from filepaths, and
apply explicit relocation maps only to retention references, not silently
relabel missing original inputs as current.

`--format json|text|summary` controls presentation only. Saved output is always
the full schema-4 `gc-plan`, written exclusively to a **new file outside the
store**; its parent must already exist. Plans always state `read_only: true` and
`deletion_authorized: false`. Deletion requires the separate explicit application
below; there is no delete/force override.
GC summaries preview at most ten entries per list and give exact per-list
`omitted` counts. Per-run changes/dependencies and per-input consumers are counts,
not unbounded provenance dumps. Use the full saved plan for all paths and reasons.

### Retention decisions

- Every immediate store child directory is a collection unit. Only structurally
  recognized, completed schema-4 `analysis-run` archives are supported. Root
  files are never candidates.
- Explicit pins retain the whole transitive run dependency closure, including
  dependencies referenced by recorded file fingerprints and embedded review
  decisions. If the retention manifest itself is inside a unit, that unit is
  retained too.
- `review_projects` selects analysis-project documents as the path context for
  their append-only review directories. **All review histories** and their
  evidence dependencies are retained, not just the latest accepted decision.
  Review evidence/fingerprint paths resolve against the original project, not
  against the review file or acceptance receipt.
- The grace interval defaults to 24 hours; integer values 0..87600 are accepted.
  Recent output and future timestamps protect a unit and retain its dependencies.
- Missing, malformed, legacy/private/unknown or unfinished archives, unresolved
  references, unreadable scope, loose root JSON other than the manifest, reparse
  points in managed output and observed scan changes block **all collection candidates**. An
  `incomplete` plan includes diagnostics and exits 2. A structurally ready plan
  exits 0, even when it reports changes requiring rechecks.

An accepted run is not automatically a permanent root, and stale evidence is not
automatically garbage. Pin evidence still relied upon, including useful defect
controls. The planner cannot infer undeclared external consumers or arbitrary
Markdown references; declare that retention explicitly. Do not suppress unknown
archives just to produce candidates.

### Recheck impact is separate from retention

`changed_inputs` reports changed/missing/unreadable recorded file inputs, their
actual hash when readable, and the consumers whose own expected hash no longer
matches. `recheck` lists direct and transitively affected runs/reviews, with
changed-input paths, immediate affected dependencies and recommended actions.
Thus changing a model can flag its consuming run **and downstream runs even
before that run's output bytes change**. Unrelated consumers remain unaffected.
`entries[].recheck_required` exposes the same impact next to retention status.
Retained stale evidence stays retained; an unreferenced unit may need a recheck
without becoming a retention root.

The impact scope is deliberately **whole-run conservative**, not an invented
stage-level invalidation plan. A clear recheck list is not a proof, applicability
or acceptance verdict: the planner does not evaluate nonfile runtime metadata,
review obligations/dispositions or backend semantics. Re-run the affected
analysis, review changed contracts/assumptions as appropriate, then use the
independent `accept` command. Do not replace a baseline or alter acceptance pins
merely to clear a change.
SANY `library:` member identities remain opaque metadata, consistent with the
frontend; the separately recorded JAR file participates in ordinary file-input
tracking. Library identities are not normalized as Windows filepaths.

Byte estimates are logical file lengths, not physical reclaimable space.
Managed output links are not followed; hard-link identity and concurrent filesystem atomicity
are not proven. Input document reads are capped at 16 MiB, the store at 10,000
units and scanning at 100,000 entries. Metadata and read bytes are rechecked
before reporting, but the report is **never deletion authorization**.

On Windows, recorded input links are resolved through opened read handles and
the same handles supply the hashed bytes. Resolved physical identities supply
store dependency edges and are rechecked alongside original recorded paths.
This permits a linked SDK input without assuming it is outside the store.
Managed outputs, catalogs and deletion ancestors still prohibit links. Missing
linked inputs and unsupported host resolution fail closed.

### Explicit guarded application (Windows)

Stop all writers to the store and selected review histories before applying a
saved full plan. The assertion below means the caller has ensured exclusive
maintenance; it does not automatically discover or stop processes:

```powershell
.\TlaLink.ps1 gc --apply .\gc-plan-new.json --exclusive true --output .\gc-apply-new.json --format summary
```

Apply uses the saved store, pins and grace interval; `--store`, `--pins` and
`--grace-hours` overrides are rejected. A summary, incomplete plan, changed
candidate set, changed filesystem snapshot or changed input/decision hashes
cannot authorize deletion. Apply re-plans, verifies candidate artifact and
archived-input bytes, and rechecks observed inputs before removing anything.
The full plan's `snapshot` and `file_inputs` are intentionally omitted from
presentation summaries.

Initial application is **Windows-only** so native directory handles can prevent
ancestor replacement. Links/reparse points are rejected through handle
attributes; a sibling exclusive `.gc.lock` lease prevents competing applications.
Other platforms still support planning and explicitly reject apply. Unknown
native/private archives continue to block the entire store; preserving them is
not an excuse to ignore their possible references to other runs.

Only the saved, revalidated candidate files are removed, followed by
nonrecursive empty-directory deletion. New files are never swept into a
recursive delete. The default 24-hour protection remains unchanged, so today's
runs normally remain protected even if unreferenced.

`--output` is mandatory, must be a new file outside the store and scanned review
directories, and is reserved/flushed as an incomplete schema-4 `gc-apply-result`
before mutation. The final receipt records removed files, deleted/pending units,
any partially removed unit, authorization and diagnostics. Completed application
exits 0; failure exits 2 and stops further deletion. Deletion is not transactional
and has no rollback: inspect an incomplete receipt before doing anything else.
A process interrupted while rewriting its final receipt may leave malformed or
incomplete JSON, not a completion claim. Do not rerun the same plan to force past
a partial failure; reassess the remaining store first.

## Integrated TLA+ and Dafny workflow

Select the trusted local **Dafny 4.11.0** distribution explicitly with `--dafny`.
It must contain its frontend assemblies and bundled solver; a global tool shim
alone is not sufficient. No package installation or public service is involved.
Using the repository-local tool cache:

```powershell
.\TlaLink.ps1 inspect-integrated --annotation-version 4 --bindings .\examples\Integrated\bindings.json --dafny .\.tools\dafny\Dafny.exe --jar .\.tools\tla\v1.8.0\tla2tools.jar --output .\Results\integrated-review-new
.\TlaLink.ps1 verify-integrated --annotation-version 4 --bindings .\examples\Integrated\bindings.json --dafny .\.tools\dafny\Dafny.exe --jar .\.tools\tla\v1.8.0\tla2tools.jar --output .\Results\integrated-proof-new
.\TlaLink.ps1 test-integrated --annotation-version 4 --bindings .\examples\Integrated\bindings.json --dafny .\.tools\dafny\Dafny.exe --jar .\.tools\tla\v1.8.0\tla2tools.jar --output .\Results\integrated-test-new --timeout 60
.\TlaLink.ps1 read-integrated --result .\Results\integrated-test-new\result.json --current true
```

All run output directories must be new. A current `integrated-result` packet is retained
as `result.json`, alongside native logs, exact subprocess invocations, generated
sources/runtime/binary, case comparisons and content-addressed input copies.
The distribution, transitive source includes, C# references, compiler and tool
are hashed. Archives can be sizable: they retain the actual tool/reference
bytes, not only source files. Keep shared-machine disk usage in mind.

| Command | Evidence produced |
|---|---|
| `inspect-integrated` | Resolved C#/Dafny links, inventories, optional TLA links, explicit correspondence obligations; no proof or execution |
| `verify-integrated` | The above plus Dafny verification and a clean assumption audit over the complete local include closure |
| `test-integrated` | The above plus verified concrete call preconditions, generated Dafny C# oracle/runtime and actual C# adapter comparisons |
| `read-integrated` | Archived byte consistency and optional current-file applicability, separately from recorded outcomes; no rerun or project acceptance |

Integrated commands accept `--reference`, `--define`, `--timeout`, `--format`
and `--baseline` (a prior current-format result). A baseline detects changed, added or
removed maintained inputs; it does not replace review. `--jar` is required only
when the manifest selects TLA+ models. Existing TLA-only commands retain their
command names and do not require Dafny; their JSON uses the same current schema.

### One explicit binding manifest, not another source annotation language

[The example manifest](examples/Integrated/bindings.json) uses kind `integrated-bindings`:

- `sources`: explicit C# file/directory scopes, relative to the manifest.
- `scope`: one correspondence scope whose aliases select explicit native files.
  Proof/model inputs come from the selected inline bindings' aliases, not
  duplicate `proofs`/`models` arrays.
- `tla_targets`: explicit TLA+ aliases to review alongside Dafny; default `[]`.
  Merely declaring another alias or having links in C# does not select that
  native frontend or require its tool.
- `bindings`: unique inline binding `id`, nullable C# `adapter`, and explicit
  `cases` with unique IDs and typed `inputs`. Source/formal endpoints belong in
  C# `bind` entries; duplicate manifest endpoints are rejected.
- Roslyn resolves the attached C# declaration directly, including overloads.
  Review-only bindings can target fields/properties. Explicit adapters retain
  resolved C# symbol selectors where the native oracle requires them.
- Dafny names/signatures/locations/ghost status come from its resolved AST.
  Review bindings can reference ghost lemmas; these do not become runtime tests.
- Executable targets currently support static non-ghost Dafny methods/functions/
  predicates with bodies, `int`/`bool` inputs and one or more `int`/`bool`
  outputs. The accessible synchronous C# adapter is a non-generic static
  `void` method with `int`/`bool` value inputs followed by matching `out`
  outputs. `adapter: null` tests the linked C# method directly.
- Case integers must fit signed 32-bit C# inputs. Oracle outputs retain
  mathematical-integer precision, so C# overflow cannot be hidden by narrowing
  the expected result. Strings, sequences, heap objects, generic/instance Dafny
  calls and direct async adapters are explicitly unsupported for execution,
  not silently approximated. Handwritten adapters can exercise an instance
  component, but their setup/abstraction/cleanup are trusted test code.
- A manifest supports up to 1000 bindings, 1000 total executable cases and
  1000 correspondence claims. Oracle comparison tests the executable oracle's
  particular outputs, not arbitrary membership in an underspecified relation.
- `correspondences`: unique `id`, linked `tla_step` key, `dafny_binding` ID,
  a textual `relation`, and nonempty `assumptions` and `obligations`. Missing
  endpoints are errors. These claims always remain `requires-review` /
  `not-proved`; matching names or two successful backends never prove composition.

### Executable C# source premises

The same `integrated-bindings` manifest optionally declares `source_obligations`
(default `[]`, maximum 1000). Each has a unique `id`, an existing `binding` ID,
a `kind`, and an exact field documentation ID in `storage`. Selectors are
case-sensitive semantic identities, not source text or CFG block numbers.

| Kind | Additional fields | Checked premise |
|---|---|---|
| `guarded-effect` | `gate`, `effect`: method documentation IDs; `when`: Boolean | Exactly one gate call on `this.storage` decides a direct Boolean branch. Every entry-to-effect path crosses the declared outcome edge, that outcome can reach the effect, and the one effect block cannot repeat. A value-type gate receiver must not be readonly. |
| `guarded-try-effect` | Same as `guarded-effect` | Extends that check only to a selected first direct call in the body of one top-level try statement. The gate must be in the method prefix outside that region. Exception dispatch cannot enter the selected first site; handler behavior and arbitrary exception-flow summaries are not established. |
| `stable-storage` | `allowed_calls`: nonempty unique source method documentation IDs; optional `allowed_properties`: getter property IDs | A private writable value-type field in a reference-type owner has no initializer, replacement, copy or ref escape in the selected scope. Every use is an authored direct method call or instance getter without a setter/ref return. Callee/getter effects and purity are not inferred. |
| `atomic-exchange-gate` | none | The bound synchronous instance method consists of `return Interlocked.Exchange(ref this.storage, 1) == 0`, or its expression-bodied/equality-operand-order equivalent, using writable private Int32 storage. |
| `monotonic-int32` | none | Selected private writable Int32 storage starts at zero and every scoped use is a recognized `Volatile.Read`, `Volatile.Write(..., 1)` or `Interlocked.Exchange(..., 1)`. Direct writes, resets and unrecognized ref escapes do not pass. |
| `reference-cas-gate` | `parameter`: source parameter name | The bound instance method returns generic reference `Interlocked.CompareExchange(ref this.storage, parameter, null) == null` with built-in, not overloaded, equality. This checks shape, not a nonnull input precondition. |
| `reference-read` | none | The selected instance getter or synchronous method returns a direct generic reference `Volatile.Read(ref this.storage)`. |
| `reference-cell` | none | Private writable reference storage starts null. Every scoped use is a recognized generic reference Volatile.Read or CompareExchange with null comparand. Nonnull replacement comparands, resets, aliases and unrecognized operations do not pass. |

For example, independently authored notification logic could declare:

```json
"source_obligations": [
  {
    "id": "delivery-guard",
    "binding": "notification",
    "kind": "guarded-effect",
    "gate": "M:SignalFlag.Raise",
    "effect": "M:Notification.OnRaised",
    "storage": "F:Notification.flag",
    "when": true
  }
]
```

Storage containers must not be partial or use explicit/inline-array layouts;
closure outside selected sources and storage-alias arguments are not invented.
Generic source declarations use their declaration documentation IDs. Recorded
flow calls retain both the constructed target and its `definition`; matching
does not collapse their display identities. Closure checks compare original
field declarations so writes through other constructed generic types cannot
escape the inventory.

Guard checks initially exclude loops, jumps, general exception regions,
`using`, locks, suspension, unsafe blocks and nested functions. More complex
branch expressions are unsupported rather than approximated. Storage checks
inspect all selected syntax trees, including nested functions. Whole-struct
replacement is rejected; escapes, copies or calls on the containing
value-type `this` require unsupported effect summaries rather than being ignored.
These checks do not infer global heap aliasing or object lifetime. Recognized
intrinsics must resolve to metadata methods with the expected Int32 or generic
reference signatures;
source lookalikes do not qualify. Framework references and CLR intrinsic
semantics remain trusted, not authenticated or proved.

A null candidate can make CompareExchange return null without publishing a
nonnull reference. Repeated such calls may all return true. At-most-one
nonnull publication and stable-first-reference claims differ from at-most-one
true result or guarded callback; the latter need an explicit nonnull argument
premise. Neither a source-shape pass nor nullable annotations supplies that
runtime precondition.

Inspection, verification and testing record `source_obligations` with per-item
`passed` / `rejected` / `incomplete`, source locations, explanations and guard
flow facts. `source_obligations_status` is `not-specified` for an empty list;
otherwise incomplete takes precedence over rejection, then all-pass. A known
failed premise exits 1; unsupported or unresolved premises exit 2. Either blocks
native proof and oracle execution. Readers check outcome consistency and compare
the recorded premises with the explicitly pinned archived binding manifest,
separately from archive/current bytes. These are selected structural premises, not a proof
that the abstraction, allowed callees, resource identity or C# implementation
matches the linked theorem. Coverage and required human reviews remain pending.

Inline JSON `bind` entries identify the Dafny and optional TLA+ correspondences;
adapters, cases and `tla_targets` remain explicit in the manifest. The example exercises
multi-output credit return, a mathematical-integer function, and Boolean
functions. Its accepted-unit-return correspondence deliberately does not cover
rejected or non-unit returns.

### Separate evidence, never one "verified C#" badge

The integrated packet independently reports `links_status`, `source_obligations_status`, `coverage_status`, `proof_status`,
`audit_status`, `conformance_status`, `correspondence_status` and
`provenance_status`. Coverage still requires review; tested and untested bindings
are listed in the execution receipt. A nonempty subset of test cases does not
establish whole-scope implementation coverage. TLA+ readiness remains a nested
`source-review` packet with `conformance_status: not-run`: use `validate-trace` for
its independent finite-execution check.

Run exit codes are 0 for the requested ready/passed evidence stages, 1 for a
completed proof rejection, audit findings or oracle disagreement, and 2 for
incomplete/unsupported inputs, drift, invalid case preconditions, timeout or
tool/build/runtime failures. A failing build is never a successful negative
control. Correspondence/coverage obligations are not discharged by exit 0.
`read-integrated` instead returns 0 for consistent requested archive/current
and semantic checks, 1 for archive/semantic inconsistency and 2 for stale requested current inputs;
its recorded run outcome and unevaluated acceptance remain explicit.

Focused integration controls:

```powershell
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . --integrated
```

They compare the synthetic integrated example with its Dafny oracle and include
an extra-credit mutation rejected specifically for disagreement, false proofs,
audited assumptions (including included files), invalid preconditions, unresolved
bindings, adapter signatures and archive/current drift. Fixtures are created in
temporary directories and removed after their test group. The complete console
run also releases each group's fixtures before starting the next group, so
byte-complete native archives do not accumulate across the entire run. It logs
each group's fixture bytes and retains all test outcomes in the final report.
The session-wide immutable-tool hashes and read leases remain active until the
whole run finishes; checker coverage and evidence validation are unchanged.

## Current C# source inventory

`inspect-source` produces a schema-4 `source-inventory` from Roslyn without a
formal backend. Supply the actual source scope and any additional references
and replacement defines explicitly:

```powershell
.\TlaLink.ps1 inspect-source --annotation-version 4 --source .\examples\Demo.cs --output .\Results\demo-source-inventory.json --format summary
```

The full inventory includes declared methods, constructors, properties, events
and fields, with resolved explicit calls, declared dispatch kind, nested-function
markers, locks, awaits, throws, using scopes and candidate field accesses.
Field initializers have their own regions. It pins source/reference bytes, the
selected defines, producing tool and runtime. Output is exclusive; use a new
path for each inventory.

Method/constructor `flow` records include reachable blocks, predecessor and
branch destinations, conditions and fixed-point block dominators. Resolved
calls identify their block, receiver field (including readonly status) and
ref-argument symbols. These facts can show a gate block dominating an effect
block or reveal a writable value-type receiver; they do not prove the called
gate's semantics, branch polarity's adequacy or a concurrent refinement.
Nested callbacks/local functions are not inlined into the parent flow.
Implicit exception/finally execution remains a separate review obligation.

The `compiler` object records C# 14, nullable mode, unsafe/overflow options,
defines and framework-reference selection. Defaults are `--nullable enable`
and `--framework-references implicit` (the host's trusted platform assemblies
plus additional references). To reproduce a recorded compiler reference closure,
use `--framework-references explicit` with every observed `--reference`, the
observed `--define` values and `--nullable enable|disable`. Explicit mode adds no
implicit framework references and requires at least one reference. This does
not load an MSBuild workspace or discover project settings.

Alternatively, `--regression-result <native-engine-directory>\result.json`
uses an independently checked schema-4 C# regression archive. Supply the selected
`--source` files explicitly. The tool takes the exact observed references,
defines and supported settings internally, adds no implicit host references,
and requires each selected source/reference to retain its observed bytes.
Do not combine this option with reference/define/nullable/framework overrides.
The inventory pins the receipt and records its compiler provenance; `sources`
lists the actual analyzed files. This is compiler-context reuse, not reuse of
the regression's correctness claim or automatic analysis of all its inputs.

Compiler errors, unresolved call targets and unsupported top-level scopes return
2 with an incomplete inventory, rather than a success-shaped empty result.
Summary output retains compiler context, status, diagnostics and source/member/call/await counts;
use full JSON or the saved file for individual regions.

This is an inventory under the supplied C# compiler context, not an MSBuild
workspace, coverage disposition or proof. Declared call targets do not establish
runtime dispatch. Implicit disposal calls, compiler-generated async machinery,
generated members and external behavior still need review. A resolved source
region is not validated code and does not discharge correspondence gates.

## Preparing annotated source copies

Use `prepare-sources` instead of maintaining annotation generators based on text
searches, surrounding lines or newline spelling:

```powershell
.\TlaLink.ps1 prepare-sources --manifest .\preparation.json --output .\Results\prepared-new --format summary
.\TlaLink.ps1 check-prepared-sources --result .\Results\prepared-new\prepared-sources.json --format summary
```

The strict manifest is schema 4, kind `source-preparation`, with one required
`annotation_version: 4`, a supported standalone `compiler` profile and explicit
`files`. Each file declares `source`, approved `sha256` and output-relative
`destination`. Supply real approved hashes, not placeholders chosen to clear
drift. Paths resolve against the manifest; destinations must remain inside a
new output directory. Original files are never edited.

For example, independently authored code containing `class Gadget`,
`int total` and `void Apply()` can declare these annotations:

```json
{
  "schema_version": 4,
  "kind": "source-preparation",
  "annotation_version": 4,
  "compiler": {
    "target_framework": "net10.0", "language_version": "14",
    "nullable": "enable", "checked": false, "allow_unsafe": true,
    "references": []
  },
  "files": [{
    "source": "Gadget.cs",
    "sha256": "<approved SHA256>",
    "destination": "scope/Gadget.cs",
    "annotations": [
      { "target": { "symbol": "T:Gadget" },
        "payload": { "bind": [{ "id": "sample::gadget" }] } },
      { "target": { "symbol": "F:Gadget.total" },
        "payload": { "bind": [{ "id": "sample::total" }] } },
      {
        "target": {
          "symbol": "M:Gadget.Apply",
          "syntax": "ExpressionStatement",
          "tokens": "total = 1;"
        },
        "payload": { "bind": [{ "id": "sample::update" }] }
      }
    ]
  }]
}
```

C# `symbol` values are Roslyn documentation declaration IDs. Type/field/member
declarations can be selected directly; statement/expression targets add a
Roslyn `SyntaxKind` name. Optional `tokens` restrict selection using C# token
kind/text, not whitespace or comments. String literals retain their exact token
text. Default selection requires exactly one match. Repeated syntax requires
explicit `expected_matches` and one-based `match`; ambiguity never silently
selects the first occurrence. Generic explicit interface members use their
actual declared IDs rather than a handwritten symbol-name approximation.

The compiler profile is the supported standalone net10.0/C#14, nullable-enable,
unchecked, unsafe context, not an MSBuild workspace. References and replacement
defines are explicit profile inputs; omitted defines use the standalone
frontend defaults. Optional `context_sources` is an array of `{ "path": "...",
"sha256": "..." }` C# inputs needed only for resolution, not copied output.
The complete selected reference/source/define context is fingerprinted.

Use `context_imports` to share those pinned compiler-only sources. Each explicit
path names another schema-4 `source-preparation` manifest with empty `files`,
no `jar`, no reference/define overrides, and the same supported standalone
compiler profile. Only its `context_sources` and nested context imports are
selected; output edits are not inherited. Paths resolve against their declaring
manifest. Import bytes are fingerprinted, cycles and conflicting pins fail,
and shared declarations are not copied into output.

Formal inputs can be copied byte-for-byte, but binding insertion is C# only.
Native declarations and PlusCal labels are resolved directly during review;
they do not need duplicate binding comments. Formal/generated translation edits
and TLA+ mutations are rejected.

Explicit C# negative controls use a file's `mutations` array. Each entry has a
`target`, `replacement` and nonempty `reason`. Replacements are checked in the
selected C# compilation, and overlapping edits are rejected. They are labeled
`comment_only: false` in the output ledger, never passed off as approved code.
Files without edits are copied byte-for-byte; edited sources preserve their
encoding, BOM and line endings. `payload` is the typed annotation object, not a
string of directives. Serialization uses standard JSON escaping, including
comment-terminator escaping, before insertion into the C# block comment.

Preparation writes `prepared-sources.json` only after input rechecks and output
publication. It records source/output hashes, selectors, typed payloads, mutations,
compiler inputs and producing tools. Output is exclusive: never overwrite a
previous preparation. `check-prepared-sources` is readonly and rejects changed
inputs, missing/changed outputs and unexpected files. Failed or partial output
is not reusable evidence; choose a fresh directory after correcting the cause.

Exit 0 establishes copy preparation or recorded-byte readiness only, not valid
capture metadata, model adequacy, atomicity, trace conformance, refinement or
acceptance. Use the normal review/instrumentation workflow afterward.

## Review commands

```powershell
.\TlaLink.ps1 inspect --annotation-version 4 --source .\examples\Demo.cs --scope .\examples\demo-scope.json --target demo --model .\examples\Demo.tla --jar .\.tools\tla\v1.8.0\tla2tools.jar
.\TlaLink.ps1 review --annotation-version 4 --source .\examples\Demo.cs --scope .\examples\demo-scope.json --target demo --model .\examples\Demo.tla --jar .\.tools\tla\v1.8.0\tla2tools.jar --step demo::Admit
.\TlaLink.ps1 check --annotation-version 4 --source .\examples\Demo.cs --scope .\examples\demo-scope.json --target demo --model .\examples\Demo.tla --jar .\.tools\tla\v1.8.0\tla2tools.jar --snapshot .\review-new.json
.\TlaLink.ps1 check --annotation-version 4 --source .\examples\Demo.cs --scope .\examples\demo-scope.json --target demo --model .\examples\Demo.tla --jar .\.tools\tla\v1.8.0\tla2tools.jar --baseline .\review-new.json
.\TlaLink.ps1 inputs --model .\examples\Demo.tla --jar .\.tools\tla\v1.8.0\tla2tools.jar
```

`inspect`, `check` and `review` produce `source-review` packets with independent
`links_status`, `coverage_status` and `conformance_status`. Coverage still needs
review; these commands do not execute C# or establish conformance.
`inputs` produces a `model-inputs` readiness record in the same current schema.
It does not require annotations or C#.

Common options:

| Option | Meaning |
|---|---|
| `--source`, `--model` | Explicit file/directory; repeatable, maximum 1,000 files per language |
| `--scope` | Schema-4 correspondence scope for TLA review/capture; optional on source inventory |
| `--target` | Explicit TLA+ alias; its one root must agree with any `--model` |
| `--jar` | Required trusted local JAR |
| `--reference` | Additional C# metadata reference; repeatable |
| `--define` | Full replacement preprocessor-symbol set; repeatable |
| `--step` | One qualified common step ID (`scope::name`), required for `review` |
| `--baseline` | Conservative added/removed/changed-input detection |
| `--snapshot` | Exclusive new report, never overwrite |
| `--format` | `json` (default), `text`, or `summary` |
| `--timeout` | 1..7200 seconds; default 30, per subprocess stage |

Default C# context is Debug, nullable-enabled C# 14/net10.0, with runtime
framework references and the standard Debug/TRACE/framework symbols.
`WINDOWS` is not automatically defined for a plain net10.0 target.
This is an explicit standalone compilation, **not** a product MSBuild workspace.
Unresolved compilation produces TL202/incomplete and blocks instrumentation.
Reflection, aliasing, external calls and accesses outside supplied files retain
review obligations. Interference analysis compares resolved field symbols,
not field-name text.

## One maintained markup: `@link`

For the vocabulary specification, see the
[source correspondence vocabulary](docs/proposals/source-correspondence.md)
and its [synthetic conformance cases](docs/proposals/source-correspondence-cases.md).
The conformance matrix includes design controls as well as behavior exercised
by the executable suites; it is not itself a test result.

Declare `"annotation_version": 4` once in the root `analysis-project`.
All imported components, C# source comments and formal endpoint comments use that grammar;
components cannot select or override it. JSON `schema_version` remains 4 and
is independent of the annotation grammar version.

Standalone `inspect-source`, `inspect`, `check`, `review`, `instrument`, `validate-trace` and the
three integrated analysis commands require `--annotation-version 4`. Project
workflows pass their declared version to child commands. Missing or unsupported
versions fail explicitly; neither filenames nor comments select a version.
Commands such as `inputs`, which do not interpret bindings, need no annotation
version. Standalone P commands read the annotation version from their explicitly
selected correspondence scope instead of a CLI version option.
The marker denotes correspondence, not general implementation-language
support: the source integration remains C#, and native checker configurations
and claims remain backend-specific.

Version 2's `model`/`state`/`step` syntax, version 3's source-side `target`
references and old `@tla` headers are rejected. Both sides independently declare
the same unique `scope::name` ID. Attach formal markers to authored declarations
or PlusCal labels, never generated translation bodies. Regenerate translations
explicitly and collect new successor evidence,
preserving historical receipts and independent configuration/requirement pins.
Direct TLA+ variable endpoints use SANY's identifier span: place the marker after
`VARIABLE`/`VARIABLES` and immediately before that variable's name. Observation
operators remain useful for derived or entity-indexed projections.

Use a native block comment containing `@link` followed by a standard JSON object.
`bind` participates on either side; `capture` and `exclude` are C# only.
`System.Text.Json`
deserializes strict typed records, including enum-valued capture choices;
unknown fields, duplicate properties, malformed JSON and invalid array entries
fail explicitly. No XML or semicolon-language alternative is accepted.

```csharp
class Lane {
    readonly object gate = new();
    /* @link {
  "bind": [
    {
      "id": "credit::available"
    }
  ],
  "capture": [
    {
      "binding": "credit::available",
      "guard": "gate",
      "type": "int",
      "units": "frames"
    }
  ]
} */
    int available;

    void Acquire() {
        lock (gate) {
            /* @link {
  "bind": [
    {
      "id": "credit::acquire"
    }
  ],
  "capture": [
    {
      "binding": "credit::acquire",
      "actor": "1",
      "operation": "1",
      "point": "commit",
      "classification": "transition",
      "observe": [
        "credit::available"
      ]
    }
  ]
} */
            available--;
        }
    }
}
```

Declare formal aliases once in a schema-4 `correspondence-scope`:

```json
{
  "schema_version": 4,
  "kind": "correspondence-scope",
  "annotation_version": 4,
  "targets": [
    {"id":"credit","language":"tla","inputs":["Credit.tla"],
     "required_bindings":["credit::available","credit::acquire"]}
  ],
  "omissions": []
}
```

Targets select `tla`, `dafny` or `p`. Inputs are explicit files, relative to the
scope document; TLA+ selects exactly one root and pins its native import closure.
TLA+ targets declare `required_bindings` so deleting one inline mapping cannot
silently reduce the selected correspondence. Dafny/P select required IDs in
their backend configurations instead.
Bindings use the same unique `alias::correspondence-name` on both sides.
Each side's attachment is resolved independently by Roslyn, SANY/PlusCal,
Dafny's resolved AST or the authoritative P frontend. Declaration spelling need
not match the correspondence name. Missing and duplicate counterparts fail.
A class can participate in several independent correspondences; there is no
implicit active model or cross-model proof.

TLA review/instrumentation commands require `--scope` and `--target`; optional
`--model` must agree with the selected target. `validate-trace` reuses the exact
scope pinned during instrumentation. Trace project stages declare `scope` and
`target`; `doctor` and execution validate the same selection.

Integrated manifests declare `scope`, C# `sources`, and selected binding IDs
with their adapters/cases. P analysis manifests declare `scope` when they select
source bindings. Source/formal endpoints live only in C#, not duplicated in
those manifests. Native P model inputs must agree with their selected target.

A review-only `bind` needs no `capture`. It can attach to declarations,
statements or supported invocation expressions, including callbacks.
State observations can be nullary TLA+ operators or entity-indexed unary
operators. `capture` currently supports only the TLC execution adapter; it is
not an Apalache trace adapter, Dafny oracle or P monitoring adapter.

Invocation-expression bindings cannot declare an executable `point`; automatic
capture still requires a supported statement. Locks enclosing a callback's
creation site are not treated as held when its body runs. A callback must
acquire its own guard for generated shared-field capture.
State capture options are `guard`, `type` (`int`, `bool`, `string`) and `units`.
Action capture options:

- `actor`, `operation`, `connection`: scalar source expressions identifying the
  logical operation. Actor/operation are required for capture; connection defaults
  to integer zero. The user must choose meaningful stable identities.
- `entity`: optional explicit scalar object/lane identity. Entity-indexed
  observations use it as their model-operator argument; separate entities may
  hold independent guards. One entity cannot silently switch observation guards.
- `point`: `invoke`, `commit`, `complete`, `fault`, `cleanup`.
  Invoke observations precede the selected statement; commit/complete follow
  successful execution. A fault observation on an expression statement records
  its exception and rethrows it; declarations with introduced variables and
  source-lock fault capture are unsupported.
  On one synchronous reference-type `using` statement/declaration, cleanup
  records successful implicit `IDisposable.Dispose`; fault records a disposer
  exception and rethrows it. Acquisition is evaluated once, failed acquisition
  emits no disposal event, null skips disposal, and nested lifetimes retain
  reverse cleanup order across awaits, returns and body exceptions. This
  selected compiler lowering does not support await-using, value-type/pattern
  disposal, boxing/user-defined conversions, multiple declarators or source
  locks. Cleanup/fault scheduling hooks are unsupported. Scope exit alone is
  never reported as a successful resource return or method completion.
- `classification`: `transition`, `stutter`, `unresolved`. Unresolved blocks
  instrumentation. Stutter preserves **all** model state.
- `observe`: a JSON array of state-binding IDs; captures typed source fields.
- `arguments`: a JSON array of 1..8 scalar local, parameter or literal C#
  expression strings, for example `["count","enabled","label"]`. C# string
  literal expressions may contain commas. Fields, getters, calls, indexers, reference
  objects, named/ref/out argument syntax and non-scalar types are rejected.
  An out parameter's scalar value can be captured after its assignment.
- `guard`, `reason`: review claims, not proofs of protection.
- `hook`: optional `before`/`after` scheduling callback, separate from capture.
  Generation rejects hooks inside held source locks.
- Target `hidden_actions` entries explicitly name native endpoints and reasons.
  These permit internal model transitions between observations; they are not
  implementation stutters. Hidden and observed mappings cannot overlap.

`exclude` entries require `id`, a `from` array of explicit aliases and `reason`,
and attach to the next declaration, statement or block. They do not remove code
from compilation/execution, source fingerprints or competing-access inspection.
Same-target bindings inside excluded regions and overlapping exclusions fail.

Scope `omissions` rules require `id`, `targets`, exact Roslyn method declaration
IDs in `calls`, `reason` and explicit boundary `assumptions`. They classify
synchronous void invocation statements without a comment on every log call.
They do not omit receiver/argument evaluation, transitively match wrappers,
erase awaits/disposal or prove logger effects harmless. Known modeled-state
conflicts fail; unresolved callee/dispatch effects remain visible obligations.
Source inventories with `--scope` and native review packets retain dispositions.

Model actions take zero identity parameters, actor, actor+operation, or
actor+operation+entity in that order, followed by any declared typed `arguments`.
Three identity parameters require an explicit captured entity; data arguments
do not double as entity identities. With `arguments="count,enabled"`, for example,
`Apply(actor, op, count, enabled)` receives two identity and two data parameters.
The trace binds exact argument count and int/bool/string types. Argument values
do not participate in operation/guard identity keys, create causal edges or
replace guarded shared-field observations. Hidden actions retain the existing
0..3 identity-only convention; no data domains are inferred for them.
Capture expressions exclude calls, getters, indexers and mutations.
Enum-valued identities are lowered to their integral value; UInt64-backed enums
are rejected because trace integers are signed. Mutable enum identity fields
still require an actually held guard. Enum state observations are not supported.
`actor=this` or `entity=this` gives a class receiver an execution-local reference
identity without calling user equality/hashing. Struct/interface `this`
identities are rejected. The `object:` prefix is reserved for these identities.
Simple class-reference fields, parameters and locals can likewise be identities;
null or boxed-value references fail explicitly, and getters/instance chains
remain unsupported. String identities retain their literal values.
Identity numbers are nominal labels, not causal ordering or domain IDs.
Independent pumps on one receiver still need distinct operation identities.

`operation="$call"` generates one collision-free local identity per invocation
of the enclosing block-bodied function, shared by its observation points and
preserved across awaits. Different concurrent invocations have different
identities; the `call:` prefix is reserved. Allocation does not add causal edges
or establish caller/callee, Task or queue handoffs. Use explicit handoffs where
those edges matter.

Automatic capture supports ordinary local declarations as well as expression
statements in blocks/switch sections. Using declarations are not completion
points: their disposal happens later and cannot be captured as if the
declaration completed the resource lifetime.
Return, throw and switch statements support invoke-only observation immediately
before control transfers; automatic post-return or exceptional capture is not
inferred.
Observed primitive fields must share an actually held readonly reference-field
lock. Cross-lock snapshots are rejected. This does not prove that *all other*
accesses obey that lock.

## Instrument, execute, validate

The generated TLC trace wrapper imports its own arithmetic and finite-set
operators; the user's model need not import them. A completed whole-trace
rejection reports diagnostic `TL511`. Project negative controls can require
`"expected": "rejected", "diagnostic": "TL511"`; syntax/tool/deadline failures
remain incomplete and cannot satisfy that control.

For a runnable example, use the `lanes` [project workflow](#project-workflow).
Its trace stage runs all three low-level commands and generates/restores the
standalone capture project automatically:

```powershell
.\TlaLink.ps1 run --project .\examples\Workflow\tlalink.project.json --scenario lanes --output .\Results\lanes-trace-new
.\TlaLink.ps1 accept --project .\examples\Workflow\tlalink.project.json --run .\Results\lanes-trace-new
```

For a custom low-level integration, `instrument` writes separate rewritten
copies, runtime source and `bindings.json`. Supply a standalone net10.0
executable project whose compiler settings match the instrumentation context
and whose compile inputs include those generated files. Restore it before
`execute`; call `TraceRuntime.Complete(args[0])` only after the workload has
been joined. The [lane workload](examples/Workflow/src/Lanes/Lanes.cs) demonstrates
completion and explicit handoffs.

`execute` takes `--manifest`, `--project`, a new `--trace` file and a new
`--output` directory. `validate-trace` takes that manifest, the
`execution.json` receipt via `--execution`, the trace, root `--model`,
approved `--config`, pinned `--jar` and a new output directory.
Inputs and existing output directories are never overwritten.

`execute` captures actual compiler source/reference hashes, compiler context,
SDK, build/run commands and the loaded entry binary. It checks generated inputs
were really compiled, and that target/language/nullable/defines match the
instrumentation context. Current execution support is one net10.0 target,
not arbitrary product projects. Additional entry-point/compiler inputs are
recorded too. Projects and the producing host are trusted, not authenticated.

Trace checking starts at configured `Init` and uses `Next` (override with
`--init`/`--next`). It keeps one consistent hidden-state trajectory while
exploring causal-compatible event orderings. Logging sequence is an identity,
not a global model ordering. Recorded operation, actual guard-object and
managed-thread orders are enforced. Manual `TraceRuntime.Emit` returns an event
ID and accepts explicit predecessor IDs for additional known causal edges.
`CaptureHandoff`/`AcceptHandoff` explicitly carry known communication edges;
see the two-lane example. They do not infer queue/task causality.
Task creation/join, queues and cross-thread handoffs are **not automatically
fully instrumented**; review/add their edges before interpreting that scope.

Supported trace configs contain only single-line `CONSTANT(S)` declarations and
single-line SPECIFICATION/INVARIANT/PROPERTY/CHECK_DEADLOCK directives. Original
properties are not re-proved: finite-prefix inclusion uses configured Init/Next
without temporal fairness. Views, symmetry and graph constraints are rejected.
One connection and 1..1000 events are supported. Parser failures, unsupported
inputs, missing completion and timeouts are incomplete, never conformance.

Result meanings:

- `conforms`: TLC found a complete matching finite-trace witness.
- `rejected`: TLC exhausted the configured finite graph without a witness,
  subject to ordinary TLC fingerprint omission risk.
- `incomplete`: no reliable result. Inspect retained logs.

Outputs include the TLC wrapper/config/log, result and content-addressed input
copies. Hashes establish recorded consistency, not host authenticity or a proof
that the compiler implements C# correctly.

## Tests and evidence

`--trace` selects scalar argument, entity/guard, causal-order and whole-trace
controls. It requires only the selected TLC JAR and pinned Java toolchain, not
Dafny, Apalache or P.

`--evidence` selects focused native regression, standalone Dafny, TLC-resource,
trace, acceptance/report and retention controls; it requires the selected
Dafny, TLC and pinned Java tools. `--core` also exercises the console regression
route without formal backends.

Consumer checkers can invoke this tool and record .NET runtime metadata.
Archive consistency, current-file applicability and project acceptance are
separate results. Preserve frozen model/source/evidence archives and do not
relabel prior runs as fresh.

The .NET suite covers grammar/encoding, semantic scope, review/coverage/drift,
capture rejection, SANY/PlusCal/import/freshness and timeout safeguards, actual
compiled executions, mutation rejection, causal ordering, explicit hidden steps
and whole-trace hidden-state consistency. Integrated controls additionally cover
Dafny resolution, verification, assumption auditing, generated-oracle comparison
and explicit cross-model correspondence obligations. See the test result
emitted by the suite for the exact current count.

SANY/PlusCal controls run inside the backend test group. Reports retain source
compiler resolution, execution receipts and independent evidence dimensions.
