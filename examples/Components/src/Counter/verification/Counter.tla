---- MODULE Counter ----
EXTENDS Naturals
VARIABLES
  (* @link {"bind":[{"id":"counter::value"}]} *)
  value
Init == value = 0
(* @link {"bind":[{"id":"counter::step"}]} *)
Next == value' = (value + 1) % 3
Safety == value \in 0..2
NeverOne == value # 1
====
