---- MODULE PermitGateHarness ----
EXTENDS Naturals, FiniteSets
\* Caller harness for the PermitGate counting-resource contract. LoseRelease
\* selects a negative control in which a holder drops its permit.

CONSTANT LoseRelease
Workers == {"w1", "w2", "w3"}
VARIABLES gate, pc
vars == <<gate, pc>>

Gate == INSTANCE TlaLinkCountingResource
    WITH Instances <- {"g"}, Waiters <- Workers, Capacity <- [i \in {"g"} |-> 1], state <- gate

Init ==
    /\ Gate!Init
    /\ pc = [w \in Workers |-> "idle"]

Request(w) ==
    /\ pc[w] = "idle"
    /\ Gate!Acquire("g", w)
    /\ pc' = [pc EXCEPT ![w] = "waiting"]

TryTake(w) ==
    /\ pc[w] = "idle"
    /\ \E ok \in BOOLEAN :
        /\ Gate!TryAcquire("g", ok)
        /\ pc' = [pc EXCEPT ![w] = IF ok THEN "holding" ELSE "idle"]

Resume(w) ==
    /\ pc[w] = "waiting"
    /\ Gate!CanResume("g", w)
    /\ Gate!Resume("g", w)
    /\ pc' = [pc EXCEPT ![w] = IF w \in gate["g"].granted THEN "holding" ELSE "idle"]

Finish(w) ==
    /\ pc[w] = "holding"
    /\ IF LoseRelease THEN UNCHANGED gate ELSE Gate!Release("g")
    /\ pc' = [pc EXCEPT ![w] = "idle"]

Shutdown ==
    /\ ~gate["g"].closed
    /\ Gate!Close("g")
    /\ UNCHANGED pc

Next == Shutdown \/ \E w \in Workers : Request(w) \/ TryTake(w) \/ Resume(w) \/ Finish(w)

Holders == {w \in Workers : pc[w] = "holding"}
KindTypeOK == Gate!TypeOK
KindInvariant == Gate!Invariant
Exclusive == Cardinality(Holders) <= 1
Accounting == gate["g"].available + Cardinality(gate["g"].granted) + Cardinality(Holders) = 1
ReleasePrecondition == Holders # {} => Gate!ReleaseEnabled(gate, "g")

====
