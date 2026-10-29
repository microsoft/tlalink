using System.Threading;
using System.Threading.Tasks;

static class Work
{
    public static void Check() { }
    public static void Fail() => throw new System.InvalidOperationException();
    public static Task RunAsync(CancellationToken token) => Task.Delay(1, token);
    public static Task FaultAsync() => Task.FromException(new System.InvalidOperationException());
    public static Task CanceledAsync() => Task.FromCanceled(new CancellationToken(true));
}
