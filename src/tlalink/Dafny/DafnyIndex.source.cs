using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Dafny;

// Built against the explicitly selected local Dafny distribution, not a replacement parser.
internal static class DafnyIndex
{
    public static async Task<int> Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(args[0], name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        return await Index(args);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> Index(string[] args)
    {
        using StringWriter diagnostics = new();
        DafnyOptions options = new(Console.In, diagnostics, diagnostics);
        options.ApplyDefaultOptions();
        ConsoleErrorReporter reporter = new(options);
        List<DafnyFile> files = args.Skip(2).Select(path => DafnyFile.HandleDafnyFile(
            OnDiskFileSystem.Instance, reporter, options, new Uri(path), Token.NoToken, false, false)).ToList();
        var (program, error) = await DafnyMain.ParseCheck(Console.In, files, "TlaLink", options);
        if (error != null || program == null)
        {
            Console.Error.WriteLine(diagnostics + "\n" + error);
            return 2;
        }
        List<object> declarations = new();
        foreach (ModuleDefinition module in program.Modules())
        foreach (TopLevelDeclWithMembers type in module.TopLevelDecls.OfType<TopLevelDeclWithMembers>())
        foreach (MemberDecl member in type.Members)
        {
            if (member.Origin.Uri is not { IsFile: true } uri) continue;
            IEnumerable<Formal> inputs = member switch { Method m => m.Ins, Function f => f.Ins, _ => [] };
            IEnumerable<object> outputs = member switch {
                Method m => m.Outs.Select(p => (object)new { name = p.Name, type = p.Type.ToString(), ghost = p.IsGhost }),
                Function f => [new { name = "result", type = f.ResultType.ToString(), ghost = f.IsGhost }],
                _ => []
            };
            declarations.Add(new {
                name = member.FullDafnyName, module = module.IsDefaultModule ? "" : module.FullDafnyName,
                kind = member.WhatKind, path = uri.LocalPath,
                line = member.StartToken.line, end_line = member.EndToken.line,
                is_static = member.IsStatic, ghost = member.IsGhost,
                has_body = member switch { Method m => m.Body != null, Function f => f.Body != null, _ => false },
                inputs = inputs.Select(p => new { name = p.Name, type = p.Type.ToString(), ghost = p.IsGhost }),
                outputs
            });
        }
        // CompilationData includes files without declarations, too.
        var dependencies = program.Compilation.Includes.Select(i => i.IncludedFilename)
            .Concat(files.Select(f => f.Uri)).Distinct().Select(uri =>
                uri.IsFile ? uri.LocalPath : throw new InvalidOperationException("Only local Dafny source includes are supported."));
        File.WriteAllText(args[1], JsonSerializer.Serialize(new { declarations, dependencies }));
        Console.Write(diagnostics.ToString());
        return 0;
    }
}
