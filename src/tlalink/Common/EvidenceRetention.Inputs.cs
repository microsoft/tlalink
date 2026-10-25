using System.Security.Cryptography;

namespace TlaLink;

public static partial class EvidenceGarbageCollection
{
    private static (string Hash, string Resolved) ReadInput(string path)
    {
        if (!OperatingSystem.IsWindows()) NoLinks(path);
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        string resolved = OperatingSystem.IsWindows() ? DirectoryLeases.FinalPath(input.SafeFileHandle) : path;
        NoLinks(resolved);
        return (Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(), resolved);
    }
}
