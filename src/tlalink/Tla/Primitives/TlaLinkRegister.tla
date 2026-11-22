---------------------------- MODULE TlaLinkRegister ----------------------------
(* Register kind: each instance holds one value or None. Set overwrites;      *)
(* TrySet installs a value only while the register is empty (first wins).    *)
CONSTANTS Instances, Values, None
VARIABLE state

ASSUME None \notin Values

InitState == [i \in Instances |-> None]
Init == state = InitState

TypeOK == state \in [Instances -> Values \cup {None}]

\* First-wins is a property of transitions, not of a single state; harnesses
\* check it with their own history.
Invariant == TRUE

SetEnabled(s, i, v) == v \in Values
SetApply(s, i, v) == [s EXCEPT ![i] = v]
Set(i, v) == SetEnabled(state, i, v) /\ state' = SetApply(state, i, v)

TrySetEnabled(s, i, v, won) == v \in Values /\ won = (s[i] = None)
TrySetApply(s, i, v, won) == IF won THEN [s EXCEPT ![i] = v] ELSE s
TrySet(i, v, won) == TrySetEnabled(state, i, v, won) /\ state' = TrySetApply(state, i, v, won)

Internal(i) == FALSE
Fairness == \A i \in Instances : WF_state(Internal(i))
================================================================================
