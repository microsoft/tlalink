using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PrimitivesExample;

// Negative trace control: Release credits two permits instead of one and no longer rejects a release at
// capacity, so nothing fails at run time. Otherwise identical to src\PermitGate.cs.
// Asynchronous permits granted in arrival order. A release hands its permit to the
// oldest waiter instead of freeing it, so a later TryAcquire cannot barge ahead.
public sealed class PermitGate
{
    private readonly object sync = new();
    private readonly Queue<TaskCompletionSource<bool>> waiters = new();
    private readonly int capacity;
    private int available;
    private bool closed;

    public PermitGate(int capacity)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
        this.available = capacity;
    }

    public int Available
    {
        get { lock (this.sync) return this.available; }
    }

    public bool TryAcquire()
    {
        lock (this.sync)
        {
            if (this.closed || this.available == 0 || this.waiters.Count > 0) return false;
            this.available--;
            return true;
        }
    }

    public Task AcquireAsync()
    {
        lock (this.sync)
        {
            if (!this.closed && this.available > 0 && this.waiters.Count == 0)
            {
                this.available--;
                return Task.CompletedTask;
            }
            TaskCompletionSource<bool> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (this.closed) waiter.SetException(new ObjectDisposedException(nameof(PermitGate)));
            else this.waiters.Enqueue(waiter);
            return waiter.Task;
        }
    }

    public void Release()
    {
        TaskCompletionSource<bool>? next = null;
        lock (this.sync)
        {
            if (!this.closed && this.waiters.Count > 0) next = this.waiters.Dequeue();
            else this.available += 2;
        }
        next?.SetResult(true);
    }

    public void Close()
    {
        List<TaskCompletionSource<bool>> failed;
        lock (this.sync)
        {
            this.closed = true;
            failed = [.. this.waiters];
            this.waiters.Clear();
        }
        foreach (TaskCompletionSource<bool> waiter in failed)
            waiter.SetException(new ObjectDisposedException(nameof(PermitGate)));
    }
}
