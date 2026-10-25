using System.Diagnostics;
using System.Text.Json;

namespace TlaLink.Tests;

internal sealed class ToolHashSession : IDisposable
{
    private sealed record Snapshot(int SchemaVersion, string Kind, Dictionary<string, string> Fingerprints);

    private readonly Dictionary<string, string> hashes = new(FilePaths.Comparer);
    private readonly List<FileStream> leases = [];
    private readonly Func<string, string?>? previousLookup;
    private readonly IReadOnlyList<string>? previousHost;
    private bool disposed;
    private long hits;
    public long Hits => Interlocked.Read(ref hits);
    public int Count => hashes.Count;
    public double StartupSeconds { get; }

    public ToolHashSession(IEnumerable<string> paths, string snapshot)
    {
        Stopwatch clock = Stopwatch.StartNew();
        previousLookup = Files.ImmutableToolHashLookup;
        previousHost = ProjectWorkflow.CliHost;
        try
        {
            foreach (string path in paths.Select(path => FilePaths.Full(path)).Distinct(FilePaths.Comparer))
            {
                leases.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                hashes.Add(path, Files.HashUncached(path));
            }
            Files.WriteNew(snapshot, new Snapshot(Schema.Version, "test-tool-hashes", hashes));
            Install(snapshot);
            StartupSeconds = clock.Elapsed.TotalSeconds;
        }
        catch
        {
            foreach (FileStream lease in leases) lease.Dispose();
            throw;
        }
    }

    private ToolHashSession(string snapshot)
    {
        previousLookup = Files.ImmutableToolHashLookup;
        previousHost = ProjectWorkflow.CliHost;
        using JsonDocument document = JsonDocument.Parse(Files.Read(snapshot));
        Files.RequireSchema(document.RootElement, "test-tool-hashes");
        Snapshot content = document.RootElement.Deserialize<Snapshot>(ProjectInputs.Json)
            ?? throw new InvalidDataException("Empty test tool hash snapshot.");
        foreach (var pin in content.Fingerprints)
        {
            if (FilePaths.Full(pin.Key) != pin.Key || pin.Value.Length != 64 ||
                !pin.Value.All(Uri.IsHexDigit))
                throw new InvalidDataException("Invalid test tool hash snapshot entry: " + pin.Key);
            hashes.Add(pin.Key, pin.Value);
        }
        Install(snapshot);
    }

    public static ToolHashSession Inherit(string snapshot) => new(FilePaths.Full(snapshot));

    private void Install(string snapshot)
    {
        Files.ImmutableToolHashLookup = path =>
        {
            if (!hashes.TryGetValue(path, out string? hash)) return null;
            Interlocked.Increment(ref hits);
            return hash;
        };
        ProjectWorkflow.CliHost = [typeof(Program).Assembly.Location, "--cli", FilePaths.Full(snapshot)];
    }

    public void Verify()
    {
        // Windows leases deny writes/replacement. Unix writers can ignore sharing locks.
        if (!OperatingSystem.IsWindows())
            foreach (var pin in hashes)
                if (Files.HashUncached(pin.Key) != pin.Value)
                    throw new InvalidDataException("Immutable test tool changed during the session: " + pin.Key);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Files.ImmutableToolHashLookup = previousLookup;
        ProjectWorkflow.CliHost = previousHost;
        foreach (FileStream lease in leases) lease.Dispose();
    }
}
