-------------------------- MODULE MCBrokenCancellationSourceReset --------------------------
(* Harness for TlaLinkCancellationSource: each source is cancelled up to     *)
(* twice, alone or with the other source in one composed step, and a waiter *)
(* resumes once cancellation is requested.                                   *)
EXTENDS Naturals
CONSTANTS s1, s2
Sources == {s1, s2}
VARIABLES sources, cancels, waiter
vars == <<sources, cancels, waiter>>

C == INSTANCE BrokenCancellationSourceReset WITH Instances <- Sources, state <- sources

Init ==
    /\ C!Init
    /\ cancels = [s \in Sources |-> 0]
    /\ waiter = [s \in Sources |-> "waiting"]

Cancel(s) ==
    /\ cancels[s] < 2 /\ cancels' = [cancels EXCEPT ![s] = @ + 1]
    /\ C!Cancel(s) /\ UNCHANGED waiter
\* One atomic step cancels both sources through the pure kind operators.
CancelBoth ==
    /\ cancels[s1] < 2 /\ cancels[s2] < 2
    /\ C!CancelEnabled(sources, s1)
    /\ C!CancelEnabled(C!CancelApply(sources, s1), s2)
    /\ sources' = C!CancelApply(C!CancelApply(sources, s1), s2)
    /\ cancels' = [cancels EXCEPT ![s1] = @ + 1, ![s2] = @ + 1]
    /\ UNCHANGED waiter
Wait(s) ==
    /\ waiter[s] = "waiting" /\ C!CanWait(s)
    /\ waiter' = [waiter EXCEPT ![s] = "done"]
    /\ UNCHANGED <<sources, cancels>>

Terminated ==
    /\ \A s \in Sources : waiter[s] = "done" /\ cancels[s] = 2
    /\ UNCHANGED vars

Next == (\E s \in Sources : Cancel(s) \/ Wait(s)) \/ CancelBoth \/ Terminated

Spec == Init /\ [][Next]_vars /\ C!Fairness

KindTypeOK == C!TypeOK
KindInvariant == C!Invariant
\* A request, once made, is never withdrawn.
RequestStable == \A s \in Sources : cancels[s] > 0 => sources[s].requested
WaiterSawRequest == \A s \in Sources : waiter[s] = "done" => sources[s].requested
\* Reachability witness: TLC must report this invariant as violated.
NoWakeWitness == ~\E s \in Sources : waiter[s] = "done"
================================================================================
