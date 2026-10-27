---------------- MODULE Credits ----------------
EXTENDS Naturals

CONSTANT Window
VARIABLE available
Init == available = Window

Return == /\ available < Window
          /\ available' = available + 1
Next == Return
================================================
