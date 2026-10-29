using System.Threading;
using System.Threading.Tasks;

sealed class AsyncService
{
    private readonly AdmissionGate gate = new AdmissionGate();
    private readonly object sync = new object();
    public async Task<int> RunAsync(bool stop, CancellationToken token)
    {
        lock (this.sync) { this.gate.Enter(); Work.Fail(); }
        try { await Work.RunAsync(token); return 1; }
        finally { this.gate.Exit(); }
    }
}
