---- MODULE Lanes ----
EXTENDS Integers

VARIABLE x
vars == <<x>>
Init == x = [lane \in 1..2 |-> 0]

(* @link {"bind":[{"id":"WorkflowLanes::Value","source":{"symbol":"F:Lane.value"}}],"capture":[{"binding":"WorkflowLanes::Value","guard":"gate","type":"int"}]} *)
Value(entity) == x[entity]

(* @link {"bind":[{"id":"WorkflowLanes::Move","source":{"symbol":"M:Lane.Move(System.Int32)","syntax":"ExpressionStatement","tokens":"value = nextValue;"}}],"capture":[{"binding":"WorkflowLanes::Move","actor":"1","operation":"1","entity":"id","point":"commit","classification":"transition","observe":["WorkflowLanes::Value"]}]} *)
Move == \E lane \in 1..2:
          /\ x[lane] = 0
          /\ (lane = 1 \/ x[1] = 1)
          /\ x' = [x EXCEPT ![lane] = 1]
Next == Move
TypeOK == x \in [1..2 -> 0..1]
SecondNeedsFirst == x[2] = 1 => x[1] = 1
Spec == Init /\ [][Next]_vars
====
