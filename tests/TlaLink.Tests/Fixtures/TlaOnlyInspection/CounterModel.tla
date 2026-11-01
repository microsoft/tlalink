---- MODULE CounterModel ----
EXTENDS Integers
VARIABLE value
Init == value = 0
(* @link {"bind":[{"id":"counter-only::increment","source":{"symbol":"M:TlaOnlyFixture.Counter.Increment","syntax":"ExpressionStatement","tokens":"Value\u002B\u002B;","match":1,"expected_matches":1}}]} *)
Increment == value' = value + 1
====
