namespace ComponentsExample;

public sealed class Counter
{
    /* @link {"bind":[{"id":"counter::value"}]} */
    public int Value { get; private set; }

    public void Step()
    {
        /* @link {"bind":[{"id":"counter::step"}]} */
        Value = (Value + 1) % 3;
    }
}
