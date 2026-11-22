----------------------- MODULE TlaLinkCancellationSource -----------------------
(* Cancellation source kind. Cancel is idempotent and runs registered        *)
(* callbacks inline on the caller's stack; models account for those          *)
(* callbacks in the caller's own step. Cancel after Dispose throws.          *)
CONSTANT Instances
VARIABLE state

InitState == [i \in Instances |-> [requested |-> FALSE, disposed |-> FALSE]]
Init == state = InitState

TypeOK == state \in [Instances -> [requested : BOOLEAN, disposed : BOOLEAN]]

\* A request is never withdrawn and disposal is permanent; those are
\* transition properties, so harnesses check them with their own history.
Invariant == TRUE

CancelEnabled(s, i) == ~s[i].disposed
CancelApply(s, i) == [s EXCEPT ![i].requested = TRUE]
Cancel(i) == CancelEnabled(state, i) /\ state' = CancelApply(state, i)

\* Cancel on a disposed source throws without changing it.
CancelRejectedEnabled(s, i) == s[i].disposed
CancelRejectedApply(s, i) == s
CancelRejected(i) == CancelRejectedEnabled(state, i) /\ state' = CancelRejectedApply(state, i)

\* Dispose is idempotent and does not withdraw an earlier request.
DisposeEnabled(s, i) == TRUE
DisposeApply(s, i) == [s EXCEPT ![i].disposed = TRUE]
Dispose(i) == DisposeEnabled(state, i) /\ state' = DisposeApply(state, i)

CanWait(i) == state[i].requested

Internal(i) == FALSE
Fairness == \A i \in Instances : WF_state(Internal(i))
================================================================================