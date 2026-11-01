---- MODULE Dispatcher ----
EXTENDS Integers, FiniteSets

CONSTANTS
  \* @type: Int;
  JobCount,
  \* @type: Int;
  Capacity,
  \* @type: Int;
  RetryLimit
VARIABLES
  \* @type: Int -> Int;
  states,
  \* @type: Int -> Int;
  attempts,
  \* @type: Int -> Bool;
  cancellationRequested,
  \* @type: Int;
  outstanding,
  \* @type: Int;
  running,
  \* @type: Bool;
  closing

\* Job IDs are zero-based, as in C#. Each successful lock-protected method
\* is one atomic transition. Rejected calls are stuttering, not disabled
\* environment behavior. Worker execution outside the lock is an environment
\* action; cancellation requests do not terminate a running worker.
Jobs == 0..(JobCount - 1)
vars == <<states, attempts, cancellationRequested, outstanding, running, closing>>
ConstInit == /\ JobCount = 3 /\ Capacity = 2 /\ RetryLimit = 2
Init == /\ states = [j \in Jobs |-> 0]
        /\ attempts = [j \in Jobs |-> 0]
        /\ cancellationRequested = [j \in Jobs |-> FALSE]
        /\ outstanding = 0 /\ running = 0 /\ closing = FALSE

(* @link {"bind":[{"id":"dispatcher-tla::admit","source":{"symbol":"M:DispatcherExample.Dispatcher.TryAdmit(System.Int32)"}}]} *)
Admit(j) == /\ ~closing /\ states[j] = 0 /\ outstanding < Capacity
            /\ states' = [states EXCEPT ![j] = 1]
            /\ outstanding' = outstanding + 1
            /\ UNCHANGED <<attempts, cancellationRequested, running, closing>>

(* @link {"bind":[{"id":"dispatcher-tla::start","source":{"symbol":"M:DispatcherExample.Dispatcher.TryStart(System.Int32)"}}]} *)
Start(j) == /\ states[j] = 1
            /\ states' = [states EXCEPT ![j] = 2]
            /\ attempts' = [attempts EXCEPT ![j] = @ + 1]
            /\ running' = running + 1
            /\ UNCHANGED <<outstanding, cancellationRequested, closing>>

Outcome(j, succeeded) ==
    IF cancellationRequested[j] THEN 4
    ELSE IF succeeded THEN 3
    ELSE IF attempts[j] = RetryLimit THEN 5 ELSE 1

(* @link {"bind":[{"id":"dispatcher-tla::complete","source":{"symbol":"M:DispatcherExample.Dispatcher.Complete(System.Int32,System.Boolean)"}}]} *)
Finish(j, succeeded) ==
    /\ states[j] = 2
    /\ states' = [states EXCEPT ![j] = Outcome(j, succeeded)]
    /\ running' = running - 1
    /\ outstanding' = IF Outcome(j, succeeded) = 1 THEN outstanding ELSE outstanding - 1
    /\ UNCHANGED <<attempts, cancellationRequested, closing>>

(* @link {"bind":[{"id":"dispatcher-tla::cancel","source":{"symbol":"M:DispatcherExample.Dispatcher.Cancel(System.Int32)"}}]} *)
Cancel(j) ==
    /\ states[j] \in {1, 2} /\ ~cancellationRequested[j]
    /\ cancellationRequested' = [cancellationRequested EXCEPT ![j] = TRUE]
    /\ states' = IF states[j] = 1 THEN [states EXCEPT ![j] = 4] ELSE states
    /\ outstanding' = IF states[j] = 1 THEN outstanding - 1 ELSE outstanding
    /\ UNCHANGED <<attempts, running, closing>>

(* @link {"bind":[{"id":"dispatcher-tla::close","source":{"symbol":"M:DispatcherExample.Dispatcher.Close"}}]} *)
Close == /\ ~closing /\ closing' = TRUE
         /\ UNCHANGED <<states, attempts, cancellationRequested, outstanding, running>>

Next == \/ \E j \in Jobs: Admit(j) \/ Start(j) \/ Cancel(j)
        \/ \E j \in Jobs, succeeded \in BOOLEAN: Finish(j, succeeded)
        \/ Close
TypeOK ==
    /\ states \in [Jobs -> 0..5]
    /\ attempts \in [Jobs -> 0..RetryLimit]
    /\ cancellationRequested \in [Jobs -> BOOLEAN]
    /\ outstanding \in 0..Capacity /\ running \in 0..Capacity
    /\ closing \in BOOLEAN
Safety ==
    /\ outstanding = Cardinality({j \in Jobs: states[j] \in {1, 2}})
    /\ running = Cardinality({j \in Jobs: states[j] = 2})
    /\ running <= outstanding /\ outstanding <= Capacity
    /\ \A j \in Jobs:
          /\ (states[j] = 0 => attempts[j] = 0 /\ ~cancellationRequested[j])
          /\ (states[j] = 2 => attempts[j] > 0)
          /\ (states[j] = 1 => ~cancellationRequested[j] /\ attempts[j] < RetryLimit)
          /\ (states[j] = 5 => attempts[j] = RetryLimit)
Inv == TypeOK /\ Safety
Drained == closing /\ outstanding = 0
ClosedStaysClosed == [][closing => closing']_vars
DrainProgress == closing ~> Drained
Fairness == \A j \in Jobs:
               WF_vars(Start(j)) /\ WF_vars(Finish(j, TRUE) \/ Finish(j, FALSE))
Spec == Init /\ [][Next]_vars
FairSpec == Spec /\ Fairness

\* These intentionally false invariants require reachable behavioral traces.
NoRetry == \A j \in Jobs: attempts[j] < 2
NoCancellation == \A j \in Jobs: states[j] # 4
NeverDrained == ~Drained
====
