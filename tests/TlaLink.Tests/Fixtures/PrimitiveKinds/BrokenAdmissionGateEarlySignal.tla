---- MODULE BrokenAdmissionGateEarlySignal ----
\* Negative control: deliberately broken copy of TlaLinkAdmissionGate.
\* Defect: Signal fires once the gate is closed, even while callers are still inside.
(* Admission gate kind: callers enter and exit while the gate is open. Once  *)
(* closed with nobody inside, the gate signals its drain by itself.          *)
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
ExitApply(s, i) == [s EXCEPT ![i].count = @ - 1]
Exit(i) == ExitEnabled(state, i) /\ state' = ExitApply(state, i)

CloseEnabled(s, i) == TRUE
CloseApply(s, i) == [s EXCEPT ![i].closed = TRUE]
Close(i) == CloseEnabled(state, i) /\ state' = CloseApply(state, i)

\* Separate from the Close or Exit that made it possible.
SignalEnabled(s, i) == s[i].closed /\ ~s[i].signaled
SignalApply(s, i) == [s EXCEPT ![i].signaled = TRUE]
Signal(i) == SignalEnabled(state, i) /\ state' = SignalApply(state, i)

CanWaitDrained(i) == state[i].signaled

Internal(i) == Signal(i)
Fairness == \A i \in Instances : WF_state(Internal(i))
================================================================================
