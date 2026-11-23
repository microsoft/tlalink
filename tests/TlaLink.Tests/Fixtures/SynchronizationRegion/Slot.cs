namespace SyncFixture;

// Single-assignment slot: the first TrySet wins and nobody waits for it.
public sealed class Slot
{
    private readonly object sync = new();
    private int? value;

    public bool TrySet(int candidate)
    {
        lock (this.sync)
        {
            if (this.value != null) return false;
            this.value = candidate;
            return true;
        }
    }

    public int? Value
    {
        get { lock (this.sync) return this.value; }
    }
}