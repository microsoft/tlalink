---------------- MODULE Credits ----------------
EXTENDS Naturals

CONSTANT Window
VARIABLE available
Init == available = 0

(* @link {"bind":[{"id":"CreditDecision::Return"}]} *)
Return == /\ available < Window
          /\ available' = available + 1
Next == Return
================================================
