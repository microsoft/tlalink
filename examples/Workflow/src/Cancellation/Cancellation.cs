using System;

public static class CancellationChecks
{
    private static bool cleaned;

    /* @link {"bind":[{"id":"cancellation-spec::cleanup"}]} */
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

    /* @link {"bind":[{"id":"rejection-spec::bad-inversion"}]} */
    public static void Invert(bool canceled, out bool result) => result = !canceled;
}
