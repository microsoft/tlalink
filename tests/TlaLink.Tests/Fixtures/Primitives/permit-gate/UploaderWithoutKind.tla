---- MODULE Uploader ----
EXTENDS Naturals
\* Mutant consumer: declares the permit-gate use but models its own counter
\* instead of instancing the shipped counting-resource module.

VARIABLES permits, sending
Init == permits = 1 /\ sending = 0
Send == permits = 1 /\ permits' = 0 /\ sending' = 1
Finish == sending = 1 /\ permits' = 1 /\ sending' = 0
Next == Send \/ Finish
OneInFlight == sending <= 1
ReleasePrecondition == TRUE
KindInvariant == TRUE

====
