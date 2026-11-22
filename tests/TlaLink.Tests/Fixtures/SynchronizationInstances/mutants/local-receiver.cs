using System.Threading.Tasks;

namespace InstanceFixture;

// Mutant: TryRun exits through a local copy of the gate, which is not an instance field.
public sealed class Pump
{
    private readonly Gate admission = new();
    private readonly Latch stopped = new();
    private readonly int limit = 2;

    public int Limit => this.limit;

    public bool TryRun()
    {
        if (!this.admission.TryEnter()) return false;
        Gate entered = this.admission;
        entered.Exit();
        return true;
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