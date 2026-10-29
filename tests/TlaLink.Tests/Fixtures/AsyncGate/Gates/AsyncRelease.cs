using System.Threading.Tasks;

sealed class AdmissionGate
{
    public void Enter() { }
    public async Task Exit() { await Task.Yield(); }
}
