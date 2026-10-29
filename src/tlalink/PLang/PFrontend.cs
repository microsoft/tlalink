using System.Reflection;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TlaLink;

public sealed record PDeclaration(string Name, string Kind, string Path, int Line, int EndLine)
{
    public int Start { get; init; }
    public int End { get; init; }
    public int Column { get; init; } = 1;
}

internal sealed record PFrontendIndex(int SchemaVersion, string Kind, string CompilerVersion, string Profile,
    List<PDeclaration> Declarations);

/// <summary>Source inventory only; native code generation and model checking are separate stages.</summary>
public static class PFrontend
{
    public static List<PDeclaration> Index(string compilerHome, List<string> sources, string output, int timeout,
        Dictionary<string, string> pins, string profile = "pchecker")
    {
        if (profile is not ("pchecker" or "pverifier"))
            throw new InputException(ErrorCode.InvalidModelBinding, "P inventory profile must be pchecker or pverifier.");
        if (timeout is < 1 or > 2147483)
            throw new InputException(ErrorCode.InvalidModelBinding, "P inventory timeout must be 1..2147483 seconds.");
        string home = FilePaths.Full(compilerHome), directory = FilePaths.Full(output);
        List<string> roots = sources.Select(FilePaths.Full).ToList();
        if (roots.Count == 0 || roots.Distinct(FilePaths.Comparer).Count() != roots.Count ||
            roots.Any(path => !File.Exists(path) || !Path.GetExtension(path).Equals(".p", StringComparison.OrdinalIgnoreCase)))
            throw new InputException(ErrorCode.InvalidModelBinding, "P inventory requires distinct explicit .p source files; .pproj discovery is not supported.");
        if (!File.Exists(Path.Combine(home, "CompilerCore.dll")))
            throw new InputException(ErrorCode.ToolFailure, "Missing selected P CompilerCore.dll: " + home);
        Directory.CreateDirectory(directory);
        foreach (string file in Directory.EnumerateFiles(home, "*", new EnumerationOptions
        {
            RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false
        }).Concat(roots)) Files.Pin(pins, FilePaths.Full(file), Files.Hash(file));
        Build(home, directory, pins);
        string dotnet = PProcess.Executable("dotnet");
        Files.Pin(pins, dotnet, Files.Hash(dotnet));
        CheckPins(pins);
        PProcessResult result = PProcess.Run(dotnet,
            [Path.Combine(directory, "PIndex.dll"), home, Path.Combine(directory, "p-index.json"),
                Schema.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), profile, .. roots],
            directory, "p-index", timeout, []);
        if (result.TimedOut || result.ExitCode != 0)
            throw new InputException(ErrorCode.ToolFailure,
                "P inventory " + (result.TimedOut ? "timed out" : "failed") + "; retained logs: " + result.Stdout + ", " + result.Stderr);
        CheckPins(pins);
        string indexPath = Path.Combine(directory, "p-index.json");
        PFrontendIndex index = JsonSerializer.Deserialize<PFrontendIndex>(Files.Read(indexPath), Files.Json)
            ?? throw new InputException(ErrorCode.ToolFailure, "P inventory did not return an index.");
        if (index.SchemaVersion != Schema.Version || index.Kind != "p-source-index")
            throw new InputException($"Unsupported document version or kind; expected schema {Schema.Version} p-source-index.");
        List<PDeclaration> declarations = index.Declarations;
        if (declarations.Any(item => !FilePaths.IsAbsolute(item.Path) ||
            !roots.Contains(item.Path, FilePaths.Comparer) || item.Line < 1 || item.EndLine < item.Line ||
            string.IsNullOrWhiteSpace(item.Name)))
            throw new InputException(ErrorCode.ToolFailure, "P inventory returned invalid or out-of-scope declarations.");
        return declarations;
    }

    private static void Build(string home, string directory, Dictionary<string, string> pins)
    {
        using Stream resource = typeof(PFrontend).Assembly.GetManifestResourceStream("TlaLink.PIndex.source.cs")
            ?? throw new InputException(ErrorCode.ToolFailure, "Missing embedded P inventory bridge.");
        string sourcePath = Path.Combine(directory, "PIndex.source.cs");
        using (FileStream copy = new(sourcePath, FileMode.CreateNew, FileAccess.Write)) resource.CopyTo(copy);
        // The embedded source bytes are covered by the input host-assembly pin;
        // this extracted copy, the bridge binary and the index are output artifacts.
        string source = Files.Read(sourcePath);
        string[] framework = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InputException(ErrorCode.ToolFailure, "Framework metadata references unavailable.")).Split(Path.PathSeparator);
        Dictionary<string, string> references = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in framework) references.TryAdd(Path.GetFileNameWithoutExtension(file), file);
        foreach (string file in Directory.EnumerateFiles(home, "*.dll"))
        {
            try { references[AssemblyName.GetAssemblyName(file).Name!] = file; }
            catch (BadImageFormatException) { }
        }
        foreach (string file in references.Values.Concat(new[] { typeof(CSharpCompilation).Assembly.Location,
            typeof(Compilation).Assembly.Location, typeof(PFrontend).Assembly.Location }).Distinct(FilePaths.Comparer))
            Files.Pin(pins, FilePaths.Full(file), Files.Hash(file));
        CSharpCompilation compilation = CSharpCompilation.Create("TlaLink.PIndex",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.CSharp14), path: sourcePath)],
            references.Values.Select(file => MetadataReference.CreateFromFile(file)),
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable, deterministic: true));
        string binary = Path.Combine(directory, "PIndex.dll");
        using (FileStream stream = new(binary, FileMode.CreateNew, FileAccess.Write))
        {
            var emitted = compilation.Emit(stream);
            if (!emitted.Success)
                throw new InputException(ErrorCode.ToolFailure, "Cannot compile authoritative P frontend bridge: " +
                    string.Join("\n", emitted.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)));
        }
        string runtime = Path.Combine(directory, "PIndex.runtimeconfig.json");
        using (StreamWriter writer = new(new FileStream(runtime, FileMode.CreateNew, FileAccess.Write)))
            writer.Write("""{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");
    }

    private static void CheckPins(Dictionary<string, string> pins)
    {
        foreach ((string file, string hash) in pins)
            if (FilePaths.IsAbsolute(file) &&
                (!FilePaths.IsNativeAbsolute(file) || !File.Exists(file) || !StringComparer.OrdinalIgnoreCase.Equals(Files.Hash(file), hash)))
                throw new InputException(ErrorCode.ToolFailure, "Pinned input changed during P inventory: " + file);
    }
}
