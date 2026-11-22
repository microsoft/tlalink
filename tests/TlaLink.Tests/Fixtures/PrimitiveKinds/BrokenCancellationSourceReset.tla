---- MODULE BrokenCancellationSourceReset ----
\* Negative control: deliberately broken copy of TlaLinkCancellationSource.
\* Defect: a second Cancel withdraws the request.
(* Cancellation source kind. Cancel is idempotent and runs registered        *)
(* callbacks inline on the caller's stack; models account for those          *)
(* callbacks in the caller's own step.                                       *)
CONSTANT Instances
VARIABLE state

InitState == [i \in Instances |-> [requested |-> FALSE]]
Init == state = InitState

TypeOK == state \in [Instances -> [requested : BOOLEAN]]

\* A request is never withdrawn; that is a transition property, so harnesses
\* check it with their own history.
Invariant == TRUE

CancelEnabled(s, i) == TRUE
CancelApply(s, i) == [s EXCEPT ![i].requested = ~@]
Cancel(i) == CancelEnabled(state, i) /\ state' = CancelApply(state, i)

CanWait(i) == state[i].requested

Internal(i) == FALSE
Fairness == \A i \in Instances : WF_state(Internal(i))
================================================================================
