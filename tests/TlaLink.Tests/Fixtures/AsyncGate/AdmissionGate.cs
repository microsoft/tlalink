using System.Threading.Tasks;

sealed class AdmissionGate
{
    public void Enter() { }
    public void Exit() { }
    public void Close() { }
    public Task Drained => Task.CompletedTask;
}
