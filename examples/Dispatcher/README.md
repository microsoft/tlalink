# Dispatcher backend campaign

A larger, independently synthetic C# sample shared by **TLA+/TLC, PlusCal,
Apalache, Dafny, PChecker, PEx, PSym, PVerifier and PObserve**. It is a
limitation-discovery campaign, not a generic correctness certificate.

## Behavior and source boundary

[Dispatcher.cs](src/Dispatcher.cs) admits unique job identities into a bounded
window, starts attempts, retries failed attempts, cancels queued/running work
and closes admission while outstanding work drains.

| State | Legal successful operations |
|---|---|
| New | Admit, if open and a credit is available |
| Queued | Start; Cancel retires immediately |
| Running | Complete; Cancel only requests cancellation |
| Succeeded / Cancelled / Failed | None: identities are not reused |

Completion after a running cancellation retires as cancelled, even if the
worker reports success. Completion before cancellation preserves its terminal
outcome. A retry retains its outstanding credit; terminal completion returns
it once. Closing is idempotent, rejects admission, and does **not** destroy
resources while work is outstanding.

Every core transition holds the same private Monitor lock. `Retire` is private
and called only inside that lock; snapshots clone all three arrays under it.
This justifies modeling successful core operations atomically, not treating
worker execution, callbacks or external I/O as atomic. Invalid indices throw;
ordinary capacity/wrong-state rejection returns false and is abstract stuttering.

The finite models fix **three jobs, two outstanding credits and two attempts**.
C# accepts configurations up to sixteen jobs/attempts, and its concurrent
admission case uses sixteen jobs; the finite checks establish no cutoff theorem.
Dafny proves preservation over arbitrary legal finite histories and positive
mathematical job/capacity/retry parameters. That is a theorem about its supplied
transitions, not a proof that C# produces only those histories.

No worker implementation, `CancellationToken`, callback reentry, detached task,
I/O backpressure, disposer or drain-before-destroy implementation is supplied.
The C# race tests use actual parallel calls, but do not exhaust schedules.
No causal C# trace matching or executable Dafny oracle comparison is claimed.

## Maintained project

The [root project](tlalink.project.json) selects code-adjacent components and
[independent requirements](verification/requirements.json). Configuration pins
use LF bytes enforced by the repository attributes. Models own Roslyn selectors;
C# contains no `@link` comments. Human correspondence review remains pending.

Restore the new console project as a **separate setup step**:

```powershell
dotnet restore .\examples\Dispatcher\src\Regression.csproj --configfile .\NuGet.Config
```

Then select one scenario explicitly for both preflight and execution:

```powershell
.\TlaLink.ps1 doctor --project .\examples\Dispatcher\tlalink.project.json --scenario tla
.\TlaLink.ps1 plan --project .\examples\Dispatcher\tlalink.project.json --scenario tla
.\TlaLink.ps1 run --project .\examples\Dispatcher\tlalink.project.json --scenario tla --output .\Results\dispatcher-tla-new
.\TlaLink.ps1 accept --project .\examples\Dispatcher\tlalink.project.json --run .\Results\dispatcher-tla-new
```

Scenarios are `source`, `tla`, `pluscal`, `apalache`, `dafny`, and `pchecker`.
Use new output paths. Reassess a saved run with `accept` before rerunning it.
An execution-completed result includes completed rejection controls; it does
not approve the source/model relation.

The maintained project selects Windows launcher pins, like the other public
project examples. Linux launcher/distribution selection is a separate setup
choice; this campaign's extended native setup was measured on Windows only.

## Checks and controls

- **C#**: eleven cases cover capacity and duplicate admission, successful retry,
  exhaustion, queued/running/late cancellation, duplicate completion, shutdown,
  invalid input, snapshot isolation, concurrent admission and cancellation races.
- **TLC**: conservation and state invariants, monotonic closure and conditional
  eventual drain. Fairness is per job's start/completion action; it assumes
  workers eventually return, not merely that a scheduler runs.
  Removing fairness must reject the drain property. Leaked credit and early
  running-cancellation release are independently named model mutants.
  False invariants witness reachable retry, cancellation and drain states.
  Terminal quiescence is intentional, so deadlock checking is disabled: no
  deadlock-freedom claim follows.
- **PlusCal**: a controller selects orders of complete lock operations, with
  rejected calls as no-ops. Authored labels own source bindings; generated PC
  steps are algorithm control, not additional C# effects. This model checks
  safety only; it does not inherit the TLA+ model's fairness/progress argument.
- **Apalache**: typing, eight-step safety, initiation/preservation/implication
  induction, the same two defects, and retry reachability. Mutant modules use
  explicit local predicate wrappers because this adapter selects root operators.
- **Dafny**: count-update/subset lemmas, transition preservation and induction
  over finite histories. The terminal-credit control requires a verifier
  postcondition rejection, not a parsing/build failure.
- **PChecker**: scripted lifecycle cases followed by three reply-ordered clients,
  each choosing eight commands. It samples 100 schedules with seed 17 and a
  3000-step bound. Replies model source call ordering, not Task handoffs.
- **PEx / PSym**: the same core and scripts, followed by a finite three-client
  admission/start/completion, cancellation and closure race. This smaller
  environment is explicit; exhaustion does not cover the random-client driver
  or every possible C# caller.
- **PVerifier**: inline handlers, finite job domains, conservation invariants,
  seed requests and a false no-admission control. Native procedural helper calls
  are deliberately unsupported by TlaLink. Pure expressions are distinct from
  runtime helper calls. Proof/default obligations and the control are separate.
- **PObserve**: [a serial C# workload](src/DispatcherObservationLog.cs) emits
  successful operations for two dispatchers. The native monitor reconstructs
  legal transitions from 24 events. Stopwatch ticks only order each serial
  partition; they establish no global causal order. A duplicate-completion log
  must violate the monitor; an unknown operation must remain a parser-incomplete
  result. Missing events, rejection calls and concurrency are outside this log.

## Extended P setup and projects

Follow [the separate native setup recipe](setup/README.md). The
[external-project initializer](Initialize-ExternalProject.ps1) accepts explicitly
selected tools, artifacts, logs and build inputs, pins their bytes, and creates
ordinary schema-4 projects with required human build/source reviews. It neither
installs tools nor approves artifacts.

For PObserve, collect a fresh log before creating its project:

```powershell
.\TlaLink.ps1 check-regression --config .\examples\Dispatcher\src\observation-regression.json --output .\Results\dispatcher-observations-new
```

Use the resulting `dispatcher.log` and regression receipt as selected build/review
inputs. Synthetic rejection/parser logs are separate maintained fixtures, not
assertions that C# emitted those defects.

## Campaign reporting and selective reruns

Select each run explicitly and use its declaring project:

```powershell
.\TlaLink.ps1 report --project .\examples\Dispatcher\tlalink.project.json --run .\Results\dispatcher-source-new --run .\Results\dispatcher-tla-new --run .\Results\dispatcher-dafny-new --output .\Results\dispatcher-comparison-new.md --format summary
.\TlaLink.ps1 explain --project .\examples\Dispatcher\tlalink.project.json --run .\Results\dispatcher-tla-new --format summary
```

The report adds a stage matrix with authored expected outcomes, observed
outcomes, requirement dispositions, wall times, available native metrics,
incomplete reasons and reviews. It keeps each run's archive consistency,
current applicability and acceptance separate. Missing statistics are not zero
and rejected controls remain rejected even when they satisfy a requirement.
External-P generated projects need their own reports; do not pretend their
smaller environments are equivalent to the root project's scenarios.

Acceptance/explanation `next_steps` are read-only argument vectors and reasons,
not automatic retries. Review-only changes need new decisions and acceptance,
not replay. Stale/incomplete execution suggests scenario preflight and a fresh
run, with optional isolated TLC searches only through a declared result store.
This sample does not declare one; it therefore does not receive isolated
`check-stage` advice. Changed independent requirements and damaged archives
require investigation rather than automatic execution advice.

## Measured findings

Measured on Windows with the pinned tools and SDK in this checkout. Times are
illustrative wall-clock stage times, not a cross-machine performance contract.
Generated receipts/logs remain under ignored result directories.

| Backend | Native result | Important limitation |
|---|---|---|
| C# regression | Eleven selected cases passed; serial log workload passed | Tests and trusted producing host, not exhaustive execution |
| TLC | 3962 distinct states exhausted; safety/fair drain passed; five controls rejected | Fixed domain, stated fairness and fingerprint omission risk |
| PlusCal/TLC | 23772 distinct states exhausted; safety passed | Extra algorithm control expands the state graph; no progress claim |
| Apalache | Typing and all three induction obligations passed (~18 s); defects/retry controls rejected | Eight-step healthy search exceeded 120 s and remains incomplete |
| Dafny | 24 verification obligations completed; terminal-credit control rejected | Supplied mathematical history model, not C# refinement or async progress |
| PChecker | Requested 100 schedules completed; both defects rejected | Sampling, not exhaustion |
| PEx | 182 distinct states / 79 schedules; native `correct for any depth`; both defects rejected | Smaller finite caller environment and pending artifact review |
| PSym | One symbolic execution, depth 55; native `correct for any depth`; both defects rejected | Supported legacy artifact/runtime boundary and pending artifact review |
| PVerifier | Six invariants including default obligations completed (~71 s) | The false admission control also contains a solver-unknown goal; it remains incomplete |
| PObserve | 24/24 events, 2/2 partitions monitored; duplicate completion rejected; parser control incomplete | Serial observational property checking, not causal whole-trace inclusion |

Do **not** lower the eight-step bound, remove the PVerifier control, relax its
unknown classification or approve reviews merely to obtain acceptance.
Native outcomes, archived-byte consistency, current applicability and acceptance
must be reported separately. Tool/model edits made earlier runs stale; historical
outcomes were not rewritten or carried forward as approval.

### Tool changes discovered here

1. **Fixed native executable selection.** Complete Java distributions contain
   files such as `java.md`; stem matching selected these alongside `java.exe`
   and crashed the command. Selection now requires one exact native executable
   and reports ambiguity/missing prerequisites explicitly.
2. **Fixed PObserve native options and reader checks.** The runtime expects
   `--inputkind TEXT|JSON` and `--replaySize`, not the former argument spellings.
   Independent reading now rechecks the configured Java/runtime/monitor/parser/
   log and exact option vector.
3. **Blocked unsafe PVerifier helper calls.** A no-admission control exposed
   native generated callers restoring cached fields/messages after a helper
   committed its effects. The source is now conservatively rejected before
   proof, using the native call graph. Inline handlers preserve the intended
   transitions; a native pass alone did not discover this defect.

The separate native compiler setup also needs two upstream generation fixes:
an empty-package PEx import and PObserve's generated equality parameter type.
Their exact source deltas are maintained in the setup patches, not applied to
the selected official P installation or to generated model bodies.

### Efficiency additions

1. **Structured diagnostics and measured timing.** PVerifier/Dafny retain
   bounded native goal diagnostics and source locations where supplied.
   Dafny also archives native CSV verification-effort durations/resources;
   these are not timings assigned to individual error messages. Apalache
   retains available native phase timestamp intervals, including reporting.
   Readers reconstruct the projections without changing classifications.
2. **Explicit pinned setup and manual native CI.** The
   [setup wrapper](setup/Initialize-Native.ps1) reuses the maintained recipes,
   with `-Plan`/`-CheckPrerequisites` for nonmutating preparation. Provisioning
   stays separate from analysis. The optional
   [CI lane](../../.github/workflows/extended-p.yml) requires reviewed dependency
   inputs and retains the full build closure with human review pending.
3. **Focused translation controls.**
   [Named synthetic fixtures](../../tests/TlaLink.Tests/Fixtures/PTranslation/README.md)
   exercise inline event/field persistence, helper rejection, supported verifier
   invariants and selected PObserve/PEx Java generation. Healthy controls,
   native property counterexamples, Java compilation defects and parser
   incompleteness remain distinct; optional unselected controls are uncovered.
4. **Campaign comparison.** The existing project report now retains typed
   stage matrix data with expected/observed outcomes, available native metrics,
   incomplete reasons and separate review/archive/applicability/acceptance.
5. **Selective rerun guidance.** Existing acceptance/explanation results expose
   read-only `next_steps`, preserving review-only reassessment and the sole
   supported TLC result-store reuse path.

The SDK 10.0.204 campaign exercised both fresh online setup and the frozen-cache
offline-dependency setup for all four extended P engines. Core controls
passed 452/452, native evidence 35/35, Apalache 28/28 and explicitly selected
P translation controls 39/39 without skips. PEx/PSym healthy and credit-defect
controls, and PObserve healthy/duplicate-completion controls completed.
The fresh PVerifier healthy stage passed (~75 s); admission remained incomplete
(~209 s, `PL104`). These are measured local outcomes, not a hosted CI run or
human source/build approval. Later common-pin changes require fresh applicable
evidence rather than rewriting these runs.

Shared tool selections now come from the root
[SDK](../../global.json), [JDK](../../java-toolchain.json) and
[native prerequisite](../../native-toolchain.json) pins; see
[the setup recipes](setup/README.md) for compatibility exceptions.
These additions do not solve the healthy eight-step Apalache
timeout or the mixed-unknown PVerifier admission control. Investigate their
native diagnostics before changing budgets or duplicating models. The measured
induction/search difference is not a universal engine ranking, and focused
translation controls are not general native compiler attestation.

A separately specified real worker/cleanup interface and captured causal
handoffs remain necessary before claiming cancellation settlement or
drain-before-destroy coverage.
