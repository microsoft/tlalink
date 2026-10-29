---- MODULE Counter ----
EXTENDS Naturals
VARIABLES
  (* @link {"bind":[{"id":"counter::value","source":{"symbol":"P:ComponentsExample.Counter.Value"}}]} *)
  value
Init == value = 0
(* @link {"bind":[{"id":"counter::step","source":{"symbol":"M:ComponentsExample.Counter.Step","syntax":"ExpressionStatement","tokens":"Value = (Value + 1) % 3;"}}]} *)
Next == value' = (value + 1) % 3
Safety == value \in 0..2
NeverOne == value # 1
====
