-------------------------- MODULE MCBrokenCancellationSourceReset --------------------------
(* Harness for TlaLinkCancellationSource: each source is cancelled up to     *)
(* twice, alone or with the other source in one composed step, may be       *)
(* disposed once, and a waiter resumes once cancellation is requested.      *)
EXTENDS Naturals
CONSTANTS s1, s2
Sources == {s1, s2}
VARIABLES sources, cancels, requested, disposals, waiter
vars == <<sources, cancels, requested, disposals, waiter>>

C == INSTANCE BrokenCancellationSourceReset WITH Instances <- Sources, state <- sources

Init ==
    /\ C!Init
    /\ cancels = [s \in Sources |-> 0]
    /\ requested = [s \in Sources |-> FALSE]
    /\ disposals = [s \in Sources |-> 0]
    /\ waiter = [s \in Sources |-> "waiting"]

Cancel(s) ==
    /\ cancels[s] < 2 /\ cancels' = [cancels EXCEPT ![s] = @ + 1]
    /\ \/ C!Cancel(s) /\ requested' = [requested EXCEPT ![s] = TRUE]
       \/ C!CancelRejected(s) /\ UNCHANGED requested
    /\ UNCHANGED <<disposals, waiter>>
Dispose(s) ==
    /\ disposals[s] = 0 /\ disposals' = [disposals EXCEPT ![s] = 1]
    /\ C!Dispose(s) /\ UNCHANGED <<cancels, requested, waiter>>
\* One atomic step cancels both sources through the pure kind operators.
CancelBoth ==
    /\ cancels[s1] < 2 /\ cancels[s2] < 2
    /\ C!CancelEnabled(sources, s1)
    /\ C!CancelEnabled(C!CancelApply(sources, s1), s2)
    /\ sources' = C!CancelApply(C!CancelApply(sources, s1), s2)
    /\ cancels' = [cancels EXCEPT ![s1] = @ + 1, ![s2] = @ + 1]
    /\ requested' = [requested EXCEPT ![s1] = TRUE, ![s2] = TRUE]
    /\ UNCHANGED <<disposals, waiter>>
Wait(s) ==
    /\ waiter[s] = "waiting" /\ C!CanWait(s)
    /\ waiter' = [waiter EXCEPT ![s] = "done"]
    /\ UNCHANGED <<sources, cancels, requested, disposals>>

Terminated ==
    /\ \A s \in Sources : cancels[s] = 2 /\ disposals[s] = 1 /\ (waiter[s] = "done" \/ ~sources[s].requested)
    /\ UNCHANGED vars

Next == (\E s \in Sources : Cancel(s) \/ Dispose(s) \/ Wait(s)) \/ CancelBoth \/ Terminated

Spec == Init /\ [][Next]_vars /\ C!Fairness

KindTypeOK == C!TypeOK
KindInvariant == C!Invariant
\* A request, once made, is never withdrawn.
RequestStable == \A s \in Sources : requested[s] => sources[s].requested
WaiterSawRequest == \A s \in Sources : waiter[s] = "done" => sources[s].requested
\* Disposal is permanent.
DisposalStable == \A s \in Sources : disposals[s] > 0 => sources[s].disposed
\* Reachability witness: TLC must report this invariant as violated.
NoWakeWitness == ~\E s \in Sources : waiter[s] = "done"
================================================================================
