using System;
using System.Threading.Tasks;
using TlaLink.Runtime;

namespace PrimitivesExample;

// Trace workload: one sender uploads two chunks in sequence through a single-permit gate, so every acquire is
// granted at once. Instrumentation snapshots the gate's mapped state after the acquire and release sites.
internal sealed class MemorySink : IChunkSink
{
    public Task WriteAsync(ReadOnlyMemory<byte> chunk) => Task.CompletedTask;
}

internal static class Program
{
    private static async Task Main(string[] args)
    {
        Uploader uploader = new(new PermitGate(1), new MemorySink());
        await uploader.SendAsync(new byte[] { 1, 2 }).ConfigureAwait(false);
        await uploader.SendAsync(new byte[] { 3 }).ConfigureAwait(false);
        TraceRuntime.Complete(args[0]);
    }
}