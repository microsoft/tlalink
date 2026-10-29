# Working on TlaLink

Read [README.md](README.md) for the human-facing, agent-first introduction,
[REFERENCE.md](REFERENCE.md) for commands/setup/schema, and [DESIGN.md](DESIGN.md)
for architecture and evidence boundaries before changing behavior. This is a
C#/.NET 10 tool for TLA+, Dafny and P source/model evidence, not a bundled agent
or hosted AI service. Design and document it around the C#/.NET toolchain:
Roslyn source resolution, explicit C# compiler context, and build/execution
evidence. Formal backends complement that focus; do not present the tool as
language-agnostic or imply support for other implementation languages.

Keep shared workflow/C# infrastructure in `Common`, P in `PLang`, SANY and
TLA bindings in `Tla`, TLC execution/trace matching in `Tlc`, PlusCal-specific
frontend helpers in `PlusCal`, symbolic safety in `Apalache`, and Dafny in
`Dafny`. Preserve the public
namespace and flat deployed bridge-asset names when moving implementation.

## Pre-alpha development

TlaLink is pre-alpha. Prefer a coherent current design over backward
compatibility for CLI commands, APIs, schemas and evidence formats. Update
producers, consumers, examples, tests and documentation together; remove
superseded paths rather than adding compatibility layers or migration machinery
solely to preserve earlier prototypes. Collect fresh evidence with the current
tool instead of treating old runs or tool pins as a compatibility requirement.
Breaking changes do not authorize weakening claims, requirements or checks,
rewriting historical outcomes, or carrying forward review approval.

When assessing the state of the art or designing modeling, abstraction,
method-summary or agent-workflow features, consult the
[Specula research](docs/research/specula/README.md) before introducing new
machinery. Use its overlap analysis and pinned sources to identify reusable
ideas; recheck upstream for current claims rather than treating the research
snapshot as exhaustive or permanently current.

## Agent CLI skills

Workspace workflows live under `.github/skills`; load only the matching skill.
They orchestrate the existing CLI, not a second tool implementation or evidence
format. [REFERENCE.md](REFERENCE.md) remains authoritative for command contracts.

| Skill | Use when |
|---|---|
| [tlalink-source-navigation](.github/skills/tlalink-source-navigation/SKILL.md) | Finding Roslyn IDs, inspecting C# members or following references/callers/callees with explicit compiler context |
| [tlalink-inspect-correspondence](.github/skills/tlalink-inspect-correspondence/SKILL.md) | Reading model/C# pairs, selecting useful context/captures/calls, or filtering/paging inspection output without edits |
| [tlalink-maintain-links](.github/skills/tlalink-maintain-links/SKILL.md) | Creating, updating, checking, removing or explicitly migrating model-owned connections |

Skill examples are independently synthetic public material. Private package
recipes and generated reports stay in their ignored package; do not copy them
into shared skills. Missing compiler/native prerequisites remain incomplete.
Skills do not install tools, approve correspondence or discharge human gates.

## Branch and PR workflow

- While a PR is open, keep related work on its one existing feature branch and
  push subsequent changes there to update that PR. Do not create or switch to
  parallel branches, stacked branches or additional PRs without explicit user
  instructions.
- After that PR merges, pull the latest main before starting the next feature
  branch. Follow explicit user instructions to work on main or another branch
  instead; do not move unfinished work between branches on your own.

## Helping a user obtain evidence

- Publication is limited to independently authored public material. Never put
  non-public names, paths, identifiers, content, provenance or references in
  source code, examples, committed documents, commit messages, branch names or
  PR text, including statements revealing their existence. Keep public tool
  enhancements generic and public regression fixtures independently synthetic.
- Start with the user's concrete behavior/risk and relevant source scope.
  Recommend the smallest suitable claim and checker; make the abstraction,
  bounds, assumptions and missing context visible for human review.
- Handle CLI commands, explicit manifests, prerequisite checks and evidence
  collection. Provision only documented pinned tools as a separate setup step,
  never during analysis; report unavailable prerequisites rather than substitute
  another engine or weaken the claim.
- Prefer project `doctor`/`plan`/`run`/`accept`/`explain` for repeatable work.
  Select the scenario explicitly for preflight as well as execution when only
  that scenario is needed. Start from synthetic examples, but do not treat
  their acceptance policies as requirements for a consumer system.
- Report the requested claim, scope, native outcomes, evidence locations,
  archive consistency, current applicability and acceptance separately.
  Identify coverage gaps, uncaptured causal edges and outstanding reviews.
  Agent reasoning, resolved links and backend passes do not compose a proof
  of C# correctness; do not summarize them as a generic "verified" badge.
- Keep the README concise and human-facing. Put command/format details in the
  reference, architecture and claim boundaries in the design, and actionable
  agent safeguards here. Update the relevant documents together when behavior
  changes. TlaLink's local execution does not establish the agent's own data
  handling policy.

## Model authoring and self-checks

- Maintained public examples keep C# free of `@link` comments. Their formal
  TLA+/PlusCal, Dafny or P comments own typed `bind.source` selectors using
  Roslyn declaration IDs and the shared syntax-selector contract, together
  with any capture metadata. Do not introduce a separate binding catalogue,
  infer declaration-name pairs or replace semantic selectors with line numbers.
  Use the documented `symbols` and `links` CLI commands to discover, inspect,
  validate and maintain these connections. Migration starts with examples;
  do not rewrite historical evidence or private consumer packages implicitly.
- Use `links inspect` for inline model/C# review. Start with one binding or
  endpoint and compact output; request block/member context for earlier guards
  or cleanup and `--calls true` for explicit call identities. Omitted entries
  and call details are not coverage claims. Keep positional-selector risks,
  lexical-versus-runtime context, implicit effects and required human review
  visible. Save generated inspection reports separately from authored models;
  never treat them as another binding catalogue or approval receipt.
- Reusable method summaries use paired method/TLA+ bindings and the existing
  correspondence-scope `method_summaries` metadata, not a new annotation or
  contract language. TLC/trace consumers declare summary dependencies explicitly.
  Keep relations stateless and justify pure/atomic abstractions through
  source-pinned inspection, including competing accesses, failures and relevant
  callees. Never hide relevant interleavings or imply termination by removing
  retries. Check caller preconditions explicitly, not merely as disabling guards.
  Record agent inspection with `origin=agent`; it cannot discharge a human
  obligation. See [method summaries](REFERENCE.md#reviewed-method-summaries).
- Use annotation version 4 with standard JSON inside native `@link` comments:
  typed `bind`, `capture` and `exclude` arrays. Do not introduce XML, another
  tokenizer, raw JSON binding stores or version-2 acceptance branches. Formal
  aliases and exact logging API omissions use one schema-4
  `correspondence-scope`. Each formal endpoint owns its unique `scope::name`
  ID and, in maintained examples, a typed Roslyn `source` selector; the compiler
  resolves the C# attachment without changing that file. Never
  restore source-side `target` names or infer pairs from declaration spelling.
  Native manifests select IDs and retain backend-specific execution/claim semantics.
  Exclusions never remove runtime code, input pins or relevant argument/callee
  effects. See the [vocabulary](docs/proposals/source-correspondence.md).
- Prepare immutable annotation copies with one schema-4 `source-preparation`
  manifest and the `prepare-sources` command. Use Roslyn declaration IDs and
  syntax selectors and typed annotation payloads for C#, and explicit reasons
  for control mutations. Do not introduce text-replacement generators, regex
  model parsers or parallel copy-ledger formats. Check existing output with
  `check-prepared-sources`; changed inputs require a new output directory.
  Share pinned compiler-only sources through context-only `context_imports`
  using that same format, not duplicated hash catalogs.
- Reuse a recorded regression compiler context through
  `inspect-source --regression-result`; do not maintain scripts that reconstruct
  its references/defines or work around native argument limits. Keep selected
  sources explicit. Source inventories record their exact source list.
  Their method/constructor `flow` records contain Roslyn blocks, branch
  conditions, resolved calls/ref arguments and dominance. Treat these as
  structural correspondence evidence, not proof of call effects, implicit
  exception paths, atomicity or concurrency semantics.
- Declare executable C# premises in `integrated-bindings.source_obligations`,
  not a second proof-packet format or generated prose. Use binding IDs and
  Roslyn documentation IDs, never CFG block numbers. The initial checks cover
  guarded single effects, stable instance gate storage, an Int32 exchange gate
  and scoped monotonic Int32 writes. Unsupported control flow or alias escapes
  are incomplete, not successful approximations. These checks do not approve
  the source/model relation, infer CLR semantics or discharge human review.
- Reference publication uses `reference-cas-gate` and `reference-cell` premises:
  CompareExchange against null and scoped recognized reads/writes. A successful
  null-valued attempt need not publish anything; never infer one successful
  caller without a nonnull argument premise. Stable storage may explicitly
  allow source property getters; that does not prove getter purity. Generic
  source selectors use declaration IDs while flow facts retain constructed
  call targets. `guarded-try-effect` only extends the guard check to the first
  direct call in one top-level try body; it does not model handler semantics.
- Keep reference-helper authorization in the same source-obligation format:
  `reference-cas-effect` checks a null-comparand CAS winner before dispatch,
  while `callback-effect` checks the single private callback wiring through
  selected helper storage. Require cell-write and stable-storage premises
  separately; these checks do not automatically compose a theorem about
  callback effects, task settlement or progress. Readonly reference storage
  uses a direct fresh constructor initializer, not an inferred factory or
  a parallel helper-contract format.
- `balanced-async-gate` checks one normal acquisition immediately enclosing a
  top-level try/finally with one matching release attempt, optionally after a
  Monitor-lock prefix. It trusts structured C# finally/lock semantics, not raw
  CFG dominance as an exception proof. Keep gate semantics, successful release,
  eventual completion, transitive detached work and drain-before-destroy
  obligations separate. Unsupported aliases, delegates, loops and task
  transfers are incomplete, not approximated successes.
- Selected synchronous reference-type `using` lifetimes may use `point=cleanup`
  after a successful implicit `IDisposable.Dispose`, and `point=fault` for a
  throwing disposer. Instrumentation lowers that narrow compiler pattern once,
  retaining acquisition, null checks, reverse disposal order and exception
  propagation; it does not merely observe leaving a scope. Await-using,
  value-type/pattern disposal, multiple declarators and cleanup under source
  locks remain unsupported. Statement fault observations rethrow the original
  exception and do not imply successful cleanup. Keep these events distinct
  from resource-return attempts and whole-method completion.
- Keep report dispositions authored in the existing `evidence-report` manifest.
  Use `source_inventories` for mechanical member-region expansion and project
  `report --source-report` for presentation composition. Require explicit file
  defaults and unambiguous symbol-prefix overrides; never infer code coverage
  from checker outcomes or filename conventions.
- Own model development and technical self-review; do not delegate reading
  TLA+, Dafny or P to the user as the normal workflow. Follow the
  [authoring procedure](REFERENCE.md#model-authoring-and-self-checks), then
  explain the claim, assumptions, simplifications and gaps in plain English.
- Revisit the C# independently of the drafted model. Map relevant operations,
  competing accesses, awaits, cancellation, exceptions and cleanup to modeled
  behavior; list omissions. Resolved bindings alone do not establish this map.
  At an explicit interface boundary, use declared contract guarantees only,
  not behavior from excluded concrete or native implementations. Surface
  missing guarantees for a contract decision rather than inventing them.
- Justify atomic steps and state reductions. Do not hide relevant interleavings
  through serialization, invented snapshots or stronger environment assumptions.
  Obtain an explicit decision when an assumption changes the requested claim.
- Check reachability/non-vacuity and use defect-relevant positive and negative
  controls. A parser error, missing dependency or timeout is not a successful
  rejection control. Report untested controls and correspondence gaps.
- Investigate counterexamples before changing code, models or properties;
  rerun affected controls after changes. Do not tune independent requirements
  or approved configuration pins to fit observed outcomes.
- Keep agent self-review distinct from required human review. Never assert
  a user's approval or record yourself as the human reviewer. Instructions
  and self-checks are not an automatic guarantee of model adequacy or C#
  correctness; leave unsupported claims and required reviews outstanding.
- Pass measured call data through typed step `arguments`, not actor, operation
  or entity identities. Arguments are scalar values from locals, parameters or literals at
  the selected point, not guarded shared-state observations or causal edges.
  Keep serial-owner assumptions explicit for unguarded synchronous components.

## Build and validation

Prefer maintained sample source and contract files under
`tests/TlaLink.Tests/Fixtures` over embedded code strings in new tests. Share
canonical inputs between core and native suites where appropriate; keep
deliberate source mutants as named files rather than replacement strings.
Compile only explicitly selected fixture files, copy them to fresh test work
directories before use, and never mutate maintained samples. Fixture C# must
be content, not part of compiling the test runner.

- Source language selection uses `compiler.language_version` in JSON and
  `--language-version` for standalone source commands, with canonical values
  `8` or `14` and default `14`. Reuse that selection in source parsing,
  preparation, recorded compiler-context inventories and archived readers.
  Do not equate language compatibility with runtime/API compatibility.
  Tool-generated instrumentation hosts remain .NET 10/C# 14 and record their
  actual compilation context separately; regression builds must match their
  declared source language.

```powershell
dotnet build .\TlaLink.sln
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . --core
.\TlaLink.ps1 doctor --project .\examples\Workflow\tlalink.project.json --scenario lanes
.\TlaLink.ps1 plan --project .\examples\Workflow\tlalink.project.json --scenario lanes
.\TlaLink.ps1 review --annotation-version 4 --source .\examples\Demo.cs --scope .\examples\demo-scope.json --target demo --model .\examples\Demo.tla --jar .\.tools\tla\v1.8.0\tla2tools.jar --step demo::Admit
```

The core suite needs no external backends. The example preflight/review
commands require their selected pinned tools. Run the focused native tests
for a changed backend and the full suite when appropriate after provisioning
its prerequisites; see [testing](REFERENCE.md#tests-and-evidence).

- The root solution, public tests and examples must not depend on `local`.
  Private consumer work packages, copied sources, proofs and evidence belong
  under that Git-ignored directory. Follow each package's own instructions.
  Never commit or publish anything from `local`, and never force-add it.
  Never use its files or content as sample code, examples or documentation,
  including copied, adapted, renamed or sanitized versions. Public samples and
  documentation examples must be independently authored synthetic material,
  not derived from private consumer work. Use the allowlisted source export script.
- Tool distributions belong in the ignored root `.tools` cache. Public tests
  accept `TLALINK_TLA_JAR`, `TLALINK_DAFNY`, `TLALINK_P_COMPILER` and
  `TLALINK_APALACHE_JAR` overrides;
  `--core` runs without external backend tools. Provisioning is explicit;
  analysis commands must not install or upgrade tools.
- Java selection uses the single `java-toolchain.json` version pin: Temurin JDK 25
  with explicit Windows/Linux x64 distributions, provisioned with
  `scripts\Initialize-Java.ps1` under PowerShell 7.
  SANY/PlusCal, TLC, Apalache and their tests use `JavaToolchain`, never PATH or
  `JAVA_HOME`. `TLALINK_JAVA_HOME` may relocate only the same pinned JDK.
  Keep native P manifests' explicitly selected Java dependencies independent.
  Setup verifies the distribution archive; selection checks the pinned release
  and launcher bytes. This is not authentication or full installed-file attestation.
- Supply narrow sources/references/defines explicitly. Roslyn is not a product
  MSBuild workspace; unresolved context blocks instrumentation.
- Follow calls, competing accesses, awaits, cancellation, cleanup and external
  effects beyond the supplied scope. Symbol resolution does not prove atomicity.
- Use `inspect-source` with explicit sources/references/defines for a current
  Roslyn inventory before assigning source dispositions. Its calls and lock/
  await/access facts are not validated code, runtime dispatch or model coverage;
  review implicit disposal, async machinery and external effects separately.
- Use only `@link`, with `"annotation_version": 4` declared once in the root
  project. Components inherit it and must not override it. Standalone annotation
  commands require `--annotation-version 4`; never guess a version, restore
  per-comment version headers or add compatibility parsing for `@tla`.
  Review-only bindings need not have executable metadata.
- Instrument separate copied sources, never product code or frozen profiles.
  Keep selected observation points and missing coverage explicit.
- Use `execute` to bind generated sources to compiler inputs and a loaded binary,
  then `validate-trace` for a whole execution. Inspect explicit hidden-step
  assumptions and uncaptured Task/queue/handoff edges.
- Preserve archive consistency, current applicability and acceptance as distinct
  results. Never replace a baseline simply to clear drift.
- Evidence retention uses one schema-4 `evidence-retention` manifest with
  explicit path/reason pins and review-project roots. GC defaults to read-only;
  unknown reference scope blocks candidates and apply. Explicit Windows apply
  requires a saved full plan, offline writers, an exclusive assertion and a new
  receipt; never bypass its snapshot, byte or directory-handle guards.
  Native/legacy consumers use byte-complete protected catalogs from explicit
  trusted adapters, not ignore lists or public legacy acceptance. Preserve
  original applicability hashes and declaring bases; linked input targets must
  supply real retention edges and be revalidated by opened-handle identity.
  Retention and file-change/recheck impact
  are distinct from acceptance; do not infer stage-level impact or deletion
  safety from this graph. See the design for boundary rationale.
- Prefer project `doctor`/`plan`/`run`/`accept`/`explain` for complete workflows.
  Keep requirements separate from execution and reviews in append-only sidecars.
  TLC/trace requirements pin approved configuration bytes, including properties
  and bounds; do not update those pins merely to clear an acceptance failure.
  Pinned fixture inputs must declare any required checkout line endings in
  `.gitattributes`, so host defaults cannot change their approved bytes.
  Workflow, evidence-stage and Apalache examples pin CRLF configuration bytes; preserve those
  bytes on either host rather than changing requirements after checkout.

## Maintenance safeguards

- Project composition uses explicit schema-4 `analysis-project` roots and
  `project-component`, `project-input-sets` and `project-execution-profiles`
  documents. Resolve each path against its declaring file, select components
  explicitly per scenario, and reject duplicate IDs/imports, cycles and mixed
  inline/profile budgets. Input sets use explicit file lists or explicit
  `prepared_sources` ledger references, never globs or directory discovery.
  Ledger-derived review scope is a file-list expansion, not proof or retention
  approval; preserve every required input when replacing manual listings.
  Keep one normalized `AnalysisProject` for execution and save a
  `project-expansion` snapshot with all authored-file pins. Independent readers
  reconstruct that snapshot from archived authoring bytes. Editor schemas are
  generated from the typed authoring contracts using JSON Schema 2020-12;
  they are not another project language or an evidence-document format.
- Maintained consumer inputs use document-relative file references. Review
  writers emit project-relative evidence/fingerprint paths through `FilePaths`;
  runtime receipts retain observed absolute identities. Regression arguments
  are opaque strings except explicit zero-based `path_arguments` indices,
  whose file inputs resolve against the declaring regression config and are
  pinned. Never guess paths from argument text or rewrite historical receipts.
- TLC resource settings use one typed `resources` object (`workers`,
  `max_heap_mib`) on TLC/trace stages and the equivalent explicit trace CLI
  options. Record requested/effective values and the native invocation; reject
  unsupported settings rather than forwarding arbitrary arguments. JVM heap
  is not a total process-memory limit. Resource experiments retain logical
  configuration pins, requirements and prior incomplete runs.
- Console regression evidence uses `csharp-regression` manifests and
  `csharp-regression-result` receipts, with a `regression-observations` document
  from the harness. Native proof-only Dafny evidence uses `dafny-analysis` and
  `dafny-result`, reusing the integrated frontend/verifier/audit implementation.
  These are stage evidence, not traces, oracle comparisons or C# refinement.
  Share observed build provenance with `execute`; do not fabricate traces or
  dummy C# bindings. Keep framework-specific testhost adapters explicit.
  Record absent optional compiler configuration probes in
  `missing_build_inputs`; their later appearance is drift. Retention represents
  those expected absences with a null `expected_sha256`, not a fabricated hash.
  Project reports reread explicit runs through ordinary acceptance and produce
  the same `evidence-report` manifest; presentation freshness is not historical
  run applicability or acceptance.

- Whole-evidence human reports use one schema-4 `evidence-report` manifest and
  Markdown with embedded Mermaid, not competing diagram formats or a generic
  "proof passed" flag. Private adapters supply recorded native claims, source
  coverage dispositions and outstanding obligations; the public renderer checks pinned
  bytes, not native proof semantics. Preserve recorded outcomes, expected
  controls, current applicability and pending correspondence separately.
  A source inventory/disposition is not validated code. Reports are presentation
  snapshots, not acceptance receipts or automatic retention roots.

- Select tools by the obligation, modeling fit and evidence required, using
  the advantages/limits in the [checker selection guide](REFERENCE.md#choosing-tools-and-checkers). Describe
  capabilities and constraints, not status or a fixed tool priority.
  Frontend resolution, TLC exploration/trace matching, Apalache typing/safety,
  Dafny verification/oracles and the individual P checker claims are not
  interchangeable. State adapter availability and native test coverage
  separately; missing dependencies never justify weakening requirements.
- Tests cache exact selected immutable tool paths once per session, including
  workflow children through an explicit test host. Hold read leases; on Unix
  recheck bytes before reporting success. Keep source/configuration/generated
  inputs, archives and mutation fixtures uncached. Mutate copied tools, not
  selected installations. Do not add production cache switches, environment
  bypasses or timestamp-only fingerprint reuse.
- The declared TLC `result_store` is the only analysis-evidence reuse
  mechanism. Keys cover every answer-determining byte (model closure,
  configuration, JAR, JDK, options, fingerprint function, adapter version) and
  exclude only workers, heap and deadline. Bump `TlcResultStore.AdapterVersion`
  whenever TLC invocation, outcome classification or result fields change.
  Never store incomplete outcomes, reuse by timestamps or partial keys, or
  extend reuse to trace, regression or other backends without the same
  answer-determining key and acceptance revalidation. Use `check-stage` for
  expensive searches instead of raising routine budgets or weakening configs.
  Result-store cleanup uses `evidence-retention.result_projects` and `gc` on
  `<result_store>/tlc`, never ad hoc deletion of unmatched keys. List every
  project to protect; unknown/active entries block collection. Keep matching
  families at any age and preserve the existing saved-plan/offline-writer/
  byte/snapshot/handle checks for explicit Windows application.
- Apalache is an explicitly selected TLA+ engine, not an automatic TLC
  fallback. Its strict `apalache-analysis` manifests select typechecking,
  bounded safety or complete initiation/preservation/implication induction.
  Keep these claims distinct from TLC exhaustion and causal trace matching.
  Use native Snowcat types alongside, not instead of, `@link` bindings.
  Use the supported named nullary predicates and explicit constant initialization,
  not TLC configuration translation, temporal properties or simulation.
  Reject SANY-resolved ASSUME declarations rather than depend on differing
  upstream interpretations; encode required constraints in the selected
  predicates. Pin the complete local distribution, isolate native JSON
  configuration/environment and telemetry, and never provision during analysis.
- Keep declared safety/deadlock/progress checks explicit through
  [check obligations](REFERENCE.md#declared-check-coverage). A compatible mapped
  requirement is a plan, not a proof; never infer coverage from stage names.
  Missing mappings block runs and acceptance, and safety-only Apalache checks
  cannot discharge deadlock or progress obligations.
- P uses explicit `p-analysis` manifests and native backend-specific evidence,
  not TLA annotations or a shared "proof passed" flag. PChecker exploration,
  PEx/PSym exhaustion, PVerifier induction and PObserve log monitoring retain
  distinct claims. Only authoritative frontend declarations may resolve P
  bindings. External generated JARs require explicit source/build review;
  their hashes alone do not establish source correspondence. Unknown native
  output, partial exploration, missing tools and deadlines are incomplete.
  Pin complete local tool distributions and explicitly selected dependencies;
  never install or upgrade tools while executing an analysis.
- When multiple standards or representations are possible, choose one and
  document the choice and rationale here before introducing new usage. Apply
  it consistently to maintained code, schemas, examples and tests. Do not add
  parallel conventions, compatibility branches or competing normalization
  helpers; reuse one shared implementation. Keep upstream formats distinct
  only at explicit integration boundaries.
- Filepaths accept either separator at input boundaries through `FilePaths`;
  literal backslashes in Unix filenames are outside the supported input format.
  Emit native separators in JSON paths; URIs are not filepaths. Resolve
  CLI-relative paths against the caller's directory and document-relative paths
  against the declaring document's directory. Internal identities are normalized
  absolute native paths, with dot segments removed and no trailing separator
  except at a filesystem root. Reject foreign drive/UNC roots instead of
  reinterpreting them as local relative paths. Preserve spelling for display and
  compare paths ordinal case-insensitively on Windows, ordinal on Linux; never
  lowercase paths or use culture-sensitive comparison. Native frontend bridges
  apply the host comparer at their isolated integration boundary. Assembly names,
  hashes and stable IDs retain their separate comparison semantics.
  Artifact paths are run-relative and must remain inside the run.
  Normalize at input boundaries and reuse canonical values internally, not
  independent per-component normalization rules. Do not treat lexical path
  normalization as proof that symlinks or hard links identify different files.
  Stable IDs, model symbols, URIs and hashes retain their own documented
  semantics and must not be processed as filepaths.
- Keep Java SANY/PlusCal authoritative; do not hand-parse TLA+ expressions or
  replace TLC with an ad hoc interpreter.
- Preserve transitive import pinning, nonmutating translation freshness,
  timeout/termination handling, strict input validation and exclusive writes.
- All maintained public JSON uses `Schema.Version` (4) and an explicit document
  kind. Update producers/readers/examples/tests together; do not add legacy
  acceptance branches or rewrite historical evidence. `@link` is a separate
  source annotation grammar, not an older JSON schema.
- Fixed JSON shapes use named C# contracts: records for documents, receipts,
  native messages and presentation, with typed mutable builders only where
  execution accumulates state. Deserialize at the boundary, then validate and
  project typed values; do not serialize into a DOM to implement application
  logic. Variable keys use typed dictionaries, not `JsonObject`. Keep raw JSON
  only for generated JSON Schema transformation, genuinely variable native
  payloads, discriminator dispatch and malformed-input diagnostics. Trace
  scalars use the shared constrained runtime contract and preserve their
  original wire spelling where it participates in identity keys. New command
  results require an explicit typed summary projection; unknown results must
  fail visibly rather than silently lose fields. Tests may inspect or corrupt
  JSON independently of production contracts. Types do not replace semantic,
  archive, applicability, acceptance or retention validation.
- Oracle case inputs use the native Int32/Boolean scalar contract, not arbitrary
  JSON or trace identities. Preserve their accepted wire spellings and reject
  coercions, fractional/exponent encodings and unsupported kinds explicitly.
- Keep capture side effects and scheduling hooks separate. Never run hooks
  under the recorder lock or generate them inside source locks.
- Receiver/reference and `$call` identities are nominal correlation labels,
  never ordering or Task-handoff evidence. Use separate operation identities
  for independent pumps on one receiver and `$call` for overlapping invocations;
  never merge concurrent calls into a constant operation merely to obtain a
  witness. Invoke-only return/throw/switch capture does not observe completion.
- No global timestamp/sequence ordering, invented cross-lock snapshots or
  independently reset per-event model states.
- The producing host/build project is trusted, not authenticated. A matching
  finite observed prefix is not general C# refinement or liveness.
- Exit 0 means structural readiness or a trace witness for the chosen command;
  1 means structural failure or a completed trace rejection; 2 means incomplete.
  Project `run` instead reports execution completion, including completed
  rejection controls. Only `accept` evaluates the independent requirements.
- Add a regression/control for a motivating defect; update directly related
  docs and run the focused/full console suite as appropriate.
- Do not edit generated PlusCal bodies, frozen evidence, canonical source
  profiles or consumer acceptance requirements. Intentional consumer-runner
  integration changes require focused runner validation.
- On Windows `src\tlalink` and `src\TlaLink` alias each other. Treat them as one
  source directory. Maintained references must use the actual `src\tlalink`
  spelling so Linux builds remain case-correct.
