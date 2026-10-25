# Working on TlaLink

Read [README.md](README.md) for usage/schema and [DESIGN.md](DESIGN.md) before
changing behavior. This is a C#/.NET 10 tool for TLA+, Dafny and P source/model
evidence.

Keep shared workflow/C# infrastructure in `Common`, P in `PLang`, SANY and
TLA bindings in `Tla`, TLC execution/trace matching in `Tlc`, PlusCal-specific
frontend helpers in `PlusCal`, symbolic safety in `Apalache`, and Dafny in
`Dafny`. Preserve the public
namespace and flat deployed bridge-asset names when moving implementation.

## Workflow

```powershell
dotnet build .\TlaLink.sln
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj
.\TlaLink.ps1 plan --project .\examples\Workflow\project.json --scenario lanes
.\TlaLink.ps1 review --source .\examples\Demo.cs --model .\examples\Demo.tla --jar .\.tools\tla\v1.8.0\tla2tools.jar --step Admit
```

- The root solution, public tests and examples must not depend on `local`.
  Private consumer work packages, copied sources, milestones and evidence belong
  under that Git-ignored directory. Follow each package's own instructions.
  Never publish it or force-add it. Use the allowlisted source export script.
- Tool distributions belong in the ignored root `.tools` cache. Public tests
  accept `TLALINK_TLA_JAR`, `TLALINK_DAFNY`, `TLALINK_P_COMPILER` and
  `TLALINK_APALACHE_JAR` overrides;
  `--core` runs without external backend tools. Tool lifecycle management is
  planned separately; do not imply automatic installation already exists.
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
- Use only `@tla 2`. Review-only bindings need not have executable metadata.
- Instrument separate copied sources, never product code or frozen profiles.
  Keep selected observation points and missing coverage explicit.
- Use `execute` to bind generated sources to compiler inputs and a loaded binary,
  then `validate-trace` for a whole execution. Inspect explicit hidden-step
  assumptions and uncaptured Task/queue/handoff edges.
- Preserve archive consistency, current applicability and acceptance as distinct
  results. Never replace a baseline simply to clear drift.
- Prefer project `doctor`/`plan`/`run`/`accept`/`explain` for complete workflows.
  Keep requirements separate from execution and reviews in append-only sidecars.
  TLC/trace requirements pin approved configuration bytes, including properties
  and bounds; do not update those pins merely to clear an acceptance failure.
  Pinned fixture inputs must declare any required checkout line endings in
  `.gitattributes`, so host defaults cannot change their approved bytes.
  Workflow and Apalache examples pin CRLF configuration bytes; preserve those
  bytes on either host rather than changing requirements after checkout.

## Maintenance safeguards

- Select tools by the obligation, modeling fit and evidence required, using
  the advantages/limits in README's checker selection guide. Describe
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
- Apalache is an explicitly selected TLA+ engine, not an automatic TLC
  fallback. Its strict `apalache-analysis` manifests select typechecking,
  bounded safety or complete initiation/preservation/implication induction.
  Keep these claims distinct from TLC exhaustion and causal trace matching.
  Use native Snowcat types alongside, not instead of, `@tla 2` bindings.
  Initially use named nullary predicates and explicit constant initialization,
  not TLC configuration translation, temporal properties or simulation.
  Reject SANY-resolved ASSUME declarations rather than depend on differing
  upstream interpretations; encode required constraints in the selected
  predicates. Pin the complete local distribution, isolate native JSON
  configuration/environment and telemetry, and never provision during analysis.
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
  acceptance branches or rewrite historical evidence. `@tla 2` is a separate
  source annotation grammar, not an older JSON schema.
- Keep capture side effects and scheduling hooks separate. Never run hooks
  under the recorder lock or generate them inside source locks.
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
