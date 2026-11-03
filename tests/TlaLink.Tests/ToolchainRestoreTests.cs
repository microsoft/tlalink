using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class ToolchainRestoreTests
{
    public static void Register(Action<string, Action> test, string root, string work)
    {
        test("prebuilt restore reuses authoritative Java/P pins for both supported platforms", () =>
        {
            foreach (string platform in new[] { "win-x64", "linux-x64" })
            {
                List<AnalysisToolPin> pins = ToolchainRestore.Catalogue(platform);
                CoreTests.Equal(5, pins.Count);
                CoreTests.Equal(5, pins.Select(p => p.Id).Distinct().Count());
                CoreTests.Equal("3.1.0", pins.Single(p => p.Id == "p").Version);
                CoreTests.Equal("p.dll", pins.Single(p => p.Id == "p").Entrypoint);
                CoreTests.True(pins.Single(p => p.Id == "java").Directory.Contains(platform));
            }
            CoreTests.ThrowsAny<InputException>(() => ToolchainRestore.Catalogue("unsupported"));
        });
        test("offline restore installs from pinned bytes and repeat checks do not rewrite healthy files", () =>
        {
            Fixture f = new(root, work);
            ToolchainRestoreResult first = f.Restore();
            CoreTests.Equal("installed", first.Tools.Single().Status);
            string entry = first.Tools.Single().Entrypoint;
            DateTime timestamp = File.GetLastWriteTimeUtc(entry);
            ToolchainRestoreResult second = f.Restore();
            CoreTests.Equal("current", second.Tools.Single().Status);
            CoreTests.Equal(timestamp, File.GetLastWriteTimeUtc(entry));
            CoreTests.Equal(2, second.Tools.Single().FileCount);
            CoreTests.True(second.Tools.Single().ReplacedDirectory == null);
            CoreTests.Equal(Files.Hash(f.Payload), Files.Hash(entry));
        });
        test("repair requires offline-users assertion and retains every displaced byte including extras", () =>
        {
            Fixture f = new(root, work);
            RestoredTool tool = f.Restore().Tools.Single();
            File.WriteAllText(tool.Entrypoint, "damaged");
            string extra = Path.Combine(tool.Directory, "unrecognized.txt");
            File.WriteAllText(extra, "retain me");
            CoreTests.ThrowsAny<InputException>(() => f.Restore());
            CoreTests.Equal("damaged", File.ReadAllText(tool.Entrypoint));
            tool = f.Restore(exclusive: true).Tools.Single();
            CoreTests.Equal("repaired", tool.Status);
            CoreTests.True(tool.ReplacedDirectory != null);
            CoreTests.Equal("damaged", File.ReadAllText(Path.Combine(tool.ReplacedDirectory!, "payload.txt")));
            CoreTests.Equal("retain me", File.ReadAllText(Path.Combine(tool.ReplacedDirectory!, "unrecognized.txt")));
            CoreTests.True(!File.Exists(extra));
            CoreTests.Equal("current", f.Restore().Tools.Single().Status);
            File.Delete(tool.Entrypoint);
            CoreTests.Equal("repaired", f.Restore(exclusive: true).Tools.Single().Status);
        });
        test("damaged or missing cached archives are incomplete offline even with a healthy installation", () =>
        {
            Fixture f = new(root, work);
            RestoredTool tool = f.Restore().Tools.Single();
            File.WriteAllText(f.Archive, "not the pinned archive");
            CoreTests.ThrowsAny<InputException>(() => f.Restore(exclusive: true));
            CoreTests.Equal(Files.Hash(f.Payload), Files.Hash(tool.Entrypoint));
            File.Delete(f.Archive);
            CoreTests.ThrowsAny<InputException>(() => f.Restore());
        });
        test("an exclusive restore lease rejects concurrent writers without changing installations", () =>
        {
            Fixture f = new(root, work);
            f.Restore();
            using FileStream lease = new(Path.Combine(f.Directory, ".restore.lock"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            CoreTests.ThrowsAny<IOException>(() => f.Restore(exclusive: true));
        });
        test("archive path traversal duplicate entries and external links fail before installation", () =>
        {
            foreach (string mutant in new[] { "traversal", "duplicate", "link", "cycle" })
            {
                Fixture f = new(root, work, mutant);
                CoreTests.ThrowsAny<InputException>(() => f.Restore());
                CoreTests.True(!System.IO.Directory.Exists(Path.Combine(f.Directory, "probe")));
                CoreTests.True(!File.Exists(Path.Combine(f.Directory, "escape.txt")));
            }
        });
        test("tar.gz internal file links are materialized and executable modes are retained", () =>
        {
            Fixture f = new(root, work, "tar");
            RestoredTool result = f.Restore().Tools.Single();
            string copy = Path.Combine(result.Directory, "copy.txt");
            CoreTests.Equal(Files.Hash(f.Payload), Files.Hash(copy));
            CoreTests.True(File.Exists(Path.Combine(result.Directory, "empty.txt")));
            CoreTests.Equal(0L, new FileInfo(Path.Combine(result.Directory, "empty.txt")).Length);
            CoreTests.True((File.GetAttributes(copy) & FileAttributes.ReparsePoint) == 0);
            if (!OperatingSystem.IsWindows())
            {
                CoreTests.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(result.Entrypoint));
                File.SetUnixFileMode(result.Entrypoint, UnixFileMode.UserRead);
                CoreTests.ThrowsAny<InputException>(() => f.Restore());
                CoreTests.Equal("repaired", f.Restore(exclusive: true).Tools.Single().Status);
            }
            CoreTests.Equal("current", f.Restore().Tools.Single().Status);
        });
        if (!OperatingSystem.IsWindows())
            test("restore refuses linked cache roots and dangling lock symlinks without writing their targets", () =>
            {
                Fixture f = new(root, work);
                string link = Path.Combine(work, "cache-link-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateSymbolicLink(link, f.Directory);
                CoreTests.ThrowsAny<InputException>(() =>
                    ToolchainRestore.Restore(link, ToolchainRestore.Catalogue(), true, false, 30));
                string target = Path.Combine(f.Directory, "uncreated-target");
                File.CreateSymbolicLink(Path.Combine(f.Directory, ".restore.lock"), target);
                CoreTests.ThrowsAny<InputException>(() => f.Restore());
                CoreTests.True(!File.Exists(target));
            });
        test("toolchain CLI validates selections booleans and emits typed summaries without provisioning in offline mode", () =>
        {
            CoreTests.ThrowsAny<InputException>(() => new CliOptions(["toolchain", "upgrade"]));
            foreach (string[] args in new[] {
                new[] { "--tool", "unknown" }, new[] { "--tool", "p", "--tool", "p" },
                new[] { "--offline", "yes" } })
            {
                using StringWriter output = new();
                TextWriter previous = Console.Out;
                try
                {
                    Console.SetOut(output);
                    CoreTests.Equal(2, TlaLink.Program.Main(["toolchain", "restore",
                        "--directory", Path.Combine(work, "not-created"), .. args]));
                }
                finally { Console.SetOut(previous); }
                using JsonDocument result = JsonDocument.Parse(output.ToString());
                CoreTests.Equal("incomplete", result.RootElement.GetProperty("status").GetString());
                CoreTests.True(!System.IO.Directory.Exists(Path.Combine(work, "not-created")));
            }
            ToolchainRestoreResult value = new(Schema.Version, "toolchain-restore-result", "ready", work, [],
                ToolchainRestore.Boundary);
            CommandSummaryResult summary = CommandSummary.Create(value,
                new CliOptions(["toolchain", "restore", "--directory", work, "--format", "summary"]));
            CoreTests.True(summary is ToolchainRestoreSummary { Status: "ready" });
        });
    }

    private sealed class Fixture
    {
        public string Directory { get; }
        public string Payload { get; }
        public string Archive { get; }
        private readonly AnalysisToolPin pin;

        public Fixture(string root, string work, string? mutant = null)
        {
            Directory = Path.Combine(work, "restore-" + Guid.NewGuid().ToString("N"));
            string fixtures = Path.Combine(root, "tests", "TlaLink.Tests", "Fixtures", "ToolchainRestore");
            Payload = Path.Combine(fixtures, "payload.txt");
            string archive = Path.Combine(work, Guid.NewGuid().ToString("N") + ".archive");
            if (mutant == "tar")
            {
                using FileStream file = File.Create(archive);
                using GZipStream gzip = new(file, CompressionLevel.Fastest);
                using TarWriter tar = new(gzip);
                using FileStream payload = File.OpenRead(Payload);
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "dist/payload.txt") {
                    DataStream = payload, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                });
                tar.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "dist/copy.txt") {
                    LinkName = "payload.txt"
                });
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "dist/empty.txt") {
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
            }
            else
            {
                using ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create);
                zip.CreateEntryFromFile(Payload, "dist/payload.txt");
                zip.CreateEntryFromFile(Path.Combine(fixtures, "launcher.sh"), "dist/launcher.sh");
                if (mutant == "traversal") zip.CreateEntry("dist/../../escape.txt");
                if (mutant == "duplicate") zip.CreateEntryFromFile(Payload, "dist/payload.txt");
                if (mutant == "link")
                {
                    ZipArchiveEntry link = zip.CreateEntry("dist/link");
                    link.ExternalAttributes = 40960 << 16;
                    using StreamWriter writer = new(link.Open(), Encoding.UTF8);
                    writer.Write("../../escape.txt");
                }
                if (mutant == "cycle")
                    foreach ((string name, string target) in new[] { ("a", "b"), ("b", "a") })
                    {
                        ZipArchiveEntry link = zip.CreateEntry("dist/" + name);
                        link.ExternalAttributes = 40960 << 16;
                        using StreamWriter writer = new(link.Open(), Encoding.UTF8);
                        writer.Write(target);
                    }
            }
            pin = new("probe", "1.0", "any", "https://example.invalid/probe", Files.Hash(archive),
                mutant == "tar" ? "tar.gz" : "zip", "dist/", "probe", "payload.txt");
            Archive = Path.Combine(Directory, ".archives", pin.Sha256);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Archive)!);
            File.Move(archive, Archive);
        }

        public ToolchainRestoreResult Restore(bool exclusive = false) =>
            ToolchainRestore.Restore(Directory, [pin], offline: true, exclusive, timeout: 30);
    }
}
