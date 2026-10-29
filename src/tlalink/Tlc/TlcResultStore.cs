using System.Globalization;
using System.Text.Json;

namespace TlaLink;

public sealed record TlcKeyInput(string Path, string Sha256);
public sealed record TlcResultKey(int SchemaVersion, string Kind, int AdapterVersion, string Model, string Config,
    string Jar, List<string> Options, List<TlcKeyInput> Inputs, int Fingerprint);
public sealed record TlcStoredResult(int SchemaVersion, string Kind, string Key, string Family, TlcResultKey Material,
    string Outcome, string Stage, string Producer, DateTimeOffset StoredAt, string TlalinkSha256,
    TlcResources? RequestedResources, TlcResources Resources, int TimeoutSeconds, double DurationSeconds,
    TlcStatistics? Statistics, Dictionary<string, string> Files);

/// <summary>
/// Content-addressed completed TLC outcomes. A key covers every byte and option that determines the native
/// answer, but not workers, heap or deadline, which only decide whether a search completes.
/// </summary>
public static class TlcResultStore
{
    // Increment whenever the TLC invocation, outcome classification or recorded result fields change.
    internal const int AdapterVersion = 1;
    public const int MaxFingerprint = 130;
    internal static readonly string[] Options = ["-tool"];
    private static readonly string[] StoredFiles = ["result.json", "tlc.log", "tlc.invocation.json"];

    internal static TlcResultKey Key(string model, string config, string jar,
        IReadOnlyDictionary<string, string> dependencies, int fingerprint)
    {
        Dictionary<string, string> inputs = new(FilePaths.Comparer);
        foreach (var dependency in dependencies) Files.Pin(inputs, dependency.Key, dependency.Value);
        Files.Pin(inputs, model, Files.Hash(model));
        Files.Pin(inputs, config, Files.Hash(config));
        Files.Pin(inputs, jar, Files.Hash(jar));
        return new(Schema.Version, "tlc-result-key", AdapterVersion, model, config, jar, [.. Options],
            inputs.OrderBy(i => i.Key, StringComparer.Ordinal).Select(i => new TlcKeyInput(i.Key, i.Value)).ToList(),
            fingerprint);
    }

    internal static string Hash(TlcResultKey key) => Schema.Hash(key).ToLowerInvariant();
    internal static string Family(TlcResultKey key) => Hash(key with { Fingerprint = -1 });
    private static string EntryName(int fingerprint) => "fp-" + fingerprint.ToString(CultureInfo.InvariantCulture);
    internal static string StoredDirectory(int fingerprint) => Path.Combine("stored", EntryName(fingerprint));

    internal static List<(TlcStoredResult Entry, string Directory)> Find(string store, TlcResultKey key)
    {
        string family = Path.Combine(store, "tlc", Family(key));
        List<(TlcStoredResult, string)> found = [];
        if (!Directory.Exists(family)) return found;
        foreach (string directory in Directory.EnumerateDirectories(family, "fp-*").Order(StringComparer.Ordinal))
            if (Validate(directory, out TlcStoredResult? entry).Count == 0 &&
                Hash(entry!.Material with { Fingerprint = key.Fingerprint }) == Hash(key))
                found.Add((entry, directory));
        return found;
    }

    internal static List<Issue> Validate(string directory, out TlcStoredResult? entry, bool inStore = true)
    {
        entry = null;
        List<Issue> issues = [];
        void Bad(string message) => issues.Add(new(ErrorCode.WorkflowProvenanceInconsistent, "Stored TLC result " + directory + ": " + message));
        try
        {
            entry = ProjectInputs.Read<TlcStoredResult>(Path.Combine(directory, "entry.json"));
            if (entry.SchemaVersion != Schema.Version || entry.Kind != "tlc-stored-result")
                throw new InputException(ErrorCode.InvalidWorkflowInput, "Expected current tlc-stored-result.");
        }
        catch (Exception error) when (error is InputException or IOException or UnauthorizedAccessException or JsonException)
        {
            Bad("unreadable entry: " + error.Message);
            return issues;
        }
        if (entry == null) { Bad("empty entry"); return issues; }
        if (entry.Material.SchemaVersion != Schema.Version || entry.Material.Kind != "tlc-result-key")
            Bad("unsupported key");
        if (Hash(entry.Material) != entry.Key || Family(entry.Material) != entry.Family ||
            Path.GetFileName(directory) != EntryName(entry.Material.Fingerprint) ||
            inStore && Path.GetFileName(Path.GetDirectoryName(directory)) != entry.Family)
            Bad("key or location does not match its material");
        if (entry.Outcome is not ("passed" or "rejected")) Bad("only completed outcomes are stored");
        Dictionary<string, string> recorded = entry.Files;
        if (recorded.Count != StoredFiles.Length || StoredFiles.Any(f => !recorded.ContainsKey(f)) ||
            Directory.EnumerateFileSystemEntries(directory).Count() != StoredFiles.Length + 1)
            Bad("unexpected file set");
        foreach (var file in entry.Files)
        {
            string stored = Path.Combine(directory, file.Key);
            if (!StoredFiles.Contains(file.Key) || !File.Exists(stored) || Files.Hash(stored) != file.Value)
                Bad("missing or changed file " + file.Key);
        }
        if (issues.Count > 0) return issues;
        TlcResult result = ProjectInputs.Read<TlcResult>(Path.Combine(directory, "result.json"));
        if (ProjectWorkflow.Outcome("tlc", result) != entry.Outcome || result.Fingerprint != entry.Material.Fingerprint)
            Bad("result contradicts its entry");
        string[] arguments = ProjectInputs.Read<NativeProcessInvocation>(Path.Combine(directory, "tlc.invocation.json"))
            .Arguments.ToArray();
        int index = Array.IndexOf(arguments, "-fp");
        if (index < 0 || index + 1 >= arguments.Length || arguments.Count(a => a == "-fp") != 1 ||
            arguments[index + 1] != entry.Material.Fingerprint.ToString(CultureInfo.InvariantCulture))
            Bad("invocation fingerprint function differs");
        return issues;
    }

    /// <summary>Stores a completed outcome once; an existing entry for the same key is kept.</summary>
    internal static string? Store(string store, TlcResultKey key, string stage, string producer, string source,
        TlcResult result, TlcResources? requested, TlcResources resources, int timeout, double duration)
    {
        string outcome = ProjectWorkflow.Outcome("tlc", result);
        if (outcome is not ("passed" or "rejected")) return null;
        string family = Path.Combine(store, "tlc", Family(key));
        string final = Path.Combine(family, EntryName(key.Fingerprint));
        if (Directory.Exists(final)) return null;
        Directory.CreateDirectory(family);
        string temporary = Path.Combine(family, ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Files.WriteNew(Path.Combine(temporary, "result.json"), result);
            foreach (string file in new[] { "tlc.log", "tlc.invocation.json" })
                File.Copy(Path.Combine(source, file), Path.Combine(temporary, file));
            Dictionary<string, string> files = new(StringComparer.Ordinal);
            foreach (string file in StoredFiles) files[file] = Files.Hash(Path.Combine(temporary, file));
            Files.WriteNew(Path.Combine(temporary, "entry.json"), new TlcStoredResult(Schema.Version, "tlc-stored-result",
                Hash(key), Family(key), key, outcome, stage, producer, DateTimeOffset.UtcNow,
                Files.Hash(typeof(Program).Assembly.Location), requested, resources, timeout, duration, result.Statistics, files));
            try { Directory.Move(temporary, final); }
            catch (IOException) when (Directory.Exists(final)) { return null; }
            return final;
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }

    internal static void Copy(string entry, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(entry))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
    }

    /// <summary>
    /// Checks stored outcomes copied into a run against the current declaration and the run's recorded inputs.
    /// Reused primary evidence is validated against its own recorded budget rather than the routine one.
    /// </summary>
    internal static List<Issue> ValidateRunStage(string path, AnalysisProject project, AnalysisStage declared,
        WorkflowStageResult stage, string stageDirectory, TlcResult result, Dictionary<string, string> fingerprints,
        out AnalysisStage evidenceStage)
    {
        evidenceStage = declared;
        List<Issue> issues = [];
        void Bad(string message) => issues.Add(new(ErrorCode.WorkflowStageInconsistent, "TLC stage " + stage.Id + ": " + message));
        string outcome = ProjectWorkflow.Outcome("tlc", result);
        int fingerprint = result.Fingerprint;
        List<StoredStageResult> stored = stage.StoredResults ?? [];
        StoredStageResult? primary = stored.Count(s => s.Primary) > 1 ? null : stored.SingleOrDefault(s => s.Primary);
        if (stored.Count(s => s.Primary) > 1) Bad("more than one primary stored result");
        if (fingerprint != (declared.Fingerprint ?? 0) && !(outcome == "rejected" && primary?.Origin == "reused"))
            Bad("fingerprint function differs from the declaration");
        string model = ProjectInputs.Resolve(path, declared.Models![0]);
        string config = ProjectInputs.Resolve(path, declared.Config!);
        string jar = ProjectInputs.Resolve(path, project.Tools.Jar!.Path);
        HashSet<int> seen = [];
        string? family = null;
        foreach (StoredStageResult reference in stored)
        {
            if (reference.Directory != StoredDirectory(reference.Fingerprint) || reference.Origin is not ("reused" or "executed") ||
                reference.Origin == "executed" && !reference.Primary)
            {
                Bad("invalid stored-result reference");
                continue;
            }
            string directory = ProjectAcceptance.SafePath(stageDirectory, reference.Directory);
            issues.AddRange(Validate(directory, out TlcStoredResult? entry, inStore: false));
            if (entry == null) continue;
            if (Files.Hash(Path.Combine(directory, "entry.json")) != reference.EntrySha256 || entry.Key != reference.Key ||
                entry.Family != reference.Family || entry.Outcome != reference.Outcome ||
                entry.Material.Fingerprint != reference.Fingerprint)
                Bad("stored-result reference differs from its copied entry");
            if (!seen.Add(reference.Fingerprint)) Bad("duplicate fingerprint function");
            family ??= entry.Family;
            if (family != entry.Family) Bad("stored results describe different inputs");
            TlcResultKey material = entry.Material;
            if (material.AdapterVersion != AdapterVersion || !FilePaths.Comparer.Equals(material.Model, model) ||
                !FilePaths.Comparer.Equals(material.Config, config) || !FilePaths.Comparer.Equals(material.Jar, jar) ||
                !material.Options.SequenceEqual(Options))
                Bad("stored result does not describe the declared stage");
            foreach (TlcKeyInput input in material.Inputs)
                if (!fingerprints.TryGetValue(input.Path, out string? hash) || hash != input.Sha256)
                    Bad("stored result input differs from the run's recorded inputs: " + input.Path);
            if (reference.Primary)
            {
                if (Schema.Hash(ProjectInputs.Read<TlcResult>(Path.Combine(directory, "result.json"))) != Schema.Hash(result))
                    Bad("stage evidence differs from its stored result");
                if (reference.Origin == "reused")
                    evidenceStage = declared with { Resources = entry.RequestedResources, TimeoutSeconds = entry.TimeoutSeconds };
            }
            else if (entry.Outcome != "passed" || outcome != "passed")
                Bad("additional fingerprint-function evidence must be passes");
        }
        return issues;
    }

    internal static HashSet<int> PassedFingerprints(WorkflowStageResult stage, TlcResult result)
    {
        HashSet<int> passed = (stage.StoredResults ?? []).Where(s => s.Outcome == "passed").Select(s => s.Fingerprint).ToHashSet();
        if (stage.Outcome == "passed") passed.Add(result.Fingerprint);
        return passed;
    }
}
