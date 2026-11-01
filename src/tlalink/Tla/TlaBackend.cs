using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TlaLink;

public sealed record TlaEndpoint(
    string Name, string Kind, string Scope, int Start, int End,
    int Line, int Column, int EndLine, int EndColumn, int Arity = 0);

[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record TlaBackendDiagnostic(
    string Code, string Severity, string Message, string? Path = null, int? Line = null)
{
    public TlaBackendDiagnostic(ErrorCode code, string message, string? path = null, int? line = null)
        : this(code.Id(), code.Severity(), message, path, line) { }

    public bool Is(params ErrorCode[] codes) => codes.Any(code => Code == code.Id());
}

public sealed record TlaDocument(
    string Path, string Text, string Hash,
    IReadOnlyList<TlaEndpoint> Endpoints,
    IReadOnlyList<TlaBackendDiagnostic> Diagnostics,
    IReadOnlyDictionary<string, string> DependencyFingerprints)
{
    internal IReadOnlyList<TlaNativeRow>? NativeIndex { get; init; }
    public bool ParserSucceeded => Diagnostics.All(d => d.Code != ErrorCode.ToolFailure.Id());
    public string Module { get; init; } = "";
    public IReadOnlyList<string> StateVariables { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Variables => StateVariables;
}

/// <summary>Read-only SANY/PlusCal bridge; offsets are UTF-16, end-exclusive.</summary>
public static partial class TlaBackend
{
    public static TlaDocument Parse(string path, string jar, int timeoutSeconds)
    {
        string text = "", hash = "";
        var endpoints = new List<TlaEndpoint>();
        var diagnostics = new List<TlaBackendDiagnostic>();
        var dependencies = new Dictionary<string, string>(FilePaths.Comparer);
        IReadOnlyList<string> stateVariables = Array.Empty<string>();
        string module = "";
        IReadOnlyList<TlaNativeRow>? index = null;
        string? work = null;
        try
        {
            if (timeoutSeconds < 1)
                throw new BackendFailure("Java process timeout must be positive.");
            path = FilePaths.Full(path);
            jar = FilePaths.Full(jar);
            (text, hash) = ReadSource(path);
            if (!File.Exists(jar))
                throw new BackendFailure($"TLA parser JAR not found: {jar}");
            JavaInstallation java = JavaToolchain.Resolve();
            string adapter = FindAdapter();
            string jarHash = Digest(jar), adapterHash = Digest(adapter);
            work = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(adapter)!,
                ".tlalink-backend-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            Run("javac", ["-J-Djava.io.tmpdir=" + work, "-encoding", "UTF-8",
                "-cp", jar, "-d", work, adapter], work, timeoutSeconds);
            TlaNativeParse parsed = Index(path, text, jar, work, timeoutSeconds);
            index = parsed.Entries.AsReadOnly();
            AddIndex(index, path, text, true, endpoints, diagnostics, dependencies);
            (module, stateVariables) = AddMetadata(parsed.Metadata, endpoints);
            CheckImports(path, jar, work, timeoutSeconds, endpoints, diagnostics, dependencies);
            foreach (var pin in java.Fingerprints) Files.Pin(dependencies, pin.Key, pin.Value);
            CheckFingerprints(dependencies);
            if (Digest(path) != hash)
                throw new BackendFailure($"Source changed while parsing: {path}");
            if (Digest(jar) != jarHash || Digest(adapter) != adapterHash)
                throw new BackendFailure("TLA parser JAR or adapter changed while parsing.");
        }
        catch (Exception error) when (error is BackendFailure or InputException or IOException
            or UnauthorizedAccessException or ArgumentException or JsonException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            diagnostics.Add(new(ErrorCode.ToolFailure, error.Message, path));
        }
        finally
        {
            if (work is not null && Directory.Exists(work))
            {
                try { Directory.Delete(work, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(new(ErrorCode.ToolFailure, $"Cannot remove owned parser directory {work}: {error.Message}", path));
                }
            }
        }
        return new(path, text, hash, endpoints.AsReadOnly(), diagnostics.AsReadOnly(),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(dependencies))
            { Module = module, StateVariables = stateVariables, NativeIndex = index };
    }

    private static (string Module, IReadOnlyList<string> Variables) AddMetadata(TlaNativeMetadata metadata,
        List<TlaEndpoint> endpoints)
    {
        for (int i = 0; i < endpoints.Count; i++)
            if (endpoints[i].Kind == "operator")
                endpoints[i] = endpoints[i] with
                    { Arity = metadata.Arities.TryGetValue(endpoints[i].Name, out int arity) ? arity :
                        throw new BackendFailure("Missing parser operator arity: " + endpoints[i].Name) };
        return (metadata.Module, Array.AsReadOnly(metadata.Variables
            .Select(v => v ?? throw new BackendFailure("Invalid state variable.")).ToArray()));
    }

    private static void CheckImports(string root, string jar, string work, int timeout,
        List<TlaEndpoint> endpoints, List<TlaBackendDiagnostic> diagnostics,
        Dictionary<string, string> dependencies)
    {
        var checkedPaths = new HashSet<string>(FilePaths.Comparer) { root };
        while (true)
        {
            string? path = dependencies.Keys.Where(p => !checkedPaths.Contains(p))
                .Order(FilePaths.Comparer).FirstOrDefault();
            if (path is null)
                return;
            checkedPaths.Add(path);
            if (path.StartsWith("library:", StringComparison.Ordinal))
                continue;
            var (text, hash) = ReadSource(path);
            if (hash != dependencies[path])
                throw new BackendFailure($"Dependency changed while parsing: {path}");
            if (AlgorithmArguments(text).Length > 0)
                AddIndex(Index(path, text, jar, work, timeout).Entries, path, text, false,
                    endpoints, diagnostics, dependencies);
        }
    }

    private static void CheckFingerprints(Dictionary<string, string> dependencies)
    {
        foreach (var (path, hash) in dependencies)
            if (!path.StartsWith("library:", StringComparison.Ordinal) && Digest(path) != hash)
                throw new BackendFailure($"Dependency changed while parsing: {path}");
    }

    private static TlaNativeParse Index(string path, string text, string jar, string work, int timeout)
    {
        string[] arguments = ["-Dfile.encoding=UTF-8", "-Djava.io.tmpdir=" + work,
            "-cp", work + System.IO.Path.PathSeparator + jar, "pcal.TlaIndex", path,
            .. AlgorithmArguments(text)];
        string json = Run("java", arguments, System.IO.Path.GetDirectoryName(path)!, timeout);
        return TlaNativeJson.ReadParse(json);
    }

    private static string Run(string executable, string[] arguments, string directory, int timeout)
    {
        JavaInstallation java = JavaToolchain.Resolve();
        string selected = executable == "javac" ? java.Javac : java.Java;
        var start = new ProcessStartInfo(selected)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new BackendFailure($"Cannot start {executable}.");
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new BackendFailure($"Cannot start required Java tool {executable}: {error.Message}");
        }
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        try
        {
            Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr)
                .WaitAsync(deadline.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000))
                throw new BackendFailure($"{executable} did not stop within the timeout cleanup budget.");
            throw new BackendFailure($"{executable} exceeded the {timeout}-second process timeout.");
        }
        if (process.ExitCode != 0)
            throw new BackendFailure(
                $"{executable} TLA+/PlusCal parser failed (exit {process.ExitCode}):\n{stderr.Result}\n{stdout.Result}");
        return stdout.Result;
    }

    private static void AddIndex(IReadOnlyList<TlaNativeRow> index, string path, string text, bool includeEndpoints,
        List<TlaEndpoint> endpoints, List<TlaBackendDiagnostic> diagnostics,
        Dictionary<string, string> dependencies)
    {
        foreach (TlaNativeRow row in index)
        {
            switch (row)
            {
                case TlaNativeAssumptions or TlaNativeOperatorLevel:
                    break;
                case TlaNativeDependency dependency:
                    AddDependency(dependencies, dependency.Path, dependency.Sha256);
                    break;
                case TlaNativeTranslation translation:
                    AddTranslation(translation, path, diagnostics);
                    break;
                case TlaNativeEndpoint endpoint:
                    if (includeEndpoints)
                        endpoints.Add(Endpoint(endpoint, text));
                    break;
                default:
                    throw new BackendFailure("Invalid TLA parser entry kind.");
            }
        }
    }

    private static void AddDependency(Dictionary<string, string> dependencies, string path, string hash)
    {
        if (!Regex.IsMatch(hash, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant))
            throw new BackendFailure($"Invalid parser dependency hash: {path}");
        if (dependencies.TryGetValue(path, out string? previous) && previous != hash)
            throw new BackendFailure($"Module changed between parser invocations: {path}");
        dependencies[path] = hash;
    }

    private static TlaEndpoint Endpoint(TlaNativeEndpoint row, string text)
    {
        int line = row.Line, column = row.Column;
        int endLine = row.EndLine, endColumn = row.EndColumn;
        int start = Offset(text, line, column);
        int end = Offset(text, endLine, Math.Max(1, endColumn)) + 1;
        if (end < start || end > text.Length)
            throw new BackendFailure("Invalid parser source extent.");
        return new(row.Name, row.Kind, row.Scope,
            start, end, line, column, endLine, endColumn);
    }

    internal static int ArchivedOffset(string text, int line, int column)
    {
        try { return Offset(text, line, column); }
        catch (BackendFailure error) { throw new InputException(ErrorCode.InvalidModelBinding, error.Message); }
    }

    private static int Offset(string text, int line, int column)
    {
        if (line < 1 || column < 1)
            throw new BackendFailure($"Invalid parser source position {line}:{column}");
        int start = 0;
        for (int current = 1; current < line; current++)
        {
            int newline = text.IndexOf('\n', start);
            if (newline < 0)
                throw new BackendFailure($"Invalid parser source line {line}");
            start = newline + 1;
        }
        int visual = 0, index = start;
        while (index < text.Length && text[index] != '\n')
        {
            if (visual >= column - 1)
                return index;
            visual += text[index] == '\t' ? 8 - visual % 8 : 1;
            index++;
        }
        if (visual == column - 1)
            return index;
        throw new BackendFailure($"Parser source column exceeds line: {line}:{column}");
    }

    private static (string Text, string Hash) ReadSource(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })
            || bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            throw new BackendFailure($"{path}: the TLA parser adapter requires UTF-8 input.");
        int skip = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        try
        {
            return (new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip),
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        catch (DecoderFallbackException error)
        {
            throw new BackendFailure($"{path}: expected valid UTF-8 input: {error.Message}");
        }
    }

    private static string Digest(string path) => Files.Hash(path);

    private static string FindAdapter()
    {
        foreach (string root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            {
                foreach (string relative in new[] { "TlaIndex.java",
                    System.IO.Path.Combine("src", "TlaLink", "Tla", "TlaIndex.java") })
                {
                    string candidate = System.IO.Path.Combine(directory.FullName, relative);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }
        throw new BackendFailure("TLA Java parser adapter TlaIndex.java not found.");
    }

    private sealed class BackendFailure(string message) : Exception(message);

}
