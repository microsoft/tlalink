using System;
using System.Threading;

namespace SummaryExample;

public sealed class PermitGate
{
    private int remaining;

    public PermitGate(int permits)
    {
        if (permits < 0) throw new ArgumentOutOfRangeException(nameof(permits));
        this.remaining = permits;
    }

    public bool TryTake()
    {
        int observed = Volatile.Read(ref this.remaining);
        while (observed > 0)
        {
            int previous = Interlocked.CompareExchange(ref this.remaining, observed - 1, observed);
            if (previous == observed) return true;
            observed = previous;
        }
        return false;
    }
}
