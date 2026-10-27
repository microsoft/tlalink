module DecisionsSpec {
  /* @link {"bind":[{"id":"decisions-spec::boolean-function"}]} */
  function Invert(value: bool): bool { !value }
  /* @link {"bind":[{"id":"decisions-spec::credit-return"}]} */
  method Return(window: int, available: int, count: int) returns (next: int, accepted: bool)
    requires 0 <= available <= window <= 2147483647
    ensures 0 <= next <= window
    ensures accepted <==> 0 < count <= window - available
    ensures next == (if accepted then available + count else available)
  {
    accepted := 0 < count <= window - available;
    next := if accepted then available + count else available;
  }

  /* @link {"bind":[{"id":"decisions-spec::record-append"}]} */
  function Append(length: int, addition: int, maximum: int): int
    requires 0 <= length <= maximum <= 2147483647
    requires 0 <= addition <= 2147483647
    ensures 0 <= Append(length, addition, maximum) <= maximum
  {
    if addition <= maximum - length then length + addition else length
  }
}
