-------------------------------- MODULE MCBrokenOnceDoubleWinner --------------------------------
(* Harness for TlaLinkOnce: per instance, two callers Publish. The winner    *)
(* starts and settles the work; a loser waits until it is settled.           *)
EXTENDS Naturals, FiniteSets
CONSTANTS o1, o2, Callers
Onces == {o1, o2}
VARIABLES onces, phase
vars == <<onces, phase>>

O == INSTANCE BrokenOnceDoubleWinner WITH Instances <- Onces, state <- onces

Init == O!Init /\ phase = [o \in Onces |-> [c \in Callers |-> "idle"]]

Move(o, c, from, to) == phase[o][c] = from /\ phase' = [phase EXCEPT ![o][c] = to]

Publish(o, c, won) == Move(o, c, "idle", IF won THEN "won" ELSE "waiting") /\ O!Publish(o, won)
Start(o, c) == Move(o, c, "won", "running") /\ O!Start(o)
Settle(o, c) == Move(o, c, "running", "settled") /\ O!Settle(o)
Wait(o, c) == Move(o, c, "waiting", "resumed") /\ O!CanWait(o) /\ UNCHANGED onces

Terminated ==
    /\ \A o \in Onces, c \in Callers : phase[o][c] \in {"settled", "resumed"}
    /\ UNCHANGED vars

Next ==
    \/ \E o \in Onces, c \in Callers :
        \/ \E won \in BOOLEAN : Publish(o, c, won)
        \/ Start(o, c) \/ Settle(o, c) \/ Wait(o, c)
    \/ Terminated

Spec == Init /\ [][Next]_vars /\ O!Fairness

KindTypeOK == O!TypeOK
KindInvariant == O!Invariant
Winners(o) == {c \in Callers : phase[o][c] \in {"won", "running", "settled"}}
SingleWinner == \A o \in Onces : Cardinality(Winners(o)) <= 1
PhaseMatches == \A o \in Onces :
    /\ onces[o] = "published" => \E c \in Callers : phase[o][c] = "won"
    /\ onces[o] = "running" => \E c \in Callers : phase[o][c] = "running"
LoserSawSettled == \A o \in Onces, c \in Callers : phase[o][c] = "resumed" => onces[o] = "settled"
\* Reachability witness: TLC must report this invariant as violated.
NoLoserResumedWitness == ~\E o \in Onces, c \in Callers : phase[o][c] = "resumed"
================================================================================
