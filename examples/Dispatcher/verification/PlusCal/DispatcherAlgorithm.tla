---- MODULE DispatcherAlgorithm ----
EXTENDS Integers, FiniteSets, TLC
CONSTANTS JobCount, Capacity, RetryLimit
Jobs == 0..(JobCount - 1)

\* One controller chooses the order of atomic lock operations, not worker
\* execution duration. Rejected calls stutter. PC/choice transitions are
\* algorithm control only; no progress claim uses this serialized controller.
(* --algorithm DispatcherAlgorithm
variables states = [j \in Jobs |-> 0],
          attempts = [j \in Jobs |-> 0],
          cancelled = [j \in Jobs |-> FALSE],
          outstanding = 0, running = 0, closing = FALSE;
begin
Choose:
  while TRUE do
    either
(* @link {"bind":[{"id":"dispatcher-pluscal::admit","source":{"symbol":"M:DispatcherExample.Dispatcher.TryAdmit(System.Int32)","match":1,"expected_matches":1}}]} *)
Admit:
      with j \in Jobs do
        if ~closing /\ states[j] = 0 /\ outstanding < Capacity then
          states[j] := 1;
          outstanding := outstanding + 1;
        end if;
      end with;
    or
(* @link {"bind":[{"id":"dispatcher-pluscal::start","source":{"symbol":"M:DispatcherExample.Dispatcher.TryStart(System.Int32)","match":1,"expected_matches":1}}]} *)
Start:
      with j \in Jobs do
        if states[j] = 1 then
          states[j] := 2;
          attempts[j] := attempts[j] + 1;
          running := running + 1;
        end if;
      end with;
    or
(* @link {"bind":[{"id":"dispatcher-pluscal::complete","source":{"symbol":"M:DispatcherExample.Dispatcher.Complete(System.Int32,System.Boolean)","match":1,"expected_matches":1}}]} *)
Finish:
      with j \in Jobs, succeeded \in BOOLEAN do
        if states[j] = 2 then
          running := running - 1;
          if cancelled[j] then
            states[j] := 4;
            outstanding := outstanding - 1;
          elsif succeeded then
            states[j] := 3;
            outstanding := outstanding - 1;
          elsif attempts[j] = RetryLimit then
            states[j] := 5;
            outstanding := outstanding - 1;
          else
            states[j] := 1;
          end if;
        end if;
      end with;
    or
(* @link {"bind":[{"id":"dispatcher-pluscal::cancel","source":{"symbol":"M:DispatcherExample.Dispatcher.Cancel(System.Int32)","match":1,"expected_matches":1}}]} *)
Cancel:
      with j \in Jobs do
        if states[j] \in {1, 2} /\ ~cancelled[j] then
          cancelled[j] := TRUE;
          if states[j] = 1 then
            states[j] := 4;
            outstanding := outstanding - 1;
          end if;
        end if;
      end with;
    or
(* @link {"bind":[{"id":"dispatcher-pluscal::close","source":{"symbol":"M:DispatcherExample.Dispatcher.Close","match":1,"expected_matches":1}}]} *)
Close:
      closing := TRUE;
    end either;
  end while;
end algorithm; *)
\* BEGIN TRANSLATION (chksum(pcal) = "f57a70bb" /\ chksum(tla) = "66956615")
VARIABLES pc, states, attempts, cancelled, outstanding, running, closing

vars == << pc, states, attempts, cancelled, outstanding, running, closing >>

Init == (* Global variables *)
        /\ states = [j \in Jobs |-> 0]
        /\ attempts = [j \in Jobs |-> 0]
        /\ cancelled = [j \in Jobs |-> FALSE]
        /\ outstanding = 0
        /\ running = 0
        /\ closing = FALSE
        /\ pc = "Choose"

Choose == /\ pc = "Choose"
          /\ \/ /\ pc' = "Admit"
             \/ /\ pc' = "Start"
             \/ /\ pc' = "Finish"
             \/ /\ pc' = "Cancel"
             \/ /\ pc' = "Close"
          /\ UNCHANGED << states, attempts, cancelled, outstanding, running, 
                          closing >>

Admit == /\ pc = "Admit"
         /\ \E j \in Jobs:
              IF ~closing /\ states[j] = 0 /\ outstanding < Capacity
                 THEN /\ states' = [states EXCEPT ![j] = 1]
                      /\ outstanding' = outstanding + 1
                 ELSE /\ TRUE
                      /\ UNCHANGED << states, outstanding >>
         /\ pc' = "Choose"
         /\ UNCHANGED << attempts, cancelled, running, closing >>

Start == /\ pc = "Start"
         /\ \E j \in Jobs:
              IF states[j] = 1
                 THEN /\ states' = [states EXCEPT ![j] = 2]
                      /\ attempts' = [attempts EXCEPT ![j] = attempts[j] + 1]
                      /\ running' = running + 1
                 ELSE /\ TRUE
                      /\ UNCHANGED << states, attempts, running >>
         /\ pc' = "Choose"
         /\ UNCHANGED << cancelled, outstanding, closing >>

Finish == /\ pc = "Finish"
          /\ \E j \in Jobs:
               \E succeeded \in BOOLEAN:
                 IF states[j] = 2
                    THEN /\ running' = running - 1
                         /\ IF cancelled[j]
                               THEN /\ states' = [states EXCEPT ![j] = 4]
                                    /\ outstanding' = outstanding - 1
                               ELSE /\ IF succeeded
                                          THEN /\ states' = [states EXCEPT ![j] = 3]
                                               /\ outstanding' = outstanding - 1
                                          ELSE /\ IF attempts[j] = RetryLimit
                                                     THEN /\ states' = [states EXCEPT ![j] = 5]
                                                          /\ outstanding' = outstanding - 1
                                                     ELSE /\ states' = [states EXCEPT ![j] = 1]
                                                          /\ UNCHANGED outstanding
                    ELSE /\ TRUE
                         /\ UNCHANGED << states, outstanding, running >>
          /\ pc' = "Choose"
          /\ UNCHANGED << attempts, cancelled, closing >>

Cancel == /\ pc = "Cancel"
          /\ \E j \in Jobs:
               IF states[j] \in {1, 2} /\ ~cancelled[j]
                  THEN /\ cancelled' = [cancelled EXCEPT ![j] = TRUE]
                       /\ IF states[j] = 1
                             THEN /\ states' = [states EXCEPT ![j] = 4]
                                  /\ outstanding' = outstanding - 1
                             ELSE /\ TRUE
                                  /\ UNCHANGED << states, outstanding >>
                  ELSE /\ TRUE
                       /\ UNCHANGED << states, cancelled, outstanding >>
          /\ pc' = "Choose"
          /\ UNCHANGED << attempts, running, closing >>

Close == /\ pc = "Close"
         /\ closing' = TRUE
         /\ pc' = "Choose"
         /\ UNCHANGED << states, attempts, cancelled, outstanding, running >>

Next == Choose \/ Admit \/ Start \/ Finish \/ Cancel \/ Close

Spec == Init /\ [][Next]_vars

\* END TRANSLATION 

TypeOK == /\ states \in [Jobs -> 0..5]
          /\ attempts \in [Jobs -> 0..RetryLimit]
          /\ cancelled \in [Jobs -> BOOLEAN]
          /\ outstanding \in 0..Capacity /\ running \in 0..Capacity
          /\ closing \in BOOLEAN
Safety == /\ outstanding = Cardinality({j \in Jobs: states[j] \in {1, 2}})
          /\ running = Cardinality({j \in Jobs: states[j] = 2})
          /\ running <= outstanding
          /\ \A j \in Jobs:
                 /\ (states[j] = 0 => attempts[j] = 0 /\ ~cancelled[j])
                 /\ (states[j] = 2 => attempts[j] > 0)
                 /\ (states[j] = 1 => ~cancelled[j] /\ attempts[j] < RetryLimit)
                 /\ (states[j] = 5 => attempts[j] = RetryLimit)
====
