namespace TlaLink;

public static class FilePaths
{
    public static StringComparer Comparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Native(string path) => path.Replace('\\', Path.DirectorySeparatorChar)
        .Replace('/', Path.DirectorySeparatorChar);

    public static string Full(string path) => Full(path, Environment.CurrentDirectory);

    public static string Full(string path, string directory)
    {
        if (!OperatingSystem.IsWindows() && (HasDrive(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal)))
            throw new InputException("A foreign Windows drive or UNC path cannot be resolved on this host: " + path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Native(path), Native(directory)));
    }

    public static bool IsAbsolute(string path) => Path.IsPathFullyQualified(Native(path)) ||
        HasDrive(path) && path.Length > 2 && path[2] is '\\' or '/' ||
        path.StartsWith(@"\\", StringComparison.Ordinal);

    public static string ExecutableName(string name) => name + (OperatingSystem.IsWindows() ? ".exe" : "");

    private static bool HasDrive(string path) => path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
}
