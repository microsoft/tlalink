using System;
using System.Threading;
using TlaLink.Runtime;

/* @link model WorkflowLanes; */
internal sealed class Lane
{
    private readonly object gate = new();
    private readonly int id;

    /* @link state Value guard=gate type=int; */
    private int value;

    public Lane(int id) { this.id = id; }

    public void Move(int nextValue = 1)
    {
        lock (gate)
        {
            /* @link step Move actor="1" operation="1" entity=id point=commit classification=transition observe=Value; */
            value = nextValue;
        }
    }
}

internal static class Program
{
    private static void Main(string[] args)
    {
        string mode = args.Length > 1 ? args[1] : "healthy";
        if (mode is not ("healthy" or "corrupt" or "reverse" or "unordered"))
            throw new ArgumentException("Expected healthy, corrupt, reverse or unordered workload mode.");
        bool reversed = mode is "reverse" or "unordered";
        var first = new Lane(reversed ? 2 : 1);
        var second = new Lane(reversed ? 1 : 2);
        TraceHandoff? handoff = null;
        Exception? failure = null;
        var producer = new Thread(() =>
        {
            try
            {
                first.Move();
                if (mode != "unordered") handoff = TraceRuntime.CaptureHandoff();
            }
            catch (Exception error) { failure = error; }
        });
        var consumer = new Thread(() =>
        {
            try
            {
                if (mode != "unordered") TraceRuntime.AcceptHandoff(handoff!);
                second.Move(mode == "corrupt" ? 2 : 1);
            }
            catch (Exception error) { failure = error; }
        });
        producer.Start();
        producer.Join();
        if (failure is not null) throw failure;
        consumer.Start();
        consumer.Join();
        if (failure is not null) throw failure;
        TraceRuntime.Complete(args[0]);
    }
}
