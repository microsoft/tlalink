using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DispatcherExample;

public static class DispatcherCases
{
    public static readonly string[] Names = [
        "capacity", "retry", "exhaustion", "queued-cancellation", "running-cancellation",
        "completion-before-cancellation", "shutdown", "invalid-input", "snapshot-isolation",
        "concurrent-admission", "cancel-complete-race"
    ];

    public static void Run(string name)
    {
        var dispatcher = new Dispatcher(3, 2, 2);
        switch (name)
        {
            case "capacity":
                Require(dispatcher.TryAdmit(0) && dispatcher.TryAdmit(1), "admission");
                Require(!dispatcher.TryAdmit(0) && !dispatcher.TryAdmit(2), "duplicate/capacity rejection");
                Require(dispatcher.TryStart(0) && dispatcher.Complete(0, true), "completion");
                Require(dispatcher.TryAdmit(2), "returned credit");
                break;
            case "retry":
                Require(dispatcher.TryAdmit(0) && dispatcher.TryStart(0) && dispatcher.Complete(0, false), "first attempt");
                Require(dispatcher.Snapshot().States[0] == JobState.Queued && dispatcher.Snapshot().Outstanding == 1, "retry retains credit");
                Require(dispatcher.TryStart(0) && dispatcher.Complete(0, true), "successful retry");
                Require(dispatcher.Snapshot().States[0] == JobState.Succeeded && dispatcher.Snapshot().Attempts[0] == 2, "retry outcome");
                break;
            case "exhaustion":
                Require(dispatcher.TryAdmit(0), "admission");
                for (int i = 0; i < 2; i++) Require(dispatcher.TryStart(0) && dispatcher.Complete(0, false), "failed attempt");
                Require(dispatcher.Snapshot().States[0] == JobState.Failed && !dispatcher.TryStart(0), "retry limit");
                break;
            case "queued-cancellation":
                Require(dispatcher.TryAdmit(0) && dispatcher.Cancel(0), "queued cancellation");
                Require(dispatcher.Snapshot().Outstanding == 0 && !dispatcher.TryStart(0) && !dispatcher.Cancel(0), "retirement is one-shot");
                break;
            case "running-cancellation":
                Require(dispatcher.TryAdmit(0) && dispatcher.TryStart(0) && dispatcher.Cancel(0), "running cancellation");
                Require(dispatcher.Snapshot().Outstanding == 1 && dispatcher.Snapshot().Running == 1, "cancel does not release live ownership");
                Require(dispatcher.Complete(0, true) && !dispatcher.Complete(0, true), "completion is one-shot");
                Require(dispatcher.Snapshot().States[0] == JobState.Cancelled, "cancellation wins before completion");
                break;
            case "completion-before-cancellation":
                Require(dispatcher.TryAdmit(0) && dispatcher.TryStart(0) && dispatcher.Complete(0, true), "completion");
                Require(!dispatcher.Cancel(0) && dispatcher.Snapshot().States[0] == JobState.Succeeded, "late cancellation does not rewrite outcome");
                break;
            case "shutdown":
                Require(dispatcher.TryAdmit(0) && dispatcher.TryAdmit(1) && dispatcher.TryStart(0), "live work");
                dispatcher.Close();
                dispatcher.Close();
                Require(!dispatcher.TryAdmit(2) && !dispatcher.Snapshot().Drained, "closed admission and pending drain");
                Require(dispatcher.Cancel(1) && dispatcher.Complete(0, false), "cancel and retry during drain");
                Require(!dispatcher.Snapshot().Drained && dispatcher.TryStart(0) && dispatcher.Complete(0, true), "retry must settle");
                Require(dispatcher.Snapshot().Drained && !dispatcher.TryAdmit(2), "drained is not reopened");
                break;
            case "invalid-input":
                ExpectRange(() => dispatcher.TryAdmit(-1));
                ExpectRange(() => dispatcher.TryStart(3));
                ExpectRange(() => dispatcher.Cancel(int.MaxValue));
                ExpectRange(() => dispatcher.Complete(-1, true));
                ExpectRange(() => new Dispatcher(0, 1, 1));
                ExpectRange(() => new Dispatcher(3, 4, 1));
                ExpectRange(() => new Dispatcher(3, 1, 0));
                Require(!dispatcher.TryStart(0) && !dispatcher.Complete(0, true) && !dispatcher.Cancel(0), "wrong-state operations reject");
                break;
            case "snapshot-isolation":
                Require(dispatcher.TryAdmit(0), "admission");
                DispatcherSnapshot copy = dispatcher.Snapshot();
                copy.States[0] = JobState.Failed;
                copy.Attempts[0] = 99;
                copy.CancellationRequested[0] = true;
                Require(dispatcher.Snapshot().States[0] == JobState.Queued && dispatcher.Snapshot().Attempts[0] == 0, "snapshot cannot mutate storage");
                break;
            case "concurrent-admission":
                dispatcher = new Dispatcher(16, 2, 2);
                int admitted = 0;
                Parallel.For(0, 16, job => { if (dispatcher.TryAdmit(job)) Interlocked.Increment(ref admitted); });
                Require(admitted == 2, "concurrent capacity");
                dispatcher.Close();
                Parallel.For(0, 16, job => {
                    if (dispatcher.TryStart(job)) Require(dispatcher.Complete(job, true), "concurrent completion");
                });
                Require(dispatcher.Snapshot().Drained, "concurrent drain");
                break;
            case "cancel-complete-race":
                for (int i = 0; i < 100; i++)
                {
                    dispatcher = new Dispatcher(1, 1, 2);
                    Require(dispatcher.TryAdmit(0) && dispatcher.TryStart(0), "race setup");
                    Parallel.Invoke(() => dispatcher.Cancel(0), () => dispatcher.Complete(0, true));
                    DispatcherSnapshot result = dispatcher.Snapshot();
                    Require(result.States[0] is JobState.Cancelled or JobState.Succeeded, "legal race winner");
                    Require(result.Outstanding == 0 && result.Running == 0, "race releases once");
                    Check(result, 1, 2);
                }
                break;
            default: throw new ArgumentException("Unknown dispatcher case: " + name);
        }
        Check(dispatcher.Snapshot(), 2, 2);
    }

    public static void Check(DispatcherSnapshot snapshot, int capacity, int retryLimit)
    {
        Require(snapshot.Outstanding == snapshot.States.Count(s => s is JobState.Queued or JobState.Running), "outstanding conservation");
        Require(snapshot.Running == snapshot.States.Count(s => s == JobState.Running), "running conservation");
        Require(snapshot.Running >= 0 && snapshot.Running <= snapshot.Outstanding && snapshot.Outstanding <= capacity, "capacity bounds");
        Require(snapshot.Attempts.All(a => a >= 0 && a <= retryLimit), "attempt bounds");
        for (int job = 0; job < snapshot.States.Length; job++)
        {
            Require(snapshot.States[job] != JobState.Running || snapshot.Attempts[job] > 0, "running has an attempt");
            Require(snapshot.States[job] != JobState.Queued || !snapshot.CancellationRequested[job], "cancelled queue is retired");
            Require(snapshot.States[job] != JobState.Failed || snapshot.Attempts[job] == retryLimit, "failure exhausts retries");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectRange(Action action)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { return; }
        throw new InvalidOperationException("Expected an explicit range error.");
    }
}
