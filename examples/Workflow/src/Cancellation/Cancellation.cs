using System;

public static class CancellationChecks
{
    private static bool cleaned;

    public static void Process(bool requested)
    {
        cleaned = false;
        try
        {
            if (requested) throw new OperationCanceledException();
        }
        finally
        {
            cleaned = true;
        }
    }

    public static void Observe(bool requested, out bool canceled, out bool cleanupCompleted)
    {
        canceled = false;
        try { Process(requested); }
        catch (OperationCanceledException) { canceled = true; }
        cleanupCompleted = cleaned;
    }

    public static void Invert(bool canceled, out bool result) => result = !canceled;
}
