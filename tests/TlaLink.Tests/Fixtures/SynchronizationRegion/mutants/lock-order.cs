using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SyncFixture;

public interface IFrameSink
{
    Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken token);
    void Post(int id);
    void Flush();
}

// Synthetic connection: sends are admitted by a gate, bookkeeping runs under two ordered locks
// and a pump task is joined on close. The scope declares this file as its synchronization region.
public sealed class Connection(IFrameSink sink)
{
    private readonly object stateLock = new();
    private readonly object tableLock = new();
    private readonly Dictionary<int, int> pending = [];
    private readonly Gate sends = new();
    private readonly Latch closed = new();
    private readonly CancellationTokenSource shutdown = new();
    private Task pump = Task.CompletedTask;
    private int next;

    public void Start()
    {
        this.pump = Task.Run(this.PumpAsync);
        _ = Task.Run(this.Heartbeat);
    }

    public bool TrySend(int id)
    {
        if (!this.sends.TryEnter()) return false;
        try
        {
            lock (this.stateLock)
            {
                this.next++;
                this.Track(id);
                sink.Post(id);
            }
            return true;
        }
        finally
        {
            this.sends.Exit();
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> frame)
    {
        if (!this.sends.TryEnter()) throw new InvalidOperationException("The connection is closed.");
        try
        {
            await sink.SendAsync(frame, this.shutdown.Token).ConfigureAwait(false);
        }
        finally
        {
            this.sends.Exit();
        }
    }

    public async Task CloseAsync()
    {
        this.sends.Close();
        this.shutdown.Cancel();
        this.closed.TrySet();
        await this.sends.WaitDrainedAsync().ConfigureAwait(false);
        await this.pump.ConfigureAwait(false);
        sink.Flush();
    }

    private async Task PumpAsync()
    {
        await this.closed.WaitAsync().ConfigureAwait(false);
        await this.DrainAsync().ConfigureAwait(false);
    }

    private Task DrainAsync()
    {
        lock (this.stateLock) this.next = 0;
        return Task.CompletedTask;
    }

    private void Track(int id)
    {
        lock (this.tableLock) this.pending[id] = this.next;
    }

    private void Heartbeat()
    {
        lock (this.tableLock)
        {
            lock (this.stateLock) sink.Post(-1);
        }
    }
}