---------------- MODULE Credits ----------------
EXTENDS Naturals
(* @link model CreditDecision; *)
CONSTANT Window
VARIABLE available
Init == available = Window
(* @link step Return; *)
Return == /\ available < Window
          /\ available' = available + 1
Next == Return
================================================
