using System.Threading.Tasks;

namespace SyncFixture;

// Admission gate: TryEnter admits while open; WaitDrainedAsync completes once closed with nobody inside.
public sealed class Gate
{
    private readonly object sync = new();
    private readonly TaskCompletionSource<bool> drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int count;
    private bool closed;

    public bool TryEnter()
    {
        lock (this.sync)
        {
            if (this.closed) return false;
            this.count++;
            return true;
        }
    }

    public void Exit()
    {
        bool signal;
        lock (this.sync)
        {
            this.count--;
            signal = this.closed && this.count == 0;
        }
        if (signal) this.drained.TrySetResult(true);
    }

    public void Close()
    {
        bool signal;
        lock (this.sync)
        {
            this.closed = true;
            signal = this.count == 0;
        }
        if (signal) this.drained.TrySetResult(true);
    }

    public Task WaitDrainedAsync() => this.drained.Task;
}