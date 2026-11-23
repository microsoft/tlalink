# Agent inspection argument for the synthetic PermitGate primitive

This is an agent-authored explanation, not human approval or a review receipt.
Record a new decision against current bytes with the project workflow.

Abstract state. One `PermitGate` instance is one element of `Instances` in
`TlaLinkCountingResource`. `available` is the private `available` field,
`closed` the private `closed` flag and `queue` the order of pending
`TaskCompletionSource` entries in `waiters`. `granted` holds waiters whose task
was completed successfully but whose continuation has not yet run.

Every field is read and written only inside `lock (sync)`, so each operation's
state change happens in one critical section:

- `TryAcquire` succeeds exactly when the gate is open, a permit is free and no
  waiter is queued, matching `TryAcquire(i, ok)` and the no-barging rule.
- `AcquireAsync` takes a permit immediately under the same condition and
  returns a completed task (`Acquire` grants, `Resume` completes at once).
  Otherwise it enqueues a new waiter, or returns a failed task after `Close`,
  matching `Acquire` followed later by `Resume` once `CanResume` holds.
- `Release` hands its permit to the oldest queued waiter while open, otherwise
  frees it. Releasing at capacity throws; the manifest names `ReleaseEnabled`
  as the caller precondition, so callers must not release a permit they do not
  hold.
- `Close` marks the gate closed and fails every queued waiter. The kind keeps
  failed waiters queued until they resume; the implementation removes them at
  once. Both make every pending acquire end in failure.

Waiter tasks are created with `RunContinuationsAsynchronously`. `Release` and
`Close` complete them outside the lock; the only completion inside the lock is
a waiter failed at creation after `Close`, before any continuation can attach.
So no waiter code runs on a releasing or closing caller's stack. That is the
declared `signals-async` guarantee; with `fifo-waiters` it is an outstanding
obligation in this release, not a checked property.

State mapping. `state_mapping` reads `available` and `closed` directly and
`waiting` as `waiters.Count`, under `state_lock` (`sync`), the lock every
operation holds while changing them, so one snapshot is one consistent
critical-section state. The kind's `waiting` view is the queue length of an
open resource and zero once closed; `Close` clears `waiters` in the same
critical section, so the two agree. `granted` is not mapped: a handed-over
permit leaves no trace in the fields once its waiter is dequeued.

The argument relies on the selected source scope, the .NET `Monitor` and
`TaskCompletionSource` contracts and the manifest assumptions. It does not
cover cancellation, scheduling delay or termination. Finite model checks of the
caller harness do not prove that every implementation execution matches it.
