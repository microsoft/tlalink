---- MODULE Credit ----
EXTENDS Integers
CONSTANT
  \* @type: Int;
  Capacity
VARIABLE
  \* @type: Int;
  credit
ConstInit == Capacity = 3
Init == credit = 0
Next == \/ /\ credit < Capacity
           /\ credit' = credit + 1
        \/ /\ credit = Capacity
           /\ UNCHANGED credit
TypeOK == credit \in 0..Capacity
Safety == credit <= Capacity
TooSmall == credit < 2
WeakInvariant == credit \in 0..2
PositiveInvariant == credit \in 1..Capacity
====
