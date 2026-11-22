using System.Threading.Tasks;

namespace SyncFixture;

// Set-once latch whose waiters resume asynchronously.
public sealed class Latch
{
    private readonly TaskCompletionSource<bool> state = new();

    public bool TrySet() => this.state.TrySetResult(true);

    public Task WaitAsync() => this.state.Task;
}