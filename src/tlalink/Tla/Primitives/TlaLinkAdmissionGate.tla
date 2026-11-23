------------------------- MODULE TlaLinkAdmissionGate -------------------------
(* Admission gate kind: callers enter and exit while the gate is open. The  *)
(* Close or Exit that leaves the gate closed and empty signals its drain in *)
(* the same step; waiters resume in a later step of their own.              *)
EXTENDS Naturals
CONSTANT Instances
VARIABLE state

InitState == [i \in Instances |-> [count |-> 0, closed |-> FALSE, signaled |-> FALSE]]
Init == state = InitState

TypeOK == state \in [Instances -> [count : Nat, closed : BOOLEAN, signaled : BOOLEAN]]

Invariant == \A i \in Instances : state[i].signaled => state[i].closed /\ state[i].count = 0

EnterEnabled(s, i) == ~s[i].closed
EnterApply(s, i) == [s EXCEPT ![i].count = @ + 1]
Enter(i) == EnterEnabled(state, i) /\ state' = EnterApply(state, i)

\* A throwing Enter on a closed gate is an explicit step that changes nothing.
EnterRejectedEnabled(s, i) == s[i].closed
EnterRejectedApply(s, i) == s
EnterRejected(i) == EnterRejectedEnabled(state, i) /\ state' = EnterRejectedApply(state, i)

TryEnterEnabled(s, i, limit, ok) == ok = (~s[i].closed /\ s[i].count < limit)
TryEnterApply(s, i, limit, ok) == IF ok THEN [s EXCEPT ![i].count = @ + 1] ELSE s
TryEnter(i, limit, ok) == TryEnterEnabled(state, i, limit, ok) /\ state' = TryEnterApply(state, i, limit, ok)

ExitEnabled(s, i) == s[i].count > 0
ExitApply(s, i) == [s EXCEPT ![i].count = @ - 1, ![i].signaled = (s[i].closed /\ s[i].count = 1)]
Exit(i) == ExitEnabled(state, i) /\ state' = ExitApply(state, i)

CloseEnabled(s, i) == TRUE
CloseApply(s, i) == [s EXCEPT ![i].closed = TRUE, ![i].signaled = (s[i].count = 0)]
Close(i) == CloseEnabled(state, i) /\ state' = CloseApply(state, i)

CanWaitDrained(i) == state[i].signaled

Internal(i) == FALSE
Fairness == \A i \in Instances : WF_state(Internal(i))

\* Scalar projection of one instance's state, compared with a primitive's state_mapping in trace validation.
View(i) == [count |-> state[i].count, closed |-> state[i].closed, signaled |-> state[i].signaled]
================================================================================
