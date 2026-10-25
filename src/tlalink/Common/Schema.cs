namespace TlaLink;

public static class Schema
{
    public const int Version = 4;

    public static bool IsStableId(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100 &&
        value is not ("." or "..") && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static string Hash<T>(T value) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value, Files.Json)));
}
