---------------- MODULE Credits ----------------
EXTENDS Naturals
(* @tla 2 model CreditDecision; *)
CONSTANT Window
VARIABLE available
Init == available = Window
(* @tla 2 step Return; *)
Return == /\ available < Window
          /\ available' = available + 1
Next == Return
================================================
