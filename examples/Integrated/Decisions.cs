/* @tla 2 model CreditDecision; */
public static class Decisions
{
    public static void Return(int window, int available, int count, out int next, out bool accepted)
    {
        accepted = count > 0 && count <= window - available;
        /* @tla 2 step Return; */
        next = accepted ? available + count : available;
    }

    public static void Append(int length, int addition, int maximum, out int next)
    {
        bool accepted = addition <= maximum - length;
        next = accepted ? length + addition : length;
    }

    public static void Invert(bool value, out bool inverted) => inverted = !value;
}
