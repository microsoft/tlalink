---- MODULE Counter ----
EXTENDS Naturals
VARIABLES value
Init == value = 0
Next == value' = (value + 1) % 3
Safety == value \in 0..2
NeverOne == value # 1
====
