using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace TlaLink.Runtime;

public sealed record TraceEvent(long Sequence, string Model, string Step, string Point, string Classification,
    object Connection, object Actor, object Operation, Dictionary<string, object> Observations, long? Guard,
    int Thread, long[] Causes, long[] Predecessors,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Entity = null);

public sealed class TraceHandoff
{
    internal TraceHandoff(long predecessor) { Predecessor = predecessor; }
    internal long Predecessor { get; }
}

public static class TraceRuntime
{
    private sealed class Identity(long value) { public long Value { get; } = value; }
    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<object, Identity> Guards = new();
    private static readonly ConditionalWeakTable<object, Identity> Objects = new();
    private static readonly Dictionary<string, long> Last = [];
    private static readonly Dictionary<int, HashSet<long>> PendingHandoffs = [];
    private static readonly ConcurrentQueue<TraceEvent> Events = new();
    private static string? binding;
    private static long sequence;
    private static long identity;
    private static long objectIdentity;
    private static long operationIdentity;
    private static bool finished;
    public static Action<string, string>? SchedulingHook { get; set; }

    public static void Checkpoint(string step, string position) => SchedulingHook?.Invoke(step, position);

    public static string ObjectIdentity(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.GetType().IsValueType) throw new InvalidOperationException("Object identities require a reference object, not a boxed value.");
        lock (Gate)
        {
            if (finished) throw new InvalidOperationException("Trace was already completed.");
            return "object:" + Objects.GetValue(value, _ => new Identity(++objectIdentity)).Value.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static string NewOperationIdentity()
    {
        lock (Gate)
        {
            if (finished) throw new InvalidOperationException("Trace was already completed.");
            return "call:" + (++operationIdentity).ToString(CultureInfo.InvariantCulture);
        }
    }

    public static long Emit(string bindingId, string model, string step, string point, string classification,
        object connection, object actor, object operation, object? guard, Dictionary<string, object> observations, params long[] causes)
        => EmitCore(bindingId, model, step, point, classification, connection, actor, operation, null, guard, observations, causes);

    public static long EmitEntity(string bindingId, string model, string step, string point, string classification,
        object connection, object actor, object operation, object entity, object? guard, Dictionary<string, object> observations, params long[] causes)
    {
        Validate(entity);
        if (entity is bool) throw new InvalidOperationException("Trace entity identities require integers or strings.");
        return EmitCore(bindingId, model, step, point, classification, connection, actor, operation, entity, guard, observations, causes);
    }

    public static TraceHandoff CaptureHandoff(long predecessor)
    {
        lock (Gate)
        {
            if (finished || predecessor <= 0 || predecessor > sequence)
                throw new InvalidOperationException("Handoffs require an existing event in an unfinished trace.");
            return new(predecessor);
        }
    }

    public static TraceHandoff CaptureHandoff()
    {
        lock (Gate)
        {
            string threadKey = "thread:" + Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture);
            if (finished || !Last.TryGetValue(threadKey, out long predecessor))
                throw new InvalidOperationException("Handoffs require a captured event on the current thread.");
            return new(predecessor);
        }
    }

    public static void AcceptHandoff(TraceHandoff handoff)
    {
        if (handoff is null) throw new ArgumentNullException(nameof(handoff));
        lock (Gate)
        {
            if (finished) throw new InvalidOperationException("Trace was already completed.");
            int thread = Environment.CurrentManagedThreadId;
            if (!PendingHandoffs.TryGetValue(thread, out HashSet<long>? pending))
                PendingHandoffs[thread] = pending = [];
            pending.Add(handoff.Predecessor);
        }
    }

    private static long EmitCore(string bindingId, string model, string step, string point, string classification,
        object connection, object actor, object operation, object? entity, object? guard, Dictionary<string, object> observations, long[] causes)
    {
        Validate(connection); Validate(actor); Validate(operation);
        if (connection is bool || actor is bool || operation is bool)
            throw new InvalidOperationException("Trace identities require integers or strings, not booleans.");
        foreach (object value in observations.Values) Validate(value);
        string operationKey = "operation:" + JsonSerializer.Serialize(new object?[] { connection, actor, operation, entity });
        lock (Gate)
        {
            if (finished) throw new InvalidOperationException("Trace was already completed.");
            if (binding is not null && binding != bindingId) throw new InvalidOperationException("Mixed trace binding manifests.");
            binding = bindingId;
            long? guardId = guard is null ? null : Guards.GetValue(guard, _ => new Identity(++identity)).Value;
            string? guardKey = guardId is null ? null : "guard:" + guardId.Value.ToString(CultureInfo.InvariantCulture);
            HashSet<long> predecessors = [];
            if (Last.TryGetValue(operationKey, out long operationPrevious)) predecessors.Add(operationPrevious);
            if (guardKey is not null && Last.TryGetValue(guardKey, out long guardPrevious)) predecessors.Add(guardPrevious);
            int thread = Environment.CurrentManagedThreadId;
            string threadKey = "thread:" + thread.ToString(CultureInfo.InvariantCulture);
            if (Last.TryGetValue(threadKey, out long threadPrevious)) predecessors.Add(threadPrevious);
            if (causes.Any(c => c <= 0 || c > sequence)) throw new InvalidOperationException("Causal dependencies must name previous trace events.");
            predecessors.UnionWith(causes);
            HashSet<long> explicitCauses = new(causes);
            if (PendingHandoffs.Remove(thread, out HashSet<long>? pending))
            {
                explicitCauses.UnionWith(pending);
                predecessors.UnionWith(pending);
            }
            long current = ++sequence;
            Last[operationKey] = current;
            Last[threadKey] = current;
            if (guardKey is not null) Last[guardKey] = current;
            Events.Enqueue(new(current, model, step, point, classification, connection, actor, operation,
                new(observations), guardId, thread, explicitCauses.Order().ToArray(), predecessors.Order().ToArray(), entity));
            return current;
        }
    }

    public static void Complete(string path)
    {
        lock (Gate)
        {
            if (finished || binding is null) throw new InvalidOperationException("Trace missing or already completed.");
            if (PendingHandoffs.Count != 0) throw new InvalidOperationException("Accepted handoffs must be consumed by a captured event before completion.");
            finished = true;
            Assembly assembly = Assembly.GetEntryAssembly() ?? throw new InvalidOperationException("No entry assembly.");
            string executable = assembly.Location;
            Dictionary<string, string> binaries = [];
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
            {
                if (!string.IsNullOrEmpty(loaded.Location))
                    binaries[loaded.Location] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(loaded.Location))).ToLowerInvariant();
            }
            JsonSerializerOptions json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
            using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(file, new
            {
                SchemaVersion = global::TlaLink.Schema.Version, Kind = "trace", BindingId = binding, Complete = true, EventCount = Events.Count,
                Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
                Executable = executable, Binaries = binaries, Events = Events.ToArray(),
                Boundary = "Loaded assembly hashes and runtime identity; not host authentication or a full OS dependency closure."
            }, json);
        }
    }

    private static void Validate(object value)
    {
        if (value is not (sbyte or byte or short or ushort or int or uint or long or bool or string))
            throw new InvalidOperationException("Trace values must be non-null signed-64-bit-compatible integers, booleans or strings.");
    }
}
