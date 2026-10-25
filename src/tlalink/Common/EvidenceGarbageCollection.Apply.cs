using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace TlaLink;

public sealed class GcApplyResult
{
    public int SchemaVersion { get; } = Schema.Version;
    public string Kind { get; } = "gc-apply-result";
    public string Status { get; set; } = "incomplete";
    public required string Store { get; init; }
    public required string Plan { get; init; }
    public required string PlanSha256 { get; init; }
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public bool ReadOnly => RemovedFiles.Count == 0 && DeletedUnits.Count == 0;
    public bool DeletionAuthorized { get; set; }
    public List<string> DeletedUnits { get; } = [];
    public List<string> PendingUnits { get; init; } = [];
    public string? PartialUnit { get; set; }
    public List<string> RemovedFiles { get; } = [];
    public int RemovedFileCount => RemovedFiles.Count;
    public List<Issue> Diagnostics { get; } = [];
    public List<string> Boundaries { get; } = [
        "Offline, explicitly selected completed workflow archives only; not proof validation or acceptance.",
        "Unknown reference scope blocks application. Directory handles prevent ancestor replacement; all writers must still be stopped.",
        "Deletion is not transactional. An incomplete receipt may describe partial removal; interrupted writes may leave an incomplete or malformed receipt.",
        "Logical file lengths do not guarantee physical space reclaimed."
    ];
}

public static partial class EvidenceGarbageCollection
{
    private static int Apply(CliOptions options)
    {
        if (!OperatingSystem.IsWindows())
            throw new InputException(ErrorCode.InvalidRetention, "Guarded GC apply requires Windows directory-handle safeguards; planning remains cross-platform.");
        if (options.Get("exclusive", "false") != "true")
            throw new InputException(ErrorCode.InvalidRetention, "Stop all store writers and assert --exclusive true before applying a plan.");
        if (options.Has("store") || options.Has("pins") || options.Has("grace-hours"))
            throw new InputException(ErrorCode.InvalidRetention, "Apply uses the saved plan's store, pins and grace; overrides are not allowed.");
        string planPath = FilePaths.Full(options.Required("apply"));
        string output = FilePaths.Full(options.Required("output"));
        var read = new Scan(Path.GetDirectoryName(planPath)!);
        JsonElement json = read.Read(planPath);
        Files.RequireSchema(json, "gc-plan");
        GcPlan saved = json.Deserialize<GcPlan>(ProjectInputs.Json)!;
        if (saved.Status != "ready" || !saved.ReadOnly || saved.DeletionAuthorized ||
            saved.GraceHours is < 0 or > 87600 || !FilePaths.IsNativeAbsolute(saved.Store) ||
            !FilePaths.IsNativeAbsolute(saved.Retention) || saved.Snapshot.Count == 0 ||
            saved.Snapshot.Any(p => !FilePaths.IsNativeAbsolute(p.Key) || p.Value is null) ||
            saved.Entries.Any(e => e is null || !FilePaths.IsNativeAbsolute(e.Path)) ||
            saved.ChangedInputs.Any(c => c is null))
            throw new InputException(ErrorCode.InvalidRetention, "Apply requires an intact, ready, full read-only GC plan.");
        string store = FilePaths.Full(saved.Store);
        if (FilePaths.Comparer.Equals(store, Path.GetPathRoot(store)) || !Directory.Exists(store) ||
            Inside(store, output) || Inside(store, planPath) ||
            saved.Snapshot.Any(p => (p.Value.Attributes & FileAttributes.Directory) != 0 && Inside(p.Key, output)))
            throw new InputException(ErrorCode.InvalidRetention, "Plan and new receipt must be outside the store and the receipt outside all scanned directories.");
        NoLinks(store);
        NoLinks(output);
        NoLinks(planPath);
        List<string> candidates = saved.Entries.Where(e => e.Status == "candidate").Select(e => e.Path).Order(FilePaths.Comparer).ToList();
        if (candidates.Any(p => !FilePaths.Comparer.Equals(Path.GetDirectoryName(p), store)) ||
            candidates.Distinct(FilePaths.Comparer).Count() != candidates.Count)
            throw new InputException(ErrorCode.InvalidRetention, "Candidates must be distinct immediate store children.");

        using var directories = new DirectoryLeases();
        directories.Pin(store);
        directories.Pin(Path.GetDirectoryName(output)!);
        string lockPath = store + ".gc.lock";
        NoLinks(lockPath);
        using FileStream lease = new(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            1, FileOptions.DeleteOnClose);
        using FileStream receiptFile = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var result = new GcApplyResult {
            Store = store, Plan = planPath, PlanSha256 = read.DocumentHashes[planPath], PendingUnits = [.. candidates]
        };
        WriteReceipt(receiptFile, result);
        try
        {
            foreach (var entry in saved.Snapshot.Where(p => (p.Value.Attributes & FileAttributes.Directory) != 0))
                directories.Pin(entry.Key);
            GcPlan fresh = Plan(store, saved.Retention, saved.GraceHours, DateTimeOffset.UtcNow);
            if (fresh.Status != "ready" || !Same(saved.Snapshot, fresh.Snapshot) ||
                !Same(saved.Fingerprints, fresh.Fingerprints) ||
                !Same(saved.FileInputs, fresh.FileInputs) ||
                !Same(saved.ResolvedInputs, fresh.ResolvedInputs) ||
                !Same(InputStates(saved), InputStates(fresh)) ||
                !candidates.SequenceEqual(fresh.Entries.Where(e => e.Status == "candidate").Select(e => e.Path).Order(FilePaths.Comparer), FilePaths.Comparer))
                throw new InputException(ErrorCode.RetentionApplyIncomplete, "Plan is stale or reference scope changed; regenerate and inspect a new plan.");
            foreach (string candidate in candidates) VerifyArchive(candidate);
            var verify = new Scan(store);
            foreach (var entry in fresh.Snapshot) verify.Stamps.Add(entry.Key, entry.Value);
            foreach (var entry in fresh.Fingerprints) verify.DocumentHashes.Add(entry.Key, entry.Value);
            foreach (var entry in fresh.FileInputs) verify.CurrentHashes.Add(entry.Key, entry.Value);
            foreach (var entry in fresh.ResolvedInputs) verify.ResolvedInputs.Add(entry.Key, entry.Value);
            foreach (var entry in fresh.ChangedInputs.Where(c => c.Status == "missing")) verify.MissingPaths.Add(entry.Path);
            verify.Verify();
            if (verify.Diagnostics.Count > 0)
                throw new InputException(ErrorCode.RetentionApplyIncomplete, string.Join("; ", verify.Diagnostics.Select(d => d.Message)));
            result.DeletionAuthorized = true;
            foreach (string candidate in candidates)
            {
                result.PartialUnit = candidate;
                foreach (var entry in fresh.Snapshot.Where(p => Inside(candidate, p.Key) &&
                    (p.Value.Attributes & FileAttributes.Directory) == 0).OrderBy(p => p.Key, FilePaths.Comparer))
                {
                    NoLinks(entry.Key);
                    if (Scan.GetStamp(entry.Key) != entry.Value)
                        throw new InputException(ErrorCode.RetentionApplyIncomplete, "Candidate file changed before deletion: " + entry.Key);
                    File.Delete(entry.Key);
                    result.RemovedFiles.Add(entry.Key);
                }
                foreach (string directory in fresh.Snapshot.Where(p => Inside(candidate, p.Key) &&
                    (p.Value.Attributes & FileAttributes.Directory) != 0).Select(p => p.Key).OrderByDescending(p => p.Length))
                {
                    NoLinks(directory);
                    directories.Release(directory);
                    Directory.Delete(directory, recursive: false);
                }
                result.DeletedUnits.Add(candidate);
                result.PendingUnits.Remove(candidate);
                result.PartialUnit = null;
            }
            result.Status = "completed";
        }
        catch (Exception error) when (error is InputException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            result.Diagnostics.Add(new(ErrorCode.RetentionApplyIncomplete, error.Message));
        }
        WriteReceipt(receiptFile, result);
        if (options.Get("format", "json") == "text")
        {
            Console.WriteLine($"gc-apply-result: {result.Status}; deleted {result.DeletedUnits.Count} units, {result.RemovedFileCount} files; pending {result.PendingUnits.Count}");
            foreach (Issue issue in result.Diagnostics) Console.WriteLine($"{issue.Code}: {issue.Message}");
        }
        else ProjectWorkflow.Render(result, options);
        return result.Status == "completed" ? 0 : 2;
    }

    private static Dictionary<string, string> InputStates(GcPlan plan) =>
        plan.ChangedInputs.ToDictionary(c => c.Path, c => c.Status + ":" + c.ActualSha256, FilePaths.Comparer);

    private static bool Same<T>(Dictionary<string, T> expected, Dictionary<string, T> actual)
    {
        var normalized = new Dictionary<string, T>(actual, FilePaths.Comparer);
        return expected.Count == normalized.Count &&
            expected.All(p => normalized.TryGetValue(p.Key, out T? value) && EqualityComparer<T>.Default.Equals(p.Value, value));
    }

    private static void VerifyArchive(string candidate)
    {
        WorkflowRun run = ProjectInputs.Read<WorkflowRun>(Path.Combine(candidate, "run.json"));
        foreach (var artifact in run.Artifacts)
            if (!Files.Hash(LocalArtifact(candidate, artifact.Key)).Equals(artifact.Value, StringComparison.OrdinalIgnoreCase))
                throw new InputException(ErrorCode.RetentionApplyIncomplete, "Candidate artifact hash mismatch: " + artifact.Key);
        foreach (var input in run.Fingerprints.Where(p => FilePaths.IsAbsolute(p.Key)))
            if (!Files.Hash(Path.Combine(candidate, "inputs", input.Value)).Equals(input.Value, StringComparison.OrdinalIgnoreCase))
                throw new InputException(ErrorCode.RetentionApplyIncomplete, "Candidate archived input hash mismatch: " + input.Key);
    }

    private static void WriteReceipt(FileStream file, GcApplyResult result)
    {
        file.Position = 0;
        file.SetLength(0);
        JsonSerializer.Serialize(file, result, Files.Json);
        file.Flush(flushToDisk: true);
    }

    internal sealed partial class DirectoryLeases : IDisposable
    {
        private const uint ReadAttributes = 0x80, ShareReadWrite = 3, OpenExisting = 3;
        private const uint BackupSemanticsAndOpenReparsePoint = 0x02200000;
        private const int AttributeTagInformation = 9;
        private readonly Dictionary<string, SafeFileHandle> handles = new(FilePaths.Comparer);
        public void Pin(string path)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows directory guards are required.");
            Stack<string> parents = new();
            for (string? current = FilePaths.Full(path); current != null; current = Path.GetDirectoryName(current))
                parents.Push(current);
            while (parents.TryPop(out string? current))
            {
                if (handles.ContainsKey(current)) continue;
                // Omitting delete sharing prevents ancestor rename/replacement.
                SafeFileHandle handle = CreateFile(current, ReadAttributes, ShareReadWrite, IntPtr.Zero, OpenExisting,
                    BackupSemanticsAndOpenReparsePoint, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    throw new IOException("Cannot guard directory: " + current, new Win32Exception(error));
                }
                if (!GetAttributes(handle, AttributeTagInformation, out AttributeTag info, 8))
                {
                    int error = Marshal.GetLastPInvokeError();
                    handle.Dispose();
                    throw new IOException("Cannot inspect guarded directory: " + current, new Win32Exception(error));
                }
                if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
                    (info.Attributes & (uint)FileAttributes.Directory) == 0)
                {
                    handle.Dispose();
                    throw new InputException(ErrorCode.InvalidRetention, "Guarded directory is a reparse point or not a directory: " + current);
                }
                handles.Add(current, handle);
            }
        }
        public void Release(string path)
        {
            if (handles.Remove(path, out SafeFileHandle? handle)) handle.Dispose();
        }
        public void Dispose()
        {
            foreach (SafeFileHandle handle in handles.Values) handle.Dispose();
            handles.Clear();
        }
        internal static string ResolvePath(string path)
        {
            using SafeFileHandle handle = CreateFile(path, ReadAttributes, 7, IntPtr.Zero, OpenExisting, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException("Cannot resolve input identity: " + path, new Win32Exception(Marshal.GetLastPInvokeError()));
            return FinalPath(handle);
        }
        internal static unsafe string FinalPath(SafeFileHandle handle)
        {
            char[] buffer = new char[32768];
            fixed (char* pointer = buffer)
            {
                uint length = GetFinalPathName(handle, pointer, 32768, 0);
                if (length == 0 || length >= 32768)
                    throw new IOException("Cannot resolve final input path.", new Win32Exception(Marshal.GetLastPInvokeError()));
                string path = new(pointer, 0, checked((int)length));
                if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
                else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
                return FilePaths.Full(path);
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTag { public uint Attributes; public uint ReparseTag; }
        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetAttributes(SafeFileHandle handle, int informationClass, out AttributeTag information, uint size);
        [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
        private static unsafe partial uint GetFinalPathName(SafeFileHandle handle, char* buffer, uint size, uint flags);
    }
}
