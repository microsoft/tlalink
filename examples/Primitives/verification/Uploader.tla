---- MODULE Uploader ----
EXTENDS Naturals, FiniteSets
\* Two senders share one PermitGate permit and hold it across one transport call.
\* AcquireAsync is two visible steps: Acquire enqueues or takes the permit, and
\* Send resumes once the permit is granted and starts the transport call.

Senders == {"a", "b"}
VARIABLES gate, pc
vars == <<gate, pc>>

(* @link {"instance":[{"id":"uploader::gate","field":"F:PrimitivesExample.Uploader.gate","kind_instance":"Gate","model":"\"g\""}]} *)
Gate == INSTANCE TlaLinkCountingResource
    WITH Instances <- {"g"}, Waiters <- Senders, Capacity <- [i \in {"g"} |-> 1], state <- gate

Init ==
    /\ Gate!Init
    /\ pc = [s \in Senders |-> "idle"]

Acquire(s) ==
    /\ pc[s] = "idle"
    /\ Gate!Acquire("g", s)
    /\ pc' = [pc EXCEPT ![s] = "acquiring"]

(* @link {"bind":[{"id":"uploader::send","source":{"symbol":"M:PrimitivesExample.Uploader.SendAsync(System.ReadOnlyMemory{System.Byte})","syntax":"ExpressionStatement","tokens":"await sink.WriteAsync(chunk).ConfigureAwait(false);"}}]} *)
Send(s) ==
    /\ pc[s] = "acquiring"
    /\ Gate!CanResume("g", s)
    /\ Gate!Resume("g", s)
    /\ pc' = [pc EXCEPT ![s] = "sending"]

(* @link {"bind":[{"id":"uploader::acquire","source":{"symbol":"M:PrimitivesExample.Uploader.SendAsync(System.ReadOnlyMemory{System.Byte})","syntax":"ExpressionStatement","tokens":"await this.gate.AcquireAsync().ConfigureAwait(false);"}}]} *)
AcquirePermit(s) == Acquire(s) \/ Send(s)

(* @link {"bind":[{"id":"uploader::release","source":{"symbol":"M:PrimitivesExample.Uploader.SendAsync(System.ReadOnlyMemory{System.Byte})","syntax":"ExpressionStatement","tokens":"this.gate.Release();"}}]} *)
Finish(s) ==
    /\ pc[s] = "sending"
    /\ Gate!Release("g")
    /\ pc' = [pc EXCEPT ![s] = "idle"]

Next == \E s \in Senders : AcquirePermit(s) \/ Finish(s)

InFlight == {s \in Senders : pc[s] = "sending"}
OneInFlight == Cardinality(InFlight) <= 1
ReleasePrecondition == InFlight # {} => Gate!ReleaseEnabled(gate, "g")
KindInvariant == Gate!Invariant

====