---- MODULE BrokenChannelLateAbortIgnored ----
\* Negative control: deliberately broken copy of TlaLinkChannel.
\* Defect: an abort after a drain changes nothing, so draining items survive the abort.
(* Unbounded channel kind with one consumer. completion is "open", "drain"   *)
(* or "abort". The first Complete sets it; a later abort still discards the *)
(* items of a draining channel; every other repeated Complete is a no-op.    *)
(* waiting records a consumer parked on an empty open channel; the enqueue   *)
(* or completion that lets it resume also clears it.                         *)
EXTENDS Sequences
CONSTANTS Instances, Items
VARIABLE state

InitState == [i \in Instances |-> [items |-> <<>>, completion |-> "open", waiting |-> FALSE]]
Init == state = InitState

TypeOK == state \in [Instances ->
    [items : Seq(Items), completion : {"open", "drain", "abort"}, waiting : BOOLEAN]]

Invariant == \A i \in Instances :
    /\ state[i].completion = "abort" => state[i].items = <<>>
    /\ state[i].waiting => state[i].items = <<>> /\ state[i].completion = "open"

TryEnqueueEnabled(s, i, x, ok) == x \in Items /\ ok = (s[i].completion = "open")
TryEnqueueApply(s, i, x, ok) ==
    IF ok THEN [s EXCEPT ![i].items = Append(@, x), ![i].waiting = FALSE] ELSE s
TryEnqueue(i, x, ok) == TryEnqueueEnabled(state, i, x, ok) /\ state' = TryEnqueueApply(state, i, x, ok)

DequeueEnabled(s, i, x) == s[i].items # <<>> /\ x = Head(s[i].items)
DequeueApply(s, i, x) == [s EXCEPT ![i].items = Tail(@)]
Dequeue(i, x) == DequeueEnabled(state, i, x) /\ state' = DequeueApply(state, i, x)

StartWaitEnabled(s, i) == s[i].items = <<>> /\ s[i].completion = "open" /\ ~s[i].waiting
StartWaitApply(s, i) == [s EXCEPT ![i].waiting = TRUE]
StartWait(i) == StartWaitEnabled(state, i) /\ state' = StartWaitApply(state, i)

\* "drain" keeps queued items for the consumer; "abort" discards them.
CompleteEnabled(s, i, mode) == mode \in {"drain", "abort"}
CompleteApply(s, i, mode) ==
    IF s[i].completion = "open"
    THEN [s EXCEPT ![i].completion = mode,
                   ![i].items = IF mode = "abort" THEN <<>> ELSE @,
                   ![i].waiting = FALSE]
    ELSE s
Complete(i, mode) == CompleteEnabled(state, i, mode) /\ state' = CompleteApply(state, i, mode)

CanDequeue(i) == state[i].items # <<>> \/ state[i].completion # "open"

Internal(i) == FALSE
Fairness == \A i \in Instances : WF_state(Internal(i))
================================================================================
