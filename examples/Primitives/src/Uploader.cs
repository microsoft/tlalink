using System;
using System.Threading.Tasks;

namespace PrimitivesExample;

// Caller component: at most one chunk is in flight per gate permit.
public sealed class Uploader(PermitGate gate, Func<ReadOnlyMemory<byte>, Task> transport)
{
    public async Task SendAsync(ReadOnlyMemory<byte> chunk)
    {
        await gate.AcquireAsync().ConfigureAwait(false);
        try
        {
            await transport(chunk).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
