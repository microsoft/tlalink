------------------------------ MODULE TlaLinkOnce ------------------------------
(* Run-once kind. Exactly one caller wins Publish; the winner then takes     *)
(* Start and later Settle. Losers wait until the work is settled.            *)
CONSTANT Instances
VARIABLE state

InitState == [i \in Instances |-> "empty"]
Init == state = InitState

TypeOK == state \in [Instances -> {"empty", "published", "running", "settled"}]

\* The single winner is a property of transitions; harnesses check it.
Invariant == TRUE

PublishEnabled(s, i, won) == won = (s[i] = "empty")
PublishApply(s, i, won) == IF won THEN [s EXCEPT ![i] = "published"] ELSE s
Publish(i, won) == PublishEnabled(state, i, won) /\ state' = PublishApply(state, i, won)

StartEnabled(s, i) == s[i] = "published"
StartApply(s, i) == [s EXCEPT ![i] = "running"]
Start(i) == StartEnabled(state, i) /\ state' = StartApply(state, i)

SettleEnabled(s, i) == s[i] = "running"
SettleApply(s, i) == [s EXCEPT ![i] = "settled"]
Settle(i) == SettleEnabled(state, i) /\ state' = SettleApply(state, i)

CanWait(i) == state[i] = "settled"

Internal(i) == FALSE
Fairness == \A i \in Instances : WF_state(Internal(i))

\* Scalar projection of one instance's state, compared with a primitive's state_mapping in trace validation.
View(i) == [status |-> state[i]]
================================================================================
