using System;
using System.Threading.Tasks;

namespace PrimitivesExample;

// The transport below the uploader: an environment outside the synchronization region.
public interface IChunkSink
{
    Task WriteAsync(ReadOnlyMemory<byte> chunk);
}

// Negative control: the write is discarded, so the permit is released before the chunk is written.
public sealed class Uploader(PermitGate gate, IChunkSink sink)
{
    private readonly PermitGate gate = gate;
    private readonly object statsLock = new();
    private long sent;

    public long Sent
    {
        get { lock (this.statsLock) return this.sent; }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> chunk)
    {
        await this.gate.AcquireAsync().ConfigureAwait(false);
        try
        {
            _ = sink.WriteAsync(chunk);
            lock (this.statsLock) this.sent += chunk.Length;
        }
        finally
        {
            this.gate.Release();
        }
    }
}