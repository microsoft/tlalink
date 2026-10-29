# Agent inspection argument for the synthetic permit summary

This is an agent-authored explanation, not human approval or a review receipt.
Record a new decision against current bytes with the project workflow.

The abstract state is the private `remaining` Int32 of one `PermitGate`.
Construction rejects negative inputs. The only later writer decrements a
strictly positive value through CompareExchange. The value stays nonnegative
and arithmetic cannot underflow.

On success, CompareExchange observes the expected value and atomically replaces
it with its predecessor. That successful exchange is the logical effect point;
the method returns true. A failed exchange does not write. Its returned value
becomes the next observation, so retries do not have an additional abstract
effect.

On failure, the initial Volatile.Read or a failed exchange observes zero, the
loop exits and the method returns false. Since the selected implementation has
no increases or resets, zero remains zero. The observation of zero supplies
the logical failure point.

There are no callbacks, suspension points or additional effects in this method.
Each instance has separate private storage. The argument relies on the declared
framework atomic/visibility contracts, the selected source scope and the absence
of other field writers. Source resolution does not establish those runtime
contracts.

The summary preserves counter changes and returned Booleans. It does not model
retry counts, duration, starvation or termination. Caller models must establish
the precondition and use separate abstract storage for unrelated instances.
Finite model checks do not prove every implementation execution matches this
argument.
