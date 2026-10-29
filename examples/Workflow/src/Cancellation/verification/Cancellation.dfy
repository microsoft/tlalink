module CancellationSpec {
  /* @link {"bind":[{"id":"cancellation-spec::cleanup","source":{"symbol":"M:CancellationChecks.Process(System.Boolean)"}}]} */
  method Observe(requested: bool) returns (canceled: bool, cleaned: bool)
    ensures canceled == requested
    ensures cleaned
  {
    canceled := requested;
    cleaned := true;
  }
}
