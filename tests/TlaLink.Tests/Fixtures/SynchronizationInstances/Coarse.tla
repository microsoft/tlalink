---- MODULE Coarse ----
\* A coarser second target over the same region: it binds all of StopAsync as one shutdown step and does not model
\* the gate or latch with kind instances, so its bind is not contract-checked against their sites.

VARIABLE phase
vars == <<phase>>

Init == phase = "running"

(* @link {"bind":[{"id":"coarse::stop","source":{"symbol":"M:InstanceFixture.Pump.StopAsync"}}]} *)
Shutdown ==
    /\ phase = "running"
    /\ phase' = "stopped"

Next == Shutdown

====
