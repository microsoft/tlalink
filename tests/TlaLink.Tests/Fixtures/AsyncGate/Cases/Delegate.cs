using System;
using System.Threading;
using System.Threading.Tasks;

sealed class AsyncService
{
    private readonly AdmissionGate gate = new AdmissionGate();
    public async Task<int> RunAsync(bool stop, CancellationToken token)
    {
        this.gate.Enter();
        try { Action callback = Work.Check; callback(); await Work.RunAsync(token); return 1; }
        finally { this.gate.Exit(); }
    }
}
