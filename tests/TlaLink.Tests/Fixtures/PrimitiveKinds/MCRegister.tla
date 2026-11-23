------------------------------ MODULE MCRegister ------------------------------
(* Harness for TlaLinkRegister: per register, two callers race TrySet and a  *)
(* writer performs one unconditional Set at any point.                       *)
EXTENDS Naturals, FiniteSets
CONSTANTS r1, r2, Callers, Vals, NoValue
Registers == {r1, r2}
VARIABLES regs, phase, writer, winner
vars == <<regs, phase, writer, winner>>

R == INSTANCE TlaLinkRegister WITH Instances <- Registers, Values <- Vals, None <- NoValue, state <- regs

Init ==
    /\ R!Init
    /\ phase = [r \in Registers |-> [c \in Callers |-> "idle"]]
    /\ writer = [r \in Registers |-> "idle"]
    /\ winner = [r \in Registers |-> NoValue]

TrySet(r, c, v, won) ==
    /\ phase[r][c] = "idle"
    /\ R!TrySet(r, v, won)
    /\ phase' = [phase EXCEPT ![r][c] = IF won THEN "won" ELSE "lost"]
    /\ winner' = IF won THEN [winner EXCEPT ![r] = v] ELSE winner
    /\ UNCHANGED writer
Set(r, v) ==
    /\ writer[r] = "idle" /\ writer' = [writer EXCEPT ![r] = "done"]
    /\ R!Set(r, v)
    /\ UNCHANGED <<phase, winner>>

Terminated ==
    /\ \A r \in Registers : writer[r] = "done" /\ \A c \in Callers : phase[r][c] # "idle"
    /\ UNCHANGED vars

Next ==
    \/ \E r \in Registers, c \in Callers, v \in Vals, won \in BOOLEAN : TrySet(r, c, v, won)
    \/ \E r \in Registers, v \in Vals : Set(r, v)
    \/ Terminated

Spec == Init /\ [][Next]_vars /\ R!Fairness

KindTypeOK == R!TypeOK
KindInvariant == R!Invariant
SingleWinner == \A r \in Registers : Cardinality({c \in Callers : phase[r][c] = "won"}) <= 1
\* Until the unconditional writer runs, the winning TrySet value stays.
FirstWriteWins == \A r \in Registers : winner[r] # NoValue /\ writer[r] = "idle" => regs[r] = winner[r]
LoserSawValue == \A r \in Registers, c \in Callers : phase[r][c] = "lost" => regs[r] # NoValue
\* Reachability witness: TLC must report this invariant as violated.
NoLossWitness == ~\E r \in Registers, c \in Callers : phase[r][c] = "lost"
================================================================================
