---- MODULE Uploader ----
EXTENDS Naturals, FiniteSets
\* Two senders share one PermitGate permit and hold it across one transport call.

Senders == {"a", "b"}
VARIABLES gate, pc
vars == <<gate, pc>>

Gate == INSTANCE TlaLinkCountingResource
    WITH Instances <- {"g"}, Waiters <- Senders, Capacity <- [i \in {"g"} |-> 1], state <- gate

Init ==
    /\ Gate!Init
    /\ pc = [s \in Senders |-> "idle"]

Acquire(s) ==
    /\ pc[s] = "idle"
    /\ Gate!Acquire("g", s)
    /\ pc' = [pc EXCEPT ![s] = "acquiring"]

(* @link {"bind":[{"id":"uploader::send","source":{"symbol":"M:PrimitivesExample.Uploader.SendAsync(System.ReadOnlyMemory{System.Byte})"}}]} *)
Send(s) ==
    /\ pc[s] = "acquiring"
    /\ Gate!CanResume("g", s)
    /\ Gate!Resume("g", s)
    /\ pc' = [pc EXCEPT ![s] = "sending"]

Finish(s) ==
    /\ pc[s] = "sending"
    /\ Gate!Release("g")
    /\ pc' = [pc EXCEPT ![s] = "idle"]

Next == \E s \in Senders : Acquire(s) \/ Send(s) \/ Finish(s)

InFlight == {s \in Senders : pc[s] = "sending"}
OneInFlight == Cardinality(InFlight) <= 1
ReleasePrecondition == InFlight # {} => Gate!ReleaseEnabled(gate, "g")
KindInvariant == Gate!Invariant

====
