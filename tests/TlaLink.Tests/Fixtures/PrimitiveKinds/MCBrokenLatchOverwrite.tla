------------------------------- MODULE MCBrokenLatchOverwrite -------------------------------
(* Harness for TlaLinkLatch: per latch, two callers race TrySet or TryFail   *)
(* and a waiter resumes once the latch completes.                            *)
EXTENDS Naturals, FiniteSets
CONSTANTS l1, l2, Callers
Latches == {l1, l2}
VARIABLES latches, phase, waiter, first
vars == <<latches, phase, waiter, first>>

L == INSTANCE BrokenLatchOverwrite WITH Instances <- Latches, state <- latches

Init ==
    /\ L!Init
    /\ phase = [l \in Latches |-> [c \in Callers |-> "idle"]]
    /\ waiter = [l \in Latches |-> "waiting"]
    /\ first = [l \in Latches |-> "none"]

Complete(l, c, won, outcome) ==
    /\ phase' = [phase EXCEPT ![l][c] = IF won THEN "won" ELSE "lost"]
    /\ first' = IF won /\ first[l] = "none" THEN [first EXCEPT ![l] = outcome] ELSE first
    /\ UNCHANGED waiter
TrySet(l, c, won) == phase[l][c] = "idle" /\ L!TrySet(l, won) /\ Complete(l, c, won, "set")
TryFail(l, c, won) == phase[l][c] = "idle" /\ L!TryFail(l, won) /\ Complete(l, c, won, "failed")
Wait(l) ==
    /\ waiter[l] = "waiting" /\ L!CanWait(l)
    /\ waiter' = [waiter EXCEPT ![l] = "done"]
    /\ UNCHANGED <<latches, phase, first>>

Terminated ==
    /\ \A l \in Latches : waiter[l] = "done" /\ \A c \in Callers : phase[l][c] # "idle"
    /\ UNCHANGED vars

Next ==
    \/ \E l \in Latches, c \in Callers, won \in BOOLEAN : TrySet(l, c, won) \/ TryFail(l, c, won)
    \/ \E l \in Latches : Wait(l)
    \/ Terminated

Spec == Init /\ [][Next]_vars /\ L!Fairness

KindTypeOK == L!TypeOK
KindInvariant == L!Invariant
\* The first outcome is never reset or overwritten.
OutcomeStable == \A l \in Latches : first[l] # "none" => latches[l] = first[l]
SingleWinner == \A l \in Latches : Cardinality({c \in Callers : phase[l][c] = "won"}) <= 1
WaiterSawOutcome == \A l \in Latches : waiter[l] = "done" => latches[l] # "unset"
\* Reachability witness: TLC must report this invariant as violated.
NoFailedWakeWitness == ~\E l \in Latches : waiter[l] = "done" /\ latches[l] = "failed"
================================================================================
