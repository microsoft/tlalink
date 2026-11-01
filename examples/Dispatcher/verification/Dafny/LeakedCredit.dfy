include "Dispatcher.dfy"

module LeakedCredit {
  import opened DispatcherHistory

  // Behavioral control: completion changes the state but forgets its credit.
  lemma TerminalCompletionReturnsCredit()
    ensures Valid(State([3], [1], [false], 1, 0, false), 1, 2)
  {
    assert Count([3], {1, 2}) == 0;
  }
}
