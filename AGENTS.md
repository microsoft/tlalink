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

## Helping a user obtain evidence

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

## Build and validation

```powershell
dotnet build .\TlaLink.sln
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . --core
.\TlaLink.ps1 doctor --project .\examples\Workflow\tlalink.project.json --scenario lanes
.\TlaLink.ps1 plan --project .\examples\Workflow\tlalink.project.json --scenario lanes
.\TlaLink.ps1 review --annotation-version 2 --source .\examples\Demo.cs --model .\examples\Demo.tla --jar .\.tools\tla\v1.8.0\tla2tools.jar --step Admit
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
- Use only `@link`, with `"annotation_version": 2` declared once in the root
  project. Components inherit it and must not override it. Standalone annotation
  commands require `--annotation-version 2`; never guess a version, restore
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
  inline/profile budgets. Input sets are explicit file lists, not globs.
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
