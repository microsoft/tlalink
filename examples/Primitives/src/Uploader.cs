using System;
using System.Threading.Tasks;

namespace PrimitivesExample;

// The transport below the uploader: an environment outside the synchronization region.
public interface IChunkSink
{
    Task WriteAsync(ReadOnlyMemory<byte> chunk);
}

// Caller component and synchronization region: at most one chunk is in flight per gate permit.
public sealed class Uploader(PermitGate gate, IChunkSink sink)
{
    private readonly object statsLock = new();
    private long sent;

    public long Sent
    {
        get { lock (this.statsLock) return this.sent; }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> chunk)
    {
        await gate.AcquireAsync().ConfigureAwait(false);
        try
        {
            await sink.WriteAsync(chunk).ConfigureAwait(false);
            lock (this.statsLock) this.sent += chunk.Length;
        }
        finally
        {
            gate.Release();
        }
    }
}