using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace DispatcherExample;

public static class DispatcherObservationLog
{
    public static void Write(string directory)
    {
        using StreamWriter log = new(new FileStream(Path.Combine(directory, "dispatcher.log"), FileMode.CreateNew));
        for (int instance = 0; instance < 2; instance++)
        {
            var dispatcher = new Dispatcher(3, 2, 2);
            void Emit(string operation, int job, bool accepted)
            {
                if (!accepted) throw new InvalidOperationException("Unexpected rejected observation operation.");
                DispatcherCases.Check(dispatcher.Snapshot(), 2, 2);
                log.WriteLine(string.Join('\t', "dispatcher-" + instance,
                    Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture), operation,
                    job.ToString(CultureInfo.InvariantCulture)));
            }
            Emit("admit", 0, dispatcher.TryAdmit(0));
            Emit("start", 0, dispatcher.TryStart(0));
            Emit("failure", 0, dispatcher.Complete(0, false));
            Emit("start", 0, dispatcher.TryStart(0));
            Emit(instance == 0 ? "success" : "failure", 0, dispatcher.Complete(0, instance == 0));
            Emit("admit", 1, dispatcher.TryAdmit(1));
            Emit("cancel", 1, dispatcher.Cancel(1));
            Emit("admit", 2, dispatcher.TryAdmit(2));
            Emit("start", 2, dispatcher.TryStart(2));
            Emit("cancel", 2, dispatcher.Cancel(2));
            dispatcher.Close();
            Emit("close", 0, true);
            Emit("success", 2, dispatcher.Complete(2, true));
            if (!dispatcher.Snapshot().Drained) throw new InvalidOperationException("Observation workload did not drain.");
        }
    }
}
