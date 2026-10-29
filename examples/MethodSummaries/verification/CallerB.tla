---- MODULE CallerB ----
EXTENDS PermitSummary, FiniteSets

Workers == {"east", "west"}
VARIABLES available, acquired, pending
vars == <<available, acquired, pending>>

Init ==
    /\ available = [worker \in Workers |-> 1]
    /\ acquired = [worker \in Workers |-> FALSE]
    /\ pending = Workers

Next ==
    \/ \E worker \in pending, after \in 0..1, result \in BOOLEAN :
         /\ TryTake(available[worker], after, result)
         /\ available' = [available EXCEPT ![worker] = after]
         /\ acquired' = [acquired EXCEPT ![worker] = result]
         /\ pending' = pending \ {worker}
    \/ /\ pending = {}
       /\ UNCHANGED vars

CallerPrecondition == \A worker \in pending : Requires(available[worker])
Safe == /\ available \in [Workers -> 0..1]
        /\ acquired \in [Workers -> BOOLEAN]
        /\ \A worker \in Workers \ pending :
             acquired[worker] /\ available[worker] = 0

====
