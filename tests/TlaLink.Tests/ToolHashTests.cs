using System.Diagnostics;
using System.Text.Json;
using static TlaLink.Tests.CoreTests;

namespace TlaLink.Tests;

internal static class ToolHashTests
{
    public static void Register(Action<string, Action> test, string work)
    {
        test("tool hashes are computed once per canonical file and mutable copies stay uncached", () =>
        {
            string tool = Path.Combine(work, "immutable-tool.bin"), copy = Path.Combine(work, "mutable-tool-copy.bin");
            File.WriteAllText(tool, "original tool bytes");
            string expected = Files.Hash(tool);
            File.Copy(tool, copy);
            using ToolHashSession session = new([tool, Path.Combine(work, "unused", "..", "immutable-tool.bin")],
                Path.Combine(work, "hash-dedup.json"));
            for (int i = 0; i < 100; i++) Equal(expected, Files.Hash(tool));
            Equal(1, session.Count);
            Equal(100L, session.Hits);
            Equal(expected, Files.Hash(copy));
            File.WriteAllText(copy, "changed copied tool or source/configuration bytes");
            True(expected != Files.Hash(copy));
            Equal(100L, session.Hits);
            session.Verify();
        });
        test("hash cache disposal restores byte hashing and releases immutable tools", () =>
        {
            string tool = Path.Combine(work, "released-tool.bin");
            File.WriteAllText(tool, "before");
            string before = Files.Hash(tool);
            using (ToolHashSession session = new([tool], Path.Combine(work, "hash-release.json")))
                Equal(before, Files.Hash(tool));
            File.WriteAllText(tool, "after");
            True(before != Files.Hash(tool));
        });
        test("immutable tools reject writes or report session drift", () =>
        {
            string tool = Path.Combine(work, "locked-tool.bin");
            File.WriteAllText(tool, "before");
            using ToolHashSession session = new([tool], Path.Combine(work, "hash-lock.json"));
            if (OperatingSystem.IsWindows())
                ThrowsAny<IOException>(() => File.WriteAllText(tool, "after"));
            else
            {
                File.WriteAllText(tool, "after");
                ThrowsAny<InvalidDataException>(session.Verify);
            }
        });
        test("test child hosts reuse startup hashes without changing the production CLI", () =>
        {
            string tool = Path.Combine(work, "child-tool.bin"), snapshot = Path.Combine(work, "hash-child.json");
            File.WriteAllText(tool, "child fixture");
            using ToolHashSession session = new([tool], snapshot);
            ProcessStartInfo start = new(FilePaths.ExecutableName("dotnet"))
            {
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string argument in new[] { typeof(Program).Assembly.Location, "--hash-cache-probe", snapshot, tool })
                start.ArgumentList.Add(argument);
            using Process child = Process.Start(start) ?? throw new InvalidOperationException("Could not start hash cache probe.");
            Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(15000))
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit();
                throw new TimeoutException("Hash cache probe exceeded 15 seconds.");
            }
            if (child.ExitCode != 0) throw new InvalidOperationException(stderr.GetAwaiter().GetResult());
            using JsonDocument result = JsonDocument.Parse(stdout.GetAwaiter().GetResult());
            Equal(Files.Hash(tool), result.RootElement.GetProperty("hash").GetString());
            Equal(1L, result.RootElement.GetProperty("hits").GetInt64());
        });
        test("missing immutable tool files fail explicitly without replacing the active cache", () =>
        {
            var previous = Files.ImmutableToolHashLookup;
            ThrowsAny<FileNotFoundException>(() =>
            {
                using ToolHashSession session = new([Path.Combine(work, "missing-tool.bin")], Path.Combine(work, "hash-missing.json"));
            });
            True(ReferenceEquals(previous, Files.ImmutableToolHashLookup));
        });
    }
}
