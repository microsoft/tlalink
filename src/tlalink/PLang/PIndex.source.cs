using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Antlr4.Runtime;
using Plang.Compiler;
using Plang.Compiler.TypeChecker;
using Plang.Compiler.TypeChecker.AST;
using Plang.Compiler.TypeChecker.AST.Declarations;

internal static class PIndex
{
    private const string SupportedVersion = "3.1.0+857776015de5f2683f11945728a1dc57f4b74b33";

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 5) throw new ArgumentException("Expected compiler home, output, schema version, profile and explicit .p files.");
            string home = Path.GetFullPath(args[0]);
            AssemblyLoadContext.Default.Resolving += (_, name) =>
            {
                string path = Path.Combine(home, name.Name + ".dll");
                return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
            };
            Assembly compiler = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(home, "CompilerCore.dll"));
            string? version = compiler.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (compiler.GetName().Version != new Version(3, 1, 0, 0) || version != SupportedVersion)
                throw new NotSupportedException("P inventory requires CompilerCore " + SupportedVersion + "; found " + version);
            Index(args);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception is TargetInvocationException invocation ? invocation.InnerException : exception);
            return 1;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Index(string[] args)
    {
        string profile = args[3];
        if (profile is not ("pchecker" or "pverifier"))
            throw new ArgumentException("P inventory profile must be pchecker or pverifier.");
        string[] paths = args.Skip(4).Select(Path.GetFullPath).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length ||
            paths.Any(path => !File.Exists(path) || !Path.GetExtension(path).Equals(".p", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Only distinct, existing, explicit .p sources are supported.");
        CompilerConfiguration config = new()
        {
            InputPFiles = paths.ToList(),
            ProjectRootPath = new DirectoryInfo(Directory.GetCurrentDirectory()),
            OutputDirectory = new DirectoryInfo(Directory.GetCurrentDirectory()),
            Output = new DefaultCompilerOutput(new DirectoryInfo(Directory.GetCurrentDirectory())),
            OutputLanguages = new List<CompilerOutput>
                { profile == "pverifier" ? CompilerOutput.PVerifier : CompilerOutput.PChecker },
            ContinueOnError = false
        };
        config.Diagnostics = new DefaultDiagnosticCollector(false);
        config.Handler = new DefaultTranslationErrorHandler(config.LocationResolver, config.Diagnostics);
        // The pinned native parser is private; do not replace it with a parallel grammar.
        MethodInfo parse = typeof(Compiler).GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static,
            null, new[] { typeof(ICompilerConfiguration), typeof(FileInfo) }, null)
            ?? throw new NotSupportedException("The pinned Compiler.Parse entry point is unavailable.");
        var trees = paths.Select(path =>
        {
            var tree = (PParser.ProgramContext)(parse.Invoke(null, new object[] { config, new FileInfo(path) })
                ?? throw new InvalidOperationException("P returned no parse tree."));
            config.LocationResolver.RegisterRoot(tree, new FileInfo(path));
            return tree;
        }).ToArray();
        Scope scope = Analyzer.AnalyzeCompilationUnit(config, trees);
        if (config.Diagnostics.HasErrors) throw new InvalidOperationException("Native P analysis reported errors.");
        List<Declaration> declarations = Inventory(config, scope, paths);
        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
        using FileStream output = new(Path.GetFullPath(args[1]), FileMode.CreateNew, FileAccess.Write);
        JsonSerializer.Serialize(output, new
        {
            SchemaVersion = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture),
            Kind = "p-source-index", CompilerVersion = SupportedVersion, Profile = profile, Declarations = declarations
        }, json);
    }

    private static List<Declaration> Inventory(CompilerConfiguration config, Scope scope, string[] paths)
    {
        List<Declaration> declarations = new();
        void Add(IPDecl node, string name, string kind)
        {
            ParserRuleContext context = node.SourceLocation;
            var location = config.LocationResolver.GetLocation(context);
            // Native synthetic declarations (halt, default implementation, anonymous handlers)
            // have no user-source identity and must never become binding targets.
            if (location.File == null || location.Line < 1) return;
            string path = Path.GetFullPath(location.File.FullName);
            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native inventory escaped explicit source scope: " + path);
            int endLine = context.Stop.Line + (context.Stop.Text?.Count(character => character == '\n') ?? 0);
            declarations.Add(new(name, kind, path, location.Line, Math.Max(location.Line, endLine)));
        }
        foreach (Machine machine in scope.Machines)
        {
            Add(machine, machine.Name, machine.IsSpec ? "spec" : "machine");
            foreach (Variable field in machine.Fields) Add(field, machine.Name + "." + field.Name, "field");
            foreach (Function function in machine.Methods.Where(function => !function.IsAnon))
                Add(function, machine.Name + "." + function.Name, "function");
            foreach (var state in machine.States) Add(state, state.QualifiedName, "state");
        }
        foreach (Function function in scope.Functions.Where(function => !function.IsAnon)) Add(function, function.Name, "function");
        foreach (Event item in scope.Events) Add(item, item.Name, "event");
        foreach (var item in scope.SafetyTests) Add(item, item.Name, "testcase");
        foreach (var item in scope.RefinementTests) Add(item, item.Name, "refinement-testcase");
        return declarations.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Line).ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal).ToList();
    }

    private sealed record Declaration(string Name, string Kind, string Path, int Line, int EndLine);
}
