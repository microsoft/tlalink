using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TlaLink;

public static class TraceValidation
{
    public static object Validate(string manifestPath, string tracePath, string configPath, string modelPath,
        string module, List<string> variables, List<ModelBinding> models, Dictionary<string, string> modelPins,
        string jar, string output, int timeout, string init, string next, string execution, TlcResources? requestedResources = null)
    {
        TlcResources resources = TlcResources.Effective("trace", requestedResources);
        if (variables.Count == 0) throw new InputException(ErrorCode.InvalidTrace, "Model has no state variables.");
        if (!Regex.IsMatch(init, @"^[A-Za-z_]\w*$") || !Regex.IsMatch(next, @"^[A-Za-z_]\w*$"))
            throw new InputException(ErrorCode.InvalidTrace, "Init/next must name model operators.");
        InstrumentManifest manifest = Instrumentation.ReadManifest(manifestPath);
        Dictionary<string, string> pins = new(manifest.Fingerprints, FilePaths.Comparer);
        JavaInstallation java = JavaToolchain.Resolve();
        foreach (var pin in java.Fingerprints) Files.Pin(pins, pin.Key, pin.Value);
        foreach (var pair in modelPins) Files.Pin(pins, pair.Key, pair.Value);
        foreach (var pair in manifest.GeneratedFiles) Files.Pin(pins, pair.Key, pair.Value);
        foreach (string path in new[] { manifestPath, tracePath, configPath, modelPath, jar })
            Files.Pin(pins, FilePaths.Full(path), Files.Hash(path));
        Execution.Validate(execution, manifestPath, tracePath, pins);
        CheckPins(pins);
        using JsonDocument trace = JsonDocument.Parse(File.ReadAllText(tracePath));
        JsonElement root = trace.RootElement;
        Files.RequireSchema(root, "trace");
        if (!root.GetProperty("complete").GetBoolean() ||
            root.GetProperty("binding_id").GetString() != manifest.BindingId)
            throw new InputException(ErrorCode.InvalidTrace, "Trace is incomplete or belongs to a different binding manifest.");
        JsonElement[] events = root.GetProperty("events").EnumerateArray().ToArray();
        if (events.Length == 0 || events.Length > 1000 || root.GetProperty("event_count").GetInt32() != events.Length)
            throw new InputException(ErrorCode.InvalidTrace, "Trace count invalid or exceeds the explicit 1000-event pilot limit.");
        foreach (JsonProperty binary in root.GetProperty("binaries").EnumerateObject())
            Files.Pin(pins, binary.Name, binary.Value.GetString() ?? throw new InputException(ErrorCode.InvalidTrace, "Invalid binary hash."));
        CheckPins(pins);
        List<string> matches = [];
        List<string> prerequisites = [];
        HashSet<string> actionNames = [];
        Dictionary<string, long> causalLast = [];
        Dictionary<string, long> observationGuards = [];
        string Tuple() => "<<" + string.Join(",", variables) + ">>";
        for (int index = 0; index < events.Length; index++)
        {
            JsonElement entry = events[index];
            long sequence = entry.GetProperty("sequence").GetInt64();
            if (sequence != index + 1) throw new InputException(ErrorCode.InvalidTrace, "Events require unique contiguous sequence identities.");
            string model = entry.GetProperty("model").GetString()!;
            string step = entry.GetProperty("step").GetString()!;
            EventBinding binding = manifest.Events.SingleOrDefault(e => e.Model == model && e.Step == step)
                ?? throw new InputException(ErrorCode.InvalidTrace, "Unknown event; events are never silently ignored.");
            if (entry.GetProperty("point").GetString() != binding.Point ||
                entry.GetProperty("classification").GetString() != binding.Classification)
                throw new InputException(ErrorCode.InvalidTrace, "Trace event metadata disagrees with binding.");
            if (binding.Classification is not ("transition" or "stutter")) throw new InputException(ErrorCode.InvalidTrace, "Unresolved event.");
            bool hasEntity = entry.TryGetProperty("entity", out JsonElement entity);
            if (hasEntity != binding.Entity || hasEntity && entity.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
                throw new InputException(ErrorCode.InvalidTrace, "Event entity capture disagrees with binding.");
            string? entityValue = hasEntity ? TlaValue(entity) : null;
            if (binding.Classification == "transition" && !models.Any(m => m.Model == model && m.Tag.Kind == "step" &&
                m.Tag.Identity == step && m.Endpoint.Name == binding.Operator && m.Arity == binding.Arity))
                throw new InputException(ErrorCode.InvalidTrace, "Model action mapping disagrees with manifest.");
            long[] predecessors = entry.GetProperty("predecessors").EnumerateArray().Select(e => e.GetInt64()).ToArray();
            if (predecessors.Distinct().Count() != predecessors.Length || predecessors.Any(p => p <= 0 || p >= sequence))
                throw new InputException(ErrorCode.InvalidTrace, "Invalid causal predecessor.");
            string operationKey = "operation:" + JsonSerializer.Serialize(new object?[] {
                entry.GetProperty("connection"), entry.GetProperty("actor"), entry.GetProperty("operation"), hasEntity ? entity : null });
            long? guard = entry.GetProperty("guard").ValueKind == JsonValueKind.Null ? null : entry.GetProperty("guard").GetInt64();
            if (guard <= 0 || guard is null && binding.Observations.Count != 0)
                throw new InputException(ErrorCode.InvalidTrace, "Invalid or missing source guard identity.");
            string? guardKey = guard is null ? null : "guard:" + guard.Value.ToString(CultureInfo.InvariantCulture);
            int thread = entry.GetProperty("thread").GetInt32();
            if (thread <= 0) throw new InputException(ErrorCode.InvalidTrace, "Invalid managed thread identity.");
            string threadKey = "thread:" + thread.ToString(CultureInfo.InvariantCulture);
            HashSet<long> expected = [];
            if (causalLast.TryGetValue(operationKey, out long lastOperation)) expected.Add(lastOperation);
            if (guardKey is not null && causalLast.TryGetValue(guardKey, out long lastGuard)) expected.Add(lastGuard);
            if (causalLast.TryGetValue(threadKey, out long lastThread)) expected.Add(lastThread);
            long[] causes = entry.GetProperty("causes").EnumerateArray().Select(c => c.GetInt64()).ToArray();
            if (causes.Distinct().Count() != causes.Length || causes.Any(c => c <= 0 || c >= sequence))
                throw new InputException(ErrorCode.InvalidTrace, "Invalid explicit causal dependency.");
            expected.UnionWith(causes);
            if (!expected.SetEquals(predecessors)) throw new InputException(ErrorCode.InvalidTrace, "Causal predecessors disagree with operation/guard order.");
            causalLast[operationKey] = sequence;
            if (guardKey is not null) causalLast[guardKey] = sequence;
            causalLast[threadKey] = sequence;
            prerequisites.Add("{" + string.Join(",", predecessors) + "}");
            string actor = TlaValue(entry.GetProperty("actor")), operation = TlaValue(entry.GetProperty("operation"));
            _ = TlaValue(entry.GetProperty("connection"));
            if (new[] { "actor", "operation", "connection" }.Any(name =>
                entry.GetProperty(name).ValueKind is not (JsonValueKind.Number or JsonValueKind.String)))
                throw new InputException(ErrorCode.InvalidTrace, "Trace identities require integers or strings.");
            string action = binding.Classification == "stutter" ? $"UNCHANGED {Tuple()}" :
                binding.Operator + (binding.Arity switch {
                    0 => "", 1 => $"({actor})", 2 => $"({actor},{operation})",
                    3 when binding.Entity => $"({actor},{operation},{entityValue})",
                    _ => throw new InputException(ErrorCode.InvalidTrace, "Unsupported action arity or missing entity.") });
            if (binding.Classification == "transition") actionNames.Add(binding.Operator);
            JsonElement observations = entry.GetProperty("observations");
            if (observations.EnumerateObject().Count() != binding.Observations.Count)
                throw new InputException(ErrorCode.InvalidTrace, "Missing or extra typed observations.");
            List<string> predicates = [action];
            if (binding.Classification == "transition") predicates.Add(next);
            foreach (Observation observation in binding.Observations)
            {
                if (observation.Arity is < 0 or > 1 || observation.Arity == 1 && !binding.Entity)
                    throw new InputException(ErrorCode.InvalidTrace, "Unsupported observation arity or missing entity identity.");
                string guardedState = JsonSerializer.Serialize(new { model, Connection = entry.GetProperty("connection"), observation.Id,
                    Entity = observation.Arity == 1 ? entityValue : null });
                if (observationGuards.TryGetValue(guardedState, out long previousGuard) && previousGuard != guard)
                    throw new InputException(ErrorCode.InvalidTrace, "One model state/connection/entity was observed through different source guards.");
                observationGuards[guardedState] = guard!.Value;
                if (!models.Any(m => m.Model == model && m.Tag.Kind == "state" && m.Tag.Identity == observation.Id &&
                    m.Endpoint.Name == observation.Operator && m.Arity == observation.Arity))
                    throw new InputException(ErrorCode.InvalidTrace, "Model observation mapping disagrees with manifest.");
                JsonElement value = observations.GetProperty(observation.Id);
                bool valid = observation.Type switch
                {
                    "int" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                    "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "string" => value.ValueKind == JsonValueKind.String,
                    _ => false
                };
                if (!valid) throw new InputException(ErrorCode.InvalidTrace, "Observed value has incorrect type.");
                string expression = observation.Operator + (observation.Arity == 1 ? $"({entityValue})" : "");
                string observed = binding.Point == "invoke" ? expression : $"({expression})'";
                predicates.Add($"{observed} = {TlaValue(value)}");
            }
            matches.Add("(" + string.Join(" /\\ ", predicates.Select(p => "(" + p + ")")) + ")");
        }
        HashSet<string> connections = events.Select(e => TlaValue(e.GetProperty("connection"))).ToHashSet();
        if (connections.Count != 1) throw new InputException(ErrorCode.InvalidTrace, "Pilot validates one model connection per trace.");
        string actors = "{" + string.Join(",", events.Select(e => TlaValue(e.GetProperty("actor"))).Distinct()) + "}";
        string operations = "{" + string.Join(",", events.Select(e => TlaValue(e.GetProperty("operation"))).Distinct()) + "}";
        string entities = "{" + string.Join(",", events.Where(e => e.TryGetProperty("entity", out _))
            .Select(e => TlaValue(e.GetProperty("entity"))).Distinct()) + "}";
        List<string> hidden = [];
        foreach (ModelBinding binding in models.Where(m => m.Tag.Kind == "step" && m.Tag.Options.GetValueOrDefault("visibility") == "hidden"))
        {
            if (actionNames.Contains(binding.Endpoint.Name)) throw new InputException(ErrorCode.InvalidTrace, "A model action cannot be both hidden and observable.");
            hidden.Add(binding.Arity switch
            {
                0 => binding.Endpoint.Name,
                1 => $"(\\E a \\in {actors}: {binding.Endpoint.Name}(a))",
                2 => $"(\\E a \\in {actors}, op \\in {operations}: {binding.Endpoint.Name}(a,op))",
                3 when entities != "{}" => $"(\\E a \\in {actors}, op \\in {operations}, entity \\in {entities}: {binding.Endpoint.Name}(a,op,entity))",
                _ => throw new InputException(ErrorCode.InvalidTrace, "Unsupported hidden action arity.")
            });
        }
        string code = $$"""
            ---------------- MODULE TlaLinkTraceCheck ----------------
            EXTENDS {{module}}, Naturals, FiniteSets
            VARIABLE tlalinkSeen
            TLPrerequisites == <<{{string.Join(",", prerequisites)}}>>
            TLMatch(i) == CASE {{string.Join(" [] ", matches.Select((m, i) => $"i = {i + 1} -> {m}"))}}
            TLHidden == {{(hidden.Count == 0 ? "FALSE" : string.Join(" \\/ ", hidden.Select(h => "(" + h + ")")))}}
            TLInit == {{init}} /\ tlalinkSeen = {}
            TLNext ==
              \/ /\ {{next}} /\ TLHidden /\ UNCHANGED tlalinkSeen
              \/ \E i \in 1..{{events.Length}}:
                   /\ i \notin tlalinkSeen
                   /\ TLPrerequisites[i] \subseteq tlalinkSeen
                   /\ TLMatch(i)
                   /\ tlalinkSeen' = tlalinkSeen \cup {i}
            TLSpec == TLInit /\ [][TLNext]_<<{{string.Join(",", variables)}},tlalinkSeen>>
            TLNotMatched == Cardinality(tlalinkSeen) < {{events.Length}}
            =========================================================
            """;
        string config = File.ReadAllText(configPath);
        if (Regex.IsMatch(config, @"(?m)^\s*(?:VIEW|SYMMETRY|CONSTRAINTS?|ACTION_CONSTRAINTS?)\b"))
            throw new InputException(ErrorCode.InvalidTrace, "Trace checking requires the unreduced model, without graph restrictions.");
        List<string> lines = [];
        foreach (string line in config.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("\\*", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(trimmed, @"^(?:SPECIFICATION|INVARIANTS?|PROPERT(?:Y|IES)|CHECK_DEADLOCK)\s+[A-Za-z_][\w \t!]*$")) continue;
            if (!trimmed.StartsWith("CONSTANTS ", StringComparison.Ordinal) && !trimmed.StartsWith("CONSTANT ", StringComparison.Ordinal))
                throw new InputException(ErrorCode.InvalidTrace, "Pilot supports single-line CONSTANT(S) configuration directives only; unsupported config is not truncated.");
            lines.Add(trimmed);
        }
        lines.AddRange(["SPECIFICATION TLSpec", "INVARIANT TLNotMatched", "CHECK_DEADLOCK FALSE"]);
        output = FilePaths.Full(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new InputException(ErrorCode.InvalidTrace, "Trace output must be a new directory.");
        Directory.CreateDirectory(output);
        Dictionary<string, string> archived = [];
        string closure = Directory.CreateDirectory(Path.Combine(output, "inputs")).FullName;
        foreach (var pin in pins.Where(p => Path.IsPathRooted(p.Key)))
        {
            string file = Path.Combine(closure, pin.Value + Path.GetExtension(pin.Key));
            if (!File.Exists(file)) File.Copy(pin.Key, file);
            if (Files.Hash(file) != pin.Value) throw new InputException(ErrorCode.InvalidTrace, "Input changed during trace archival.");
            archived[pin.Key] = Path.GetFileName(file);
        }
        Files.WriteNew(Path.Combine(closure, "archive.json"), new { SchemaVersion = Schema.Version, Kind = "trace-input-archive", Fingerprints = pins, Files = archived });
        foreach (string path in pins.Keys.Where(p => Path.IsPathRooted(p) && Path.GetExtension(p) == ".tla"))
        {
            string destination = Path.Combine(output, Path.GetFileName(path));
            if (File.Exists(destination)) throw new InputException(ErrorCode.InvalidTrace, "Ambiguous imported module basenames.");
            File.Copy(Path.Combine(closure, archived[path]), destination);
        }
        File.WriteAllText(Path.Combine(output, "TlaLinkTraceCheck.tla"), code, new UTF8Encoding(false));
        File.WriteAllLines(Path.Combine(output, "trace.cfg"), lines);
        string[] arguments = ["-XX:+UseParallelGC", resources.HeapArgument, "-cp", Path.Combine(closure, archived[FilePaths.Full(jar)]), "tlc2.TLC",
            "-workers", resources.WorkerArgument, "-seed", "42", "-fp", "0", "-config", "trace.cfg", "-metadir", "states", "TlaLinkTraceCheck"];
        ProcessStartInfo start = new(java.Java) { WorkingDirectory = output, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InputException(ErrorCode.InvalidTrace, "Could not start TLC.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        bool timedOut = !process.WaitForExit(checked(timeout * 1000));
        if (timedOut) { process.Kill(entireProcessTree: true); process.WaitForExit(); }
        string log = stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(output, "TLC.log"), log);
        CheckPins(pins);
        bool witness = !timedOut && process.ExitCode == 12 && log.Contains("Invariant TLNotMatched is violated.", StringComparison.Ordinal);
        bool rejected = !timedOut && process.ExitCode == 0 &&
            log.Contains("Model checking completed. No error has been found.", StringComparison.Ordinal) &&
            Regex.IsMatch(log, @"(?m)^\d+ states generated, \d+ distinct states found, 0 states left on queue\.");
        object result = new
        {
            SchemaVersion = Schema.Version, Kind = "trace-validation", Command = "validate-trace", Status = witness ? "conforms" : rejected ? "rejected" : "incomplete",
            EventCount = events.Length, BindingId = manifest.BindingId, ExitCode = process.ExitCode, TimedOut = timedOut,
            RequestedResources = requestedResources, Resources = resources, TimeoutSeconds = timeout,
            Invocation = new { Executable = java.Java, Arguments = arguments, WorkingDirectory = output },
            Mapping = new { Init = init, Next = next, Hidden = hidden },
            Diagnostics = rejected
                ? new[] { new Issue(ErrorCode.TraceRejected, "The complete recorded execution has no witness in the selected model and hidden-step assumptions.") }
                : Array.Empty<Issue>(),
            WrapperSHA256 = Files.Hash(Path.Combine(output, "TlaLinkTraceCheck.tla")),
            ConfigSHA256 = Files.Hash(Path.Combine(output, "trace.cfg")),
            Fingerprints = pins, LogSHA256 = Files.Hash(Path.Combine(output, "TLC.log")),
            Scope = "Existence of one causal-order-compatible finite trace in configured Init/Next bounds. Explicit hidden steps only; no liveness, general refinement, collision-free enumeration or authenticated execution claim."
        };
        Files.WriteNew(Path.Combine(output, "result.json"), result);
        return result;
    }

    private static void CheckPins(Dictionary<string, string> pins)
    {
        foreach (var pin in pins.Where(p => FilePaths.IsAbsolute(p.Key)))
            if (!FilePaths.IsNativeAbsolute(pin.Key) || !File.Exists(pin.Key) || Files.Hash(pin.Key) != pin.Value)
                throw new InputException(ErrorCode.InvalidTrace, $"Current input mismatch: {pin.Key}");
    }

    private static string TlaValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt64(out long number) => number.ToString(CultureInfo.InvariantCulture),
        JsonValueKind.True => "TRUE",
        JsonValueKind.False => "FALSE",
        JsonValueKind.String => Quote(value.GetString()!),
        _ => throw new InputException(ErrorCode.InvalidTrace, "Only signed-64-bit-compatible integers, booleans and strings are trace values.")
    };

    private static string Quote(string value)
    {
        if (value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new InputException(ErrorCode.InvalidTrace, "Unsupported control character in trace value.");
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
    }
}
