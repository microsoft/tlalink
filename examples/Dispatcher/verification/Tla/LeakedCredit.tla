---- MODULE LeakedCredit ----
EXTENDS Dispatcher
ControlInit == Init
ControlInv == Inv
ControlConstInit == ConstInit

\* Defect: terminal completion forgets to return the outstanding credit.
LeakyFinish(j, succeeded) ==
    /\ states[j] = 2
    /\ states' = [states EXCEPT ![j] = Outcome(j, succeeded)]
    /\ running' = running - 1
    /\ UNCHANGED <<outstanding, attempts, cancellationRequested, closing>>
BadNext == \/ \E j \in Jobs: Admit(j) \/ Start(j) \/ Cancel(j)
           \/ \E j \in Jobs, succeeded \in BOOLEAN: LeakyFinish(j, succeeded)
           \/ Close
====
