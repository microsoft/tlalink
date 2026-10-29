using System.Globalization;
using System.Text.Json;

namespace TlaLink;

public sealed record TlcResources(int Workers, int MaxHeapMib)
{
    public static TlcResources Effective(string kind, TlcResources? requested)
    {
        TlcResources resources = requested ?? new(1, kind == "trace" ? 1024 : 512);
        if (resources.Workers is < 1 or > 64 || resources.MaxHeapMib is < 128 or > 65536)
            throw new InputException(ErrorCode.InvalidWorkflowInput, "TLC resources require 1..64 workers and 128..65536 MiB JVM heap. Heap is not a total process-memory cap.");
        return resources;
    }

    public static TlcResources? FromCli(CliOptions options)
    {
        if (!options.Has("workers") && !options.Has("max-heap-mib")) return null;
        if (!int.TryParse(options.Get("workers", "1"), NumberStyles.None, CultureInfo.InvariantCulture, out int workers) ||
            !int.TryParse(options.Get("max-heap-mib", "1024"), NumberStyles.None, CultureInfo.InvariantCulture, out int heap))
            throw new InputException(ErrorCode.InvalidWorkflowInput, "TLC workers and max-heap-mib must be positive integers.");
        return Effective("trace", new(workers, heap));
    }

    public string HeapArgument => "-Xmx" + MaxHeapMib.ToString(CultureInfo.InvariantCulture) + "m";
    public string WorkerArgument => Workers.ToString(CultureInfo.InvariantCulture);

    internal static List<Issue> ValidateEvidence(IAnalysisResult result, AnalysisStage stage, string directory)
    {
        List<Issue> issues = [];
        TlcResources effective = Effective(stage.Kind, stage.Resources);
        bool matches = result switch {
            TlcResult tlc => tlc.Resources == effective && tlc.RequestedResources == stage.Resources,
            TraceValidationResult trace => trace.Resources == effective && trace.RequestedResources == stage.Resources,
            _ => false
        };
        if (matches)
        {
            string[] arguments = result switch {
                TlcResult tlc => tlc.Invocation.Arguments.ToArray(),
                TraceValidationResult trace => trace.Invocation.Arguments,
                _ => throw new InvalidOperationException("Unsupported TLC evidence.")
            };
            matches = arguments.Count(a => a.StartsWith("-Xmx", StringComparison.Ordinal)) == 1 &&
                arguments.Contains(effective.HeapArgument) && arguments.Count(a => a == "-workers") == 1;
            int workers = Array.IndexOf(arguments, "-workers");
            matches &= workers >= 0 && workers + 1 < arguments.Length && arguments[workers + 1] == effective.WorkerArgument;
            if (stage.Kind == "tlc")
            {
                NativeProcessInvocation observed = ProjectInputs.Read<NativeProcessInvocation>(Path.Combine(directory, "tlc.invocation.json"));
                matches &= observed.TimeoutSeconds == stage.TimeoutSeconds &&
                    result is TlcResult tlc && Schema.Hash(observed) == Schema.Hash(tlc.Invocation);
            }
            else
            {
                string native = Path.Combine(directory, "validation", "result.json");
                matches &= result is TraceValidationResult trace && trace.TimeoutSeconds == stage.TimeoutSeconds &&
                    File.Exists(native) && Schema.Hash(ProjectInputs.Read<TraceValidationResult>(native)) == Schema.Hash(trace);
            }
        }
        if (!matches) issues.Add(new(ErrorCode.WorkflowStageInconsistent, "TLC resource request, effective settings or invocation differ from the declared stage: " + stage.Id));
        return issues;
    }
}
