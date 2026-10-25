using System.Text.Json;

namespace TlaLink;

public sealed record JavaInstallation(string Home, string Java, string Javac,
    IReadOnlyDictionary<string, string> Fingerprints);

public static class JavaToolchain
{
    private sealed record ToolchainPin(int SchemaVersion, string Kind, string Version,
        string Directory, string ArchiveUrl, string ArchiveSha256, Dictionary<string, string> Files);

    public static JavaInstallation Resolve()
    {
        using Stream stream = typeof(JavaToolchain).Assembly.GetManifestResourceStream("TlaLink.java-toolchain.json")
            ?? throw new InputException(ErrorCode.ToolFailure, "Missing embedded Java toolchain pin.");
        ToolchainPin pin = JsonSerializer.Deserialize<ToolchainPin>(stream, ProjectInputs.Json)
            ?? throw new InputException(ErrorCode.ToolFailure, "Empty Java toolchain pin.");
        if (pin.SchemaVersion != Schema.Version || pin.Kind != "java-toolchain")
            throw new InputException(ErrorCode.ToolFailure, "Invalid Java toolchain pin schema/kind.");
        string? selected = Environment.GetEnvironmentVariable("TLALINK_JAVA_HOME");
        string home = selected is null ? DefaultHome(pin.Directory) :
            !string.IsNullOrWhiteSpace(selected) && Path.IsPathFullyQualified(selected) ? Path.GetFullPath(selected) :
            throw new InputException(ErrorCode.ToolFailure, "TLALINK_JAVA_HOME must be an absolute pinned JDK directory.");
        home = Path.TrimEndingDirectorySeparator(home);
        Dictionary<string, string> fingerprints = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in pin.Files)
        {
            string path = Path.GetFullPath(file.Key, home);
            if (!File.Exists(path))
                throw new InputException(ErrorCode.ToolFailure,
                    $"Pinned Temurin JDK {pin.Version} file missing or changed: {path}. " +
                    @"Run scripts\Initialize-Java.ps1 explicitly; Java is never selected from PATH or JAVA_HOME.");
            string hash = Files.Hash(path);
            if (!hash.Equals(file.Value, StringComparison.OrdinalIgnoreCase))
                throw new InputException(ErrorCode.ToolFailure, $"Pinned Temurin JDK {pin.Version} file changed: {path}.");
            fingerprints.Add(path, hash);
        }
        return new(home, Path.Combine(home, @"bin\java.exe"), Path.Combine(home, @"bin\javac.exe"), fingerprints);
    }

    private static string DefaultHome(string directory)
    {
        for (DirectoryInfo? ancestor = new(Path.GetDirectoryName(typeof(JavaToolchain).Assembly.Location)!);
            ancestor != null; ancestor = ancestor.Parent)
            if (File.Exists(Path.Combine(ancestor.FullName, "java-toolchain.json")) &&
                File.Exists(Path.Combine(ancestor.FullName, "TlaLink.sln")))
                return Path.Combine(ancestor.FullName, @".tools\java", directory);
        throw new InputException(ErrorCode.ToolFailure,
            "Cannot locate the repository-local pinned JDK. Set TLALINK_JAVA_HOME to an absolute directory containing the same pinned distribution.");
    }
}
