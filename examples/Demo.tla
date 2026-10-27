---------------------------- MODULE Demo ----------------------------
EXTENDS Naturals



(* --algorithm Lifecycle
variables active = 0;
begin
Admit:
    active := active + 1;
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


ObservedActive == active
====================================================================
