using System.Globalization;
using System.Text.Json;

namespace TlaLink;

public sealed record RetentionPin(string Path, string Reason);
public sealed record EvidenceRetention(int SchemaVersion, string Kind, List<RetentionPin> Pins, List<string> ReviewProjects,
    string? Catalog = null);
public sealed record CatalogInput(string Path, string Sha256);
public sealed record ProtectedConsumer(string Path, string Reason, Dictionary<string, string> Files,
    List<CatalogInput> Inputs, List<string> References);
public sealed record ProtectedConsumerCatalog(int SchemaVersion, string Kind, string Store,
    Dictionary<string, string> ProducerFingerprints, List<ProtectedConsumer> Consumers);
public sealed record GcInputUse(string Consumer, string ExpectedSha256);
public sealed record GcInputChange(string Path, string Status, string? ActualSha256, List<GcInputUse> Consumers);
public sealed record GcRecheck(string Path, string Kind, List<string> ChangedInputs, List<string> AffectedBy,
    string Scope, List<string> Actions);
public sealed record GcEntry(string Path, string Status, long? LogicalBytes, List<string> Reasons,
    List<string> Dependencies, bool RecheckRequired);
public sealed record GcCounts(int Units, int Candidates, int Retained, int Protected,
    long CandidateLogicalBytes, int ChangedInputs, int Rechecks);
public sealed record GcPathState(FileAttributes Attributes, long Length, DateTime Modified);
public sealed record GcPlan(int SchemaVersion, string Kind, string Status, string Store, string Retention,
    DateTimeOffset PlannedAt, int GraceHours, bool ReadOnly, bool DeletionAuthorized,
    GcCounts Counts, List<GcEntry> Entries, List<string> ProtectedPaths, List<GcInputChange> ChangedInputs,
    List<GcRecheck> Recheck, Dictionary<string, string> Fingerprints, Dictionary<string, GcPathState> Snapshot,
    Dictionary<string, string> FileInputs, Dictionary<string, string> ResolvedInputs,
    List<Issue> Diagnostics, List<string> Boundaries);

public static partial class EvidenceGarbageCollection
{
    public static int Run(CliOptions options)
    {
        try { return RunCore(options); }
        catch (ArgumentException error) { throw new InputException(ErrorCode.InvalidRetention, error.Message); }
    }

    private static int RunCore(CliOptions options)
    {
        if (options.Has("apply")) return Apply(options);
        if (options.Has("exclusive")) throw new InputException(ErrorCode.InvalidRetention, "--exclusive requires --apply.");
        if (!int.TryParse(options.Get("grace-hours", "24"), NumberStyles.None, CultureInfo.InvariantCulture, out int hours) ||
            hours is < 0 or > 87600)
            throw new InputException(ErrorCode.InvalidRetention, "Grace hours must be 0..87600.");
        string store = FilePaths.Full(options.Required("store"));
        string? destination = options.Optional("output") is string output ? FilePaths.Full(output) : null;
        if (destination != null)
        {
            if (Inside(store, destination)) throw new InputException(ErrorCode.InvalidRetention, "GC output must be outside the inspected store.");
            NoLinks(Path.GetDirectoryName(destination)!);
        }
        GcPlan plan = Plan(store, options.Required("pins"), hours, DateTimeOffset.UtcNow);
        if (destination != null) Files.WriteNew(destination, plan);
        if (options.Get("format", "json") == "text")
        {
            Console.WriteLine($"gc-plan: {plan.Status}; read-only; deletion not authorized");
            Console.WriteLine(JsonSerializer.Serialize(plan.Counts, Files.Json));
            foreach (GcEntry entry in plan.Entries)
                Console.WriteLine($"{entry.Status}: {entry.Path} - {string.Join("; ", entry.Reasons)}");
            foreach (GcRecheck recheck in plan.Recheck)
                Console.WriteLine($"recheck {recheck.Kind}: {recheck.Path}; inputs: {string.Join(", ", recheck.ChangedInputs)}");
            foreach (Issue issue in plan.Diagnostics) Console.WriteLine($"{issue.Code}: {issue.Message}");
        }
        else ProjectWorkflow.Render(plan, options);
        return plan.Status == "ready" ? 0 : 2;
    }

    internal static GcPlan Plan(string store, string retention, int graceHours, DateTimeOffset now)
    {
        store = FilePaths.Full(store);
        retention = FilePaths.Full(retention);
        if (!Directory.Exists(store) || FilePaths.Comparer.Equals(store, Path.GetPathRoot(store)))
            throw new InputException(ErrorCode.InvalidRetention, "Store must be an existing directory, not a filesystem root.");
        NoLinks(store);
        NoLinks(retention);
        var state = new Scan(store);
        JsonElement policyJson = state.Read(retention);
        Files.RequireSchema(policyJson, "evidence-retention");
        EvidenceRetention policy = policyJson.Deserialize<EvidenceRetention>(ProjectInputs.Json)!;
        if (policy.Pins.Any(p => p is null || string.IsNullOrWhiteSpace(p.Path) || string.IsNullOrWhiteSpace(p.Reason)) ||
            policy.ReviewProjects.Any(string.IsNullOrWhiteSpace))
            throw new InputException(ErrorCode.InvalidRetention, "Retention pins require paths/reasons and review projects require paths.");
        string physicalStore = OperatingSystem.IsWindows() ? DirectoryLeases.ResolvePath(store) : store;
        var catalog = new Dictionary<string, ProtectedConsumer>(FilePaths.Comparer);
        string? catalogPath = policy.Catalog is null ? null : FilePaths.Full(policy.Catalog, Path.GetDirectoryName(retention)!);
        if (catalogPath != null)
            state.Try(catalogPath, () => {
                if (Inside(store, catalogPath))
                    throw new InputException(ErrorCode.InvalidRetention, "Protected-consumer catalogs must be outside their store.");
                JsonElement document = state.Read(catalogPath);
                Files.RequireSchema(document, "protected-consumer-catalog");
                ProtectedConsumerCatalog exported = document.Deserialize<ProtectedConsumerCatalog>(ProjectInputs.Json)!;
                if (!FilePaths.Comparer.Equals(FilePaths.Full(exported.Store, Path.GetDirectoryName(catalogPath)!), store) ||
                    exported.ProducerFingerprints.Count == 0)
                    throw new InputException(ErrorCode.InvalidRetention, "Catalog store/producer identity is invalid.");
                foreach (var producer in exported.ProducerFingerprints)
                {
                    string source = FilePaths.Full(producer.Key, Path.GetDirectoryName(catalogPath)!);
                    NoLinks(source);
                    if (!Hash(producer.Value) || Files.Hash(source) != producer.Value)
                        throw new InputException(ErrorCode.InvalidRetention, "Catalog producer changed: " + source);
                    state.DocumentHashes.Add(source, producer.Value);
                }
                foreach (ProtectedConsumer item in exported.Consumers)
                {
                    if (item is null || string.IsNullOrWhiteSpace(item.Path) || string.IsNullOrWhiteSpace(item.Reason) ||
                        item.Files.Count == 0 || item.Inputs.Any(i => i is null || !FilePaths.IsNativeAbsolute(i.Path) || !Hash(i.Sha256)) ||
                        item.References.Any(p => !FilePaths.IsNativeAbsolute(p)))
                        throw new InputException(ErrorCode.InvalidRetention, "Malformed protected consumer.");
                    string path = FilePaths.Full(item.Path, Path.GetDirectoryName(catalogPath)!);
                    if (!FilePaths.Comparer.Equals(Path.GetDirectoryName(path), store) || !catalog.TryAdd(path, item))
                        throw new InputException(ErrorCode.InvalidRetention, "Catalog consumers must be distinct immediate store children.");
                }
            });
        var unitPaths = new Dictionary<string, Unit>(FilePaths.Comparer);
        state.Observe(store);
        foreach (string child in Directory.EnumerateFileSystemEntries(store).Order(FilePaths.Comparer))
        {
            FileAttributes attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                state.Block(child, "Reparse points are not traversed.");
                continue;
            }
            if ((attributes & FileAttributes.Directory) != 0)
                unitPaths.Add(FilePaths.Full(child), new Unit(FilePaths.Full(child)));
            else
            {
                state.Observe(child);
                if (!FilePaths.Comparer.Equals(child, retention))
                {
                    state.ProtectedPaths.Add(child);
                    if (Path.GetExtension(child).Equals(".json", StringComparison.OrdinalIgnoreCase) && !catalog.ContainsKey(child))
                        state.Block(child, "Loose JSON has no workflow ownership/reference context.");
                }
            }
            if (unitPaths.Count > 10000) throw new InputException(ErrorCode.InvalidRetention, "Store exceeds 10000 units; select a narrower store.");
        }
        Unit? Owner(string path) => unitPaths.GetValueOrDefault(FilePaths.Full(
            Path.GetRelativePath(store, path).Split(Path.DirectorySeparatorChar)[0], store));
        var consumers = new Dictionary<string, Consumer>(FilePaths.Comparer);
        var inputs = new Dictionary<string, List<GcInputUse>>(FilePaths.Comparer);
        Consumer ConsumerFor(string path, string kind)
        {
            if (!consumers.TryGetValue(path, out Consumer? consumer))
                consumers.Add(path, consumer = new Consumer(path, kind));
            return consumer;
        }
        void Reference(string path, Consumer? consumer, string? rootReason = null)
        {
            if (!Inside(store, path) && (File.Exists(path) || Directory.Exists(path)) && OperatingSystem.IsWindows())
            {
                string resolved = DirectoryLeases.ResolvePath(path);
                if (Inside(physicalStore, resolved))
                    path = FilePaths.Full(Path.GetRelativePath(physicalStore, resolved), store);
            }
            if (!Inside(store, path)) return;
            Unit? owner = Owner(path);
            if (owner == null)
            {
                if (catalog.ContainsKey(path))
                {
                    if (consumer != null && !FilePaths.Comparer.Equals(consumer.Path, path)) consumer.Dependencies.Add(path);
                    return;
                }
                if (state.ProtectedPaths.Contains(path) && File.Exists(path)) return;
                state.Block(path, "Reference inside the store has no managed workflow unit.");
                return;
            }
            if (consumer != null && !FilePaths.Comparer.Equals(consumer.Path, owner.Path))
                consumer.Dependencies.Add(owner.Path);
            if (rootReason != null) owner.Reasons.Add(rootReason);
        }
        void Input(string path, string hash, Consumer consumer)
        {
            if (!Hash(hash)) throw new InputException(ErrorCode.InvalidRetention, "Invalid fingerprint hash: " + path);
            if (!inputs.TryGetValue(path, out List<GcInputUse>? uses)) inputs.Add(path, uses = []);
            uses.Add(new(consumer.Path, hash));
            Reference(path, consumer);
        }
        void Review(JsonElement json, string projectDirectory, Consumer consumer)
        {
            Files.RequireSchema(json, "review-decision");
            ReviewDecision review = json.Deserialize<ReviewDecision>(ProjectInputs.Json)!;
            if (review.Disposition is not ("accepted" or "rejected" or "pending") || review.Fingerprints.Count == 0 ||
                review.Evidence.Count == 0 || review.Evidence.Any(string.IsNullOrWhiteSpace) ||
                review.Evidence.Any(p => !review.Fingerprints.ContainsKey(p)))
                throw new InputException(ErrorCode.InvalidRetention, "Malformed review evidence/fingerprints.");
            foreach (var pin in review.Fingerprints)
                Input(FilePaths.Full(pin.Key, projectDirectory), pin.Value, consumer);
        }
        if (Inside(store, retention) && Owner(retention) != null)
            Reference(retention, null, "retention manifest");
        void Protected(string path, List<string> files, ProtectedConsumer item, Consumer consumer)
        {
            string baseDirectory = Directory.Exists(path) ? path : store;
            var declared = new Dictionary<string, string>(FilePaths.Comparer);
            foreach (var file in item.Files)
            {
                string full = LocalArtifact(baseDirectory, file.Key);
                if (!Hash(file.Value) || !declared.TryAdd(full, file.Value))
                    throw new InputException(ErrorCode.InvalidRetention, "Invalid protected-consumer file inventory.");
            }
            if (files.Count != declared.Count || files.Any(f => !declared.ContainsKey(f)))
                throw new InputException(ErrorCode.InvalidRetention, "Protected consumer file set changed: " + path);
            foreach (string file in files)
            {
                if (Files.Hash(file) != declared[file])
                    throw new InputException(ErrorCode.InvalidRetention, "Protected consumer bytes changed: " + file);
                Files.Pin(state.DocumentHashes, file, declared[file]);
            }
            foreach (CatalogInput input in item.Inputs) Input(FilePaths.Full(input.Path), input.Sha256, consumer);
            foreach (string reference in item.References) Reference(FilePaths.Full(reference), consumer);
            Reference(path, null, "protected catalog consumer: " + item.Reason);
        }
        foreach (var item in catalog.Where(p => !Directory.Exists(p.Key)))
            state.Try(item.Key, () => {
                NoLinks(item.Key);
                state.Observe(item.Key);
                Protected(item.Key, [item.Key], item.Value, ConsumerFor(item.Key, "protected-consumer"));
            });
        foreach (Unit unit in unitPaths.Values)
        {
            Consumer consumer = ConsumerFor(unit.Path, "run");
            state.Try(unit.Path, () => {
                Tree tree = state.Walk(unit.Path);
                List<string> files = tree.Files;
                unit.LogicalBytes = files.Sum(f => state.Stamps[f].Length);
                unit.Latest = tree.Latest;
                if (catalog.TryGetValue(unit.Path, out ProtectedConsumer? exportedConsumer))
                {
                    Protected(unit.Path, files, exportedConsumer, consumer);
                    unit.Protected = true;
                    unit.Recognized = true;
                    return;
                }
                string record = Path.Combine(unit.Path, "run.json");
                if (!files.Contains(record, FilePaths.Comparer))
                    throw new InputException(ErrorCode.InvalidRetention, "No completed workflow receipt; unknown or potentially active archive.");
                JsonElement json = state.Read(record);
                Files.RequireSchema(json, "analysis-run");
                WorkflowRun run = json.Deserialize<WorkflowRun>(ProjectInputs.Json)!;
                if (run.ExecutionStatus != "completed" || run.Stages.Count == 0 || run.Fingerprints.Count == 0 ||
                    run.Artifacts.Count == 0 || !FilePaths.IsNativeAbsolute(run.ProjectDirectory) ||
                    run.Stages.Any(s => s is null || s.ExecutionStatus != "completed" || s.Outcome is not ("passed" or "rejected")))
                    throw new InputException(ErrorCode.InvalidRetention, "Unfinished or malformed workflow archive is protected.");
                var artifactPaths = new HashSet<string>(FilePaths.Comparer);
                foreach (var artifact in run.Artifacts)
                {
                    string path = LocalArtifact(unit.Path, artifact.Key);
                    if (!Hash(artifact.Value) || !artifactPaths.Add(path) || !files.Contains(path, FilePaths.Comparer))
                        throw new InputException(ErrorCode.InvalidRetention, "Missing, duplicate or malformed artifact: " + artifact.Key);
                }
                foreach (WorkflowStageResult stage in run.Stages)
                    if (!Hash(stage.Sha256) || !run.Artifacts.TryGetValue(stage.Evidence, out string? hash) ||
                        !hash.Equals(stage.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InputException(ErrorCode.InvalidRetention, "Stage evidence is not in the artifact closure.");
                foreach (var pin in run.Fingerprints)
                {
                    if (!FilePaths.IsAbsolute(pin.Key))
                    {
                        if (pin.Key.StartsWith("library:", StringComparison.Ordinal))
                        {
                            if (!Hash(pin.Value)) throw new InputException(ErrorCode.InvalidRetention, "Invalid SANY library fingerprint: " + pin.Key);
                            continue;
                        }
                        if (!pin.Key.StartsWith("runtime:", StringComparison.Ordinal) && !pin.Key.StartsWith("tool:", StringComparison.Ordinal))
                            throw new InputException(ErrorCode.InvalidRetention, "Unknown non-file fingerprint: " + pin.Key);
                        continue;
                    }
                    if (!FilePaths.IsNativeAbsolute(pin.Key))
                        throw new InputException(ErrorCode.InvalidRetention, "Foreign fingerprint root cannot be traced: " + pin.Key);
                    if (!Hash(pin.Value) || !files.Contains(Path.Combine(unit.Path, "inputs", pin.Value), FilePaths.Comparer))
                        throw new InputException(ErrorCode.InvalidRetention, "Missing archived fingerprint input: " + pin.Key);
                    Input(FilePaths.Full(pin.Key), pin.Value, consumer);
                }
                foreach (string file in files.Where(f => Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                    !FilePaths.Comparer.Equals(f, record) && !Inside(Path.Combine(unit.Path, "inputs"), f)))
                {
                    JsonElement document = state.Read(file);
                    if (document.ValueKind != JsonValueKind.Object || !document.TryGetProperty("kind", out JsonElement kind)) continue;
                    if (kind.ValueKind != JsonValueKind.String) throw new InputException(ErrorCode.InvalidRetention, "Invalid document kind: " + file);
                    if (kind.GetString() == "review-decision") Review(document, run.ProjectDirectory, consumer);
                    else if (kind.GetString() == "acceptance-result")
                    {
                        Files.RequireSchema(document, "acceptance-result");
                        if (!document.TryGetProperty("project", out JsonElement project) || project.ValueKind != JsonValueKind.String ||
                            project.GetString() != run.Project ||
                            !document.TryGetProperty("reviews", out JsonElement reviews) || reviews.ValueKind != JsonValueKind.Array)
                            throw new InputException(ErrorCode.InvalidRetention, "Acceptance review context is missing: " + file);
                        foreach (JsonElement item in reviews.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("decision", out JsonElement decision))
                                throw new InputException(ErrorCode.InvalidRetention, "Acceptance review decision is missing: " + file);
                            if (decision.ValueKind != JsonValueKind.Null) Review(decision, run.ProjectDirectory, consumer);
                        }
                    }
                }
                unit.Recognized = true;
            });
        }
        string declaringDirectory = Path.GetDirectoryName(retention)!;
        var roots = new HashSet<string>(FilePaths.Comparer);
        foreach (RetentionPin pin in policy.Pins)
        {
            string path = FilePaths.Full(pin.Path, declaringDirectory);
            if (!roots.Add(path)) throw new InputException(ErrorCode.InvalidRetention, "Duplicate retention pin: " + path);
            state.Try(path, () => {
                NoLinks(path);
                if (!Inside(store, path) || Owner(path) == null || !File.Exists(path) && !Directory.Exists(path))
                    throw new InputException(ErrorCode.InvalidRetention, "Pins must identify existing units or files inside units in this store.");
                Reference(path, null, "pin: " + pin.Reason);
            });
        }
        foreach (string selected in policy.ReviewProjects)
        {
            string path = FilePaths.Full(selected, declaringDirectory);
            if (!roots.Add(path)) throw new InputException(ErrorCode.InvalidRetention, "Duplicate retention root: " + path);
            state.Try(path, () => {
                JsonElement json = state.Read(path);
                Files.RequireSchema(json, "analysis-project");
                AnalysisProject project = json.Deserialize<AnalysisProject>(ProjectInputs.Json)!;
                string directory = Path.GetDirectoryName(path)!;
                Reference(path, null, "review project: " + path);
                Reference(FilePaths.Full(project.Requirements, directory), null, "review project requirements: " + path);
                foreach (ReviewObligation obligation in project.Obligations)
                {
                    if (obligation is null || obligation.Inputs.Any(string.IsNullOrWhiteSpace))
                        throw new InputException(ErrorCode.InvalidRetention, "Malformed review obligation.");
                    foreach (string input in obligation.Inputs)
                        Reference(FilePaths.Full(input, directory), null, "review project input: " + path);
                }
                string reviews = FilePaths.Full(project.ReviewDirectory, directory);
                NoLinks(reviews);
                if (!Directory.Exists(reviews)) return;
                foreach (string file in state.Walk(reviews).Files.Where(f => Path.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase)))
                {
                    Reference(file, null, "review record: " + path);
                    Consumer consumer = ConsumerFor(file, "review");
                    Review(state.Read(file), directory, consumer);
                    foreach (string dependency in consumer.Dependencies)
                        if (unitPaths.TryGetValue(dependency, out Unit? referenced))
                            referenced.Reasons.Add("review evidence: " + file);
                }
            });
        }
        var changes = new List<GcInputChange>();
        foreach (var input in inputs.OrderBy(p => p.Key, FilePaths.Comparer))
        {
            string? actual = null;
            string status = "missing";
            bool readable = state.Try(input.Key, () => {
                if (Directory.Exists(input.Key)) throw new InputException(ErrorCode.InvalidRetention, "Recorded file is a directory.");
                if (File.Exists(input.Key))
                {
                    (string hash, string resolved) = ReadInput(input.Key);
                    actual = hash;
                    state.CurrentHashes[input.Key] = actual;
                    state.ResolvedInputs[input.Key] = resolved;
                    status = "changed";
                }
                else { NoLinks(input.Key); state.MissingPaths.Add(input.Key); }
            });
            if (!readable) status = "unreadable";
            List<GcInputUse> affected = input.Value.Where(p => actual == null ||
                !p.ExpectedSha256.Equals(actual, StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
            if (affected.Count == 0) continue;
            changes.Add(new(input.Key, status, actual, affected));
            foreach (GcInputUse use in affected) consumers[use.Consumer].ChangedInputs.Add(input.Key);
        }
        foreach (Unit unit in unitPaths.Values.Where(u => u.Latest > now.UtcDateTime.AddHours(-graceHours)))
            unit.Reasons.Add("Protected by the grace interval; its dependencies must also be retained.");
        bool again;
        do
        {
            again = false;
            foreach (Consumer consumer in consumers.Values)
                foreach (string dependency in consumer.Dependencies)
                    if (consumers.TryGetValue(dependency, out Consumer? producer) &&
                        (producer.ChangedInputs.Count > 0 || producer.AffectedBy.Count > 0))
                        again |= consumer.AffectedBy.Add(dependency);
            foreach (Consumer consumer in consumers.Values.Where(c => unitPaths.ContainsKey(c.Path) && unitPaths[c.Path].Reasons.Count > 0))
                foreach (string dependency in consumer.Dependencies)
                    if (unitPaths.TryGetValue(dependency, out Unit? referenced))
                        again |= referenced.Reasons.Add("dependency of retained run: " + consumer.Path);
            foreach (Consumer consumer in consumers.Values.Where(c => catalog.ContainsKey(c.Path)))
                foreach (string dependency in consumer.Dependencies)
                    if (unitPaths.TryGetValue(dependency, out Unit? referenced))
                        again |= referenced.Reasons.Add("dependency of protected consumer: " + consumer.Path);
        } while (again);
        state.Verify();
        bool complete = state.Diagnostics.Count == 0;
        List<GcEntry> entries = unitPaths.Values.OrderBy(u => u.Path, FilePaths.Comparer).Select(unit => {
            Consumer consumer = consumers[unit.Path];
            bool recent = unit.Latest > now.UtcDateTime.AddHours(-graceHours);
            List<string> reasons = unit.Reasons.Order(StringComparer.Ordinal).ToList();
            string status;
            if (!complete || !unit.Recognized) { status = "protected"; reasons.Add("Incomplete reference scope; no collection candidate can be established."); }
            else if (recent || unit.Protected) { status = "protected"; reasons.Add(unit.Protected ? "Catalog consumer is not a collection candidate." : "Output is inside the grace interval or has a future timestamp."); }
            else if (reasons.Count > 0) status = "retained";
            else { status = "candidate"; reasons.Add("Unpinned and unreferenced within the declared scope; not deletion authorization."); }
            return new GcEntry(unit.Path, status, unit.LogicalBytes, reasons,
                consumer.Dependencies.Order(FilePaths.Comparer).ToList(), consumer.ChangedInputs.Count > 0 || consumer.AffectedBy.Count > 0);
        }).ToList();
        List<GcRecheck> recheck = consumers.Values.Where(c => c.ChangedInputs.Count > 0 || c.AffectedBy.Count > 0)
            .OrderBy(c => c.Path, FilePaths.Comparer).Select(c => new GcRecheck(c.Path, c.Kind,
                c.ChangedInputs.Order(FilePaths.Comparer).ToList(), c.AffectedBy.Order(FilePaths.Comparer).ToList(),
                c.Kind == "run" ? "whole-run-conservative" : "review",
                c.Kind == "run" ? ["reassess-inputs-and-affected-analysis", "renew-reviews-and-acceptance"] :
                    new List<string> { "reconsider-review", "reaccept-dependent-runs" })).ToList();
        return new(Schema.Version, "gc-plan", complete ? "ready" : "incomplete", store, retention, now, graceHours, true, false,
            new(entries.Count, entries.Count(e => e.Status == "candidate"), entries.Count(e => e.Status == "retained"),
                entries.Count(e => e.Status == "protected"), entries.Where(e => e.Status == "candidate").Sum(e => e.LogicalBytes ?? 0),
                changes.Count, recheck.Count),
            entries, state.ProtectedPaths.Order(FilePaths.Comparer).ToList(), changes, recheck, state.DocumentHashes, state.Stamps, state.CurrentHashes, state.ResolvedInputs, state.Diagnostics, [
                "Read-only scoped inventory, not archive validation, acceptance, or deletion authorization. Only completed schema-4 workflow units are candidates; byte-pinned catalog consumers remain protected.",
                "Pins and selected review projects define retention scope; undeclared external consumers, Markdown links and other formats cannot be inferred.",
                "Impact checks recorded file hashes and transitive run references, not stage-level dependencies, runtime metadata or current review selection. No file change is not a current-applicability verdict.",
                "Unfinished/unknown archives, unresolved references, reparse points and observed scan changes block all candidates. Grace alone does not prove inactivity.",
                "Logical bytes are not guaranteed reclaimed space. Path checks and metadata observations are not hard-link identity proofs or an atomic filesystem snapshot."
            ]);
    }

    private static bool Hash(string? value) => value != null && value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static bool Inside(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
    private static string LocalArtifact(string root, string relative)
    {
        string path = FilePaths.Full(relative, root);
        if (FilePaths.IsAbsolute(relative) || !Inside(root, path) || FilePaths.Comparer.Equals(root, path))
            throw new InputException(ErrorCode.InvalidRetention, "Artifact escapes its workflow unit: " + relative);
        return path;
    }
    private static void NoLinks(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InputException(ErrorCode.InvalidRetention, "Reparse point is not supported: " + current);
        }
    }
    private sealed class Unit(string path)
    {
        public string Path { get; } = path;
        public bool Recognized { get; set; }
        public bool Protected { get; set; }
        public long? LogicalBytes { get; set; }
        public DateTime Latest { get; set; }
        public HashSet<string> Reasons { get; } = new(StringComparer.Ordinal);
    }
    private sealed class Consumer(string path, string kind)
    {
        public string Path { get; } = path;
        public string Kind { get; } = kind;
        public HashSet<string> Dependencies { get; } = new(FilePaths.Comparer);
        public HashSet<string> ChangedInputs { get; } = new(FilePaths.Comparer);
        public HashSet<string> AffectedBy { get; } = new(FilePaths.Comparer);
    }
    internal sealed record Tree(List<string> Files, DateTime Latest);
    internal sealed class Scan(string store)
    {
        public Dictionary<string, GcPathState> Stamps { get; } = new(FilePaths.Comparer);
        public Dictionary<string, string> DocumentHashes { get; } = new(FilePaths.Comparer);
        public Dictionary<string, string> CurrentHashes { get; } = new(FilePaths.Comparer);
        public Dictionary<string, string> ResolvedInputs { get; } = new(FilePaths.Comparer);
        public HashSet<string> MissingPaths { get; } = new(FilePaths.Comparer);
        public HashSet<string> ProtectedPaths { get; } = new(FilePaths.Comparer);
        public List<Issue> Diagnostics { get; } = [];
        public void Block(string path, string message)
        {
            ProtectedPaths.Add(path);
            Diagnostics.Add(new(ErrorCode.RetentionScanIncomplete, path + ": " + message));
        }
        public bool Try(string path, Action action)
        {
            try { action(); return true; }
            catch (Exception error) when (error is InputException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { Block(path, error.Message); return false; }
        }
        public JsonElement Read(string path)
        {
            NoLinks(path);
            if (new FileInfo(path).Length > 16 * 1024 * 1024)
                throw new InputException(ErrorCode.InvalidRetention, "JSON scope exceeds 16 MiB: " + path);
            return JsonSerializer.Deserialize<JsonElement>(Files.ReadPinned(path, DocumentHashes), ProjectInputs.Json);
        }
        internal static GcPathState GetStamp(string path)
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InputException(ErrorCode.InvalidRetention, "Reparse point observed: " + path);
            return new(attributes, (attributes & FileAttributes.Directory) != 0 ? 0 : new FileInfo(path).Length,
                File.GetLastWriteTimeUtc(path));
        }
        public void Observe(string path)
        {
            GcPathState stamp = GetStamp(path);
            if (Stamps.TryGetValue(path, out GcPathState? previous) && previous != stamp)
                throw new InputException(ErrorCode.InvalidRetention, "Path changed during scan: " + path);
            Stamps[path] = stamp;
            if (Stamps.Count > 100000) throw new InputException(ErrorCode.InvalidRetention, "Scan exceeds 100000 entries.");
        }
        public Tree Walk(string root)
        {
            NoLinks(root);
            List<string> files = [];
            DateTime latest = DateTime.MinValue;
            Stack<string> pending = new([root]);
            while (pending.TryPop(out string? directory))
            {
                Observe(directory);
                if (Stamps[directory].Modified > latest) latest = Stamps[directory].Modified;
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    Observe(path);
                    if (Stamps[path].Modified > latest) latest = Stamps[path].Modified;
                    if ((Stamps[path].Attributes & FileAttributes.Directory) != 0) pending.Push(path);
                    else files.Add(path);
                }
            }
            return new(files, latest);
        }
        public void Verify()
        {
            foreach (var stamp in Stamps) Try(stamp.Key, () => {
                NoLinks(stamp.Key);
                if (GetStamp(stamp.Key) != stamp.Value) Block(stamp.Key, "Filesystem entry changed during scan.");
            });
            foreach (var pin in DocumentHashes) Try(pin.Key, () => {
                NoLinks(pin.Key);
                if (Files.Hash(pin.Key) != pin.Value) Block(pin.Key, "File bytes changed during scan.");
            });
            foreach (var pin in CurrentHashes) Try(pin.Key, () => {
                (string hash, string resolved) = ReadInput(pin.Key);
                if (hash != pin.Value || ResolvedInputs.TryGetValue(pin.Key, out string? original) && !FilePaths.Comparer.Equals(original, resolved))
                    Block(pin.Key, "Input bytes or resolved identity changed during scan.");
            });
            foreach (string path in MissingPaths)
                if (File.Exists(path) || Directory.Exists(path)) Block(path, "Missing input appeared during scan.");
            Try(store, () => NoLinks(store));
        }
    }
}
