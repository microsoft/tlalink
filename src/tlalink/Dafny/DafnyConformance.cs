using System.Globalization;
using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TlaLink;

public sealed record CaseComparison(string Binding, string Id, string[] Expected, string[] Actual, bool Matches);

public static class DafnyConformance
{
    public static void Run(IntegratedManifest manifest, CSharpScope source,
        List<(ISymbol Symbol, Endpoint Endpoint)> symbols, DafnyBackend backend, IntegratedPacket packet)
    {
        if (manifest.Bindings.Sum(b => b.Cases.Count) == 0)
            throw new InputException(ErrorCode.InvalidOracleInput, "test-integrated requires explicit cases; empty testing is not conformance.");
        string output = backend.Output;
        StringBuilder wrapper = new();
        foreach (string path in manifest.Proofs) wrapper.AppendLine("include " + JsonSerializer.Serialize(path));
        wrapper.AppendLine("module TlaLinkCases {");
        foreach (string module in packet.Links.Where(l => manifest.Bindings.Any(b => b.Id == l.Id && b.Cases.Count > 0))
            .Select(l => l.Specification.Module.Split('.')[0]).Where(m => m.Length > 0).Distinct())
            wrapper.AppendLine("import " + module);
        StringBuilder calls = new();
        List<string> tested = [];
        int caseIndex = 0;
        foreach (DafnyBinding binding in manifest.Bindings.Where(b => b.Cases.Count > 0))
        {
            DafnyDeclaration declaration = packet.Links.Single(l => l.Id == binding.Id).Specification;
            IMethodSymbol adapter = IntegratedReview.Resolve(symbols, binding.Adapter ?? binding.Source).Symbol as IMethodSymbol
                ?? throw new InputException(ErrorCode.InvalidOracleInput, "Executable bindings require a C# method adapter.");
            CheckSignature(adapter, declaration);
            tested.Add(binding.Id);
            foreach (ConformanceCase test in binding.Cases)
            {
                if (test.Inputs.Count != declaration.Inputs.Count)
                    throw new InputException(ErrorCode.InvalidOracleInput, $"{binding.Id}/{test.Id}: wrong input count.");
                string[] literals = test.Inputs.Select((value, i) => Literal(value, declaration.Inputs[i].Type)).ToArray();
                string[] names = declaration.Outputs.Select((_, i) => "r" + i).ToArray();
                string returns = string.Join(", ", declaration.Outputs.Select((p, i) => names[i] + ": " + p.Type));
                string arguments = string.Join(", ", literals);
                wrapper.AppendLine($"method Case{caseIndex}() returns ({returns}) {{");
                wrapper.AppendLine($"{string.Join(", ", names)} := {declaration.Name}({arguments});");
                wrapper.AppendLine("}");
                calls.AppendLine("{");
                if (declaration.Outputs.Count == 1)
                    calls.AppendLine($"{OracleType(declaration.Outputs[0].Type)} e0 = global::TlaLinkCases.@__default.Case{caseIndex}();");
                else
                    calls.AppendLine($"global::TlaLinkCases.@__default.Case{caseIndex}({string.Join(", ", declaration.Outputs.Select((p, i) => $"out {OracleType(p.Type)} e{i}"))});");
                string target = adapter.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + ".@" + adapter.Name;
                calls.AppendLine($"{target}({string.Join(", ", literals.Concat(declaration.Outputs.Select((p, i) => $"out {CSharpType(p.Type)} a{i}")))});");
                string Values(string prefix) => "new string[] { " + string.Join(", ", names.Select((_, i) =>
                    $"{prefix}{i}.ToString()" + (declaration.Outputs[i].Type == "int" ? "" : ".ToLowerInvariant()"))) + " }";
                calls.AppendLine($"Add({JsonSerializer.Serialize(binding.Id)}, {JsonSerializer.Serialize(test.Id)}, {Values("e")}, {Values("a")});");
                calls.AppendLine("}");
                caseIndex++;
            }
        }
        wrapper.AppendLine("}");
        string casesPath = Path.Combine(output, "Cases.dfy");
        File.WriteAllText(casesPath, wrapper.ToString());
        Files.Pin(packet.GeneratedFiles, casesPath, Files.Hash(casesPath));
        StageResult preconditions = backend.Run("case-preconditions", backend.Executable,
            ["verify", casesPath, "--verify-included-files", "--verification-time-limit", backend.TimeoutSeconds.ToString(CultureInfo.InvariantCulture)]);
        if (preconditions.Status != "passed")
        {
            packet.ConformanceStatus = "incomplete";
            packet.Diagnostics.Add(new(ErrorCode.OraclePreconditionsFailed, "Cases do not have verified call preconditions: " + preconditions.Log));
            return;
        }
        string oracle = Path.Combine(output, "Oracle.cs");
        StageResult translation = backend.Run("oracle-translate", backend.Executable,
            ["translate", "cs", casesPath, "--include-runtime", "--output", oracle]);
        if (translation.Status != "passed" || !File.Exists(oracle))
            throw new InputException(ErrorCode.OracleCompilationFailed, "Dafny oracle translation did not complete: " + translation.Log);
        IntegratedInputs.CheckPins(packet.GeneratedFiles);
        string oracleSource = Files.ReadPinned(oracle, packet.GeneratedFiles);
        Dictionary<string, string> referencePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (PortableExecutableReference reference in source.Compilation.References.OfType<PortableExecutableReference>())
        {
            string path = reference.FilePath ?? throw new InputException(ErrorCode.OracleCompilationFailed, "References must be explicit files.");
            string name = AssemblyName.GetAssemblyName(path).Name ?? throw new InputException(ErrorCode.OracleCompilationFailed, "Invalid reference identity.");
            if (referencePaths.TryGetValue(name, out string? prior) && !prior.Equals(path, FilePaths.Comparison))
                throw new InputException(ErrorCode.OracleCompilationFailed, "Ambiguous runtime reference identity: " + name);
            referencePaths[name] = path;
        }
        string runtimeReferences = string.Join(",\n", referencePaths.Select(p =>
            $"[{JsonSerializer.Serialize(p.Key)}] = {JsonSerializer.Serialize(p.Value)}"));
        string comparisons = Path.Combine(output, "case-results.json");
        string harnessPath = Path.Combine(output, "Conformance.cs");
        string harness = $$"""
            using System;
            using System.IO;
            using System.Linq;
            using System.Collections.Generic;
            using System.Globalization;
            using System.Text.Json;
            using System.Reflection;
            using System.Runtime.Loader;
            using System.Runtime.CompilerServices;
            internal static class TlaLinkConformance {
              sealed class CaseComparison {
                public string Binding { get; }
                public string Id { get; }
                public string[] Expected { get; }
                public string[] Actual { get; }
                public bool Matches { get; }
                public CaseComparison(string binding, string id, string[] expected, string[] actual) {
                  Binding = binding; Id = id; Expected = expected; Actual = actual;
                  Matches = expected.SequenceEqual(actual);
                }
              }
              sealed class OracleComparisons {
                public int SchemaVersion => {{Schema.Version}};
                public string Kind => "oracle-comparisons";
                public string Binary { get; }
                public string BinarySHA256 { get; }
                public List<CaseComparison> Cases { get; }
                public OracleComparisons(string binary, string binarySHA256, List<CaseComparison> cases) {
                  Binary = binary; BinarySHA256 = binarySHA256; Cases = cases;
                }
              }
              static readonly List<CaseComparison> Results = new List<CaseComparison>();
              static void Add(string binding, string id, string[] expected, string[] actual) {
                Results.Add(new CaseComparison(binding, id, expected, actual));
              }
              static int Main() {
                var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { {{runtimeReferences}} };
                AssemblyLoadContext.Default.Resolving += (_, name) => references.TryGetValue(name.Name!, out string? path)
                  ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
                return Run();
              }
              [MethodImpl(MethodImplOptions.NoInlining)]
              static int Run() {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                {{calls}}
                string binary = Assembly.GetExecutingAssembly().Location;
                var result = new OracleComparisons(binary,
                  Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(binary))).ToLowerInvariant(),
                  Results);
                File.WriteAllText({{JsonSerializer.Serialize(comparisons)}}, JsonSerializer.Serialize(result,
                  new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
                return 0;
              }
            }
            """;
        File.WriteAllText(harnessPath, harness);
        Files.Pin(packet.GeneratedFiles, harnessPath, Files.Hash(harnessPath));
        var parse = (CSharpParseOptions)source.Compilation.SyntaxTrees.First().Options;
        CSharpCompilation executable = source.Compilation
            .AddSyntaxTrees(CSharpSyntaxTree.ParseText(oracleSource, parse, oracle),
                CSharpSyntaxTree.ParseText(harness, parse, harnessPath))
            .WithAssemblyName("TlaLink.Conformance")
            .WithOptions(source.Compilation.Options.WithOutputKind(OutputKind.ConsoleApplication)
                .WithMainTypeName("TlaLinkConformance"));
        string binary = Path.Combine(output, "Conformance.dll");
        var emitted = executable.Emit(binary);
        if (!emitted.Success)
            throw new InputException(ErrorCode.OracleCompilationFailed, "Oracle/adapter compilation failed: " +
                string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        File.WriteAllText(Path.Combine(output, "Conformance.runtimeconfig.json"),
            """{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}""");
        Dictionary<string, string> compiled = new(FilePaths.Comparer);
        foreach (string path in source.Texts.Keys) Files.Pin(compiled, path, source.Fingerprints[path]);
        foreach (string path in new[] { oracle, harnessPath }) Files.Pin(compiled, path, packet.GeneratedFiles[path]);
        foreach (string path in new[] { binary, Path.Combine(output, "Conformance.runtimeconfig.json") })
        {
            Files.Pin(packet.GeneratedFiles, path, Files.Hash(path));
        }
        IntegratedInputs.CheckPins(packet.Fingerprints);
        IntegratedInputs.CheckPins(compiled);
        IntegratedInputs.CheckPins(packet.GeneratedFiles);
        StageResult execution = backend.Run("oracle-compare", "dotnet", [binary]);
        IntegratedInputs.CheckPins(compiled);
        IntegratedInputs.CheckPins(packet.GeneratedFiles);
        if (execution.Status != "passed" || !File.Exists(comparisons))
            throw new InputException(ErrorCode.OracleExecutionFailed, "Conformance execution did not complete: " + execution.Log);
        OracleComparisons result = JsonSerializer.Deserialize<OracleComparisons>(Files.Read(comparisons), IntegratedInputs.Json)
            ?? throw new InputException(ErrorCode.OracleExecutionFailed, "Missing case results.");
        if (result.SchemaVersion != Schema.Version || result.Kind != "oracle-comparisons")
            throw new InputException(ErrorCode.OracleExecutionFailed, "Expected current oracle-comparisons format.");
        if (result.Binary != binary || result.BinarySHA256 != packet.GeneratedFiles[binary])
            throw new InputException(ErrorCode.OracleExecutionFailed, "Conformance did not execute the pinned entry binary.");
        ImmutableArray<CaseComparison> cases = result.Cases;
        var expectedCases = manifest.Bindings.SelectMany(b => b.Cases.Select(c => (b.Id, c.Id))).ToList();
        if (cases.Length != expectedCases.Count || cases.Any(c => c == null || c.Expected == null || c.Actual == null) ||
            cases.Select(c => (c.Binding, c.Id)).Distinct().Count() != cases.Length ||
            !cases.Select(c => (c.Binding, c.Id)).ToHashSet().SetEquals(expectedCases) ||
            cases.Any(c => c.Matches != c.Expected.SequenceEqual(c.Actual)))
            throw new InputException(ErrorCode.OracleExecutionFailed, "Missing, duplicate or inconsistent comparison results.");
        packet.ConformanceStatus = cases.All(c => c.Matches) ? "conforms" : "rejected";
        if (packet.ConformanceStatus == "rejected")
            packet.Diagnostics.Add(new(ErrorCode.OracleComparisonFailed, "Compiled C# adapter disagreed with the generated Dafny oracle; see " + comparisons));
        packet.Execution = new(compiled.ToImmutableDictionary(FilePaths.Comparer),
            referencePaths.Values.ToImmutableDictionary(p => p, p => source.Fingerprints[p], FilePaths.Comparer),
            new(parse.LanguageVersion.ToString(), [.. parse.PreprocessorSymbolNames],
                executable.Options.NullableContextOptions.ToString(), "net10.0", typeof(CSharpCompilation).Assembly.Location),
            binary, packet.GeneratedFiles[binary], cases, [.. tested],
            [.. manifest.Bindings.Where(b => b.Cases.Count == 0).Select(b => b.Id)], Files.Hash(comparisons),
            "Explicit Roslyn compilation of supplied sources, generated oracle/runtime and harness. Adapters are trusted test code; outputs only, no automatic side-effect, exception, concurrency or C# refinement proof.");
    }

    public static void CheckSignature(ISymbol symbol, DafnyDeclaration declaration)
    {
        if (!declaration.IsStatic || declaration.Ghost || !declaration.HasBody ||
            declaration.Kind is not ("method" or "function" or "predicate") ||
            declaration.Inputs.Concat(declaration.Outputs).Any(p => p.Ghost || p.Type is not ("int" or "bool")) ||
            declaration.Outputs.Count == 0)
            throw new InputException(ErrorCode.InvalidOracleInput, "Executable oracles require static, non-ghost methods/functions with bodies, int/bool inputs and at least one int/bool output.");
        if (symbol is not IMethodSymbol adapter || !adapter.IsStatic || adapter.IsGenericMethod || adapter.ContainingType.IsGenericType ||
            adapter.IsAsync || !adapter.ReturnsVoid || adapter.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal) ||
            adapter.Parameters.Length != declaration.Inputs.Count + declaration.Outputs.Count)
            throw new InputException(ErrorCode.InvalidOracleInput, "Adapter must be an accessible, non-generic synchronous static void method with value inputs followed by out outputs.");
        var parameters = declaration.Inputs.Concat(declaration.Outputs).ToList();
        for (int i = 0; i < parameters.Count; i++)
            if (adapter.Parameters[i].Type.SpecialType != (parameters[i].Type == "int" ? SpecialType.System_Int32 : SpecialType.System_Boolean) ||
                adapter.Parameters[i].RefKind != (i < declaration.Inputs.Count ? RefKind.None : RefKind.Out))
                throw new InputException(ErrorCode.InvalidOracleInput, "Adapter int/bool parameter types or out modes do not match the resolved Dafny signature.");
    }

    private static string Literal(OracleInputScalar value, string type)
    {
        if (type == "bool" && value.Boolean is bool boolean)
            return boolean ? "true" : "false";
        if (type == "int" && value.Integer is int number)
            return number.ToString(CultureInfo.InvariantCulture);
        throw new InputException(ErrorCode.InvalidOracleInput, "Cases require correctly typed bool or signed 32-bit int inputs; no coercions.");
    }

    private static string CSharpType(string type) => type == "int" ? "int" : "bool";
    private static string OracleType(string type) => type == "int" ? "global::System.Numerics.BigInteger" : "bool";
}
