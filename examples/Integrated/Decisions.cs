
public static class Decisions
{
    /* @link {"bind":[{"id":"credit-return","target":"decisions-spec::DecisionsSpec.Return"}]} */
    public static void Return(int window, int available, int count, out int next, out bool accepted)
    {
        accepted = count > 0 && count <= window - available;
        /* @link {"bind":[{"id":"Return","target":"CreditDecision::Return"}]} */
        next = accepted ? available + count : available;
    }

    /* @link {"bind":[{"id":"record-append","target":"decisions-spec::DecisionsSpec.Append"}]} */
    public static void Append(int length, int addition, int maximum, out int next)
    {
        bool accepted = addition <= maximum - length;
        next = accepted ? length + addition : length;
    }

    /* @link {"bind":[{"id":"boolean-function","target":"decisions-spec::DecisionsSpec.Invert"}]} */
    public static void Invert(bool value, out bool inverted) => inverted = !value;
}
