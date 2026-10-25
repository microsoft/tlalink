# TlaLink

A .NET 10 C# tool for reviewing C#/TLA+/Dafny/P correspondence, checking selected
executions against TLA+, and comparing C# adapters with verified Dafny oracles.
The TlaLink name and low-level commands are retained. All maintained public
JSON documents use **schema 4** and a document `kind`; older formats are
explicitly rejected, not silently upgraded.

- Roslyn supplies C# syntax, symbols, field accesses and capture validation.
- The upstream Java SANY/PlusCal parsers remain authoritative for TLA+,
  explicit labels, imports and translation freshness.
- TLC, not a new interpreter, checks a whole finite recorded execution.
- The selected local Dafny 4.11.0 frontend resolves Dafny declarations/includes;
  its verifier, assumption auditor and C# compiler remain authoritative.
- The selected local P 3.1.0 frontend resolves P declarations. PChecker, PEx,
  PSym, PVerifier and PObserve retain separate native evidence claims; P is
  not routed through Dafny or interpreted as TLA+.

**Neither structural links, matching traces nor passing oracle tests prove all
C# executions, atomicity, general refinement or liveness. Dafny verification
proves the supplied Dafny program, not the linked C#.** See [DESIGN.md](DESIGN.md) for the
precise evidence boundary and [AGENTS.md](AGENTS.md) for the agent workflow.

## Requirements and build

Windows x64 and Linux x64 are supported. Use the installed .NET 10 SDK/runtime,
the pinned local Temurin JDK **25.0.4.1+1**, and a trusted local TLA tools JAR.
Setup scripts require PowerShell 7 (`pwsh` on Linux).
Provision Java explicitly before running Java-backed commands:

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

Current TLA validation uses the pinned
1.8.0 JAR:
`D15655EEAFF4B3F7B3D65C46B838BC0F35D131B2EF4B1E473689B62C98D4726F`.

```powershell
dotnet build .\TlaLink.sln
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj
```

[global.json](global.json) selects SDK 10.0.204 with feature-band roll-forward.
There are no third-party NuGet dependencies: Roslyn references come from the
selected SDK. [NuGet.Config](NuGet.Config) retains the approved package proxy.
No product-tree build is involved. This tool does not upload code.

## Workspace and publication boundary

Open [TlaLink.sln](TlaLink.sln) for the public tool and standalone console tests,
or [TlaLink.code-workspace](TlaLink.code-workspace) to browse the whole folder
in VS Code, including ignored local work.

- `src`, `tests` and `examples` contain maintained generic code and synthetic
  inputs. They have no dependency on private work packages.
- `local` is a Git-ignored home for private consumer projects, copied sources,
  milestones, proofs and evidence. It is not part of the public solution.
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
`TLALINK_TLA_JAR`, `TLALINK_DAFNY` and `TLALINK_P_COMPILER` overrides.
On Linux the Dafny launcher is `.tools\dafny\dafny`; P's .NET 8 runtime
must also be installed for native P tests.
Missing prerequisites fail explicitly. For backend-independent tests:

```powershell
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . --core
```

Automatic backend installation is a planned toolchain-lifecycle feature.
The explicit Java setup script above is not automatic backend installation.

## Continuous integration

[GitHub Actions](.github/workflows/ci.yml) runs on pull requests, pushes to
`main` and manual dispatches using Ubuntu 24.04 x64. It builds Release with the
.NET SDK selected by `global.json`, runs backend-independent controls, then
explicitly provisions the pinned Java, TLC 1.8.0, Dafny 4.11.0 and P 3.1.0 tools
before running the complete console suite. The .NET 8 SDK supplies P's runtime.
Archive downloads are checksum-verified; third-party actions use commit pins.
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

The single executable and public `TlaLink` namespace remain unchanged.
Sources under `src\tlalink` are grouped by responsibility:

| Directory | Responsibility |
|---|---|
| `Common` | C# frontend, diagnostics, capture/execution, integrated evidence and workflow/acceptance |
| `PLang` | Native P frontend, bindings, backend execution, result classification and evidence reader |
| `Tla` | SANY model scope, source annotations, structural review and shared upstream Java adapter |
| `Tlc` | Native TLC workflow and causal whole-execution matching |
| `PlusCal` | Algorithm discovery and translation-freshness diagnostics in the shared TLA frontend |
| `Dafny` | Native Dafny frontend, contracts, verification/audit and executable oracles |

`Program.cs` remains the composition root. Native bridge source assets retain
their flat deployed names; moving source files does not change CLI behavior,
namespaces, historical evidence or generated capture contracts.

Shared stage/preflight/evidence contracts are assessed in
[the design proposal](DESIGN.md#proposed-shared-backend-contracts-assessment-only).
They are not implemented interfaces or a universal proof result.
Tool selection follows each obligation, including P-first candidates, rather
than requiring reuse of prior TLA+/Dafny work.

## First-class P workflow

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
source bindings, assumptions and exploration bounds. C# endpoints use the
same resolved symbol convention as Dafny bindings; P endpoints use qualified
native names such as `Credit.ReturnCredit`. A binding is a reviewable
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
hash as `artifact`. P 3.1.0 no longer exposes the legacy PSym compiler, so PSym
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

Run the focused metadata/serialization controls with:

```powershell
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . --diagnostics
```

## Project workflow

Start with [the maintained project](examples/Workflow/project.json) and its
separate [acceptance policy](examples/Workflow/requirements.json):

```powershell
.\TlaLink.ps1 doctor --project .\examples\Workflow\project.json
.\TlaLink.ps1 plan --project .\examples\Workflow\project.json --scenario lanes
.\TlaLink.ps1 run --project .\examples\Workflow\project.json --scenario lanes --output .\Results\lanes-new
.\TlaLink.ps1 accept --project .\examples\Workflow\project.json --run .\Results\lanes-new --output .\Results\lanes-acceptance-new.json
.\TlaLink.ps1 explain --project .\examples\Workflow\project.json --run .\Results\lanes-new --format text
```

`doctor` checks strict configuration, tool hashes, executable availability,
selected compiler settings and input existence. It does not run proofs or
resolve native signatures. `plan` shows dependencies, capability boundaries,
required outcomes and pending/stale reviews. `run` executes fresh subprocesses
in dependency order, retaining independent stages after an unrelated failure.
Dependencies require a successful predecessor; expected rejection controls
should be independent stages. Existing output directories are never reused.

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

Supported stage kinds are `inspect`, `verify`, `oracle`, `inputs`, `tlc` and
`trace`. Integrated stages reference an existing binding manifest. TLA stages
select models; TLC/trace select exactly one root model and config. Trace stages
select copied C# sources including their entry point; the generated standalone
project passes the trace path as the first entry-point argument, followed by
the stage's optional `arguments`. No arbitrary shell commands are accepted.
The compiler profile is explicit: net10.0/C#14, nullable enabled, unchecked,
unsafe enabled, explicit references and optional replacement defines.
Unsupported settings fail rather than approximate another build.

### Execution is not acceptance

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

Acceptance exit 0 means the declared requirements are satisfied, 1 means
rejected/inconsistent evidence or unsatisfied requirements, and 2 means stale
or incomplete evidence. Expected build failures and timeouts cannot satisfy
rejection controls. Archive consistency, current applicability, execution,
individual outcomes and acceptance remain separate fields.

### Persistent review decisions

Review decisions live outside the project/run files so review after execution
does not itself invalidate the run. For example, after inspecting the actual
cleanup adapter and evidence:

```powershell
.\TlaLink.ps1 record-review --project .\examples\Workflow\project.json --obligation cleanup-adapter --reviewer "<alias>" --rationale "<review findings>" --evidence "..\..\Results\cancellation-new\cleanup\result.json"
```

Evidence paths above are project-relative. Decisions are append-only under
the configured review directory. The latest decision is authoritative; its
claim/assumption/input identity and supporting evidence hashes must still
match. Changed claims or inputs make reviews stale. Acceptance reports retain
the decisions used. Reviewer names are explicit assertions, not authenticated
identities, and accepted review is not an automatic refinement proof.

### One current public format

All public manifests, receipts, traces, packets, plans, reviews and reports use
`schema_version: 4` plus a `kind` discriminator. Each kind has a specific shape;
tool-specific evidence is not flattened into a generic proof claim. Unknown
versions, kinds, duplicate fields and unknown project options are errors.
Source annotation grammar remains `@tla 2`; that grammar number is not a JSON
schema version. Native backend output and .NET runtime/project configuration
retain their upstream formats.

Maintained examples/producers/readers/tests are updated together. There are no
legacy JSON acceptance branches. Frozen source/model profiles and historical
evidence remain untouched and are not relabeled as current evidence.

## Integrated TLA+ and Dafny workflow

Select the trusted local **Dafny 4.11.0** distribution explicitly with `--dafny`.
It must contain its frontend assemblies and bundled solver; a global tool shim
alone is not sufficient. No package installation or public service is involved.
Using the repository-local tool cache:

```powershell
.\TlaLink.ps1 inspect-integrated --bindings .\examples\Integrated\bindings.json --dafny .\.tools\dafny\Dafny.exe --jar .\.tools\tla\v1.8.0\tla2tools.jar --output .\Results\integrated-review-new
.\TlaLink.ps1 verify-integrated --bindings .\examples\Integrated\bindings.json --dafny .\.tools\dafny\Dafny.exe --jar .\.tools\tla\v1.8.0\tla2tools.jar --output .\Results\integrated-proof-new
.\TlaLink.ps1 test-integrated --bindings .\examples\Integrated\bindings.json --dafny .\.tools\dafny\Dafny.exe --jar .\.tools\tla\v1.8.0\tla2tools.jar --output .\Results\integrated-test-new --timeout 60
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
| `read-integrated` | Archived byte consistency and optional current-file applicability, separately from recorded outcomes; no rerun or milestone acceptance |

Integrated commands accept `--reference`, `--define`, `--timeout`, `--format`
and `--baseline` (a prior current-format result). A baseline detects changed, added or
removed maintained inputs; it does not replace review. `--jar` is required only
when the manifest selects TLA+ models. Existing TLA-only commands retain their
command names and do not require Dafny; their JSON uses the same current schema.

### One explicit binding manifest, not another source annotation language

[The example manifest](examples/Integrated/bindings.json) uses kind `integrated-bindings`:

- `sources`, `proofs`, `models`: explicit file/directory scopes, relative to
  the manifest (not the caller); `models` can be empty for Dafny-only use.
- `bindings`: unique `id`, C# `source` symbol, fully qualified `dafny`
  declaration, nullable C# `adapter`, and explicit `cases` with unique IDs and
  typed `inputs`. No inference from same-named identifiers.
- C# symbols can use `Namespace.Type.Method`, a full Roslyn display signature,
  or documentation IDs such as `M:Namespace.Type.Method(System.Int32)` to
  disambiguate overloads. Review-only bindings can target fields/properties.
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

There is no need to add Dafny comments to existing source. Existing `@tla 2`
annotations continue to supply optional TLA+ links. The example exercises
multi-output credit return, a mathematical-integer function, and Boolean
functions. Its accepted-unit-return correspondence deliberately does not cover
rejected or non-unit returns.

### Separate evidence, never one "verified C#" badge

The integrated packet independently reports `links_status`, `coverage_status`, `proof_status`,
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
temporary directories and removed after the suite.

## Review commands

```powershell
.\TlaLink.ps1 inspect --source .\examples\Demo.cs --model .\examples\Demo.tla --jar ..\tools\v1.8.0\tla2tools.jar
.\TlaLink.ps1 review --source .\examples\Demo.cs --model .\examples\Demo.tla --jar ..\tools\v1.8.0\tla2tools.jar --step Admit
.\TlaLink.ps1 check --source .\examples\Demo.cs --model .\examples\Demo.tla --jar ..\tools\v1.8.0\tla2tools.jar --snapshot .\review-new.json
.\TlaLink.ps1 check --source .\examples\Demo.cs --model .\examples\Demo.tla --jar ..\tools\v1.8.0\tla2tools.jar --baseline .\review-new.json
.\TlaLink.ps1 inputs --model ..\Models\CreditWaiters.tla --jar ..\tools\v1.8.0\tla2tools.jar
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
| `--jar` | Required trusted local JAR |
| `--reference` | Additional C# metadata reference; repeatable |
| `--define` | Full replacement preprocessor-symbol set; repeatable |
| `--step` | One step ID or `model:step:ID`, required for `review` |
| `--baseline` | Conservative added/removed/changed-input detection |
| `--snapshot` | Exclusive new report, never overwrite |
| `--format` | `json` (default) or `text` |
| `--timeout` | 1..7200 seconds; default 30, per subprocess stage |

Default C# context is Debug, nullable-enabled C# 14/net10.0, with runtime
framework references and the standard Debug/TRACE/framework symbols.
`WINDOWS` is not automatically defined for a plain net10.0 target.
This is an explicit standalone compilation, **not** a product MSBuild workspace.
Unresolved compilation produces TL202/incomplete and blocks instrumentation.
Reflection, aliasing, external calls and accesses outside supplied files retain
review obligations. Same-named unrelated fields are no longer lexical matches.

## One maintained markup: `@tla 2`

Use C# block comments or TLA+ comments, with semicolon-terminated directives.
Quoted values use JSON escaping. Unknown versions/options and duplicate options
are errors. IDs begin with a letter/underscore and may contain letters, digits,
underscores, dots and hyphens. Numeric expressions must be quoted.

```csharp
/* @tla 2 model CreditLane; */
class Lane {
    readonly object gate = new();
    /* @tla 2 state Available guard=gate type=int units=frames; */
    int available;

    void Acquire() {
        lock (gate) {
            /* @tla 2 step Acquire actor="1" operation="1" connection="1"
               point=commit classification=transition observe=Available; */
            available--;
        }
    }
}
```

In TLA+, annotate one module namespace and operators; explicit PlusCal labels
also supply step endpoints automatically. State observations can be
zero-argument operators, or one-argument entity-indexed operators when the
step declares an explicit scalar `entity` identity:

```tla
(* @tla 2 model CreditLane; *)
(* @tla 2 state Available type=int; *)
Available == available
(* @tla 2 step Acquire; *)
Acquire == ...
```

Each annotated C# type has one model namespace; nested types may override it.
Review-only `model`, `state` and `step` bindings do not need executable options.
State options are `guard`, `type` (`int`, `bool`, `string`) and `units`.
Step options:

- `actor`, `operation`, `connection`: scalar source expressions identifying the
  logical operation. Actor/operation are required for capture; connection defaults
  to integer zero. The user must choose meaningful stable identities.
- `entity`: optional explicit scalar object/lane identity. Entity-indexed
  observations use it as their model-operator argument; separate entities may
  hold independent guards. One entity cannot silently switch observation guards.
- `point`: `invoke`, `commit`, `complete`, `fault`, `cleanup`.
  Invoke observations are before the statement/model action; the others are
  after. Automatic exceptional/fault capture is deliberately unsupported.
- `classification`: `transition`, `stutter`, `unresolved`. Unresolved blocks
  instrumentation. Stutter preserves **all** model state.
- `observe`: comma-separated state IDs; captures typed source fields.
- `guard`, `justification`: review claims, not proofs of protection.
- `hook`: optional `before`/`after` scheduling callback, separate from capture.
  Generation rejects hooks inside held source locks.
- Model-only `visibility=hidden` requires a `justification`. This explicitly
  permits internal model transitions between observations; it is not the same
  as an implementation stutter. Hidden and observed mappings cannot overlap.

Model actions currently take zero arguments, actor, or actor+operation in that
order. Capture expressions exclude calls, getters, indexers and mutations.
Observed primitive fields must share an actually held readonly reference-field
lock. Cross-lock snapshots are rejected. This does not prove that *all other*
accesses obey that lock.

## Instrument, execute, validate

```powershell
.\TlaLink.ps1 instrument --source .\candidate\LaneSendCredits.cs --model .\examples\Credits\Credits.tla --jar ..\tools\v1.8.0\tla2tools.jar --output .\generated-new
```

This writes separate rewritten copies, runtime source and a binding manifest.
Inputs and existing output directories are never overwritten. Add a standalone
net10.0 executable project/entry point to that directory, restore it, and call
`TraceRuntime.Complete(args[0])` only after the workload has been joined:

```powershell
dotnet restore .\generated-new\Pilot.csproj --configfile .\NuGet.Config
.\TlaLink.ps1 execute --manifest .\generated-new\bindings.json --project .\generated-new\Pilot.csproj --trace .\trace-new.json --output .\execution-new
.\TlaLink.ps1 validate-trace --manifest .\generated-new\bindings.json --execution .\execution-new\execution.json --trace .\trace-new.json --model .\examples\Credits\Credits.tla --config .\examples\Credits\Credits.cfg --jar ..\tools\v1.8.0\tla2tools.jar --output .\validation-new
```

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

Consumer checkers can invoke this tool and record .NET runtime metadata.
Archive consistency, current-file applicability and milestone acceptance are
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
