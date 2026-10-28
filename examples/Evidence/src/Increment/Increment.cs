public static class Arithmetic
{
    /* @link {"bind":[{"id":"increment-spec::increment"}]} */
    public static int Increment(int value) => value < 10 ? value + 1 : 10;
}
