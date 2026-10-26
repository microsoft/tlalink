module RejectedSpec {
  method Invert(canceled: bool) returns (result: bool)
    ensures result == !canceled
  {
    result := canceled;
  }
}
