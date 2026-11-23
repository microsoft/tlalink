---------------------------- MODULE MCBrokenAdmissionGateEarlySignal ----------------------------
(* Harness for TlaLinkAdmissionGate: per gate, two callers enter or try to   *)
(* enter and then exit, and a closer closes (once alone or both gates in one *)
(* composed step), closes again and waits for the drain signal.              *)
EXTENDS Naturals, FiniteSets
CONSTANTS g1, g2, Callers
Gates == {g1, g2}
Limits == {1, 2}
VARIABLES gates, phase, closer
vars == <<gates, phase, closer>>

G == INSTANCE BrokenAdmissionGateEarlySignal WITH Instances <- Gates, state <- gates

Init ==
    /\ G!Init
    /\ phase = [g \in Gates |-> [c \in Callers |-> "idle"]]
    /\ closer = [g \in Gates |-> "open"]

Move(g, c, from, to) == phase[g][c] = from /\ phase' = [phase EXCEPT ![g][c] = to]

Enter(g, c) == Move(g, c, "idle", "in") /\ G!Enter(g) /\ UNCHANGED closer
EnterRejected(g, c) == Move(g, c, "idle", "done") /\ G!EnterRejected(g) /\ UNCHANGED closer
TryEnter(g, c, limit, ok) ==
    /\ Move(g, c, "idle", IF ok THEN "in" ELSE "done")
    /\ G!TryEnter(g, limit, ok)
    /\ UNCHANGED closer
Exit(g, c) == Move(g, c, "in", "done") /\ G!Exit(g) /\ UNCHANGED closer

Close(g) ==
    /\ closer[g] = "open" /\ closer' = [closer EXCEPT ![g] = "closed"]
    /\ G!Close(g) /\ UNCHANGED phase
\* One atomic step closes both gates through the pure kind operators.
CloseBoth ==
    /\ closer[g1] = "open" /\ closer[g2] = "open"
    /\ G!CloseEnabled(gates, g1)
    /\ G!CloseEnabled(G!CloseApply(gates, g1), g2)
    /\ gates' = G!CloseApply(G!CloseApply(gates, g1), g2)
    /\ closer' = [closer EXCEPT ![g1] = "closed", ![g2] = "closed"]
    /\ UNCHANGED phase
CloseAgain(g) ==
    /\ closer[g] = "closed" /\ closer' = [closer EXCEPT ![g] = "reclosed"]
    /\ G!Close(g) /\ UNCHANGED phase
WaitDrained(g) ==
    /\ closer[g] = "reclosed" /\ G!CanWaitDrained(g)
    /\ closer' = [closer EXCEPT ![g] = "drained"]
    /\ UNCHANGED <<gates, phase>>

Terminated ==
    /\ \A g \in Gates : closer[g] = "drained" /\ \A c \in Callers : phase[g][c] = "done"
    /\ UNCHANGED vars

Next ==
    \/ \E g \in Gates, c \in Callers :
        \/ Enter(g, c) \/ EnterRejected(g, c) \/ Exit(g, c)
        \/ \E limit \in Limits, ok \in BOOLEAN : TryEnter(g, c, limit, ok)
    \/ \E g \in Gates : Close(g) \/ CloseAgain(g) \/ WaitDrained(g)
    \/ CloseBoth
    \/ Terminated

Spec == Init /\ [][Next]_vars /\ G!Fairness

KindTypeOK == G!TypeOK
KindInvariant == G!Invariant
CountMatches == \A g \in Gates : gates[g].count = Cardinality({c \in Callers : phase[g][c] = "in"})
DrainedMeansEmpty == \A g \in Gates : closer[g] = "drained" => \A c \in Callers : phase[g][c] # "in"
\* Reachability witness: TLC must report this invariant as violated.
NoDrainWitness == ~\E g \in Gates : closer[g] = "drained"
================================================================================
