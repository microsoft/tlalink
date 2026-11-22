using System.Threading.Tasks;

namespace InstanceFixture;

// Mutant: Abort closes the gate at a site no bind or exclusion covers.
public sealed class Pump
{
    private readonly Gate admission = new();
    private readonly Latch stopped = new();
    private readonly int limit = 2;

    public int Limit => this.limit;

    public bool TryRun()
    {
        if (!this.admission.TryEnter()) return false;
        this.admission.Exit();
        return true;
    }

    public void Abort()
    {
        this.admission.Close();
    }

    public async Task StopAsync()
    {
        this.admission.Close();
        await this.admission.WaitDrainedAsync().ConfigureAwait(false);
        this.stopped.TrySet();
    }

    public async Task WaitStoppedAsync()
    {
        await this.stopped.WaitAsync().ConfigureAwait(false);
    }
}