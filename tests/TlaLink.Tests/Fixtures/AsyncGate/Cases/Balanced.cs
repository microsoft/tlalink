using System;
using System.Threading;
using System.Threading.Tasks;

sealed class AsyncService
{
    private readonly AdmissionGate gate = new AdmissionGate();
    private readonly object sync = new object();

    public async Task<int> RunAsync(bool stop, CancellationToken token)
    {
        if (stop) return 0;
        lock (this.sync)
        {
            Work.Check();
            this.gate.Enter();
        }
        try
        {
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                try { await Work.RunAsync(linked.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            }
            return 1;
        }
        finally { this.gate.Exit(); }
    }

    public void Close() { this.gate.Close(); }
    public Task Drained => this.gate.Drained;
}
