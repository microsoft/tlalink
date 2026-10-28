namespace TlaLink.Tests;

public static class TestTools
{
    public static IEnumerable<string> ImmutableAssets(string root, string? selection)
    {
        yield return typeof(TlaLink.Program).Assembly.Location;
        yield return typeof(Microsoft.CodeAnalysis.Compilation).Assembly.Location;
        yield return typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly.Location;
        string[] tools = selection switch
        {
            "--core" or "--diagnostics" => [],
            "--apalache" => [Jar(root), Apalache(root)],
            "--p" => [PCompiler(root)],
            "--trace" => [Jar(root)],
            "--integrated" or "--workflow" or "--evidence" => [Jar(root), Dafny(root)],
            null => [Jar(root), Apalache(root), Dafny(root), PCompiler(root)],
            _ => throw new ArgumentException("Unknown test selection: " + selection)
        };
        EnumerationOptions options = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (string tool in tools)
        {
            yield return tool;
            if (!Path.GetExtension(tool).Equals(".jar", StringComparison.OrdinalIgnoreCase))
                foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(tool)!, "*", options))
                    yield return file;
        }
        if (selection is not ("--core" or "--diagnostics" or "--p"))
            foreach (string file in JavaToolchain.Resolve().Fingerprints.Keys) yield return file;
    }

    public static string Apalache(string root) =>
        Resolve(root, "TLALINK_APALACHE_JAR", @".tools\apalache\0.62.4\lib\apalache.jar");

    public static string Jar(string root) =>
        Resolve(root, "TLALINK_TLA_JAR", @".tools\tla\v1.8.0\tla2tools.jar");

    public static string Dafny(string root) =>
        Resolve(root, "TLALINK_DAFNY", Path.Combine(".tools", "dafny",
            TlaLink.FilePaths.ExecutableName(OperatingSystem.IsWindows() ? "Dafny" : "dafny")));

    public static string PCompiler(string root) =>
        Resolve(root, "TLALINK_P_COMPILER", @".tools\p\.store\p\3.1.0\p\3.1.0\tools\net8.0\any\p.dll");

    private static string Resolve(string root, string variable, string relative)
    {
        string path = TlaLink.FilePaths.Full(Environment.GetEnvironmentVariable(variable) ?? Path.Combine(root, relative));
        if (!File.Exists(path))
            throw new FileNotFoundException($"Test prerequisite missing: {path}. Set {variable} to the selected local tool.", path);
        return path;
    }
}
