---- MODULE EarlyRelease ----
EXTENDS Dispatcher
ControlInit == Init
ControlInv == Inv
ControlConstInit == ConstInit

\* Defect: cancelling running work returns its credit before completion.
EarlyCancel(j) ==
    /\ states[j] \in {1, 2} /\ ~cancellationRequested[j]
    /\ cancellationRequested' = [cancellationRequested EXCEPT ![j] = TRUE]
    /\ states' = IF states[j] = 1 THEN [states EXCEPT ![j] = 4] ELSE states
    /\ outstanding' = outstanding - 1
    /\ UNCHANGED <<attempts, running, closing>>
BadNext == \/ \E j \in Jobs: Admit(j) \/ Start(j) \/ EarlyCancel(j)
           \/ \E j \in Jobs, succeeded \in BOOLEAN: Finish(j, succeeded)
           \/ Close
====
