/* @link {"bind":[{"id":"increment-spec::increment","source":{"symbol":"M:Arithmetic.Increment(System.Int32)"}}]} */
method Increment(value: int) returns (result: int)
  requires 0 <= value <= 10
  ensures result == (if value < 10 then value + 1 else 10)
  ensures 0 <= result <= 10
{
  result := if value < 10 then value + 1 else 10;
}
