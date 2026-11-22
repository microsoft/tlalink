---- MODULE FrameSink ----
\* Synthetic environment contract for the frame sink: posted frames queue until accepted.
EXTENDS Naturals
VARIABLE queued

Init == queued = 0
Submit == queued' = queued + 1
Accept == queued > 0 /\ queued' = queued - 1
CanFlush == queued = 0
====