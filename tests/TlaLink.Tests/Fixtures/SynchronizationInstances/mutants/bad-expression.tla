---- MODULE Pump ----
EXTENDS Naturals
\* Mutant (the admission model expression does not parse). Synthetic model of InstanceFixture.Pump: workers pass the admission gate, Close closes it, Drained waits
\* for its drain, Stop sets the stopped latch and Resume lets a waiter continue once the latch is set.

Workers == {"w1", "w2"}
VARIABLES gates, latches, pc, stopper, waiter
vars == <<gates, latches, pc, stopper, waiter>>

(* @link {"instance":[{"id":"pump::admission","field":"F:InstanceFixture.Pump.admission","kind_instance":"Gate","model":"<<\"admission\""}]} *)
Gate == INSTANCE TlaLinkAdmissionGate WITH Instances <- {"admission"}, state <- gates

(* @link {"instance":[{"id":"pump::stopped","field":"F:InstanceFixture.Pump.stopped","kind_instance":"Latch","model":"\"stopped\""}]} *)
Latch == INSTANCE TlaLinkLatch WITH Instances <- {"stopped"}, state <- latches

Init ==
    /\ Gate!Init
    /\ Latch!Init
    /\ pc = [w \in Workers |-> "idle"]
    /\ stopper = "running"
    /\ waiter = "waiting"

(* @link {"bind":[{"id":"pump::enter","source":{"symbol":"M:InstanceFixture.Pump.TryRun","syntax":"IfStatement"}}]} *)
Enter(w) ==
    /\ pc[w] = "idle"
    /\ \E ok \in BOOLEAN :
        /\ Gate!TryEnter("admission", 2, ok)
        /\ pc' = [pc EXCEPT ![w] = IF ok THEN "inside" ELSE "done"]
    /\ UNCHANGED <<latches, stopper, waiter>>

(* @link {"bind":[{"id":"pump::exit","source":{"symbol":"M:InstanceFixture.Pump.TryRun","syntax":"ExpressionStatement","tokens":"this.admission.Exit();"}}]} *)
Leave(w) ==
    /\ pc[w] = "inside"
    /\ Gate!Exit("admission")
    /\ pc' = [pc EXCEPT ![w] = "done"]
    /\ UNCHANGED <<latches, stopper, waiter>>

(* @link {"bind":[{"id":"pump::close","source":{"symbol":"M:InstanceFixture.Pump.StopAsync","syntax":"ExpressionStatement","tokens":"this.admission.Close();"}}]} *)
Close ==
    /\ stopper = "running"
    /\ Gate!Close("admission")
    /\ stopper' = "closed"
    /\ UNCHANGED <<latches, pc, waiter>>

(* @link {"bind":[{"id":"pump::drained","source":{"symbol":"M:InstanceFixture.Pump.StopAsync","syntax":"ExpressionStatement","tokens":"await this.admission.WaitDrainedAsync().ConfigureAwait(false);"}}]} *)
Drained ==
    /\ stopper = "closed"
    /\ Gate!CanWaitDrained("admission")
    /\ stopper' = "drained"
    /\ UNCHANGED <<gates, latches, pc, waiter>>

(* @link {"bind":[{"id":"pump::stop","source":{"symbol":"M:InstanceFixture.Pump.StopAsync","syntax":"ExpressionStatement","tokens":"this.stopped.TrySet();"}}]} *)
Stop ==
    /\ stopper = "drained"
    /\ \E won \in BOOLEAN : Latch!TrySet("stopped", won)
    /\ stopper' = "stopped"
    /\ UNCHANGED <<gates, pc, waiter>>

(* @link {"bind":[{"id":"pump::resume","source":{"symbol":"M:InstanceFixture.Pump.WaitStoppedAsync","syntax":"ExpressionStatement"}}]} *)
Resume ==
    /\ waiter = "waiting"
    /\ Latch!CanWait("stopped")
    /\ waiter' = "resumed"
    /\ UNCHANGED <<gates, latches, pc, stopper>>

Next == Close \/ Drained \/ Stop \/ Resume \/ \E w \in Workers : Enter(w) \/ Leave(w)

====