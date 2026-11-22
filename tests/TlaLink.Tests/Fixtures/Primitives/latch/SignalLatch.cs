using System;
using System.Threading.Tasks;

namespace PrimitiveFixture;

public sealed class SignalLatch
{
    private readonly TaskCompletionSource<bool> done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsCompleted => this.done.Task.IsCompleted;

    public bool TrySet() => this.done.TrySetResult(true);

    public bool TryFail(Exception error) => this.done.TrySetException(error);

    public async Task WaitAsync() => await this.done.Task.ConfigureAwait(false);

    private void Unused() { }

    public sealed class Nested
    {
        public void Ignored() { }
    }
}
