---------------- MODULE Credits ----------------
EXTENDS Naturals

CONSTANT Window
VARIABLE available
Init == available = 0

(* @link {"bind":[{"id":"CreditDecision::Return","source":{"symbol":"M:Decisions.Return(System.Int32,System.Int32,System.Int32,System.Int32@,System.Boolean@)","syntax":"ExpressionStatement","tokens":"next = accepted ? available + count : available;"}}]} *)
Return == /\ available < Window
          /\ available' = available + 1
Next == Return
================================================
