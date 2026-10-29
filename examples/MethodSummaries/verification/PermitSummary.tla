---- MODULE PermitSummary ----
EXTENDS Naturals

Requires(before) == before \in Nat

(* @link {"bind":[{"id":"permits::try-take","source":{"symbol":"M:SummaryExample.PermitGate.TryTake"}}]} *)
TryTake(before, after, took) ==
    /\ after = IF before > 0 THEN before - 1 ELSE before
    /\ took = (before > 0)

====
