using System.Text.Json;

namespace TlaLink;

public sealed record JavaInstallation(string Home, string Java, string Javac,
    IReadOnlyDictionary<string, string> Fingerprints);

public static class JavaToolchain
{
    private sealed record ToolchainPin(int SchemaVersion, string Kind, string Version,
        string Directory, Dictionary<string, DistributionPin> Platforms);
    private sealed record DistributionPin(string ArchiveUrl, string ArchiveSha256, Dictionary<string, string> Files);

    public static JavaInstallation Resolve()
    {
        using Stream stream = typeof(JavaToolchain).Assembly.GetManifestResourceStream("TlaLink.java-toolchain.json")
            ?? throw new InputException(ErrorCode.ToolFailure, "Missing embedded Java toolchain pin.");
        ToolchainPin pin = JsonSerializer.Deserialize<ToolchainPin>(stream, ProjectInputs.Json)
            ?? throw new InputException(ErrorCode.ToolFailure, "Empty Java toolchain pin.");
        if (pin.SchemaVersion != Schema.Version || pin.Kind != "java-toolchain")
            throw new InputException(ErrorCode.ToolFailure, "Invalid Java toolchain pin schema/kind.");
        string platform = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64
            ? OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : ""
            : "";
        if (!pin.Platforms.TryGetValue(platform, out DistributionPin? distribution))
            throw new InputException(ErrorCode.ToolFailure, "The pinned Java toolchain supports Windows and Linux x64.");
        string? selected = Environment.GetEnvironmentVariable("TLALINK_JAVA_HOME");
        string home = selected is null ? DefaultHome(platform, pin.Directory) :
            !string.IsNullOrWhiteSpace(selected) && FilePaths.IsAbsolute(selected) ? FilePaths.Full(selected) :
            throw new InputException(ErrorCode.ToolFailure, "TLALINK_JAVA_HOME must be an absolute pinned JDK directory.");
        home = Path.TrimEndingDirectorySeparator(home);
        Dictionary<string, string> fingerprints = new(FilePaths.Comparer);
        foreach (var file in distribution.Files)
        {
            string path = FilePaths.Full(file.Key, home);
            if (!File.Exists(path))
                throw new InputException(ErrorCode.ToolFailure,
                    $"Pinned Temurin JDK {pin.Version} file missing or changed: {path}. " +
                    @"Run scripts\Initialize-Java.ps1 explicitly; Java is never selected from PATH or JAVA_HOME.");
            string hash = Files.Hash(path);
            if (!hash.Equals(file.Value, StringComparison.OrdinalIgnoreCase))
                throw new InputException(ErrorCode.ToolFailure, $"Pinned Temurin JDK {pin.Version} file changed: {path}.");
            fingerprints.Add(path, hash);
        }
        return new(home, Path.Combine(home, "bin", FilePaths.ExecutableName("java")),
            Path.Combine(home, "bin", FilePaths.ExecutableName("javac")), fingerprints);
    }

    private static string DefaultHome(string platform, string directory)
    {
        for (DirectoryInfo? ancestor = new(Path.GetDirectoryName(typeof(JavaToolchain).Assembly.Location)!);
            ancestor != null; ancestor = ancestor.Parent)
            if (File.Exists(Path.Combine(ancestor.FullName, "java-toolchain.json")) &&
                File.Exists(Path.Combine(ancestor.FullName, "TlaLink.sln")))
                return Path.Combine(ancestor.FullName, ".tools", "java", platform, directory);
        throw new InputException(ErrorCode.ToolFailure,
            "Cannot locate the repository-local pinned JDK. Set TLALINK_JAVA_HOME to an absolute directory containing the same pinned distribution.");
    }
}
