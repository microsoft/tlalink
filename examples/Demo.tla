---------------------------- MODULE Demo ----------------------------
EXTENDS Naturals



(* --algorithm Lifecycle
variables active = 0;
begin
(* @link {"bind":[{"id":"demo::Admit","reason":"lock","source":{"symbol":"M:Demo.Open","syntax":"ExpressionStatement","tokens":"this.active++;"}}],"capture":[{"binding":"demo::Admit","actor":"1","operation":"1","point":"commit","classification":"transition","observe":["demo::active"]}]} *)
Admit:
    active := active + 1;
(* @link {"bind":[{"id":"demo::Release","reason":"lock","source":{"symbol":"M:Demo.Release","syntax":"ExpressionStatement","tokens":"this.active--;"}}],"capture":[{"binding":"demo::Release","actor":"1","operation":"1","point":"commit","classification":"transition","observe":["demo::active"]}]} *)
Release:
    active := active - 1;
end algorithm; *)
\* BEGIN TRANSLATION (chksum(pcal) = "4861e716" /\ chksum(tla) = "b9c1dd4b")
VARIABLES pc, active

vars == << pc, active >>

Init == (* Global variables *)
        /\ active = 0
        /\ pc = "Admit"

Admit == /\ pc = "Admit"
         /\ active' = active + 1
         /\ pc' = "Release"

Release == /\ pc = "Release"
           /\ active' = active - 1
           /\ pc' = "Done"

(* Allow infinite stuttering to prevent deadlock on termination. *)
Terminating == pc = "Done" /\ UNCHANGED vars

Next == Admit \/ Release
           \/ Terminating

Spec == Init /\ [][Next]_vars

Termination == <>(pc = "Done")

\* END TRANSLATION 


(* @link {"bind":[{"id":"demo::active","source":{"symbol":"F:Demo.active"}}],"capture":[{"binding":"demo::active","guard":"this.gate","type":"int"}]} *)
ObservedActive == active
====================================================================
