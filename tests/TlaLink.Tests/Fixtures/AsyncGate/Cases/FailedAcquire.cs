using System.Threading;
using System.Threading.Tasks;

sealed class AsyncService
{
    private readonly AdmissionGate gate = new AdmissionGate();
    public async Task<int> RunAsync(bool stop, CancellationToken token)
    {
        try { this.gate.Enter(); await Work.RunAsync(token); return 1; }
        finally { this.gate.Exit(); }
    }
}
