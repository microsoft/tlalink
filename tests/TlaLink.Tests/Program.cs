namespace TlaLink.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] is "--cli" or "--hash-cache-probe")
        {
            using ToolHashSession inherited = ToolHashSession.Inherit(args[1]);
            if (args[0] == "--cli") return TlaLink.Program.Main(args[2..]);
            if (args.Length != 3) throw new ArgumentException("Hash cache probe requires a snapshot and file.");
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Hash = Files.Hash(args[2]), inherited.Hits }, Files.Json));
            return 0;
        }
        if (args.Length == 1 && args[0] == "--timeout-probe")
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            return 0;
        }
        string root = FilePaths.Full(args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\.."));
        string[] testArguments = args.Skip(1).ToArray();
        string[] selections = ["--core", "--trace", "--apalache", "--p", "--diagnostics", "--integrated", "--workflow", "--evidence"];
        if (testArguments.Length > 2 ||
            testArguments.Count(a => a.StartsWith("--", StringComparison.Ordinal)) > 1 ||
            testArguments.Count(a => !a.StartsWith("--", StringComparison.Ordinal)) > 1 ||
            testArguments.Any(a => a.StartsWith("--", StringComparison.Ordinal) && !selections.Contains(a)))
        {
            Console.Error.WriteLine("Usage: TlaLink.Tests [root [report.json] [--core|--trace|--apalache|--p|--diagnostics|--integrated|--workflow|--evidence]]");
            return 2;
        }
        string? selection = testArguments.SingleOrDefault(a => a.StartsWith("--", StringComparison.Ordinal));
        string? reportPath = testArguments.SingleOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        string work = Directory.CreateTempSubdirectory("tlalink-dotnet-tests-").FullName;
        List<object> results = [];
        int failed = 0;
        ToolHashSession? hashes = null;
        void Suite(string name, Action<string> register) => RunSuite(work, name, register);
        void Test(string name, Action action)
        {
            System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
            try { action(); Console.WriteLine($"PASS {name} ({clock.Elapsed.TotalSeconds:F2}s)"); results.Add(new { Name = name, Status = "passed", DurationSeconds = clock.Elapsed.TotalSeconds }); }
            catch (Exception error)
            {
                failed++; Console.WriteLine("FAIL " + name + "\n" + error);
                results.Add(new { Name = name, Status = "failed", Error = error.ToString(), DurationSeconds = clock.Elapsed.TotalSeconds });
            }
        }
        int Finish()
        {
            if (hashes is not null)
            {
                Test("immutable tool files retain their startup bytes", hashes.Verify);
                Console.WriteLine($"Immutable tool hashes: {hashes.Count} files, {hashes.Hits} in-process cache hits; startup {hashes.StartupSeconds:F2}s.");
            }
            Console.WriteLine($"{results.Count - failed}/{results.Count} passed.");
            if (reportPath is not null)
                TlaLink.Files.WriteNew(reportPath, new { SchemaVersion = Schema.Version, Kind = "test-results", Results = results, Root = root, Failed = failed, Runtime = Environment.Version.ToString() });
            return failed == 0 ? 0 : 1;
        }
        try
        {
            using ToolHashSession session = new(TestTools.ImmutableAssets(root, selection), Path.Combine(work, "tool-hashes.json"));
            hashes = session;
            ToolHashTests.Register(Test, work);
            if (selection is null or "--core" or "--apalache") ApalacheTests.RegisterExample(Test, root);
            DiagnosticTests.Register(Test);
            if (selection == "--evidence")
            {
                Suite("evidence-core", directory => StageEvidenceTests.RegisterCore(Test, root, directory));
                Suite("evidence-native", directory => StageEvidenceTests.RegisterNative(Test, root, directory));
                return Finish();
            }
            if (selection == "--apalache")
            {
                Suite("apalache-core", directory => ApalacheTests.RegisterCore(Test, directory));
                Suite("apalache-native", directory => ApalacheTests.RegisterNative(Test, root, directory));
                return Finish();
            }
            if (selection == "--p")
            {
                Suite("p", directory => PTests.Register(Test, root, directory));
                return Finish();
            }
            if (selection == "--diagnostics")
            {
                return Finish();
            }
            if (selection == "--core")
            {
                Suite("apalache-core", directory => ApalacheTests.RegisterCore(Test, directory));
                Suite("java-core", directory => JavaToolchainTests.RegisterCore(Test, directory));
                Suite("core", directory => CoreTests.Register(Test, directory));
                Suite("tlc-store-core", directory => TlcResultStoreTests.RegisterCore(Test, directory));
                Suite("reports", directory => EvidenceReportTests.Register(Test, directory));
                Suite("retention", directory => EvidenceRetentionTests.Register(Test, directory));
                Suite("evidence-core", directory => StageEvidenceTests.RegisterCore(Test, root, directory));
                return Finish();
            }
            if (selection == "--integrated")
            {
                Suite("integrated", directory => IntegratedTests.Register(Test, root, directory));
                return Finish();
            }
            if (selection == "--trace")
            {
                Suite("paired-native", directory => PairedCorrespondenceTests.RegisterNative(Test, root, directory));
                Suite("arguments-core", directory => ActionArgumentTests.Register(Test, directory));
                Suite("entities", directory => EntityTraceTests.Register(Test, root, directory));
                Suite("arguments-native", directory => ActionArgumentTests.RegisterNative(Test, root, directory));
                Suite("traces", directory => TraceTests.Register(Test, root, directory));
                return Finish();
            }
            if (selection == "--workflow")
            {
                Suite("workflow", directory => ProjectWorkflowTests.Register(Test, root, directory));
                Suite("tlc-store", directory => TlcResultStoreTests.RegisterNative(Test, root, directory));
                Suite("hardening", directory => EvidenceHardeningTests.Register(Test, root, directory));
                Suite("entities", directory => EntityTraceTests.Register(Test, root, directory));
                return Finish();
            }
            Suite("core", directory => CoreTests.Register(Test, directory));
            Suite("paired-native", directory => PairedCorrespondenceTests.RegisterNative(Test, root, directory));
            Suite("tlc-store-core", directory => TlcResultStoreTests.RegisterCore(Test, directory));
            Suite("reports", directory => EvidenceReportTests.Register(Test, directory));
            Suite("retention", directory => EvidenceRetentionTests.Register(Test, directory));
            Suite("evidence-core", directory => StageEvidenceTests.RegisterCore(Test, root, directory));
            Suite("evidence-native", directory => StageEvidenceTests.RegisterNative(Test, root, directory));
            Suite("apalache-core", directory => ApalacheTests.RegisterCore(Test, directory));
            Suite("apalache-native", directory => ApalacheTests.RegisterNative(Test, root, directory));
            Suite("java-core", directory => JavaToolchainTests.RegisterCore(Test, directory));
            Suite("java-native", directory => JavaToolchainTests.RegisterNative(Test, directory));
            Suite("p", directory => PTests.Register(Test, root, directory));
            Suite("integration", directory => IntegrationTests.Register(Test, root, directory));
            Suite("integrated", directory => IntegratedTests.Register(Test, root, directory));
            Suite("workflow", directory => ProjectWorkflowTests.Register(Test, root, directory));
            Suite("tlc-store", directory => TlcResultStoreTests.RegisterNative(Test, root, directory));
            Suite("hardening", directory => EvidenceHardeningTests.Register(Test, root, directory));
            Suite("entities", directory => EntityTraceTests.Register(Test, root, directory));
            Suite("prepare-native", directory => SourcePreparationTests.RegisterNative(Test, directory, TestTools.Jar(root)));
            Suite("arguments-native", directory => ActionArgumentTests.RegisterNative(Test, root, directory));
            Test("Java/SANY/PlusCal backend parity controls", () =>
                TlaBackendTests.Run(TestTools.Jar(root),
                    (condition, message) => { if (!condition) throw new InvalidOperationException(message); }));
            Suite("traces", directory => TraceTests.Register(Test, root, directory));
            return Finish();
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    internal static void RunSuite(string work, string name, Action<string> register)
    {
        string directory = Directory.CreateDirectory(Path.Combine(work, name)).FullName;
        try
        {
            register(directory);
            EnumerationOptions options = new() {
                RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false
            };
            long bytes = Directory.EnumerateFiles(directory, "*", options).Sum(file => new FileInfo(file).Length);
            Console.WriteLine($"SUITE {name}: {bytes} fixture bytes released.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
