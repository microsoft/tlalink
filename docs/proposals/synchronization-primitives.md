# Synchronization primitives and environments

Status: increments 1 to 3 and the environment manifests of increment 4 are
implemented; see [synchronization primitives](../../REFERENCE.md#synchronization-primitives)
for the current contract and section 14 for what differs. The rest is a
proposal. First consumer: a private multiplexed RPC and bulk-transfer
library, a two-endpoint protocol over one ordered frame transport. Its mapping
stays with that consumer; every example here is synthetic.

This proposal replaces reviewed method summaries with a broader concept. It is
written against the problem, not the current schema; where it conflicts with
existing behavior, the existing behavior changes.

## 1. Problem

TlaLink links models to C# in one direction: a model's `bind` entries must
resolve to source, and a scope lists the bindings each target requires. That is
enough for "this action corresponds to that statement". It is not enough for
the question concurrent .NET code most often needs answered: can this code
deadlock or stop making progress?

Answering that with a model requires four things TlaLink cannot express today.

1. **Which C# types synchronize, and how.** Nothing declares that a type is a
   gate, a latch or a queue, or which of its members wait and which signal. A
   new `SemaphoreSlim`, a raw `TaskCompletionSource` or an unbound `await` is
   invisible to every check.
2. **Completeness in the code-to-model direction.** A progress argument is only
   as good as its wait-for graph. Every wait site and every site that can end a
   wait must be accounted for in some model, or explicitly excluded.
3. **Locks as atomic steps.** Modeling every lock region as an atomic action is
   the abstraction that keeps such models tractable. It is sound only if the
   regions never wait, never run foreign code inline and nest in a known order.
   Nothing checks that.
4. **Explicit environment boundaries.** The transport below, the RPC consumer
   above and the application using a lane are environments. Progress claims
   depend on their fairness obligations, which today are implicit and do not
   appear in reports.

Reviewed method summaries cover a narrow slice of this. They pair one
synchronous, nonvirtual method with a stateless relation. Primitives are
stateful objects with several instances, asynchronous waits, and operations
whose intermediate states matter (a gate is closed before its drain is
signaled). Summaries cannot express those, and they do not claim to.

## 2. Goals and non-goals

Goals:

- A small, fixed vocabulary of primitive kinds, each shipped by TlaLink as a
  TLA+ contract module with explicit state, actions, waits and fairness.
- One declaration manifest per primitive type, kept beside its contract and
  verification scenarios. Each primitive is verified once; component models
  reuse the contract.
- Per-instance modeling: fields holding primitives are discovered with Roslyn
  and bound to model instances, including instances indexed by an owner (one
  gate per lane).
- A declared synchronization region in which only declared primitives may
  synchronize, with completeness checks for waits, signals, spawns and lock
  regions.
- Environment manifests whose contracts and obligations appear in every
  report as assumptions, separate from what was checked.
- Large-model support: instance sets, symmetry on safety stages, fairness
  stated per wait, and trace projection per component.

Non-goals:

- Automatic C# refinement proofs or model generation.
- General alias analysis. Instances are fields; locals and parameters that
  carry a primitive are traced only through declared fields.
- Languages other than C#.
- Replacing trace validation, inspection or native checker evidence. These
  remain distinct kinds of evidence.

## 3. Concepts

| Concept | Meaning |
|---|---|
| Primitive | A C# type whose instances carry synchronization state. Declared by one manifest. |
| Kind | An abstract state machine shipped by TlaLink: state, actions, waits, fairness, guarantees. |
| Operation | A primitive member mapped to one or more kind actions, with a role. |
| Instance | A field whose declared type is a primitive, optionally indexed by its owner type. |
| Site | A call or await of an operation on an instance, found by Roslyn inside the region. |
| Environment | A party outside the region with a contract module and obligations. |
| Region | The explicit set of C# sources in which only primitives synchronize. |
| Spawn | A site that starts concurrent work: `Task.Run`, a discarded task, `async void`. |

## 4. Kind library

TlaLink ships one TLA+ module per kind. Each is parameterized by an instance
set, keeps per-instance state in functions over that set, and exports:

- `Init`, and one action per kind action, each taking the instance;
- a wait predicate per waiting operation (the condition that lets it resume);
- fairness formulas per instance and wait, for liveness stages to include;
- a `TypeOK` and the kind's own invariants.

| Kind | Abstract state | Actions | Waits |
|---|---|---|---|
| `register` | value, or none | `Set`, `TrySet` (first wins), `Read` | none |
| `latch` | unset, set or failed | `TrySet`, `TryFail` | `Wait` until set or failed |
| `once` | empty, published, running, settled | `Publish`, `Start`, `Settle` | `Wait` until settled |
| `admission-gate` | admitted count, closed, drain signaled | `Enter`, `TryEnter(limit)`, `Exit`, `Close`, `Signal` | `WaitDrained` |
| `counting-resource` | available, FIFO waiters, closed | `TryAcquire`, `Acquire`, `Release`, `Close` | `Acquire` until granted or closed |
| `channel` | item sequence, bound, completion (none, drain, abort) | `Enqueue`, `TryEnqueue`, `Dequeue`, `Complete` | `Dequeue` until item or completion |
| `critical-section` | none | the whole region is one atomic step | none |
| `cancellation-source` | requested, registered callbacks | `Cancel` (runs callbacks inline), `Register` | `Wait` until requested |
| `custom` | defined by the author's module | declared by the manifest | declared by the manifest |

Three rules make the library usable for real code:

- **Multi-step operations.** An operation maps to a sequence of kind actions
  when the code has a visible gap between them. A run-once helper's `Run` maps
  to `Publish` then `Start`, because a concurrent caller can observe the
  published task before the action starts. Effects the primitive completes by
  itself later, such as signaling a drained gate, are internal actions instead.
  This follows the Specula guidance on not collapsing relevant interleavings.
- **Asynchronous waits.** A waiting operation is modeled as an action whose
  guard is the kind's wait predicate, scheduled by the scheduler environment.
  This is what `await` on a primitive means when continuations never run
  inline. Summaries rejected async members; primitive operations accept them.
- **Inline effects are explicit.** `cancellation-source.Cancel` runs callbacks
  on the caller's stack. A primitive that can run continuations inline must say
  so (section 6), and the lock-region check treats such operations as foreign
  code.

`custom` covers primitives that do not fit a standard kind, such as a credit
protocol split across sender and receiver types. Its module must still declare
every wait predicate and its fairness, so completeness checks apply equally.

A stateless helper with no waits is a `custom` primitive whose operations are
all `pure`. That replaces today's method summaries.

### 4.1 Kind module interface

TlaLink ships the modules as tool assets named `TlaLink<Kind>.tla`, so they
cannot collide with author modules. A stage that uses a primitive gets the
library on its module search path, and its bytes are pinned like any other
model input.

Every kind module has the same shape:

- `CONSTANT Instances`, plus kind-specific constants listed below.
- One `VARIABLE state`, a function from `Instances` to a per-instance record or
  value. Authors substitute their own variable:
  `Gate == INSTANCE TlaLinkAdmissionGate WITH Instances <- GateIds, state <- gates`.
- `Init`, `TypeOK` and `Invariant` (the kind's own safety properties).
- Actions take the instance first. Each changes `state` at that instance only,
  as `state' = [state EXCEPT ![i] = ...]`; callers conjoin `UNCHANGED` for their
  other variables. Outcomes are parameters the caller binds: an action
  `TryEnter(i, limit, ok)` is enabled for exactly one value of `ok`.
- Every action `A(i, ...)` is defined from two pure operators over a state
  value `s`: `AEnabled(s, i, ...)` and `AApply(s, i, ...)`, as
  `A(i, ...) == AEnabled(state, i, ...) /\ state' = AApply(state, i, ...)`.
  Code often changes several instances of one kind in a single atomic step,
  such as one lock region closing three gates. Models compose those steps from
  the pure operators:
  `gates' = Gate!CloseApply(Gate!CloseApply(gates, a), b)`.
  `InitState` is the initial state value and `Init == state = InitState`.
- Wait predicates `Can<Wait>(i ...)` state when a waiting operation may resume.
  Resuming is the caller's own action, guarded by that predicate.
- `Internal(i)` is the disjunction of actions the primitive takes by itself,
  not on behalf of a caller, and `Fairness == \A i \in Instances : WF_state(Internal(i))`.
  Kinds without internal actions define `Internal(i) == FALSE`.

| Module | Constants | `state[i]` | Actions | Waits | Internal |
|---|---|---|---|---|---|
| `TlaLinkRegister` | `Values`, `None` | value or `None` | `Set(i, v)`, `TrySet(i, v, won)` | none | none |
| `TlaLinkLatch` | none | `"unset"`, `"set"` or `"failed"` | `TrySet(i, won)`, `TryFail(i, won)` | `CanWait(i)` | none |
| `TlaLinkOnce` | none | `"empty"`, `"published"`, `"running"` or `"settled"` | `Publish(i, won)`, `Start(i)`, `Settle(i)` | `CanWait(i)` | none |
| `TlaLinkAdmissionGate` | none | `[count, closed, signaled]` | `Enter(i)`, `EnterRejected(i)`, `TryEnter(i, limit, ok)`, `Exit(i)`, `Close(i)`, `Signal(i)` | `CanWaitDrained(i)` | `Signal(i)` |
| `TlaLinkCountingResource` | `Waiters`, `Capacity` (a function of `Instances`) | `[available, queue, granted, closed]` | `TryAcquire(i, ok)`, `Acquire(i, w)`, `Release(i)`, `Close(i)`, `Resume(i, w)` | `CanResume(i, w)` | none |
| `TlaLinkChannel` | `Items` | `[items, completion, waiting]` | `TryEnqueue(i, x, ok)`, `Dequeue(i, x)`, `StartWait(i)`, `Complete(i, mode)` | `CanDequeue(i)` | none |
| `TlaLinkCancellationSource` | none | `[requested]` | `Cancel(i)` | `CanWait(i)` | none |

Semantics the modules must make precise:

- `TlaLinkAdmissionGate`: `Enter` is enabled only while open, and
  `EnterRejected` only while closed, so a throwing `Enter` is an explicit step.
  `TryEnter` is admitted when open and `count < limit`. `Close` is idempotent.
  `Signal` sets `signaled` once the gate is closed with `count = 0`, as a step
  separate from the `Close` or `Exit` that made it possible. `Invariant`
  includes `signaled => closed /\ count = 0`.
- `TlaLinkCountingResource`: FIFO handoff without barging. `Release` grants the
  head waiter if one is queued, otherwise increments `available`, and is not
  enabled at `Capacity[i]`. `Acquire` takes a permit immediately only when one
  is available and nobody is queued; otherwise it enqueues `w`. `Close` fails
  every queued waiter. `CanResume(i, w)` holds once `w` was granted or failed,
  and `Resume` consumes that grant or failure.
- `TlaLinkChannel`: one consumer. `completion` is `"open"`, `"drain"` or
  `"abort"`. `TryEnqueue` succeeds only while open. `Complete(i, "drain")` keeps
  queued items for the consumer; `Complete(i, "abort")` discards them, including
  the remaining items of a channel that is still draining. Any other repeated
  `Complete` changes nothing.
  `Dequeue(i, x)` takes the head item; `StartWait` records a consumer waiting
  on an empty open channel; `CanDequeue` holds when an item is queued or the
  channel is complete.
- `TlaLinkOnce`: `Publish(i, won)` lets exactly one caller win; the winner then
  takes `Start` and later `Settle`. Losers wait with `CanWait`.

Each module ships with a model-checking harness over a small instance set that
checks `TypeOK`, `Invariant` and the absence of deadlock in a harness that
exercises every action. Each kind also has negative controls: deliberately broken
variants that the harness must reject.

## 5. Primitive manifest

One manifest per primitive type, `primitive.json`, in that primitive's folder
beside its scenarios. Paths are relative to the manifest.

```json
{
  "kind": "primitive",
  "id": "drain-gate",
  "type": "T:Demo.Sync.DrainGate",
  "sources": ["../../src/DrainGate.cs"],
  "primitive_kind": "admission-gate",
  "state_mapping": {
    "count": "this.state & int.MaxValue",
    "closed": "this.state < 0",
    "signaled": "this.drained.Task.IsCompleted"
  },
  "operations": [
    { "member": "M:Demo.Sync.DrainGate.#ctor", "role": "create" },
    { "member": "M:Demo.Sync.DrainGate.Enter", "role": "acquire", "actions": ["Enter"], "fails": ["EnterRejected"] },
    { "member": "M:Demo.Sync.DrainGate.TryEnter(System.Int32)", "role": "try-acquire", "actions": ["TryEnter"] },
    { "member": "M:Demo.Sync.DrainGate.Exit", "role": "release", "actions": ["Exit"] },
    { "member": "M:Demo.Sync.DrainGate.Close", "role": "close", "actions": ["Close"] },
    { "member": "P:Demo.Sync.DrainGate.Drained", "role": "wait", "wait": "CanWaitDrained" },
    { "member": "P:Demo.Sync.DrainGate.Count", "role": "query" }
  ],
  "guarantees": ["signals-async", "non-blocking"],
  "verification": {
    "scenarios": ["drain-gate"],
    "inspection": "drain-gate-review"
  },
  "assumptions": ["Each successful Enter or TryEnter is paired with exactly one Exit by its owner."],
  "limits": ["No claim about the order in which concurrent Enter calls are admitted."]
}
```

| Field | Purpose |
|---|---|
| `type`, `sources` | The C# type and its explicit source files, resolved with the project compiler profile. |
| `primitive_kind` | One kind from the library, or `custom` with a `module` property naming the author's TLA+ module. |
| `state_mapping` | Read-only C# expressions over the type's fields giving each abstract state variable. Used for trace capture and inspection; never evaluated outside a captured observation. |
| `operations` | Every public or internal member, including constructors. `role` is one of `create`, `query`, `acquire`, `try-acquire`, `release`, `signal`, `wait`, `close`, `complete`, `enqueue`, `dequeue`, `pure`. `actions` lists kind actions in order; a trailing `?` marks a conditional step. `fails` lists the kind actions that model the member's exceptional outcomes. An optional `precondition` names a module operator the caller must establish, checked as an authored property at the call boundary. |
| `guarantees` | Claims the kind or the checks rely on (section 6). |
| `verification` | Scenarios that check the primitive against its kind, and the inspection obligation that pins the source. The obligation keeps today's human or agent origin rules. |
| `depends_on` | Other primitives this one is built from, by `id`. The graph must be acyclic; support for a primitive requires current support for its dependencies. |
| `assumptions`, `limits` | Carried into every report that depends on this primitive. |

A member missing from `operations` is an error, so new members cannot bypass
the declaration.

### Verification of a primitive

A primitive is checked once, in its own scenarios:

- a model-checking stage over a small caller harness using the kind module,
  with negative controls for broken variants (an early drain, a lost signal);
- an inspection obligation that pins the sources, the manifest and the kind
  module, recorded with the existing review decisions and origins;
- optionally a trace stage: a C# workload exercises the primitive and TlaLink
  captures `state_mapping` at each operation site, then validates the trace
  against the kind module.

Component scenarios that use the primitive record a dependency on its current
verification. When the primitive's sources change, its support becomes stale;
component results stay applicable to the abstraction but are reported as
conditional until the primitive is verified again. This reuses the staleness
rules of today's summaries.

## 6. Guarantees

| Guarantee | Meaning | Discharged by |
|---|---|---|
| `signals-async` | Signal, release and complete operations never run waiter code on the caller's stack. | Static check: every `TaskCompletionSource` or `ManualResetValueTaskSourceCore` in the sources is configured to run continuations asynchronously. |
| `non-blocking` | No operation other than declared waits suspends or blocks. | Static check: no `await`, `Wait`, `Monitor.Wait` or blocking call outside wait operations. |
| `fifo-waiters` | Blocked acquirers are granted in arrival order. | Kind contract and inspection. |
| `cancellable-wait` | A wait can be abandoned by a token without changing primitive state. | Kind contract and inspection. |
| `inline-callbacks` | An operation can run foreign code on the caller's stack. | Declaration only; it restricts where the operation may be used. *Implemented* as the operation-level `inline_callbacks` flag instead of a guarantee. |

A guarantee whose discharge evidence is missing or stale is reported as an
outstanding obligation, never silently assumed.

## 7. Environment manifest

One manifest per environment, `environment.json`.

```json
{
  "kind": "environment",
  "id": "frame-transport",
  "environment_kind": "interface",
  "type": "T:Demo.Mux.IFrameChannel",
  "module": "FrameChannel.tla",
  "operations": [
    { "member": "M:Demo.Mux.IFrameChannel.WriteAsync(Demo.Mux.Frame)", "role": "submit", "actions": ["Admit", "Publish"], "lock_safe": true },
    { "member": "M:Demo.Mux.IFrameChannel.FlushAsync", "role": "wait", "wait": "CanFlush" },
    { "member": "M:Demo.Mux.IFrameChannel.ReceiveAsync", "role": "dequeue", "wait": "CanReceive" }
  ],
  "obligations": [
    "Writes whose invocations do not overlap are admitted in invocation order.",
    "An admitted publication completes, or the transport fails within its timeout."
  ],
  "evidence": ["transport-conformance"]
}
```

| Environment kind | Meaning |
|---|---|
| `interface` | Code outside the region implementing a type the region calls (a transport). |
| `consumer` | Code outside the region calling into it or reading what it produces (an RPC library reading a pipe, an application using a session). |
| `scheduler` | Built in. Spawned work eventually runs; continuations of async signals run later, never inline. |

`lock_safe` marks an environment operation whose synchronous part may run
inside a critical section: it does not block, does not take region locks and
does not run region code inline. It is an obligation of the environment, so it
appears in reports.

Obligations are listed in every report that depends on the environment, under
assumptions. `evidence` names stages (for example conformance tests run as a
regression stage) that support them; their absence leaves the obligations as
unsupported assumptions, which is legitimate but visible.

## 8. Region

The correspondence scope gains a `synchronization` section:

```json
"synchronization": {
  "region": ["../src/Connection.cs", "../src/Session.cs"],
  "primitives": ["primitives/drain-gate/primitive.json", "primitives/permit-pool/primitive.json"],
  "environments": ["environments/frame-channel/environment.json"],
  "locks": [
    { "field": "F:Demo.Mux.Connection.openOrder", "may_hold": ["F:Demo.Mux.SessionTable.sync"] },
    { "field": "F:Demo.Mux.Session.stateLock", "indexed_by": "T:Demo.Mux.Session" }
  ]
}
```

- `region` is an explicit file list. Primitive sources are checked by their own
  manifests and are not part of the region.
- `locks` declares every lock object used by a `lock` statement in the region,
  as `critical-section` instances. `may_hold` lists locks that may be acquired
  while this one is held; the declared graph must be acyclic.
- Instances of primitives are discovered from region fields whose declared type
  is a primitive. `indexed_by` on a field's owner makes the instance a function
  over that owner's identities in the model.

## 9. Model side

A component model declares the instances it models, using a new annotation
array, `instance`, beside `bind`, `capture` and `exclude`:

```tla
(* @link {"instance":[
  {"id":"conn::sends","field":"F:Demo.Mux.Connection.sends","kind_instance":"Gate","model":"\"sends\""},
  {"id":"conn::writes","field":"F:Demo.Mux.Session.writes","kind_instance":"Gate","model":"<<\"writes\", s>>","index":["s"]}
]} *)
Gate == INSTANCE TlaLinkAdmissionGate WITH Instances <- GateIds, state <- gates
```

Sites are bound with ordinary `bind` entries whose `source` selects the site.
TlaLink resolves the site's instance and operation with Roslyn, so the binding
needs no extra metadata.

*Implemented* as shown, with these refinements: `kind_instance` names the
INSTANCE definition explicitly and must be the one the comment precedes;
`model` is the instance identifier within that instance's `Instances` set (not
a state value), parse-checked by SANY with the `index` names (an array) bound;
the comment holds only `instance` entries. A bind covers a site when its
resolved selection contains the site.

## 10. Diagnostics

Codes are assigned at implementation. Each runs during preflight, before any
checker, and is a hard error unless a typed `exclude` with a reason covers the
site.

| Check | Rule |
|---|---|
| Raw synchronization | In the region, no synchronization outside declared primitives: `Interlocked`, `Volatile`, `Monitor`, `SemaphoreSlim`, `TaskCompletionSource`, `ManualResetValueTaskSourceCore`, `Channel`, `Pipe`, events, and `lock` on an undeclared object. |
| Undeclared operation | Every member of a primitive used in the region is declared in its manifest. |
| Undeclared wait | Every leaf `await` in the region targets a primitive wait, an environment operation, or a task produced by a declared spawn. Awaiting another region method is a call, checked inside that method. |
| Unbound site | Every site of an acquire, release, signal, wait, close, complete, enqueue or dequeue on an instance is bound in some component that declares the instance. |
| Contract mismatch | An action bound to a site applies the kind action the operation maps to, on the instance the site uses, checked by SANY reference resolution. |
| Lock region | Inside a declared critical section: no wait operations; no operations without `signals-async` (implemented for primitives that have waiters); no `inline-callbacks` operations; environment operations only if `lock_safe`; no delegate invocations; nested locks only per `may_hold`. |
| Spawn | Every spawn site is bound, and either joined by a bound wait or declared `detached` with the reason nothing depends on it. |
| Undischarged guarantee | A guarantee in use has current discharge evidence. |

These checks need no model checker. A project can run them alone as a source
stage, which is useful before any model exists.

## 11. Large models

- **Instance sets.** Kind modules range over instance sets supplied by the
  scenario configuration. Owner-indexed instances share one set per owner.
- **Symmetry.** An owner-indexed instance set may be declared symmetric. Stages
  may use symmetry only for safety and deadlock checking; TlaLink rejects
  symmetry on stages with temporal properties, because TLC symmetry reduction
  is unsound for liveness.
- **Fairness.** Liveness stages name the waits whose fairness they assume. The
  kind modules supply the formulas, and reports list the assumed fairness
  beside environment obligations.
- **Trace projection.** Captures at primitive sites carry the instance and the
  `state_mapping` values. A trace stage may project one recorded run onto
  several component models by instance set, instead of requiring one run per
  model.

## 12. What this replaces

- `method_summaries` in the correspondence scope and on stages is removed. A
  summary becomes a `pure` or atomic operation of a primitive manifest. Its
  inspection, origin and staleness rules carry over unchanged.
- The `examples/MethodSummaries` example becomes a primitive example: the
  `PermitGate` there is a `counting-resource`.
- Observed primitive fields in trace capture are expressed through
  `state_mapping`, so capture no longer needs per-field annotations for
  declared primitives.

## 13. Shapes the first consumer exercises

A multiplexed RPC and bulk-transfer library touches every kind:

| C# shape | Kind |
|---|---|
| One-shot flag, single-assignment reference | `register` |
| Set-once completion signal with cancellable waits | `latch` |
| Run-once cleanup shared by every caller, run on the winner's stack | `once` whose run operation declares `inline_callbacks` |
| Rundown gate for in-flight operations, with a bounded try-enter | `admission-gate` |
| FIFO async semaphore guarding send admission or a buffer pool | `counting-resource` |
| Single-consumer async queue with drain or abort completion | `channel` |
| Credit window split into sender and receiver halves | `custom` |
| Shutdown and per-operation cancellation | `cancellation-source` |
| Lifetime, ordering and table locks | `critical-section` |

Its environments are an ordered frame channel (`interface`), an RPC library a pipe and the applications using sessions (both `consumer`), and the. The consumer's own mapping, region and models stay with it.

## 14. Implementation increments

Each increment ships with a synthetic example, tests and documentation, and is
validated on the first consumer before the next starts.

1. **Kind library and primitive manifests.** Kind modules with their own
   negative controls; manifest loading, operation completeness, verification
   scenarios and inspection; stages declare the primitives they use; removal
   of `method_summaries`. `state_mapping` arrives with increment 5.
2. **Region checks without models.** Raw synchronization, undeclared operation
   and wait, lock region and spawn checks as a source-only stage.
   *Implemented* as `SR100`..`SR106` (`SR200` for an incomplete compilation),
   the `synchronization` stage kind and the `check-synchronization` command,
   with library primitives (`sources: []`) and the static `signals-async` and
   `non-blocking` guarantee checks. Differences from the text above: spawns
   and awaits that are not declared waits use scope-owned typed `spawns` and
   `waits` entries instead of model bindings or `exclude`, because no model
   exists yet; spawns are declared `joined` or `detached` rather than bound;
   `try-acquire` counts as a role that can resume waiters; the checks also run
   in preflight for model stages that use a primitive from a region scope,
   except the primitive's own verification stages; events are not raw
   synchronization by themselves, but invoking one under a lock is `SR103`.
3. **Instances and sites.** The `instance` annotation, unbound-site and
   contract-mismatch checks.
   *Implemented* as `SR107` (undeclared instance), `SR108` (unbound site),
   `SR109` (contract mismatch), `SR110` (stale instance) and `SR111`
   (unresolved instance receiver), run with the region checks whenever a
   region scope has TLA+ targets, in synchronization stages and in preflight.
   Differences from the text above: instance comments attach to the INSTANCE
   definition and carry an explicit `kind_instance`; exclusions reuse the
   formal-owned `exclude` (selecting a field excludes it and its sites); a site
   must be covered by a bind of any TLA+ target of the scope, and every bind
   covering it must reference all non-optional actions of the operation and,
   for a wait, its predicate, through the instance declared by the bind's own
   target, so multi-step operations bind an operator composing their steps; the
   SANY check establishes which kind operations an action references, not that
   it applies them to the instance the site uses; receivers that are not region
   fields are reported, not traced; Dafny and P targets are not read. Running
   these checks against a first region also changed increment 2: optional
   lists may be empty, `inline-callbacks` became the operation-level
   `inline_callbacks` flag, the lock-region resumption rule applies only to
   primitives with a `wait` operation, and environment and library-primitive
   operations may be inherited members, attributed by the receiver's static
   type.
4. **Environments and reports.** Environment manifests, `lock_safe`,
   obligations and fairness assumptions in the evidence report.
   *Environment manifests are implemented* with `types` (one or more source or
   metadata types) instead of one `type`, operations limited to the members a
   region uses, SANY resolution of `actions`/`wait` when a `module` is given,
   and `evidence` validated against project scenario and stage IDs. Plans
   list obligations, and project reports carry them as assumptions of
   synchronization stages; fairness assumptions remain for this increment.
5. **Scale.** Instance sets, symmetry rules, trace capture through
   `state_mapping` and per-component projection.

## 15. Open questions

- Whether `state_mapping` expressions should be restricted to field reads and
  a few operators, so they are checked rather than trusted.
- How to treat `CancellationTokenSource` registrations made outside the region
  that run region code inline when a region token is canceled.
- Whether pipes should be a library kind (`channel` with pause and resume
  thresholds) rather than environment-only, since other consumers will use
  `System.IO.Pipelines` inside their regions.
- Whether lock-order checking should also cover locks inside primitives, or
  rely on each primitive's `non-blocking` and `signals-async` guarantees.
