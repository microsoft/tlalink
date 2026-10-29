interface IRelease
{
    void Exit();
}

sealed class AdmissionGate : IRelease
{
    public void Enter() { }
    public void Exit() { }
    public IRelease Self => this;
}
