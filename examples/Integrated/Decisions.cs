
public static class Decisions
{
    /* @link {"bind":[{"id":"decisions-spec::credit-return"}]} */
    public static void Return(int window, int available, int count, out int next, out bool accepted)
    {
        accepted = count > 0 && count <= window - available;
        /* @link {"bind":[{"id":"CreditDecision::Return"}]} */
        next = accepted ? available + count : available;
    }

    /* @link {"bind":[{"id":"decisions-spec::record-append"}]} */
    public static void Append(int length, int addition, int maximum, out int next)
    {
        bool accepted = addition <= maximum - length;
        next = accepted ? length + addition : length;
    }

    /* @link {"bind":[{"id":"decisions-spec::boolean-function"}]} */
    public static void Invert(bool value, out bool inverted) => inverted = !value;
}
