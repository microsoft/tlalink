------------------------- MODULE MCBrokenCountingResourceBarging -------------------------
(* Harness for TlaLinkCountingResource: per resource, two callers each run   *)
(* two rounds of TryAcquire or Acquire/Resume followed by Release, and a     *)
(* closer closes the resource at any point. Resource r1 has one permit and  *)
(* r2 has two.                                                               *)
EXTENDS Naturals, Sequences, FiniteSets
CONSTANTS r1, r2, Callers
Resources == {r1, r2}
Capacity == [r \in Resources |-> IF r = r1 THEN 1 ELSE 2]
Rounds == 2
VARIABLES res, phase, rounds, closer
vars == <<res, phase, rounds, closer>>

K == INSTANCE BrokenCountingResourceBarging
        WITH Instances <- Resources, Waiters <- Callers, Capacity <- Capacity, state <- res

Init ==
    /\ K!Init
    /\ phase = [r \in Resources |-> [c \in Callers |-> "idle"]]
    /\ rounds = [r \in Resources |-> [c \in Callers |-> 0]]
    /\ closer = [r \in Resources |-> "open"]

\* Waiting callers that received a handoff in this step become "granted".
Marked(r, ph) ==
    [c \in Callers |-> IF ph[c] = "waiting" /\ c \in res'[r].granted THEN "granted" ELSE ph[c]]
Step(r, c, to, done) ==
    /\ phase' = [phase EXCEPT ![r] = Marked(r, [@ EXCEPT ![c] = to])]
    /\ rounds' = IF done THEN [rounds EXCEPT ![r][c] = @ + 1] ELSE rounds
    /\ UNCHANGED closer

TryAcquire(r, c, ok) ==
    /\ phase[r][c] = "idle" /\ rounds[r][c] < Rounds
    /\ K!TryAcquire(r, ok)
    /\ Step(r, c, IF ok THEN "holding" ELSE "idle", ~ok)
Acquire(r, c) ==
    /\ phase[r][c] = "idle" /\ rounds[r][c] < Rounds
    /\ K!Acquire(r, c)
    /\ Step(r, c, "waiting", FALSE)
Resume(r, c) ==
    /\ phase[r][c] \in {"waiting", "granted"} /\ K!CanResume(r, c)
    /\ K!Resume(r, c)
    /\ LET ok == c \in res[r].granted
       IN Step(r, c, IF ok THEN "holding" ELSE "idle", ~ok)
Release(r, c) ==
    /\ phase[r][c] = "holding"
    /\ K!Release(r)
    /\ Step(r, c, "idle", TRUE)
Close(r) ==
    /\ closer[r] = "open" /\ closer' = [closer EXCEPT ![r] = "closed"]
    /\ K!Close(r)
    /\ UNCHANGED <<phase, rounds>>

Terminated ==
    /\ \A r \in Resources : closer[r] = "closed" /\
        \A c \in Callers : phase[r][c] = "idle" /\ rounds[r][c] = Rounds
    /\ UNCHANGED vars

Next ==
    \/ \E r \in Resources, c \in Callers :
        \/ \E ok \in BOOLEAN : TryAcquire(r, c, ok)
        \/ Acquire(r, c) \/ Resume(r, c) \/ Release(r, c)
    \/ \E r \in Resources : Close(r)
    \/ Terminated

Spec == Init /\ [][Next]_vars /\ K!Fairness

KindTypeOK == K!TypeOK
KindInvariant == K!Invariant
Holders(r) == {c \in Callers : phase[r][c] = "holding"}
\* Permits are neither created nor lost.
Conservation == \A r \in Resources :
    Cardinality(Holders(r)) + res[r].available + Cardinality(res[r].granted) = Capacity[r]
\* A permit handed to a waiter is never taken by another caller.
NoBarging == \A r \in Resources, c \in Callers : phase[r][c] = "granted" => c \in res[r].granted
WaitingQueued == \A r \in Resources, c \in Callers :
    phase[r][c] = "waiting" => c \in K!Queued(res, r)
\* Reachability witness: TLC must report this invariant as violated.
NoClosedWaiterWitness == ~\E r \in Resources : res[r].closed /\ res[r].queue # <<>>
================================================================================
