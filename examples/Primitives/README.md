# Synchronization primitives

This independently synthetic example declares one C# synchronization primitive,
verifies it once against the shipped `counting-resource` kind and reuses that
contract in a caller component. Nothing here is a machine-checked refinement
proof of the C# code; see [what is and is not claimed](../../REFERENCE.md#synchronization-primitives).

- [`PermitGate`](src/PermitGate.cs): FIFO asynchronous permits without barging.
- [Primitive manifest](primitives/permit-gate/primitive.json): every public
  member mapped to `TlaLinkCountingResource` actions, a wait predicate and a
  caller precondition.
- [Caller harness](primitives/permit-gate/PermitGateHarness.tla) checked with
  [positive](primitives/permit-gate/PermitGateHarness.cfg) and
  [lost-release negative control](primitives/permit-gate/PermitGateLostRelease.cfg)
  configurations: the `permit-gate` verification scenario.
- [Agent inspection argument](primitives/permit-gate/inspection.md) for the
  `permit-gate-inspection` obligation.
- [Correspondence scope](verification/scope.json) declaring the primitive under
  `synchronization.primitives`.
- [`Uploader`](src/Uploader.cs), its [caller model](verification/Uploader.tla)
  and the [caller component](components/uploader.json) whose TLC stage declares
  the `permit-gate` use: the `uploader` scenario.
- A synchronization region: the scope also declares `src/Uploader.cs` as its
  `region`, the [`chunk-sink` environment](environments/chunk-sink/environment.json)
  below it and the `statsLock` lock. The `region` scenario checks it with a
  source-only `synchronization` stage and rejects the
  [detached-write control](controls/UploaderDetachedWrite.cs), whose discarded
  write would release the permit before the chunk is written, with `SR104`.
- Instances and sites: the caller model declares the `gate` field as the
  instance `"g"` of its `Gate` INSTANCE definition and binds both operation
  sites of `SendAsync`. `AcquireAsync` maps to the `Acquire` and `Resume` steps
  and waits on `CanResume`, so its statement binds `AcquirePermit(s)`, which
  composes both steps; `Release` binds `Finish(s)`. The
  [unbound-close control](controls/UploaderUnboundClose.cs) adds a `Shutdown`
  that closes the gate at a site no bind or exclusion covers, rejected with
  `SR108` by the `uploader-unbound-close` stage.
- Liveness under stated fairness: the `uploader-liveness` stage checks
  [`UploaderLiveness.cfg`](verification/UploaderLiveness.cfg), whose
  `EveryAcquireCompletes` property needs fairness. The stage declares
  `fairness`: the kind-module `Gate!Fairness` formula, which SANY confirms
  `LiveSpec` references, and an author assumption of weak fairness for each
  sender's steps. Plans, acceptance results and reports list both beside the
  `chunk-sink` obligations, and the property is reported as conditional on them.
- Trace capture through `state_mapping`: the manifest maps the
  counting-resource `View` fields `available`, `waiting` and `closed` to
  `PermitGate` fields, read under its `sync` lock. The `uploader-trace` stage
  runs the [sequential workload](trace/UploaderWorkload.cs) (one sender, two
  uncontended sends) and snapshots the gate after the acquire and release
  sites, whose binds use `point: commit`; the trace validator compares every
  snapshot with `Gate!View("g")` after the bound action. The
  [double-credit control](controls/PermitGateDoubleCredit.cs) credits two
  permits per release; no action or exception reveals that, but the snapshots
  do, so `uploader-trace-double-credit` is rejected with `TL511`. The workload
  never queues a waiter, so traces do not exercise the FIFO handoff.

Both models instance the shipped kind module; the caller model declares the
field it represents on the INSTANCE definition:

```tla
(* @link {"instance":[{"id":"uploader::gate","field":"F:PrimitivesExample.Uploader.gate","kind_instance":"Gate","model":"\"g\""}]} *)
Gate == INSTANCE TlaLinkCountingResource
    WITH Instances <- {"g"}, Waiters <- Senders, Capacity <- [i \in {"g"} |-> 1], state <- gate
```

TlaLink puts its shipped `primitives` library on the SANY/TLC module search path
only for stages that declare primitive uses, pins the module bytes like other
model inputs and requires the kind module in each consumer's import closure.

After the separately documented tool setup, run from the repository root:

```powershell
.\TlaLink.ps1 doctor --project .\examples\Primitives\tlalink.project.json
.\TlaLink.ps1 plan --project .\examples\Primitives\tlalink.project.json --format summary
.\TlaLink.ps1 run --project .\examples\Primitives\tlalink.project.json --scenario region --output .\Results\primitives\runs\region-new
.\TlaLink.ps1 accept --project .\examples\Primitives\tlalink.project.json --run .\Results\primitives\runs\region-new --format summary
.\TlaLink.ps1 run --project .\examples\Primitives\tlalink.project.json --scenario permit-gate --output .\Results\primitives\runs\permit-gate-new
.\TlaLink.ps1 accept --project .\examples\Primitives\tlalink.project.json --run .\Results\primitives\runs\permit-gate-new --output .\Results\primitives\permit-gate-new.acceptance.json --format summary
```

The verification scenario does not need the primitive's own support; it
establishes it. Its accepted receipt is evidence for the inspection, which an
agent records explicitly after reading the source, manifest and argument:

```powershell
.\TlaLink.ps1 record-review --project .\examples\Primitives\tlalink.project.json --obligation permit-gate-inspection --origin agent --reviewer "agent:self-review" --rationale "See the inspection argument and the accepted harness scenario." --evidence "primitives\permit-gate\inspection.md" --evidence "..\..\Results\primitives\permit-gate-new.acceptance.json"
.\TlaLink.ps1 run --project .\examples\Primitives\tlalink.project.json --scenario uploader --output .\Results\primitives\runs\uploader-new
.\TlaLink.ps1 accept --project .\examples\Primitives\tlalink.project.json --run .\Results\primitives\runs\uploader-new --format summary
```

Accepting `uploader` requires current primitive support: the accepted inspection
and a fresh re-evaluation of the recorded `permit-gate` run. Changing
`PermitGate.cs`, the manifest or the kind module makes the inspection stale;
changing the harness makes the verification run stale. Either withdraws support
until the primitive is verified and inspected again. This is agent inspection,
not human approval.

The `region` scenario needs no model checker, but because the scope has a TLA+
target its instance and site checks resolve `Uploader.tla` through SANY with
the pinned JAR. Its checks also run in preflight
for the `uploader` model stage, which uses `permit-gate` from the same scope: a
failing region makes `doctor` report `failed` and blocks `run`. The
`permit-gate` verification stages model the primitive's harness and are not
blocked. The environment's obligations are listed as assumptions of the
synchronization stage and of every `uploader` model stage; it names no
supporting evidence, so they remain unsupported assumptions.

The manifest lists `signals-async` and `fifo-waiters`. The static check
discharges `signals-async` (every `TaskCompletionSource` is created with
`RunContinuationsAsynchronously`); `fifo-waiters` remains an outstanding
obligation backed by the kind contract and inspection.

The focused native tests copy this example into isolated directories:

```powershell
dotnet run --project .\tests\TlaLink.Tests\TlaLink.Tests.csproj -- . --primitives
```
