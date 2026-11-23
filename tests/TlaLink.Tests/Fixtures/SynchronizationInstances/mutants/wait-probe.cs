using System.Threading.Tasks;

namespace InstanceFixture;

// Synthetic worker: runs pass an admission gate; stopping closes the gate, waits for its drain and sets a latch
// that waiters resume on. The scope declares this file as its region and Pump.tla models both fields.
public sealed class Pump
{
    private readonly Gate admission = new();
    private readonly Latch stopped = new();
    private readonly int limit = 2;

    public int Limit => this.limit;

    public bool Drained => this.admission.WaitDrainedAsync().IsCompleted;

    public bool TryRun()
    {
        if (!this.admission.TryEnter()) return false;
        this.admission.Exit();
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