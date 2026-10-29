---- MODULE CallerA ----
EXTENDS PermitSummary

VARIABLES available, acquired, pending
vars == <<available, acquired, pending>>

Init ==
    /\ available = 1
    /\ acquired = FALSE
    /\ pending = TRUE

Next ==
    \/ /\ pending
       /\ TryTake(available, available', acquired')
       /\ pending' = FALSE
    \/ /\ ~pending
       /\ UNCHANGED vars

CallerPrecondition == pending => Requires(available)
Safe == /\ available \in 0..1
        /\ acquired \in BOOLEAN
        /\ (~pending => acquired /\ available = 0)

====
