using System;
using System.Threading;

namespace SyncFixture;

public interface IJournal : IDisposable
{
    void Append(int value);
}

// Bookkeeping beside the connection: under its lock it reads a token, completes a slot nobody waits on and
// appends to a lock-safe journal; closing disposes the journal through its inherited IDisposable member.
public sealed class Ledger(IJournal journal)
{
    private readonly object sync = new();
    private readonly Slot last = new();
    private readonly CancellationTokenSource stop = new();

    public void Record(int value)
    {
        lock (this.sync)
        {
            if (this.stop.Token.IsCancellationRequested) return;
            this.last.TrySet(value);
            journal.Append(value);
        }
    }

    public void Close()
    {
        this.stop.Cancel();
        journal.Dispose();
    }
}