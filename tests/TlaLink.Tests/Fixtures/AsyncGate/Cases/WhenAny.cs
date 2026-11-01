using System.Threading;
using System.Threading.Tasks;

sealed class AsyncService
{
    private readonly AdmissionGate gate = new AdmissionGate();
    public async Task<int> RunAsync(bool stop, CancellationToken token)
    {
        this.gate.Enter();
        try { await Task.WhenAny(Work.RunAsync(token), Task.Delay(1)); return 1; }
        finally { this.gate.Exit(); }
    }
}
