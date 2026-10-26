# TlaLink design

See [README.md](README.md) to get started, [REFERENCE.md](REFERENCE.md) for
commands/schema and [AGENTS.md](AGENTS.md) for
maintenance instructions. Instrumentation operates on separate copied sources,
never product files.

## Agent-first operating model

TlaLink turns scoped C# behavior questions into repeatable checks and inspectable
evidence. It records the inputs, tools, assumptions and results supporting a
claim rather than inferring implementation correctness from a passing model.
Its deterministic CLI workflows can be operated directly or by a coding agent.
It does not include an agent, language-model runtime or hosted analysis
service. The human-facing README explains the value and workflow;
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
| `ObservedExecution.cs` | Shared compiler-time input/context capture and bounded .NET processes, without trace requirements |
| `CSharpRegression.cs` / `StageEvidence.cs` | Explicit console-harness selections, frozen runtime snapshots and independent regression/native archive readers |
| `DafnyAnalysis.cs` | Proof-only native Dafny roots, verification and assumption audit without artificial C# bindings |
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
| `CommandSummary.cs` | Presentation-only status/diagnostic projections; full saved evidence and exit semantics remain unchanged |
| `AcceptanceWorklist.cs` | Bounded acceptance work items from structured stage, review and requirement evaluation; no diagnostic-message parsing or changed verdicts |
| `EvidenceReport.cs` | Backend-neutral schema-4 evidence-report validation and local Markdown/Mermaid presentation; recorded claims remain separate from byte applicability and acceptance |
| `ProjectReport.cs` | Independently reread explicit project runs into the same presentation manifest; no theorem composition |
| `Program.cs` / `CliOptions.cs` | Strict command surface, drift and exit semantics |
| `PLang/PFrontend.cs` / `PLang/PIndex.source.cs` | Isolated authoritative P parse/typecheck and qualified declaration inventory |
| `PLang/PContracts.cs` | Strict explicit P scope, resolved source links, native claim dimensions |
| `PLang/PBackend.cs` / `PLang/PProcess.cs` | PChecker/PEx/PSym/PVerifier/PObserve native processes and bounded logs |
| `PLang/PResults.cs` / `PLang/PEvidence.cs` | Conservative backend-specific outcomes and independent immutable-evidence validation |
| `Apalache/ApalacheContracts.cs` | Strict selected safety scopes, queries, tool-distribution pins and native receipts |
| `Apalache/ApalacheBackend.cs` / `Apalache/ApalacheEvidence.cs` | Isolated native checks and independent mode/obligation/artifact validation |

## Human-readable evidence reports

Project report generation selects explicit immutable runs and invokes the same
independent acceptance evaluator as `accept`. It saves those evaluations and
one ordinary `evidence-report` manifest before rendering Markdown/Mermaid.
Native outcomes, expected controls, pending reviews and per-run archive/current
applicability remain separate. Multiple runs do not overwrite, merge or repair
one another's verdicts. Generated projects do not invent source dispositions.
The generic renderer's freshness describes its presentation snapshot; historical
run applicability remains the separately recorded per-run assessment.

Acceptance worklists are transient presentation metadata, not additional evidence
or a saved acceptance format. Evaluation associates unmet reviews and incomplete
stages with their requirements; other requirement gaps stay explicit. Unclassified
diagnostics remain visible by code/severity, without inferring their root cause
from message text. Summary previews have exact omitted counts; full receipts,
diagnostic messages, review freshness and exit codes are unchanged.

Console regressions use observed compiler-time sources, references, compiler
settings and build inputs, followed by a byte-checked runtime-output copy.
The harness reports its loaded entry assembly and the exact required test IDs.
Missing, duplicate, skipped, partial or contradictory observations are incomplete,
not successful executions or assertion counterexamples. A native assertion
failure requires complete observations and exit 1. This is not test-framework
integration, a causal trace or model conformance. Build projects, the harness,
compiler/source-generator behavior and OS/runtime remain trusted. Inputs exposed
at the MSBuild capture boundary are recorded; this is not a complete MSBuild
workspace or authenticated compiler/runtime closure.

Standalone Dafny uses the same authoritative frontend, distribution pinning,
verification classifier and assumption audit as integrated bindings. Its native
contracts do not become C# correspondence or executable-oracle evidence.

TLC model/trace resource settings share one typed representation and conservative
validation. Defaults remain command-specific; recorded effective resources and
native invocations are independently cross-checked. Worker/heap changes do not
alter logical properties, bounds, configuration approvals or incomplete outcomes.
Heap is not hard process-memory isolation, and more resources do not remove
combinatorial state growth.

One explicit `evidence-report` manifest describes an acyclic graph of recorded
claims, their pinned supporting files, code-region dispositions, assumptions
and open gates. `report` renders it into Markdown with Mermaid role/dependency
diagrams, evidence tables and a complete selected-code appendix. It needs no
backend tool or online diagram service.

Native/private adapters perform independent backend rereads before producing
this presentation manifest. The generic renderer does not decode private
archives or infer source-to-proof edges from symbol or contract names.
Recorded claims, expected negative controls and pending reviews
retain their own recorded statuses. Green denotes a declared expectation
matched in a byte-current snapshot, not verified C#.

The renderer validates graph identity, source spans and pinned evidence,
rechecks the observed snapshot before exclusive publication, and exposes
changed/missing input files as a stale report with exit 2. A report is neither
an acceptance receipt nor an automatic retention root; no archived evidence or
independent requirements are rewritten to improve its appearance.

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
modules; Apalache manifests reject them without changing TLC behavior.
Only explicit nullary predicates and constant initialization are supported.
TLC configuration translation, temporal checking and simulation are excluded.
Project preflight reuses native execution's SANY compatibility validator and
pins imported model dependencies before creating a run. Parsing is shared only
within that preflight invocation; execution reparses and validates its inputs.
This is structural compatibility, not native Snowcat typing or a safety claim.

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
Native failure summaries retain the query, last native pass, exit/deadline and
recognized error/source text. They are bounded excerpts of archived logs, not
an alternate outcome classifier; unsupported assignment remains incomplete.
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

## Evidence retention and dependency impact

GC defaults to a read-only planner. One schema-4
`evidence-retention` document declares path/reason `pins` and `review_projects`.
CLI paths are caller-relative; manifest paths are document-relative. Store
units are immediate child directories. Only completed schema-4 workflow runs
are collection candidates. Other archives and loose sidecars may participate
through one schema-4 `protected-consumer-catalog`, selected by the retention
manifest's optional `catalog` path. This is an explicit native integration
boundary, not legacy public evidence acceptance or an ignore switch.

A catalog pins its producer sources and every file in each declared protected
unit/sidecar, and supplies native-adapter-extracted file inputs and references.
The complete on-disk file set and bytes must match; missing entries, extra files,
stale producer code or changed bytes block the scope. Catalog consumers remain
protected roots, whose dependency closure retains ordinary workflow runs.
Catalog generation is trusted, not authenticated: the native adapter must cover
its recorded path fields and declaring bases, including explicit historical
relocations, and must reject unknown reference-bearing representations.
It does not infer arbitrary external consumers or future executions of archived
code. Private adapters stay private; the public engine reads only this one
current schema. Historical source evidence is not upgraded or rewritten.

Retention follows recorded file fingerprints and review evidence transitively,
including decisions embedded in acceptance receipts. Explicit review projects
provide the project-relative base for append-only review sidecars and retain
that history, not just the latest decision. Accepted or stale status alone is
neither a retention root nor permission to collect.

The same graph reports changed/missing recorded file inputs and transitively
affected runs/reviews. Recheck is conservative at whole-run granularity, not an
invented stage-level mapping. No recorded file change is not current applicability
or acceptance; native checks, review freshness and environment changes still
require independent readers.

Plans never authorize deletion. Reparse points, unreadable/malformed records,
scan races and unsupported formats are explicit blockers. A grace interval
protects recent output but does not prove inactivity. Byte totals are logical
lengths, not guaranteed reclaimed space or hard-link identity proofs.

On Windows, existing recorded input paths may pass through links only when an
opened read handle supplies their final native path and the bytes hashed through
that same handle. Physical store-relative identities supply retention edges;
recorded spellings remain provenance/display identities. Resolved targets are
rechecked and pinned in plans/apply. Managed outputs, catalogs and deletion
ancestors still reject reparse points. Missing linked inputs and unsupported
host resolution remain explicit blockers, not assumed-distinct files.

Explicit application uses `gc --apply <full-plan> --exclusive true --output
<new-receipt>`. It never accepts a summary or overrides pins/grace. Application
is Windows-only: retained native directory handles deny ancestor
rename/replacement, and each directory is checked for reparse attributes through
its handle. Unsupported platforms fail explicitly rather than substituting
weaker path-only deletion. The caller must first stop all writers; the exclusive
assertion is a trust boundary, not authentication or automatic process discovery.
A sibling exclusive GC lease prevents competing applications.

The full plan includes observed filesystem metadata. Apply recomputes the whole
graph, compares snapshots, decision-document hashes, changed inputs and candidate
sets, and verifies candidate artifact/input bytes before deleting anything.
Deletion uses only the enumerated candidate files and nonrecursive empty-directory
removal, never a recursive delete or a rescan that would include new files.
Unknown scope still blocks all candidates; it cannot be ignored or narrowed away
to force collection. A new schema-4 `gc-apply-result` is reserved and flushed as
incomplete before mutation. Errors stop application, retain explicit partial
progress and return incomplete; interrupted final receipt writes can be
incomplete/malformed, never evidence of transactional completion. There is no
rollback, forced deletion, physical-space guarantee or inference of proof
acceptance. Catalog consumers themselves are never deletion candidates.

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

## Authoritative frontends and paths

TLA+ parsing uses SANY, not a local interpreter or replacement parser. The companion
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
native paths. Path comparison is ordinal case-insensitive on Windows and ordinal
on Linux. Foreign Windows drive/UNC roots are rejected for local resolution;
historical absolute evidence keys remain recognizable for archive validation
without pretending they are currently applicable on another host.
Canonical paths do not establish physical file identity across links.
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
of optional current source/tool applicability. Only project acceptance evaluates
the declared requirements and reviews.

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
   the recorded actors/operations; this is a finite-domain limitation.

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
and conservative single-line configurations define the supported trace scope.
Do not silently extend a graph restriction or strip unsupported syntax.

## Project orchestration and acceptance

User-authored projects may compose explicit code-adjacent `project-component`
documents and shared input-set/execution-profile documents. One typed loader
resolves paths against their declaring files, validates import identities and
profile references, and produces the same normalized `AnalysisProject` used by
inline projects. Scenario membership is explicit; no filesystem discovery,
inheritance or backend semantics are introduced by composition.

The loader retains original-byte authoring fingerprints and stage origins outside
the serialized normalized project. Preflight/run pin those exact bytes. Each run
stores a `project-expansion` snapshot; acceptance independently reconstructs it
through archived authoring files rather than trusting the saved expansion.
Review-root retention uses the same loader through its guarded scan reader.
Expansion requires no native checker and does not establish readiness or acceptance.
Editor schemas are generated from the typed contracts, not separately maintained
validation rules or an alternative project language.

Projects declare stable IDs, pinned tools, an explicit supported compiler
profile, dependency-ordered stages and review obligations. A separate policy
declares expected outcomes, diagnostic-specific rejection controls, required
bindings/cases and reviewed assumptions. Execution never weakens the policy.
Preflight makes supported capability limits explicit but does not claim native
proof/signature readiness. Low-level commands remain directly usable.
Optional `check_obligations` map declared safety/deadlock/progress obligations
to positive acceptance requirements in the same scenario. One shared coverage
implementation feeds preflight and acceptance. Empty or backend-incompatible
mappings remain missing and prevent a new run, not silently successful defaults.
Coverage mappings support TLC and Apalache's distinct capabilities; they neither
invent temporal requirements for safety-only projects nor interpret TLC
configuration semantics. Mapped checks are a plan, not evidence or a theorem.

Each stage keeps its native evidence meaning. Native TLC runs use a fresh JVM,
validated worker/heap budgets and the exact pinned configuration; process deadlines
are incomplete. A completed native deadlock (`TLC2114`) is a rejection and may
satisfy only a requirement selecting that rejection/diagnostic, not a positive
safety obligation. TLC diagnostics surface bounded native error bodies and
nested exception locations; full logs remain archived and classification still
uses native diagnostic IDs. Oracle/trace runs retain actual compiler/binary receipts.
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

New review decisions serialize evidence and fingerprint keys relative to their
declaring project. Readers resolve recorded spellings to native identities and
reject aliased fingerprint keys; original absolute records are not rewritten.
Cross-root paths that cannot be represented relatively fail before a decision
is written. Regression file arguments use explicit `path_arguments` indices,
not guessed path-looking strings; loading and independent reads use one resolver.
