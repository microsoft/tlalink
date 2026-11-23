---- MODULE CustomLatch ----
\* Author-owned module that reuses the shipped latch contract.
CONSTANT Instances
VARIABLE state
Kind == INSTANCE TlaLinkLatch
TrySet(i, won) == Kind!TrySet(i, won)
TryFail(i, won) == Kind!TryFail(i, won)
CanWait(i) == Kind!CanWait(i)
====
