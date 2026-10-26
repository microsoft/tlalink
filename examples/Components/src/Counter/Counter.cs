namespace ComponentsExample;

public sealed class Counter
{
    public int Value { get; private set; }

    public void Step() => Value = (Value + 1) % 3;
}
