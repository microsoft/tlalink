module DispatcherHistory {
  datatype Event = Admit(job: int) | Start(job: int) | Finish(job: int, succeeded: bool)
                 | Cancel(job: int) | Close
  datatype State = State(states: seq<int>, attempts: seq<int>, cancelled: seq<bool>,
                         outstanding: int, running: int, closing: bool)

  ghost function Count(states: seq<int>, selected: set<int>): int
    decreases |states|
  {
    if |states| == 0 then 0
    else (if states[0] in selected then 1 else 0) + Count(states[1..], selected)
  }

  lemma CountUpdate(states: seq<int>, job: int, value: int, selected: set<int>)
    requires 0 <= job < |states|
    ensures Count(states[job := value], selected) ==
            Count(states, selected) + (if value in selected then 1 else 0) -
                                      (if states[job] in selected then 1 else 0)
    decreases |states|
  {
    if job == 0 {
      assert states[job := value][1..] == states[1..];
    } else {
      CountUpdate(states[1..], job - 1, value, selected);
      assert states[job := value][1..] == states[1..][job - 1 := value];
    }
  }

  lemma CountSubset(states: seq<int>, smaller: set<int>, larger: set<int>)
    requires smaller <= larger
    ensures 0 <= Count(states, smaller) <= Count(states, larger)
    decreases |states|
  {
    if |states| > 0 { CountSubset(states[1..], smaller, larger); }
  }

  ghost predicate Valid(s: State, capacity: int, retryLimit: int)
  {
    1 <= capacity <= |s.states| && retryLimit >= 1 &&
    |s.attempts| == |s.states| && |s.cancelled| == |s.states| &&
    0 <= s.running <= s.outstanding <= capacity &&
    s.outstanding == Count(s.states, {1, 2}) && s.running == Count(s.states, {2}) &&
    (forall j :: 0 <= j < |s.states| ==>
      0 <= s.states[j] <= 5 && 0 <= s.attempts[j] <= retryLimit &&
      (s.states[j] == 0 ==> s.attempts[j] == 0 && !s.cancelled[j]) &&
      (s.states[j] == 2 ==> s.attempts[j] > 0) &&
      (s.states[j] == 1 ==> !s.cancelled[j] && s.attempts[j] < retryLimit) &&
      (s.states[j] == 5 ==> s.attempts[j] == retryLimit))
  }

  ghost predicate InputValid(s: State, e: Event)
  {
    e.Close? || 0 <= e.job < |s.states|
  }

  ghost function Initial(jobs: int): State
    requires jobs >= 1
  {
    State(seq(jobs, j => 0), seq(jobs, j => 0), seq(jobs, j => false), 0, 0, false)
  }

  lemma CountZeros(jobs: int, selected: set<int>)
    requires jobs >= 0 && 0 !in selected
    ensures Count(seq(jobs, j => 0), selected) == 0
    decreases jobs
  {
    if jobs > 0 {
      CountZeros(jobs - 1, selected);
      assert seq(jobs, j => 0)[1..] == seq(jobs - 1, j => 0);
    }
  }

  lemma InitialValid(jobs: int, capacity: int, retryLimit: int)
    requires 1 <= capacity <= jobs && retryLimit >= 1
    ensures Valid(Initial(jobs), capacity, retryLimit)
  {
    CountZeros(jobs, {1, 2});
    CountZeros(jobs, {2});
  }

  /* @link {"bind":[{"id":"dispatcher-dafny::admit","source":{"symbol":"M:DispatcherExample.Dispatcher.TryAdmit(System.Int32)"}}]} */
  ghost function AdmitStep(s: State, job: int, capacity: int): State
    requires 0 <= job < |s.states|
  {
    if s.closing || s.states[job] != 0 || s.outstanding == capacity then s
    else State(s.states[job := 1], s.attempts, s.cancelled, s.outstanding + 1, s.running, s.closing)
  }

  /* @link {"bind":[{"id":"dispatcher-dafny::start","source":{"symbol":"M:DispatcherExample.Dispatcher.TryStart(System.Int32)"}}]} */
  ghost function StartStep(s: State, job: int): State
    requires 0 <= job < |s.states| && |s.attempts| == |s.states|
  {
    if s.states[job] != 1 then s
    else State(s.states[job := 2], s.attempts[job := s.attempts[job] + 1],
               s.cancelled, s.outstanding, s.running + 1, s.closing)
  }

  ghost function Outcome(s: State, job: int, succeeded: bool, retryLimit: int): int
    requires 0 <= job < |s.states| && |s.attempts| == |s.states| && |s.cancelled| == |s.states|
  {
    if s.cancelled[job] then 4 else if succeeded then 3
    else if s.attempts[job] == retryLimit then 5 else 1
  }

  /* @link {"bind":[{"id":"dispatcher-dafny::complete","source":{"symbol":"M:DispatcherExample.Dispatcher.Complete(System.Int32,System.Boolean)"}}]} */
  ghost function FinishStep(s: State, job: int, succeeded: bool, retryLimit: int): State
    requires 0 <= job < |s.states| && |s.attempts| == |s.states| && |s.cancelled| == |s.states|
  {
    if s.states[job] != 2 then s
    else var outcome := Outcome(s, job, succeeded, retryLimit);
         State(s.states[job := outcome], s.attempts, s.cancelled,
               s.outstanding - (if outcome == 1 then 0 else 1), s.running - 1, s.closing)
  }

  /* @link {"bind":[{"id":"dispatcher-dafny::cancel","source":{"symbol":"M:DispatcherExample.Dispatcher.Cancel(System.Int32)"}}]} */
  ghost function CancelStep(s: State, job: int): State
    requires 0 <= job < |s.states| && |s.cancelled| == |s.states|
  {
    if s.states[job] !in {1, 2} || s.cancelled[job] then s
    else State(if s.states[job] == 1 then s.states[job := 4] else s.states,
               s.attempts, s.cancelled[job := true],
               s.outstanding - (if s.states[job] == 1 then 1 else 0), s.running, s.closing)
  }

  /* @link {"bind":[{"id":"dispatcher-dafny::close","source":{"symbol":"M:DispatcherExample.Dispatcher.Close"}}]} */
  ghost function CloseStep(s: State): State
  {
    State(s.states, s.attempts, s.cancelled, s.outstanding, s.running, true)
  }

  ghost function Step(s: State, e: Event, capacity: int, retryLimit: int): State
    requires InputValid(s, e) && |s.attempts| == |s.states| && |s.cancelled| == |s.states|
    ensures |Step(s, e, capacity, retryLimit).states| == |s.states|
    ensures |Step(s, e, capacity, retryLimit).attempts| == |s.states|
    ensures |Step(s, e, capacity, retryLimit).cancelled| == |s.states|
  {
    match e
    case Admit(j) => AdmitStep(s, j, capacity)
    case Start(j) => StartStep(s, j)
    case Finish(j, ok) => FinishStep(s, j, ok, retryLimit)
    case Cancel(j) => CancelStep(s, j)
    case Close => CloseStep(s)
  }

  lemma StepPreserves(s: State, e: Event, capacity: int, retryLimit: int)
    requires Valid(s, capacity, retryLimit) && InputValid(s, e)
    ensures Valid(Step(s, e, capacity, retryLimit), capacity, retryLimit)
    ensures s.closing ==> Step(s, e, capacity, retryLimit).closing
  {
    match e {
      case Admit(j) =>
        if !s.closing && s.states[j] == 0 && s.outstanding != capacity {
          CountUpdate(s.states, j, 1, {1, 2});
          CountUpdate(s.states, j, 1, {2});
        }
      case Start(j) =>
        if s.states[j] == 1 {
          CountUpdate(s.states, j, 2, {1, 2});
          CountUpdate(s.states, j, 2, {2});
          CountUpdate(s.states, j, 0, {1, 2});
          CountUpdate(s.states, j, 0, {2});
          CountSubset(s.states[j := 0], {2}, {1, 2});
        }
      case Finish(j, ok) =>
        if s.states[j] == 2 {
          var outcome := Outcome(s, j, ok, retryLimit);
          CountUpdate(s.states, j, outcome, {1, 2});
          CountUpdate(s.states, j, outcome, {2});
          CountSubset(s.states[j := outcome], {2}, {1, 2});
        }
      case Cancel(j) =>
        if s.states[j] == 1 && !s.cancelled[j] {
          CountUpdate(s.states, j, 4, {1, 2});
          CountUpdate(s.states, j, 4, {2});
        }
      case Close =>
    }
    var t := Step(s, e, capacity, retryLimit);
    CountSubset(t.states, {2}, {1, 2});
    forall k | 0 <= k < |t.states|
      ensures 0 <= t.states[k] <= 5 && 0 <= t.attempts[k] <= retryLimit &&
              (t.states[k] == 0 ==> t.attempts[k] == 0 && !t.cancelled[k]) &&
              (t.states[k] == 2 ==> t.attempts[k] > 0) &&
              (t.states[k] == 1 ==> !t.cancelled[k] && t.attempts[k] < retryLimit) &&
              (t.states[k] == 5 ==> t.attempts[k] == retryLimit)
    {
      assert 0 <= s.states[k] <= 5 && 0 <= s.attempts[k] <= retryLimit;
      assert s.states[k] == 0 ==> s.attempts[k] == 0 && !s.cancelled[k];
      assert s.states[k] == 1 ==> !s.cancelled[k] && s.attempts[k] < retryLimit;
      assert s.states[k] == 2 ==> s.attempts[k] > 0;
      assert s.states[k] == 5 ==> s.attempts[k] == retryLimit;
    }
  }

  ghost function Replay(s: State, events: seq<Event>, capacity: int, retryLimit: int): State
    requires |s.attempts| == |s.states| && |s.cancelled| == |s.states|
    requires forall e <- events :: e.Close? || 0 <= e.job < |s.states|
    decreases |events|
  {
    if |events| == 0 then s
    else Replay(Step(s, events[0], capacity, retryLimit), events[1..], capacity, retryLimit)
  }

  lemma HistorySafety(s: State, events: seq<Event>, capacity: int, retryLimit: int)
    requires Valid(s, capacity, retryLimit)
    requires forall e <- events :: e.Close? || 0 <= e.job < |s.states|
    ensures Valid(Replay(s, events, capacity, retryLimit), capacity, retryLimit)
    decreases |events|
  {
    if |events| > 0 {
      StepPreserves(s, events[0], capacity, retryLimit);
      HistorySafety(Step(s, events[0], capacity, retryLimit), events[1..], capacity, retryLimit);
    }
  }

  lemma ArbitraryFiniteHistory(jobs: int, capacity: int, retryLimit: int, events: seq<Event>)
    requires 1 <= capacity <= jobs && retryLimit >= 1
    requires forall e <- events :: e.Close? || 0 <= e.job < jobs
    ensures Valid(Replay(Initial(jobs), events, capacity, retryLimit), capacity, retryLimit)
  {
    InitialValid(jobs, capacity, retryLimit);
    HistorySafety(Initial(jobs), events, capacity, retryLimit);
  }
}
