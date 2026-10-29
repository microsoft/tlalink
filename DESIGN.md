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

The agent owns drafting models, properties, configurations and bindings,
running native tools, and technically challenging the resulting abstraction.
The [authoring workflow](REFERENCE.md#model-authoring-and-self-checks) requires
source correspondence review, reachability/non-vacuity checks, meaningful
controls and counterexample investigation rather than pass-chasing.
The user clarifies intent and reviews a plain-English explanation of the
claim, assumptions, limits and acceptance requirements; routine use does not
require reading TLA+, Dafny or P. Existing human or specialist review gates
remain explicit and cannot be discharged by agent self-review.
Neither an agent explanation nor a successful backend run discharges missing
correspondence or coverage obligations. The authoring instructions guide the
agent; the CLI does not automatically establish abstraction adequacy.
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

### Typed JSON contracts

Owned fixed-shape documents and native bridge messages have named C# contracts.
Execution may accumulate typed mutable state, but workflow and acceptance
operate on concrete result contracts, not serialized property bags. A small
discriminator reader selects a strict contract once at the process/document
boundary. Structural decoding remains separate from native semantic checks,
archived-byte consistency, current applicability and acceptance.

Summaries use explicit result-family records and typed projections. Worklist
grouping, omission counts and truncation happen before serialization; full
receipts are not mutated. Inspection section selection uses serialization
metadata on the existing typed inspection records, preserving selected nulls
and omitting unselected fields without a second copy of the inspection schema.
Unsupported summary types produce an explicit error.

Dynamic names (paths, bindings, counters and observations) use typed
dictionaries. Trace values have a constrained scalar contract, including exact
wire spelling where existing correlation keys depend on it. Raw JSON is limited
to JSON Schema transformation, truly variable native payloads and short-lived
boundary dispatch/invalid-input diagnostics. A fixed upstream format is not
dynamic merely because another process produced it. No compatibility schema,
historical-evidence rewrite or change to backend claim boundaries is introduced
by converting the implementation contracts.

Oracle case inputs use a separate native-boundary scalar contract restricted to
Int32 and Boolean, matching the supported C#/Dafny signatures. They are not trace
identities or arbitrary JSON values; unsupported scalar kinds and numeric
encodings fail explicitly rather than being coerced.

### Reviewed method summaries

TLA+ method summaries are stateless parameterized relations paired with one
C# method through existing binding IDs. Explicit correspondence-scope metadata
declares pure/atomic intent, selected source files, assumptions, limits,
optional native preconditions and acyclic same-scope summary dependencies.
Source resolution accepts selected concrete synchronous, nonvirtual methods
and constructors; native resolution uses SANY, not expression parsing.
Neither shape validation nor an abstraction label proves purity or linearizability.

TLC/trace stages explicitly reference summaries. Preflight resolves their
endpoints and requires their modules in the consumer's native import closure.
Import presence does not prove that the authored caller actually invokes the
relation correctly. Caller preconditions and the relation between abstract
arguments/results and C# remain authored checking/review obligations.

The existing append-only review decision carries human/agent origin and
summary-support fingerprints. Accepted inspection recording resolves the
summary and pins its source/compiler context, formal imports and tools.
Changes make support stale independently of the caller's native result.
Acceptance requires current support for the transitive declared dependencies;
agent decisions cannot satisfy human-origin obligations. Reports retain this
conditional basis, not a generic composed C# correctness claim.

No helper PC/retry state is generated into caller models. Relations take
explicit abstract state and outcomes; callers supply separate storage for
independent instances and preserve unrelated state. Atomic summaries describe
logical effects, not call duration, fairness, termination or hidden partial
effects. Formal traces, safety results and inspection remain distinct evidence.
Existing TLC result-store keys and reuse semantics are unchanged.

### Source and native correspondence

The [source correspondence vocabulary](docs/proposals/source-correspondence.md)
defines qualified `bind`, optional `capture` and
explicit `exclude` directives, including symbol-resolved logging omissions.
Its [synthetic conformance cases](docs/proposals/source-correspondence-cases.md)
cover TLA+/PlusCal, TLC/Apalache, Dafny and P; the case matrix is not itself an
execution result.

Binding comments use `@link` without an embedded version. The root project's
required `annotation_version` selects one grammar for all components and
annotated C# and formal inputs. Standalone annotation commands require explicit
version context instead. This keeps source comments focused on correspondence
and makes version changes project-wide migrations, not mixed per-comment
interpretations. Only annotation version 4 is supported; JSON schema 4 is
independent. Payloads are standard JSON deserialized into strict typed binding,
capture and exclusion records, not a bespoke token language or free-form JSON
objects. Old `@tla` markers and version-2 directives are rejected. The neutral
marker does not unify backend semantics; source resolution remains C#-specific.

Maintained examples now place `bind.source` and capture metadata in formal
comments, leaving C# unmodified. Source selectors reuse the preparation
contract's Roslyn declaration IDs, syntax kinds, token matching and explicit
occurrence counts; both commands use one resolver. `links list/check/set/remove`
indexes native declarations through their authoritative frontend before
validating or editing metadata. One ID cannot have competing C#/formal owners.
The staged example migration does not rewrite consumer inputs or historical
receipts. Archived formal-owned bindings reconstruct their source selectors
using archived source and the explicitly recorded compiler reference bytes.

`links inspect` is a read-only authoring view over the same native index and
Roslyn selector/matching implementation. Native declaration spans delimit the
displayed model text; C# excerpts mark the exact attachment and expose lexical
control, exception and lock context. Observed state bindings resolve to their
field declarations and declared guards. Optional explicit call sites reuse
the symbol-query resolver, retaining declared dispatch and source locations,
not inferred runtime targets or transitive effects. Condition evaluation is
distinct from entering a branch; lexical context outside a nested function
does not establish its invocation-time guards. Earlier guard clauses and
implicit cleanup require further review; block/member excerpts aid that review.
Unlinked declarations are not classified as model-only automatically. Compact
output omits input fingerprints, not diagnostics, and endpoint filtering records
omitted counts. Full JSON includes the checked pins. Text/Markdown/JSON are
presentations of the same inspection, not new binding stores, acceptance
receipts, instrumentation-readiness checks or automatic review decisions.
Exact multi-binding/endpoint selections define inspection scope. Subsequent
source member/file, native kind/status, capture-point and literal search
filters refine the presentation; same-binding constraints prevent accidental
cross-matches on multi-bound endpoints. Deterministic endpoint paging preserves
complete endpoint groups and reports candidate/matching/returned totals.
Whitelisted section projection removes code or metadata consistently from
JSON and readable output without removing identities, diagnostics or omission
information. A calls section requests the existing explicit-call resolver, not
transitive body expansion. Filters/pages cannot hide validation failures or
turn an empty selection into an adequacy claim; page-to-page inputs are not
transactionally frozen.

One schema-4 `correspondence-scope` names explicit language/input aliases and
symbol-resolved routine omission rules. Each connection has one unique
`scope::name` ID, a compiler-resolved source attachment and a native attachment.
Native manifests select IDs and own their cases,
assumptions and checks; C# does not spell native declaration names.
SANY/PlusCal, Dafny's resolved AST and P's authoritative frontend resolve native
endpoints from authoritative declaration spans and their annotations. Several independent
bindings may attach to one C# node. Only the TLC adapter currently consumes
`capture`; no generic native execution/refinement capability is inferred.
TLA+ state pairs may attach directly to SANY variable declarations; parameterized
observation operators remain appropriate for projections such as per-entity
state. The bridge exports both from the native AST rather than inventing wrapper
operators or parsing declarations from text.

Exclusions and logging omissions are correspondence dispositions, not code
removal or safety exemptions. Receiver/argument evaluation, competing accesses,
awaits, disposal and unresolved callee effects remain explicit. All scope and
source bytes stay fingerprinted. Archive readers reconstruct paired declarations
from archived source/formal bytes and native inventories without live sources.

Source preparation is authoring infrastructure, separate from instrumentation
and execution evidence. One strict schema-4 manifest pins explicit source bytes,
compiler context and destinations. Roslyn declared symbol identities delimit C#
syntax selection; token matching ignores trivia, with explicit counts/ordinals
for repeated syntax. Formal files are copied without inserted bindings;
PlusCal/generated translations are not edited. Typed JSON comments are inserted
at resolved C# spans and C# controls require
declared replacement text and reasons. Original encoding/BOM/newlines are retained.
All targets and resulting C# compilations are checked before new output is
published. Readonly validation checks recorded input/output bytes and rejects
unexpected files. These copies and their ledger establish neither binding
adequacy nor general source/model correspondence, and are not acceptance receipts.

Context-only preparation imports share approved source pins without inheriting
compiler settings or output edits. Explicit prepared-source ledger references
in project input sets expand to the ledger and its declared output paths through
the normal authoring reader, including archived readers; they do not scan a
directory or automatically approve the producer's semantics.

Source inventories can select the exact compiler context from independently
checked regression evidence, requiring selected current sources/references to
match the observed bytes. This avoids reconstructing long command lines without
loading MSBuild or claiming all recorded compiler sources were analyzed.
The `symbols find/show/resolve` commands share that compiler-context loader
without running inventory CFG/call analysis. They query Roslyn declarations
directly, group partial locations by documentation ID and preserve ambiguity
rather than guessing from line proximity or span length. Symbol queries ignore
annotation payloads; annotation-aware commands retain their validation.
They emit source identities, signatures, physical UTF-16 spans and input pins,
not metadata-wide lookup, runtime dispatch or source/model correspondence.
Focused `references/callers/callees/describe` actions reuse the same symbol
resolution and compilation. They inspect explicit syntax/semantic targets in
the selected sources, preserving candidate references, constructed generic
targets, extension declaration identities and nested-function boundaries.
Incoming call sites do not infer possible implementations or delegate targets.
Metadata declaration IDs can select scoped uses without scanning metadata
bodies. `describe` shares member-fact extraction with source inventory but
omits CFG construction; ordinary queries do not build unrelated member CFGs.
No persistent source cache, daemon, separate query language or new evidence
acceptance claim is introduced.
Inventories declare their exact selected source list, including memberless files.
Source compiler profiles select C# 8 or C# 14 through one `language_version`
field and the equivalent standalone `--language-version` option. The selected
version is pinned and reused by archive readers and recorded-context inventories.
It is a source syntax/resolution setting, not a .NET Framework API-compatibility
claim. Tool-generated instrumentation/runtime hosts remain .NET 10/C# 14 and
record their actual build context separately from the original source context.
Method/constructor inventories also expose Roslyn's explicit control-flow blocks,
reachable-block dominance and resolved invocation receiver/ref-argument facts.
Nested function bodies are not merged into a parent's graph. This supports
reviewable guard/effect correspondence without claiming that CFG dominance
models implicit exception dispatch, finally behavior or concurrent execution.
Integrated bindings can declare executable `source_obligations` against their
existing binding IDs and exact Roslyn documentation IDs. Guarded-effect checks
require an authorized Boolean branch edge on every path to one reachable effect
site, not merely a dominating gate block. Stable-storage checks account for every
selected field use; exchange-gate and monotonic-Int32 checks recognize a narrow
set of resolved framework intrinsics. No source-to-Dafny translation is involved.
Stable storage supports both writable inline value-type gates without an
initializer and readonly reference helpers with a direct fresh constructor
initializer. The latter preserves reference identity, not helper purity:
constructor replacement, factories, copies, aliases and unapproved calls or
getters do not become accepted storage summaries.

Reference-CAS effect authorization recognizes a synchronous method's direct CAS
result local (including a predeclared uninitialized local), its immediate
built-in nonnull old-value return guard, and the following direct effect call.
An optional Volatile.Read fast path may return an already observed reference.
That same non-ref local may receive the subsequent CAS result only when its
initializer is an authoritative read of the selected instance field and its
declaration has an immediate built-in nonnull early return. Intervening writes,
ref aliases, other-field reads and stale-result guards do not satisfy that
shape. The following dispatch may discard its return with `_ = ...`; this is
still a direct call, not evidence of asynchronous completion.
Candidates must be structurally nonnull, not merely nullable-annotated; unknown
property/factory contracts remain incomplete. The one explicit property contract
is the BCL `TaskCompletionSource<T>.Task` getter on an unchanged, non-ref local
initialized directly by fresh construction. The constructed type, nonvirtual
getter and returned `Task<T>` must match framework metadata from the compiler's
core assembly or its type forwarders; source/shadow lookalikes do not qualify.
Direct BCL constructor-option constants are permitted, and the same getter can
appear in the final return. This explicitly trusts the BCL nonnull-task contract
and selected framework references, not arbitrary property purity or metadata
authentication. Factory receivers, reassignment and ref escapes stay incomplete.
Resolved intrinsic signatures and
the same CFG authorized-edge/reachability helpers check dispatch authorization.
An async callee's completion, exception handling, task settlement and progress
are outside this premise.

Callback-effect checks are a separate wiring inventory: one direct invocation
on the readonly helper receives the owner's private instance callback as its
only method-group argument. The callback's first direct effect may sit in one
top-level try/catch, with return statements after that effect. Other callback
references, delegate escapes, direct callback bypasses and other direct selected
effect invocations in the owner fail the scoped inventory. It neither summarizes
handlers or indirect calls nor proves helper authority. Claims relying on CAS
authorization, scoped cell writes and stable helper identity need their separate
premises and correspondence review; their passes are not automatically composed
into a C# correctness or at-most-once completion proof.
These checks use the same explicit compiler scope and pins as integrated links.
They reject known structural counterexamples and report unsupported exception
regions, alias escapes and partial storage containers as incomplete. They do not
summarize arbitrary callees or infer an atomicity/concurrency theorem. Source
premise outcomes remain separate from native proof, audit, conformance, coverage
and human correspondence; unmet premises block subsequent proof/execution.
Reference cells add exact generic metadata CompareExchange-to-null and
Volatile.Read shapes, with original-declaration matching for generic field
closure. Flow facts keep constructed and definition identities separately.
Authored allowed getters do not establish purity. A narrow guarded first-try
effect checks the normal prefix and unique try entry without pretending to
summarize handler semantics. Null candidates can report success without a
nonnull publication; any one-success/one-callback theorem must state its nonnull
premise independently of the shape checks and library/caller review.
The existing evidence-report format can project these regions through explicit
authored per-file dispositions and nonoverlapping symbol-prefix overrides.
Project reports compose that same authored presentation alongside independently
evaluated runs. Presentation data never approves reviews or infers validated code.

Executable steps may append typed scalar action arguments after their existing
identity parameters. Arguments are captured only from simple locals, parameters
or literals; fields/getters/calls/reference objects cannot bypass guarded state
observation rules. They are kept outside the recorder's operation/guard identity
keys, so changing a value does not fabricate or erase causal edges. Trace readers
check exact presence/count/types before applying the mapped native action.
Hidden actions do not infer domains for these data arguments.

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
| `Markup.cs` | The single maintained `@link` grammar/comment envelopes; project-level annotation version |
| `SourceInventory.cs` | Current Roslyn source/member/call/lock/await facts under explicit compiler scope; no coverage or atomicity verdict |
| `CSharpFrontend.cs` | Explicit Roslyn compilation, attachments, field symbols and boundaries |
| `SourcePreparation.cs` | Byte-pinned source copies, Roslyn/SANY annotation targets, explicit C# control mutations and readonly copy validation |
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

Source inventories can use that recorded reference closure and defines with
explicit nullable mode, rather than silently adding the tool host's framework
references. The compiler settings and reference-selection mode are recorded and
fingerprinted. This reproduces the selected Roslyn context, not MSBuild execution
or source-generator behavior; the inventory remains source facts, not coverage.

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

For TLC result-store cleanup, `result_projects` explicitly selects the complete
set of projects whose current families must be retained. The inspected store is
the result store's `tlc` directory; each immediate child is one immutable family,
containing all its fingerprint-function entries. SANY resolves current closures
before family keys are compared. Matching families are roots regardless of age;
unmatched families may be candidates under the same pins, grace, snapshot,
byte-complete verification and Windows handle guards as workflow units. Unknown,
malformed and active units block the whole plan. The project list is trusted
reference scope, not discovery or a claim that no other project needs a result.
Copied run evidence has no dependency on the store. No automatic pruning or
second cleanup command is introduced.

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
upgrades. Historical evidence is not rewritten. The `@link` source grammar
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

Invoke captures precede the source/model action; commit/complete follow success.
Selected expression-statement faults record and rethrow the original exception.
Selected synchronous reference IDisposable using lifetimes are lowered to
single acquisition and explicit try/finally disposal with interface dispatch.
Null and failed acquisitions do not invent cleanup; nested declarations close
in reverse order, including across awaits and exceptional exits. Cleanup means
Dispose returned; a disposer fault is recorded separately and rethrown, not
reclassified as release success. Unsupported resource conversions, await-using,
value/pattern disposal, multiple resources and held source locks block generation.
Rewrites are compiled before publication; this is selected execution evidence,
not a general source-language semantics proof. Scheduling callbacks run outside the recorder's internal
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

The declared TLC result store is the single reuse mechanism for analysis
evidence. A completed exhaustive TLC outcome is a function of its model closure,
configuration, tools, fingerprint function and adapter semantics, not of the
workers, heap or deadline that let it finish. The key therefore covers exactly
those answer-determining bytes, and a match reuses the same native evidence
rather than inferring a new answer. Reused entries are copied into each run and
independently revalidated by acceptance against the current declaration and the
run's recorded inputs. Runs stay self-contained, and the store is a disposable
cache, not a retention root or acceptance input. Only completed TLC outcomes are
stored. Incomplete searches, trace matching, regressions and other backends
always execute, and the recheck planner remains whole-run impact reporting,
not a reuse decision. The adapter version, not the TlaLink binary hash, scopes
entries, so it must change whenever TLC invocation or result interpretation
changes. Requiring passes under several fingerprint functions reduces, but does
not eliminate, fingerprint collision risk.

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
