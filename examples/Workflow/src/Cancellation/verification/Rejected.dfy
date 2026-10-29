module RejectedSpec {
  /* @link {"bind":[{"id":"rejection-spec::bad-inversion","source":{"symbol":"M:CancellationChecks.Invert(System.Boolean,System.Boolean@)"}}]} */
  method Invert(canceled: bool) returns (result: bool)
    ensures result == !canceled
  {
    result := canceled;
  }
}
