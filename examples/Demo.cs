
internal sealed class Demo
{
    private readonly object gate = new object();

    private int active;

    internal void Open()
    {
        lock (this.gate)
        {
            this.active++;
        }
    }

    internal void Release()
    {
        lock (this.gate)
        {
            this.active--;
        }
    }
}
