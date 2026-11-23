using System;
using System.Threading;

namespace SyncFixture;

public interface IJournal : IDisposable
{
    void Append(int value);
}

// Mutant: a latch that has waiters is also set under the ledger lock.
public sealed class Ledger(IJournal journal)
{
    private readonly object sync = new();
    private readonly Slot last = new();
    private readonly Latch recorded = new();
    private readonly CancellationTokenSource stop = new();

    public void Record(int value)
    {
        lock (this.sync)
        {
            if (this.stop.Token.IsCancellationRequested) return;
            this.last.TrySet(value);
            this.recorded.TrySet();
            journal.Append(value);
        }
    }

    public void Close()
    {
        this.stop.Cancel();
        journal.Dispose();
    }
}