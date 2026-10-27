using System;

public static class CancellationChecks
{
    private static bool cleaned;

    /* @link {"bind":[{"id":"cleanup","target":"cancellation-spec::CancellationSpec.Observe"}]} */
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

    /* @link {"bind":[{"id":"bad-inversion","target":"rejection-spec::RejectedSpec.Invert"}]} */
    public static void Invert(bool canceled, out bool result) => result = !canceled;
}
