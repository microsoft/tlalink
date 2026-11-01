using System;

namespace DispatcherExample;

public enum JobState { New, Queued, Running, Succeeded, Cancelled, Failed }

public sealed record DispatcherSnapshot(JobState[] States, int[] Attempts, bool[] CancellationRequested,
    int Outstanding, int Running, bool Closing)
{
    public bool Drained => Closing && Outstanding == 0;
}

public sealed class Dispatcher
{
    private readonly object gate = new();
    private readonly int capacity;
    private readonly int retryLimit;
    private readonly JobState[] states;
    private readonly int[] attempts;
    private readonly bool[] cancellationRequested;
    private int outstanding;
    private int running;
    private bool closing;

    public Dispatcher(int jobCount, int capacity, int retryLimit)
    {
        if (jobCount is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(jobCount));
        if (capacity < 1 || capacity > jobCount) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (retryLimit is < 1 or > 16) throw new ArgumentOutOfRangeException(nameof(retryLimit));
        this.capacity = capacity;
        this.retryLimit = retryLimit;
        states = new JobState[jobCount];
        attempts = new int[jobCount];
        cancellationRequested = new bool[jobCount];
    }

    public bool TryAdmit(int job)
    {
        ValidateJob(job);
        lock (gate)
        {
            if (closing || states[job] != JobState.New || outstanding == capacity) return false;
            states[job] = JobState.Queued;
            outstanding++;
            return true;
        }
    }

    public bool TryStart(int job)
    {
        ValidateJob(job);
        lock (gate)
        {
            if (states[job] != JobState.Queued) return false;
            states[job] = JobState.Running;
            attempts[job]++;
            running++;
            return true;
        }
    }

    public bool Complete(int job, bool succeeded)
    {
        ValidateJob(job);
        lock (gate)
        {
            if (states[job] != JobState.Running) return false;
            running--;
            if (cancellationRequested[job]) Retire(job, JobState.Cancelled);
            else if (succeeded) Retire(job, JobState.Succeeded);
            else if (attempts[job] == retryLimit) Retire(job, JobState.Failed);
            else states[job] = JobState.Queued;
            return true;
        }
    }

    public bool Cancel(int job)
    {
        ValidateJob(job);
        lock (gate)
        {
            if (states[job] is not (JobState.Queued or JobState.Running) || cancellationRequested[job]) return false;
            cancellationRequested[job] = true;
            if (states[job] == JobState.Queued) Retire(job, JobState.Cancelled);
            return true;
        }
    }

    public void Close()
    {
        lock (gate) closing = true;
    }

    public DispatcherSnapshot Snapshot()
    {
        lock (gate)
            return new((JobState[])states.Clone(), (int[])attempts.Clone(),
                (bool[])cancellationRequested.Clone(), outstanding, running, closing);
    }

    private void Retire(int job, JobState terminal)
    {
        states[job] = terminal;
        outstanding--;
    }

    private void ValidateJob(int job)
    {
        if ((uint)job >= (uint)states.Length) throw new ArgumentOutOfRangeException(nameof(job));
    }
}
