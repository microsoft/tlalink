---- MODULE Lanes ----
EXTENDS Integers

VARIABLE x
vars == <<x>>
Init == x = [lane \in 1..2 |-> 0]

(* @link {"bind":[{"id":"WorkflowLanes::Value"}]} *)
Value(entity) == x[entity]

(* @link {"bind":[{"id":"WorkflowLanes::Move"}]} *)
Move == \E lane \in 1..2:
          /\ x[lane] = 0
          /\ (lane = 1 \/ x[1] = 1)
          /\ x' = [x EXCEPT ![lane] = 1]
Next == Move
TypeOK == x \in [1..2 -> 0..1]
SecondNeedsFirst == x[2] = 1 => x[1] = 1
Spec == Init /\ [][Next]_vars
====
