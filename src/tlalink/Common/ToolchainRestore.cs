using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using TlaLink.Setup;

namespace TlaLink;

public sealed record AnalysisToolPin(string Id, string Version, string Platform, string Url, string Sha256,
    string Format, string Prefix, string Directory, string Entrypoint);
public sealed record AnalysisToolchain(int SchemaVersion, string Kind, List<AnalysisToolPin> Tools);
public sealed record RestoredTool(string Id, string Version, string Status, string Directory, string Entrypoint,
    string ArchiveSha256, int FileCount, string? ReplacedDirectory);
public sealed record ToolchainRestoreResult(int SchemaVersion, string Kind, string Status, string Directory,
    List<RestoredTool> Tools, string Boundary);
internal sealed record RestoredFile(string Sha256, int Mode);

public static class ToolchainRestore
{
    public const string Boundary = "Explicit setup, not analysis or approval. Archive pins and complete file bytes/modes " +
        "are checked; publisher authentication and runtime prerequisites are separate. Repairs require offline users " +
        "and retain displaced files. Project pins, evidence and reviews are not updated.";

    public static int Run(CliOptions options)
    {
        List<string> selected = options.Many("tool");
        List<AnalysisToolPin> tools = Catalogue();
        if (selected.Count != selected.Distinct(StringComparer.Ordinal).Count() ||
            selected.Any(id => !tools.Any(t => t.Id == id)))
            throw new InputException("Select distinct supported --tool values: java, tlc, dafny, apalache, p.");
        if (selected.Count > 0) tools = tools.Where(t => selected.Contains(t.Id, StringComparer.Ordinal)).ToList();
        static bool Flag(CliOptions options, string name) => options.Get(name, "false") switch {
            "true" => true, "false" => false, _ => throw new InputException("--" + name + " requires true or false.")
        };
        try
        {
            ToolchainRestoreResult result = Restore(options.Required("directory"), tools,
                Flag(options, "offline"), Flag(options, "exclusive"), options.Timeout);
            ProjectWorkflow.Render(result, options);
            return 0;
        }
        catch (HttpRequestException error)
        {
            throw new InputException(ErrorCode.ToolFailure, "Tool download failed: " + error.Message);
        }
        catch (OperationCanceledException error)
        {
            throw new InputException(ErrorCode.ToolFailure, "Tool download exceeded its timeout: " + error.Message);
        }
    }

    internal static List<AnalysisToolPin> Catalogue(string? platform = null)
    {
        platform ??= RuntimeInformation.OSArchitecture == Architecture.X64
            ? OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : ""
            : "";
        if (platform is not ("win-x64" or "linux-x64"))
            throw new InputException("Toolchain restore supports Windows and Linux x64.");
        string Resource(string name)
        {
            using Stream stream = typeof(ToolchainRestore).Assembly.GetManifestResourceStream("TlaLink." + name)
                ?? throw new InputException("Missing bundled toolchain pin: " + name);
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }
        AnalysisToolchain catalogue = JsonSerializer.Deserialize<AnalysisToolchain>(Resource("analysis-toolchain.json"),
            ProjectInputs.Json) ?? throw new InputException("Missing analysis toolchain.");
        if (catalogue.SchemaVersion != Schema.Version || catalogue.Kind != "analysis-toolchain")
            throw new InputException("Expected schema-4 analysis-toolchain.");
        NativeSharedJavaDocument java = NativeSharedJavaDocument.Read(Resource("java-toolchain.json"));
        NativeToolchainDocument native = NativeToolchainDocument.Read(Resource("native-toolchain.json"));
        NativeJavaPlatformPin jdk = java.platforms[platform];
        List<AnalysisToolPin> tools = catalogue.Tools.Where(t => t.Platform is "any" || t.Platform == platform).ToList();
        if (tools.Single(t => t.Id == "p").Version != native.p.version)
            throw new InputException("Prebuilt P package must match native-toolchain.json's P selection.");
        tools.Insert(0, new("java", java.version, platform, jdk.archive_url, jdk.archive_sha256,
            platform == "win-x64" ? "zip" : "tar.gz", java.directory + "/",
            $"java/{platform}/{java.directory}", platform == "win-x64" ? "bin/java.exe" : "bin/java"));
        if (tools.Count != 5 || tools.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != 5)
            throw new InputException("Expected one prebuilt selection for each of java, tlc, dafny, apalache and p.");
        foreach (AnalysisToolPin tool in tools) Validate(tool);
        return tools;
    }

    internal static ToolchainRestoreResult Restore(string directory, List<AnalysisToolPin> tools,
        bool offline, bool exclusive, int timeout)
    {
        directory = FilePaths.Full(directory);
        if (tools.Count == 0) throw new InputException("Select at least one tool to restore.");
        foreach (AnalysisToolPin tool in tools) Validate(tool);
        if (tools.Select(t => t.Directory).Distinct(FilePaths.Comparer).Count() != tools.Count)
            throw new InputException("Tool installation directories must be distinct.");
        OrdinaryPath(directory);
        Directory.CreateDirectory(directory);
        string lockPath = Path.Combine(directory, ".restore.lock");
        OrdinaryPath(lockPath);
        using FileStream lease = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        List<RestoredTool> results = [];
        foreach (AnalysisToolPin tool in tools)
        {
            string archive = Path.Combine(directory, ".archives", tool.Sha256.ToLowerInvariant());
            OrdinaryPath(archive);
            Acquire(tool, archive, offline, timeout);
            Dictionary<string, RestoredFile> expected = ReadArchive(tool, archive, null);
            string destination = SafePath(directory, tool.Directory);
            OrdinaryPath(destination);
            if (Matches(destination, expected))
            {
                results.Add(new(tool.Id, tool.Version, "current", destination,
                    SafePath(destination, tool.Entrypoint), tool.Sha256, expected.Count, null));
                continue;
            }
            bool exists = Directory.Exists(destination) || File.Exists(destination);
            if (exists && !exclusive)
                throw new InputException("Installation differs: " + destination +
                    ". Stop all users of this cache and repeat with --exclusive true to stage a replacement.");
            string stage = Path.Combine(directory, ".staging", Guid.NewGuid().ToString("N"));
            OrdinaryPath(stage);
            Directory.CreateDirectory(stage);
            ReadArchive(tool, archive, stage);
            if (!Matches(stage, expected))
                throw new InputException("Staged installation failed its complete file check; retained: " + stage);
            // Recheck the archive before changing an installation; the cache is not an immutable authority.
            if (!Files.Hash(archive).Equals(tool.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InputException("Archive changed during restore; staged files retained: " + stage);
            OrdinaryPath(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string? replaced = null;
            if (exists)
            {
                replaced = Path.Combine(directory, ".replaced", tool.Id + "-" + Guid.NewGuid().ToString("N"));
                OrdinaryPath(replaced);
                Directory.CreateDirectory(Path.GetDirectoryName(replaced)!);
                if (File.Exists(destination)) File.Move(destination, replaced);
                else Directory.Move(destination, replaced);
            }
            try { Directory.Move(stage, destination); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (replaced != null && !Directory.Exists(destination) && !File.Exists(destination))
                {
                    if (File.Exists(replaced)) File.Move(replaced, destination);
                    else Directory.Move(replaced, destination);
                }
                throw new InputException("Could not promote the staged tool; staging retained at " + stage +
                    ". Previous installation location: " + (replaced ?? "(none)") + ". " + error.Message);
            }
            results.Add(new(tool.Id, tool.Version, exists ? "repaired" : "installed", destination,
                SafePath(destination, tool.Entrypoint), tool.Sha256, expected.Count, replaced));
        }
        return new(Schema.Version, "toolchain-restore-result", "ready", directory, results, Boundary);
    }

    private static void Validate(AnalysisToolPin tool)
    {
        if (!Schema.IsStableId(tool.Id) || string.IsNullOrWhiteSpace(tool.Version) ||
            !Uri.TryCreate(tool.Url, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) || tool.Sha256.Length != 64 || !tool.Sha256.All(Uri.IsHexDigit) ||
            tool.Format is not ("file" or "zip" or "tar.gz"))
            throw new InputException("Invalid pinned tool archive: " + tool.Id);
        RelativeName(tool.Directory);
        RelativeName(tool.Entrypoint);
        if (tool.Prefix.Length > 0) RelativeName(tool.Prefix.TrimEnd('/'));
    }

    private static void Acquire(AnalysisToolPin tool, string archive, bool offline, int timeout)
    {
        if (File.Exists(archive) && Files.Hash(archive).Equals(tool.Sha256, StringComparison.OrdinalIgnoreCase)) return;
        if (offline) throw new InputException("Missing or damaged pinned archive in offline mode: " + archive);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        string temporary = archive + "." + Guid.NewGuid().ToString("N") + ".download";
        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(timeout) };
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(timeout));
        using HttpResponseMessage response = client.GetAsync(tool.Url, HttpCompletionOption.ResponseHeadersRead,
            deadline.Token).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != "https")
            throw new InputException("Tool download redirected outside HTTPS: " + tool.Id);
        using (Stream input = response.Content.ReadAsStream(deadline.Token))
        using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            input.CopyToAsync(output, deadline.Token).GetAwaiter().GetResult();
        if (!Files.Hash(temporary).Equals(tool.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InputException("Downloaded archive SHA256 mismatch; retained for diagnosis: " + temporary);
        File.Move(temporary, archive, overwrite: true);
    }

    private static Dictionary<string, RestoredFile> ReadArchive(AnalysisToolPin tool, string archive, string? output)
    {
        Dictionary<string, RestoredFile> files = new(FilePaths.Comparer);
        Dictionary<string, string> links = new(FilePaths.Comparer);
        HashSet<string> entries = new(FilePaths.Comparer);
        void Entry(string name, Stream? data, int mode, string? link = null, bool isDirectory = false)
        {
            name = name.Replace('\\', '/').TrimEnd('/');
            RelativeName(name);
            if (!entries.Add(name)) throw new InputException("Duplicate archive entry: " + name);
            if (tool.Prefix.Length > 0 && !name.StartsWith(tool.Prefix, StringComparison.Ordinal)) return;
            string relative = tool.Prefix.Length == 0 ? name : name[tool.Prefix.Length..];
            if (relative.Length == 0 || isDirectory) return;
            RelativeName(relative);
            if (link != null)
            {
                if (FilePaths.IsAbsolute(link)) throw new InputException("Absolute archive link: " + name);
                string target = FilePaths.Full(link, Path.GetDirectoryName(SafePath(Path.GetTempPath(), relative))!);
                string resolved = FilePaths.Relative(target, Path.GetTempPath());
                RelativeName(resolved);
                links.Add(FilePaths.Native(relative), FilePaths.Native(resolved));
                return;
            }
            data ??= Stream.Null;
            string hash;
            if (output == null) hash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
            else
            {
                string path = SafePath(output, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) data.CopyTo(file);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)mode);
                hash = Files.Hash(path);
            }
            files.Add(FilePaths.Native(relative), new(hash, OperatingSystem.IsWindows() ? 0 : mode));
        }
        using FileStream stream = File.OpenRead(archive);
        if (tool.Format == "file") Entry(tool.Entrypoint, stream, 420);
        else if (tool.Format == "zip")
        {
            using ZipArchive zip = new(stream, ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                int type = (entry.ExternalAttributes >> 16) & 61440;
                bool directory = entry.FullName.EndsWith('/') || type == 16384;
                if (type is not (0 or 32768 or 16384 or 40960))
                    throw new InputException("Unsupported archive entry type: " + entry.FullName);
                using Stream input = entry.Open();
                if (type == 40960)
                {
                    using StreamReader reader = new(input);
                    Entry(entry.FullName, null, 0, reader.ReadToEnd());
                }
                else
                {
                    int permissions = (entry.ExternalAttributes >> 16) & 511;
                    Entry(entry.FullName, directory ? null : input, permissions != 0 ? permissions : 420,
                        isDirectory: directory);
                }
            }
        }
        else
        {
            using GZipStream gzip = new(stream, CompressionMode.Decompress);
            using TarReader tar = new(gzip);
            while (tar.GetNextEntry() is { } entry)
            {
                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or
                    TarEntryType.Directory or TarEntryType.SymbolicLink))
                    throw new InputException("Unsupported tar entry type: " + entry.Name);
                Entry(entry.Name, entry.DataStream, (int)entry.Mode & 511,
                    entry.EntryType == TarEntryType.SymbolicLink ? entry.LinkName : null,
                    entry.EntryType == TarEntryType.Directory);
            }
        }
        RestoredFile ResolveLink(string name, HashSet<string> visiting)
        {
            name = FilePaths.Native(name);
            if (files.TryGetValue(name, out RestoredFile? file)) return file;
            if (!visiting.Add(name) || !links.TryGetValue(name, out string? target))
                throw new InputException("Cyclic, missing or external archive file link: " + name);
            RestoredFile resolved = ResolveLink(target, visiting);
            if (output != null)
            {
                string path = SafePath(output, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(SafePath(output, target), path);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)resolved.Mode);
            }
            files.Add(name, resolved);
            return resolved;
        }
        foreach (string link in links.Keys) ResolveLink(link, new(FilePaths.Comparer));
        if (files.Count == 0 || !files.ContainsKey(FilePaths.Native(tool.Entrypoint)))
            throw new InputException("Archive lacks selected tool entrypoint: " + tool.Entrypoint);
        return files;
    }

    private static bool Matches(string directory, Dictionary<string, RestoredFile> expected)
    {
        if (!Directory.Exists(directory)) return false;
        List<string> files = [];
        Queue<string> directories = new([directory]);
        while (directories.TryDequeue(out string? current))
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
                if ((attributes & FileAttributes.Directory) != 0) directories.Enqueue(entry);
                else files.Add(entry);
            }
        }
        return files.Count == expected.Count && files.All(path =>
            expected.TryGetValue(Path.GetRelativePath(directory, path), out RestoredFile? file) &&
            Files.Hash(path) == file.Sha256 &&
            (OperatingSystem.IsWindows() || (int)File.GetUnixFileMode(path) == file.Mode));
    }

    private static void RelativeName(string name)
    {
        string normalized = name.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(name) || FilePaths.IsAbsolute(name) || normalized.Contains(':') ||
            normalized.Split('/').Any(part => part is "" or "." or ".." ||
                OperatingSystem.IsWindows() && (part.EndsWith('.') || part.EndsWith(' ') ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)))
            throw new InputException("Unsafe relative archive/tool path: " + name);
    }

    private static string SafePath(string root, string relative)
    {
        RelativeName(relative);
        string path = FilePaths.Full(relative, root);
        if (!path.StartsWith(FilePaths.Full(root) + Path.DirectorySeparatorChar, FilePaths.Comparison))
            throw new InputException("Tool path escapes its declared root: " + relative);
        return path;
    }

    private static void OrdinaryPath(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InputException("Toolchain paths must not traverse filesystem links: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
