# TlaLink design

See [README.md](README.md) to get started, [REFERENCE.md](REFERENCE.md) for
commands/schema and [AGENTS.md](AGENTS.md) for
maintenance instructions. All work remains outside the product tree.

## Agent-first operating model

TlaLink exposes deterministic CLI workflows and structured evidence for a coding
agent to operate. It does not include an agent, language-model runtime or hosted
analysis service. The human-facing README explains the value and workflow;
the reference documents the commands and formats; AGENTS.md gives operational
and maintenance safeguards.

The agent helps scope a concern, prepare models/bindings, run native tools and
explain results. The user reviews the intended claim, abstraction, assumptions
and acceptance requirements. Neither an agent explanation nor a successful
backend run discharges missing correspondence or coverage obligations.
The project workflow separates planning, execution, evidence validation and
acceptance so those distinctions remain inspectable beyond the conversation.

Tool provisioning is explicit setup, separate from analysis. TlaLink's analyses
run locally; the surrounding agent's data handling is outside this tool's
guarantees. Trusted hosts, build projects and asserted reviewer identities
remain trust boundaries, not authentication.

The implementation-facing design targets the C#/.NET toolchain, not a
language-neutral source abstraction. Roslyn supplies C# resolution and capture
validation; compiler context, compiled adapters and loaded .NET binaries tie
execution evidence to that implementation. TLA+, Dafny and P remain distinct
formal backends supporting the C# workflow.

## Architecture

Sources are physically grouped into `Common`, `PLang`, `Tla`, `Tlc`,
`Apalache`, `PlusCal` and `Dafny` under `src\tlalink`. This is a responsibility split in
one executable, not independently versioned plugins. Public namespaces
and flat deployed bridge asset names remain stable. The upstream Java adapter
is shared by SANY and PlusCal and remains under `Tla`; PlusCal-only discovery
and freshness handling are in a separate partial frontend under `PlusCal`.

| Component | Responsibility |
|---|---|
| `Contracts.cs` | Evidence records, strict decoding, original-byte hashes, exclusive writes |
| `ErrorCode.cs` / `DiagnosticCatalog.cs` | Attributed diagnostic IDs/default severities, validated cached metadata and stable string output |
| `Markup.cs` | The single maintained `@tla 2` grammar/comment envelopes |
| `CSharpFrontend.cs` | Explicit Roslyn compilation, attachments, field symbols and boundaries |
| `TlaBackend.cs` | Bounded fresh Java processes, normalized spans, dependencies and freshness |
| `TlaIndex.java` | Authoritative SANY/PlusCal adapter |
| `ModelScope.cs` | Model annotations, explicit label bindings and operator arities |
| `Review.cs` | Scoped uniqueness/link/coverage diagnostics and review questions |
| `Instrumentation.cs` | Validated, separately generated source captures and binding manifest |
| `Runtime/TraceRuntime.cs` | Primitive events and actual operation/guard/thread ordering |
| `Execution.cs` | Observed build/run closure and source-to-loaded-binary receipt |
| `TraceValidation.cs` | TLC-backed existential whole-trace matching |
| `DafnyContracts.cs` | Strict explicit bindings and independent evidence dimensions |
| `DafnyIndex.source.cs` | Isolated bridge compiled against the selected Dafny frontend; resolved declarations and include closure |
| `DafnyBackend.cs` | Pinned local distribution, native verification/audit, bounded processes and logs |
| `IntegratedReview.cs` | Shared Roslyn scope, source/proof links, optional existing TLA review and explicit cross-model obligations |
| `DafnyConformance.cs` | Verified concrete-call wrappers, upstream-generated C# oracle/runtime, explicit Roslyn compilation and adapter comparisons |
| `IntegratedEvidence.cs` | Independent archived-consistency/current-applicability reader |
| `Schema.cs` | Single current public JSON schema version |
| `ProjectContracts.cs` | Strict project, compiler profile, requirements, run and review records |
| `ProjectWorkflow.cs` | Preflight, dependency plan, bounded orchestration and immutable run provenance |
| `ProjectAcceptance.cs` | Semantic acceptance, exact requirements, current review decisions and explanations |
| `Program.cs` / `CliOptions.cs` | Strict command surface, drift and exit semantics |
| `PLang/PFrontend.cs` / `PLang/PIndex.source.cs` | Isolated authoritative P parse/typecheck and qualified declaration inventory |
| `PLang/PContracts.cs` | Strict explicit P scope, resolved source links, native claim dimensions |
| `PLang/PBackend.cs` / `PLang/PProcess.cs` | PChecker/PEx/PSym/PVerifier/PObserve native processes and bounded logs |
| `PLang/PResults.cs` / `PLang/PEvidence.cs` | Conservative backend-specific outcomes and independent immutable-evidence validation |
| `Apalache/ApalacheContracts.cs` | Strict selected safety scopes, queries, tool-distribution pins and native receipts |
| `Apalache/ApalacheBackend.cs` / `Apalache/ApalacheEvidence.cs` | Isolated native checks and independent mode/obligation/artifact validation |

## P analysis and evidence claims

P analysis uses project orchestration and independent acceptance.
Explicit `p-analysis` manifests select one
native engine and reviewed source bindings. The native compiler is pinned to
P 3.1.0; the frontend bridge pins its exact API version and never parses P
expressions itself. C# symbols reuse the existing Roslyn resolver.

Each P stage has an independently pinned configuration and exact required
claim. Schedule sampling, finite exhaustive exploration, inductive invariants
and observed log properties are not interchangeable success dimensions.
Unknown output, budgets, partial searches, unresolved solver goals, parser
errors and empty observation streams are incomplete. Property rejections
retain their own diagnostic; tool/build failures cannot satisfy them.

PChecker compiles and executes P-generated C#, retaining the actual generated
outputs. This does not execute the linked C# implementation as a P model.
PEx and legacy PSym consume explicitly pinned generated JARs: the native
PEx compiler's automatic Maven latest-version update is intentionally excluded.
PObserve consumes pinned runtime and separately generated monitor/parser JARs.
External artifact/source correspondence requires a current build review and
does not acquire an automatic provenance or refinement proof.

P manifests may share workflow review obligations with TLA+/Dafny stages.
Cross-model correspondence remains an explicit human obligation; no native
success composes a theorem across backends. PObserve's timestamp-sequenced
monitoring remains separate from TLC's causal existential whole-trace matcher.

P output archives are independent of the project run: the reader checks
original inputs, generated artifacts and native outcome semantics, optionally
checks current bytes, and never silently upgrades a historical result.

## Apalache as a selected TLA+ engine

Apalache stages are explicitly selected for typed TLA+ safety obligations,
not automatic fallback from TLC.
Native Snowcat types remain distinct from source binding annotations. SANY
still establishes the local module closure, operator arities and PlusCal
freshness. The adapter reports authoritative ASSUME declarations over loaded
modules; Apalache's initial profile rejects them without changing TLC behavior.
Only explicit nullary predicates and constant initialization are supported.
TLC configuration translation, temporal checking and simulation are excluded.

The backend pins the local 0.62.4 distribution and Java toolchain, stages the
model closure, and invokes fresh local incremental Z3/oopsla19 processes.
Native configuration is generated solely from the approved manifest and uses
an explicit JSON file. Child environment/JVM home are isolated; user-wide
configuration, JVM injection and telemetry opt-in are not inherited.
No analysis installs tools or sends source code to a service.

Successful typechecking, safety through `length` Next transitions, and supplied
invariant induction have different claims. Induction requires three successful
queries with identical constants: initiation (length 0), preservation from
the candidate invariant (length 1), and implication of all selected safety
properties (length 0). The candidate must include type/domain constraints.
Those constraints must generate native initial states as well as typecheck;
a sequence length bound alone is not a sequence assignment.
Counterexamples to inductiveness do not necessarily originate from a reachable
model state. Unknowns, deadlines and empty initial searches are incomplete;
native exit zero alone is never a completed safety claim. Each successful query
also needs a native example of exactly length + 1 states, excluding vacuous
NoError outcomes with no satisfying execution. This conservatively leaves
short terminal searches and vacuous induction obligations incomplete; intended
terminal stuttering must be modeled explicitly. Deadlocks are disabled and no
deadlock-freedom claim is made.

The independent reader rederives the obligation sequence, exact native
configuration/invocations and outcomes from archived inputs and native logs,
including nonempty ITF counterexamples for property rejections. Project
acceptance checks the exact stage/mode/configuration/tool/deadline and independent
claim requirement. A bounded pass cannot satisfy an induction or TLC requirement.
Archived validity, optional current applicability and acceptance stay separate.
Existing TLC causal trace witness/rejection semantics are unchanged; no
bounded absence of an Apalache witness is promoted to trace rejection.

## Tool selection and test setup

Select by the obligation and evidence required, not by a backend hierarchy.
The [selection guide](REFERENCE.md#choosing-tools-and-checkers) covers useful cases
and limitations for the frontends, TLC, Apalache, Dafny and each P checker.
Parsing, sampled schedules, bounded safety, finite exhaustion, induction,
concrete oracle comparisons and log monitoring retain different claims.
Unsupported dependencies or incomplete searches do not justify substituting
another checker or weakening an acceptance requirement. Native test coverage
is stated separately from adapter availability.

Production hashing streams original bytes without tool-sized allocations.
The console harness installs an internal, test-session lookup only for exact
canonical paths to its selected immutable tool distributions, pinned Java
files and deployed tool/Roslyn assemblies. It hashes them at startup and holds
read leases until teardown. Windows sharing denies writes/replacement; on
Unix a final byte recheck fails the suite if a writer ignored sharing locks.
No timestamp-only cache is used. The snapshot exists only under the temporary
test directory. Workflow child commands run through the test executable and
explicitly inherit that snapshot; ordinary CLI commands cannot enable it
through arguments or ambient environment. The production process/deadline
handling remains in use.

All other paths, including sources, approved configurations, generated files,
archived tool copies and mutation fixtures, are freshly hashed. Tool mutation
controls copy the selected binaries rather than change a leased installation.
Tests may share unchanged fixture setup and a completed bounded run for reader
controls, but parsing/checking processes remain isolated. Cache startup cost,
in-process hits and per-test/native-process durations expose setup versus
solver cost; the cache does not remove JVM startup or archive-copy costs.

## Proposed shared backend contracts (assessment only)

**Decision, 2026-10-25:** shared operational contracts would help; a shared
model language or universal proof result would not. This section is a design
proposal, not an implemented interface, a new public schema, or a prerequisite
for continuing verification. Keep the current directories and single executable.

The existing seam is already visible: `ProjectContracts.cs` owns stages, tool
pins, requirements and run receipts; `ProjectWorkflow.cs` branches on stage
kinds to pin inputs and execute them; `ProjectAcceptance.cs` dispatches semantic
validation. P has its own `PPacket`/`PEvidence`, while Dafny-backed integrated
commands use `IntegratedPacket`/`IntegratedEvidence`. Reuse those contracts and
readers rather than introduce a second workflow or replace native payloads with
an untyped dictionary.

### One narrow stage adapter, not one interface per directory

A future internal `IAnalysisBackend` should describe and handle a family of
supported stages. Its responsibilities would be:

| Operation | Shared contract | Backend-owned behavior |
|---|---|---|
| Describe | Stable engine/capability IDs, supported stage kinds, prerequisite and claim metadata | Exact tool versions, supported native constructs and capability limits |
| Prepare | Existing stage/compiler profile, canonical input identities, immutable output location, explicit resource budget and review obligations | Strict typed native manifest loading, dependency closure, source resolution and native option validation |
| Execute | A prepared request, bounded execution completion, diagnostics and a reference/hash for native evidence | Native compiler/checker/prover invocation and result classification |
| Validate evidence | Archived/current consistency assessment and supported claims, without rerunning the analysis | Native receipt/log interpretation, coverage, assumptions, proof goals and counterexample checks |

Use a fixed built-in registry at the composition root. Resolve a stage to
exactly one handler; reject unsupported or duplicate registrations explicitly.
Do not add reflection discovery, arbitrary shell commands, dynamic plugins,
dependency injection infrastructure or a backend for every folder. SANY and
PlusCal are frontends/authoring support, not independent proof engines.
Shared source-index interfaces should wait until two actual consumers need the
same operation; native inventories must retain their authoritative declarations.

Keep requests and native loaders typed. The common layer needs an evidence
reference and validated summary, not permission to inspect arbitrary JSON fields
or cast a universal `object` payload. Native configuration and evidence stay
within their current backend ownership. A semantic validator must be usable by
the independent reader, not only by the process that produced a result.

### Shared metadata is not shared proof semantics

The common summary must keep these independent dimensions:

- Execution completion: completed, timed out, tool failure or blocked.
- Property outcome: established for its stated scope, counterexample or
  not-established; readiness is a separate structural claim.
- Claim and scope: engine/property identity, assumptions, bounds, source/tool
  fingerprints and coverage/completeness appropriate to that engine.
- Evidence validity: archive consistency and current applicability.
- Acceptance: evaluation against the independently pinned requirements/reviews.

There must be no common `Verified = true` that lets a sampled PChecker run stand
in for completed TLC exploration, a PVerifier invariant stand in for a Dafny
oracle comparison, or a PObserve monitor stand in for causal trace inclusion.
The existing public claim strings and diagnostics remain authoritative until
an explicitly reviewed schema change; internal typed adapters can project them
without rewriting historical evidence.

### Contracts between models, not just between C# classes

For each semantic handoff, record the source symbol/guard, native declaration
references, preconditions, pre/post projections, allowed outcomes, ownership
effects, publication and continuation boundaries, permitted stuttering, and
progress/fairness assumptions. Keep predicates in their native language; a
relation record links them but does not prove their equivalence.

For example, a Dafny credit invariant can support a P or TLA+ action only when
that action establishes the lemma's actual preconditions. P events do not
automatically equal TLA+ actions. A PObserve log sequence does not supply the
causal edges required by the TLC trace matcher. Discharged and outstanding
handoff obligations must therefore be separate from individual backend passes.

### Migration gate if implementation is later authorized

Extract adapters from existing branches one family at a time, keeping CLI
commands, native evidence, strict schemas and independent acceptance unchanged.
Start with dispatch/preflight/validation, not a rewrite of every process runner.
Share a process or archive helper only after its timeout, tree termination,
pinning and exclusive-write behavior demonstrably matches both consumers.

Require contract tests for exact stage ownership, unsupported capabilities,
timeouts, tool errors versus property rejections, claim substitution, stale
inputs, changed artifacts and offline reading. Run the existing backend and
public workflow controls after each migrated family. Optional P engines still
need real native positive/negative smoke tests: output-classifier unit tests
alone cannot validate their integrations.

Do not delay the best verification route merely to make all backend
implementations look symmetrical.

No TLA+ interpreter or replacement parser is introduced. The SANY companion
embedded in the C# bridge supplies declared variables/operator arities. The
bridge creates fresh processes
to isolate upstream static state and cleans its own temporary compiler outputs.
SANY/PlusCal compilation and execution, TLC stages and trace matching share the
repository's `java-toolchain.json` pin through `Common/JavaToolchain.cs`.
Explicit setup verifies the Temurin JDK 25.0.4.1+1 archive for Windows/Linux x64
and installs it in the platform-specific ignored cache; analysis never installs
Java or falls back to PATH.
An absolute `TLALINK_JAVA_HOME` can relocate the same pinned distribution.
Release/launcher hashes are checked at selection and included in evidence;
this does not attest every installed runtime file. Native P manifests retain
their separately explicit Java dependency pins.
`Common/FilePaths.cs` normalizes either input separator into canonical absolute
native paths. File identity is ordinal case-insensitive on Windows and ordinal
on Linux. Foreign Windows drive/UNC roots are rejected for local resolution;
historical absolute evidence keys remain recognizable for archive validation
without pretending they are currently applicable on another host.
JAR/adapter/tool binaries are pinned; local imports and standard-library contents
remain fingerprinted. Imported annotations require explicit binding scope.
PlusCal freshness regenerates the upstream translation in memory, never trusting
checksums alone and never editing the module.

## Complementary Dafny evidence

All maintained public JSON uses schema 4 with a document-kind discriminator.
Older versions are rejected, with no compatibility branches or implicit
upgrades. Historical evidence is not rewritten. The `@tla 2` source grammar
is independent of JSON versioning. Integrated commands use an external strict
binding manifest and an independent evidence packet. This keeps source parsing, file hashing, compiler
context and TLA review shared without making model-language semantics generic.
No custom Dafny expression parser, interpreter or C#-to-Dafny translator exists.

The bridge is compiled locally against the explicitly selected Dafny 4.11.0
distribution and runs in a fresh process. Its own resolver supplies qualified
declarations, locations, kinds, ghost/static/body information and formal types.
All local .dfy includes are pinned, including declaration-free include files.
Discovery precedes a second resolution pass under those pins; closure changes
and changed bytes invalidate the run. Verification includes dependencies.
Importantly, native `audit` must receive the full closure as explicit roots:
its default traversal does not audit assumptions in merely included files.
Precompiled .doo and nonlocal include dependencies are not accepted.

Executable bindings use verified, generated Dafny case wrappers rather than
handwriting the specification in C#. Each wrapper makes one concrete call.
Native verification establishes its call preconditions; native translation
produces the executable oracle and runtime. A separately generated harness
compares the oracle outputs with an explicit C# adapter. Supported int/bool
types and value/out modes are checked against both resolved signatures.
Single-output Dafny methods compile to C# return values; multiple outputs
compile to out parameters. The adapter contract remains uniform.

The original C# syntax trees, original reference set, generated oracle/runtime
and harness are emitted together through Roslyn under the analyzed parse/
nullable context. There is no product MSBuild evaluation or hidden generated
Compile item. Exact original/generated input and reference hashes, compiler
context, loaded entry binary and every expected case ID are checked/recorded.
Mathematical-integer outputs are compared without narrowing to Int32. Explicit
nonframework reference identities are available to the runtime resolver.

This is still a trusted test adapter/host boundary. A stateful adapter can set
up and call a real instance, but its abstraction, environment and cleanup are
review obligations. Automatic exception equivalence, heap/side-effect equality,
async linearizability and general C# refinement are not established. Unexecuted
bindings remain visible. Zero cases cannot yield conformance.

Cross-model relations explicitly reference real TLA+ source links and Dafny
bindings and retain their assumptions and obligations. They never acquire a
"proved" status. An accepted-unit credit return, for example, does not silently
cover rejected counts, publication races or shutdown. Native proof success and
TLC success alone cannot establish a common abstraction or compose their claims.

Every run keeps exclusive outputs, subprocess invocations/logs, tool bytes,
source/reference bytes and generated artifacts under content-addressed pins.
Maintained-input baseline drift is separate from per-run generated outputs.
The reader checks semantic consistency and archive consistency independently
of optional current source/tool applicability and never decides milestone acceptance. Historical runs
remain historical when the working tree or producing tool changes.

## Binding versus execution

Keys are `(model, kind, stable ID)`. The nearest annotated C# type supplies the
namespace. One annotated TLA+ module supplies one namespace. State binds one
field to an observation operator; a step binds a source statement to an operator
or real explicit PlusCal label. Synthetic labels are excluded. The corresponding
generated SANY operator supplies process-label arity when available.

Roslyn resolves actual field symbols, including generic original definitions;
unrelated same-named members do not become interference. Out-of-step writes
include adjacent statements, tuple assignments and ref escapes. This is still
bounded supplied-source coverage, not a proof about external code/reflection.
Syntax errors fail structurally; missing compilation context is explicitly
incomplete. Review-only steps remain useful without executable metadata.

Generation supports expression statements directly in blocks/switch sections.
It validates all rewrites with the supplied compilation before writing output.
No arbitrary getter/call/indexer is evaluated for capture. Primitive fields are
read under one actually held readonly reference-field lock. Mutable identity
fields need an explicit held-guard claim. This cannot establish that all writers
use the guard; reviewers retain that obligation.

Invoke captures precede the source/model action; other supported points capture
afterward. Fault interception is unsupported rather than silently changing
exception behavior. Scheduling callbacks run outside the recorder's internal
lock and cannot be generated inside source locks. Instrumentation necessarily
perturbs scheduling and allocations; it observes test executions, not untouched
production execution.

## Execution receipt

An injected MSBuild target hashes the actual Compile/ReferencePath items before
CoreCompile and the target binary after Build. The runner verifies generated
sources participate exactly, checks target/language/nullable/defines against
the standalone Roslyn context, hashes inputs again after execution, and matches
the entry assembly reported by the running capture. Actual reference assemblies
and helper/entry-point sources are recorded, not silently assumed identical to
the analysis runtime references.

This is provenance from trusted tooling/projects. It is not remote attestation,
a proof of the compiler, a full native/OS closure or an adversarially secure
build sandbox. No code or credentials are sent to an external service.

## Causal trace matching

For events `1..N`, the wrapper adds a `seen` set to the base model variables.

1. Start from the selected base Init with `seen = {}`.
2. An event may advance only when all recorded predecessors are in `seen`.
3. A transition applies its mapped model action **and** base Next, checks typed
   pre/post observations according to point, and adds that event to `seen`.
4. A stutter leaves all base variables unchanged and adds the event to `seen`.
5. Internal base Next steps are allowed only through explicitly annotated,
   justified `visibility=hidden` actions. Hidden action arguments range over
   the recorded actors/operations; this finite domain is a pilot limitation.

The state trajectory persists across every event. Independently reachable
per-event observations do not suffice. Recorded operation, physical source-guard
and managed-thread edges are checked for internal consistency. Event numbers
cannot replace those causal relations. One observed state/connection cannot
silently switch guard identities within an entity. Explicit scalar entities
index per-lane observations and operation identity without conflating separate
guarded objects. Manual captures can supply extra causal IDs and explicit
handoff tokens.
Automatic capture does not infer every Task join/channel/handoff edge; the
interpretation is conditional on the captured/reviewed causal coverage.

`TLNotMatched == Cardinality(seen) < N` is a deliberately searched invariant.
Its violation is a witness that all events match one execution/linearization.
Successful finite exhaustion without violation is rejection, subject to TLC's
64-bit fingerprint omission risk. An error, timeout, unbounded hidden search or
unsupported configuration is incomplete. A matching finite prefix makes no
fairness/liveness/general-refinement claim.

Wrapper names are tool-reserved. Single-root declared variables, scalar
observations, zero/one/two-argument actions, one connection, bounded trace size
and conservative single-line configurations define the current supported pilot.
Do not silently extend a graph restriction or strip unsupported syntax.

## Project orchestration and acceptance

Projects declare stable IDs, pinned tools, an explicit supported compiler
profile, dependency-ordered stages and review obligations. A separate policy
declares expected outcomes, diagnostic-specific rejection controls, required
bindings/cases and reviewed assumptions. Execution never weakens the policy.
Preflight makes supported capability limits explicit but does not claim native
proof/signature readiness. Low-level commands remain directly usable.

Each stage keeps its native evidence meaning. Native TLC runs use a fresh JVM,
fixed bounded resources and the exact pinned configuration; process deadlines
are incomplete. Oracle/trace runs retain actual compiler/binary receipts.
No generic shell/plugin runner, replacement model interpreter or automatic
source translation is introduced.

Run metadata separates execution completion from stage outcomes and acceptance.
Acceptance rechecks exact declared stages, semantic result consistency,
artifacts, archived bytes, current inputs and required review dispositions.
Artifacts are run-relative; source paths inside a relocated project are mapped
relative to its recorded root. External tool/reference paths remain explicit.

Review decisions are append-only sidecar records, never edits to pinned run
inputs. They pin the claim/assumptions, explicit source inputs and supporting
evidence. Claim or input drift invalidates prior review. The newest decision
is authoritative; acceptance reports retain decisions used. These are trusted
review assertions, not authentication or composed refinement proofs.
