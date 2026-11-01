using System.Threading;
using System.Threading.Tasks;

sealed class AsyncService
{
    private readonly AdmissionGate gate = new AdmissionGate();
    public async Task<int> RunAsync(bool stop, CancellationToken token)
    {
        this.gate.Enter();
        try
        {
            IRelease alias = this.gate.Self;
            alias.Exit();
            await Work.RunAsync(token);
            return 1;
        }
        finally { this.gate.Exit(); }
    }
}
