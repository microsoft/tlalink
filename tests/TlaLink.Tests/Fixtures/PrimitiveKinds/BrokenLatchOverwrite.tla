---- MODULE BrokenLatchOverwrite ----
\* Negative control: deliberately broken copy of TlaLinkLatch.
\* Defect: TrySet overwrites a failed latch and reports a win.
(* Latch kind: completes once, either set or failed. Later attempts lose.    *)
CONSTANT Instances
VARIABLE state

InitState == [i \in Instances |-> "unset"]
Init == state = InitState

TypeOK == state \in [Instances -> {"unset", "set", "failed"}]

\* A completed latch never changes; that is a transition property, so
\* harnesses check it with their own history.
Invariant == TRUE

TrySetEnabled(s, i, won) == won = (s[i] # "set")
TrySetApply(s, i, won) == IF won THEN [s EXCEPT ![i] = "set"] ELSE s
TrySet(i, won) == TrySetEnabled(state, i, won) /\ state' = TrySetApply(state, i, won)

TryFailEnabled(s, i, won) == won = (s[i] = "unset")
TryFailApply(s, i, won) == IF won THEN [s EXCEPT ![i] = "failed"] ELSE s
TryFail(i, won) == TryFailEnabled(state, i, won) /\ state' = TryFailApply(state, i, won)

CanWait(i) == state[i] # "unset"

Internal(i) == FALSE
Fairness == \A i \in Instances : WF_state(Internal(i))
================================================================================
