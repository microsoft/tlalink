module RejectedSpec {
  /* @link {"bind":[{"id":"rejection-spec::bad-inversion"}]} */
  method Invert(canceled: bool) returns (result: bool)
    ensures result == !canceled
  {
    result := canceled;
  }
}
