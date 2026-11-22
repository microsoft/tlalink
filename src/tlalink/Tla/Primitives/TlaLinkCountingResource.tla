----------------------- MODULE TlaLinkCountingResource -----------------------
(* Counting resource kind with FIFO handoff and no barging.                  *)
(* - Acquire(i, w) either grants w a permit at once (nobody queued, a permit  *)
(*   available, still open) or enqueues w. Either way w later takes Resume,  *)
(*   guarded by CanResume(i, w).                                             *)
(* - Release hands the permit to the head waiter, otherwise frees it.        *)
(* - granted holds waiters with a handed-over permit they have not resumed.  *)
(* - Close fails every queued waiter: after Close, a waiter still in queue   *)
(*   is failed and its Resume consumes that failure.                         *)
EXTENDS Naturals, Sequences, FiniteSets
CONSTANTS Instances, Waiters, Capacity
VARIABLE state

ASSUME Capacity \in [Instances -> Nat]

Queued(s, i) == {s[i].queue[k] : k \in 1..Len(s[i].queue)}

InitState == [i \in Instances |->
    [available |-> Capacity[i], queue |-> <<>>, granted |-> {}, closed |-> FALSE]]
Init == state = InitState

TypeOK == state \in [Instances ->
    [available : Nat, queue : Seq(Waiters), granted : SUBSET Waiters, closed : BOOLEAN]]

Invariant == \A i \in Instances :
    /\ state[i].available + Cardinality(state[i].granted) <= Capacity[i]
    /\ Cardinality(Queued(state, i)) = Len(state[i].queue)
    /\ Queued(state, i) \cap state[i].granted = {}
    \* No permit is left free while an open resource has a queued waiter.
    /\ ~state[i].closed /\ state[i].queue # <<>> => state[i].available = 0

Free(s, i) == ~s[i].closed /\ s[i].available > 0 /\ s[i].queue = <<>>

TryAcquireEnabled(s, i, ok) == ok = Free(s, i)
TryAcquireApply(s, i, ok) == IF ok THEN [s EXCEPT ![i].available = @ - 1] ELSE s
TryAcquire(i, ok) == TryAcquireEnabled(state, i, ok) /\ state' = TryAcquireApply(state, i, ok)

AcquireEnabled(s, i, w) == w \in Waiters /\ w \notin Queued(s, i) \cup s[i].granted
AcquireApply(s, i, w) ==
    IF Free(s, i)
    THEN [s EXCEPT ![i].available = @ - 1, ![i].granted = @ \cup {w}]
    ELSE [s EXCEPT ![i].queue = Append(@, w)]
Acquire(i, w) == AcquireEnabled(state, i, w) /\ state' = AcquireApply(state, i, w)

\* Enabled only while some permit is held, i.e. not at Capacity[i] counting
\* handed-over permits.
ReleaseEnabled(s, i) == s[i].available + Cardinality(s[i].granted) < Capacity[i]
ReleaseApply(s, i) ==
    IF ~s[i].closed /\ s[i].queue # <<>>
    THEN [s EXCEPT ![i].queue = Tail(@), ![i].granted = @ \cup {Head(s[i].queue)}]
    ELSE [s EXCEPT ![i].available = @ + 1]
Release(i) == ReleaseEnabled(state, i) /\ state' = ReleaseApply(state, i)

CloseEnabled(s, i) == TRUE
CloseApply(s, i) == [s EXCEPT ![i].closed = TRUE]
Close(i) == CloseEnabled(state, i) /\ state' = CloseApply(state, i)

ResumeEnabled(s, i, w) == w \in s[i].granted \/ (s[i].closed /\ w \in Queued(s, i))
ResumeApply(s, i, w) ==
    [s EXCEPT ![i].granted = @ \ {w}, ![i].queue = SelectSeq(@, LAMBDA x : x # w)]
Resume(i, w) == ResumeEnabled(state, i, w) /\ state' = ResumeApply(state, i, w)

CanResume(i, w) == ResumeEnabled(state, i, w)

Internal(i) == FALSE
Fairness == \A i \in Instances : WF_state(Internal(i))
================================================================================
