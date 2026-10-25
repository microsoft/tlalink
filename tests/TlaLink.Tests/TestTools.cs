namespace TlaLink.Tests;

public static class TestTools
{
    public static string Jar(string root) =>
        Resolve(root, "TLALINK_TLA_JAR", @".tools\tla\v1.8.0\tla2tools.jar");

    public static string Dafny(string root) =>
        Resolve(root, "TLALINK_DAFNY", @".tools\dafny\Dafny.exe");

    public static string PCompiler(string root) =>
        Resolve(root, "TLALINK_P_COMPILER", @".tools\p\.store\p\3.1.0\p\3.1.0\tools\net8.0\any\p.dll");

    private static string Resolve(string root, string variable, string relative)
    {
        string path = Path.GetFullPath(Environment.GetEnvironmentVariable(variable) ?? Path.Combine(root, relative));
        if (!File.Exists(path))
            throw new FileNotFoundException($"Test prerequisite missing: {path}. Set {variable} to the selected local tool.", path);
        return path;
    }
}
