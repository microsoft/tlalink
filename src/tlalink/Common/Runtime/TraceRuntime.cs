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

[JsonConverter(typeof(TraceScalarConverter))]
public sealed record TraceScalar
{
    private TraceScalar(JsonValueKind kind, long integer, bool boolean, string? text, string? wire)
        => (Kind, Integer, Boolean, Text, Wire) = (kind, integer, boolean, text, wire);

    public JsonValueKind Kind { get; }
    public long Integer { get; }
    public bool Boolean { get; }
    public string? Text { get; }
    internal string? Wire { get; }
    public bool IsIdentity => Kind is JsonValueKind.Number or JsonValueKind.String;

    public static TraceScalar From(object value) => value switch
    {
        sbyte or byte or short or ushort or int or uint or long =>
            new(JsonValueKind.Number, Convert.ToInt64(value, CultureInfo.InvariantCulture), false, null, null),
        bool boolean => new(boolean ? JsonValueKind.True : JsonValueKind.False, 0, boolean, null, null),
        string text => new(JsonValueKind.String, 0, false, text, null),
        _ => throw new InvalidOperationException("Trace values must be non-null signed-64-bit-compatible integers, booleans or strings.")
    };

    internal static TraceScalar Read(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out long integer) =>
            new(JsonValueKind.Number, integer, false, null, JsonSerializer.Serialize(value)),
        JsonValueKind.True or JsonValueKind.False =>
            new(value.ValueKind, 0, value.GetBoolean(), null, null),
        JsonValueKind.String => new(JsonValueKind.String, 0, false, value.GetString()!, JsonSerializer.Serialize(value)),
        _ => throw new JsonException("Only non-null signed-64-bit-compatible integers, booleans and strings are trace values.")
    };
}

public sealed class TraceScalarConverter : JsonConverter<TraceScalar>
{
    public override bool HandleNull => true;

    public override TraceScalar Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        return TraceScalar.Read(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, TraceScalar value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("Trace scalars cannot be null.");
        if (value.Wire is string wire) writer.WriteRawValue(wire);
        else if (value.Kind == JsonValueKind.Number) writer.WriteNumberValue(value.Integer);
        else if (value.Kind == JsonValueKind.String) writer.WriteStringValue(value.Text);
        else writer.WriteBooleanValue(value.Boolean);
    }
}

public sealed class TraceArgumentsConverter : JsonConverter<TraceScalar[]>
{
    public override bool HandleNull => true;

    public override TraceScalar[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => JsonSerializer.Deserialize<TraceScalar[]>(ref reader, options)
            ?? throw new JsonException("Present action arguments cannot be null.");

    public override void Write(Utf8JsonWriter writer, TraceScalar[] value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, options);
}

public sealed record TraceEnvelope(int SchemaVersion, string Kind, string BindingId, bool Complete, int EventCount,
    string Runtime, string OS, string Executable, Dictionary<string, string> Binaries, TraceEvent[] Events, string Boundary);

public sealed record TraceEvent(long Sequence, string Model, string Step, string Point, string Classification,
    TraceScalar Connection, TraceScalar Actor, TraceScalar Operation, Dictionary<string, TraceScalar> Observations, long? Guard,
    int Thread, long[] Causes, long[] Predecessors,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TraceScalar? Entity = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(TraceArgumentsConverter))]
    TraceScalar[]? Arguments = null);

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
        => EmitCore(bindingId, model, step, point, classification, connection, actor, operation, null, null, guard, observations, causes);

    public static long EmitEntity(string bindingId, string model, string step, string point, string classification,
        object connection, object actor, object operation, object entity, object? guard, Dictionary<string, object> observations, params long[] causes)
    {
        if (!TraceScalar.From(entity).IsIdentity) throw new InvalidOperationException("Trace entity identities require integers or strings.");
        return EmitCore(bindingId, model, step, point, classification, connection, actor, operation, entity, null, guard, observations, causes);
    }

    public static long EmitArguments(string bindingId, string model, string step, string point, string classification,
        object connection, object actor, object operation, object? entity, object[] arguments,
        object? guard, Dictionary<string, object> observations, params long[] causes)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Length is < 1 or > 8) throw new InvalidOperationException("Action arguments require 1..8 scalar values.");
        foreach (object value in arguments) _ = TraceScalar.From(value);
        if (entity != null && !TraceScalar.From(entity).IsIdentity) throw new InvalidOperationException("Entity identities require integers or strings.");
        return EmitCore(bindingId, model, step, point, classification, connection, actor, operation, entity,
            arguments.ToArray(), guard, observations, causes);
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
        object connection, object actor, object operation, object? entity, object[]? arguments,
        object? guard, Dictionary<string, object> observations, long[] causes)
    {
        TraceScalar capturedConnection = TraceScalar.From(connection), capturedActor = TraceScalar.From(actor),
            capturedOperation = TraceScalar.From(operation);
        if (!capturedConnection.IsIdentity || !capturedActor.IsIdentity || !capturedOperation.IsIdentity)
            throw new InvalidOperationException("Trace identities require integers or strings, not booleans.");
        Dictionary<string, TraceScalar> capturedObservations = observations.ToDictionary(pair => pair.Key, pair => TraceScalar.From(pair.Value));
        TraceScalar? capturedEntity = entity is null ? null : TraceScalar.From(entity);
        TraceScalar[]? capturedArguments = arguments?.Select(TraceScalar.From).ToArray();
        string operationKey = "operation:" + JsonSerializer.Serialize(new object?[] {
            capturedConnection, capturedActor, capturedOperation, capturedEntity });
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
            Events.Enqueue(new(current, model, step, point, classification, capturedConnection, capturedActor, capturedOperation,
                capturedObservations, guardId, thread, explicitCauses.Order().ToArray(), predecessors.Order().ToArray(), capturedEntity, capturedArguments));
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
            JsonSerializer.Serialize(file, new TraceEnvelope(global::TlaLink.Schema.Version, "trace", binding, true, Events.Count,
                RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription, executable, binaries, Events.ToArray(),
                "Loaded assembly hashes and runtime identity; not host authentication or a full OS dependency closure."), json);
        }
    }
}
