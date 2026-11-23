------------------------------ MODULE MCBrokenChannelAbortKeepsItems ------------------------------
(* Harness for TlaLinkChannel: per channel, a producer attempts two         *)
(* enqueues, a completer requests up to two completions in any mode (so     *)
(* drain then abort), and the single consumer dequeues, parks on an empty   *)
(* open channel and finishes on completion.                                 *)
EXTENDS Naturals, Sequences
CONSTANTS h1, h2, Values
Channels == {h1, h2}
Attempts == 2
Requests == 2
VARIABLES chans, sent, accepted, received, requests, consumer
vars == <<chans, sent, accepted, received, requests, consumer>>

Ch == INSTANCE BrokenChannelAbortKeepsItems WITH Instances <- Channels, Items <- Values, state <- chans

Init ==
    /\ Ch!Init
    /\ sent = [h \in Channels |-> 0]
    /\ accepted = [h \in Channels |-> <<>>]
    /\ received = [h \in Channels |-> <<>>]
    /\ requests = [h \in Channels |-> <<>>]
    /\ consumer = [h \in Channels |-> "idle"]

TryEnqueue(h, x, ok) ==
    /\ sent[h] < Attempts /\ sent' = [sent EXCEPT ![h] = @ + 1]
    /\ Ch!TryEnqueue(h, x, ok)
    /\ accepted' = IF ok THEN [accepted EXCEPT ![h] = Append(@, x)] ELSE accepted
    /\ UNCHANGED <<received, requests, consumer>>
Complete(h, mode) ==
    /\ Len(requests[h]) < Requests /\ requests' = [requests EXCEPT ![h] = Append(@, mode)]
    /\ Ch!Complete(h, mode)
    /\ UNCHANGED <<sent, accepted, received, consumer>>
\* The consumer dequeues when idle or when resumed by an item.
Dequeue(h, x) ==
    /\ consumer[h] \in {"idle", "waiting"} /\ Ch!CanDequeue(h)
    /\ Ch!Dequeue(h, x)
    /\ received' = [received EXCEPT ![h] = Append(@, x)]
    /\ consumer' = [consumer EXCEPT ![h] = "idle"]
    /\ UNCHANGED <<sent, accepted, requests>>
StartWait(h) ==
    /\ consumer[h] = "idle" /\ consumer' = [consumer EXCEPT ![h] = "waiting"]
    /\ Ch!StartWait(h)
    /\ UNCHANGED <<sent, accepted, received, requests>>
Finish(h) ==
    /\ consumer[h] \in {"idle", "waiting"} /\ Ch!CanDequeue(h) /\ chans[h].items = <<>>
    /\ consumer' = [consumer EXCEPT ![h] = "finished"]
    /\ UNCHANGED <<chans, sent, accepted, received, requests>>

Terminated ==
    /\ \A h \in Channels : consumer[h] = "finished" /\ sent[h] = Attempts
    /\ UNCHANGED vars

Next ==
    \/ \E h \in Channels :
        \/ \E x \in Values, ok \in BOOLEAN : TryEnqueue(h, x, ok)
        \/ \E mode \in {"drain", "abort"} : Complete(h, mode)
        \/ \E x \in Values : Dequeue(h, x)
        \/ StartWait(h) \/ Finish(h)
    \/ Terminated

Spec == Init /\ [][Next]_vars /\ Ch!Fairness

KindTypeOK == Ch!TypeOK
KindInvariant == Ch!Invariant
\* Items arrive in order, and only aborting may discard accepted items.
Delivery == \A h \in Channels :
    IF chans[h].completion = "abort"
    THEN received[h] = SubSeq(accepted[h], 1, Len(received[h]))
    ELSE received[h] \o chans[h].items = accepted[h]
Requested(h) == {requests[h][k] : k \in 1..Len(requests[h])}
\* Any abort request discards, even after a drain; other repeats change nothing.
AbortDiscards == \A h \in Channels :
    "abort" \in Requested(h) => chans[h].completion = "abort" /\ chans[h].items = <<>>
CompletionMatches == \A h \in Channels :
    chans[h].completion = IF requests[h] = <<>> THEN "open"
                          ELSE IF "abort" \in Requested(h) THEN "abort" ELSE "drain"
ParkedConsumer == \A h \in Channels : chans[h].waiting => consumer[h] = "waiting"
\* Reachability witness: TLC must report this invariant as violated.
NoDrainedDeliveryWitness == ~\E h \in Channels :
    consumer[h] = "finished" /\ chans[h].completion = "drain" /\ Len(received[h]) = Attempts
================================================================================
