using System.Threading;
using System.Threading.Tasks;

sealed class AsyncService
{
    private readonly AdmissionGate gate = new AdmissionGate();
    public async Task<Task> RunAsync(bool stop, CancellationToken token)
    {
        this.gate.Enter();
        try { await Work.RunAsync(token); return Work.RunAsync(token); }
        finally { this.gate.Exit(); }
    }
}
