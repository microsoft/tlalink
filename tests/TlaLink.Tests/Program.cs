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
        string[] selections = ["--core", "--apalache", "--p", "--diagnostics", "--integrated", "--workflow", "--evidence"];
        if (testArguments.Length > 2 ||
            testArguments.Count(a => a.StartsWith("--", StringComparison.Ordinal)) > 1 ||
            testArguments.Count(a => !a.StartsWith("--", StringComparison.Ordinal)) > 1 ||
            testArguments.Any(a => a.StartsWith("--", StringComparison.Ordinal) && !selections.Contains(a)))
        {
            Console.Error.WriteLine("Usage: TlaLink.Tests [root [report.json] [--core|--apalache|--p|--diagnostics|--integrated|--workflow|--evidence]]");
            return 2;
        }
        string? selection = testArguments.SingleOrDefault(a => a.StartsWith("--", StringComparison.Ordinal));
        string? reportPath = testArguments.SingleOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
        string work = Directory.CreateTempSubdirectory("tlalink-dotnet-tests-").FullName;
        List<object> results = [];
        int failed = 0;
        ToolHashSession? hashes = null;
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
                StageEvidenceTests.RegisterCore(Test, root, work);
                StageEvidenceTests.RegisterNative(Test, root, work);
                return Finish();
            }
            if (selection == "--apalache")
            {
                ApalacheTests.RegisterCore(Test, work);
                ApalacheTests.RegisterNative(Test, root, work);
                return Finish();
            }
            if (selection == "--p")
            {
                PTests.Register(Test, root, work);
                return Finish();
            }
            if (selection == "--diagnostics")
            {
                return Finish();
            }
            if (selection == "--core")
            {
                ApalacheTests.RegisterCore(Test, work);
                JavaToolchainTests.RegisterCore(Test, work);
                CoreTests.Register(Test, work);
                EvidenceReportTests.Register(Test, work);
                EvidenceRetentionTests.Register(Test, work);
                StageEvidenceTests.RegisterCore(Test, root, work);
                return Finish();
            }
            if (selection == "--integrated")
            {
                IntegratedTests.Register(Test, root, work);
                return Finish();
            }
            if (selection == "--workflow")
            {
                ProjectWorkflowTests.Register(Test, root, work);
                EvidenceHardeningTests.Register(Test, root, work);
                EntityTraceTests.Register(Test, root, work);
                return Finish();
            }
            CoreTests.Register(Test, work);
            EvidenceReportTests.Register(Test, work);
            EvidenceRetentionTests.Register(Test, work);
            StageEvidenceTests.RegisterCore(Test, root, work);
            StageEvidenceTests.RegisterNative(Test, root, work);
            ApalacheTests.RegisterCore(Test, work);
            ApalacheTests.RegisterNative(Test, root, work);
            JavaToolchainTests.RegisterCore(Test, work);
            JavaToolchainTests.RegisterNative(Test, work);
            PTests.Register(Test, root, work);
            IntegrationTests.Register(Test, root, work);
            IntegratedTests.Register(Test, root, work);
            ProjectWorkflowTests.Register(Test, root, work);
            EvidenceHardeningTests.Register(Test, root, work);
            EntityTraceTests.Register(Test, root, work);
            Test("Java/SANY/PlusCal backend parity controls", () =>
                TlaBackendTests.Run(TestTools.Jar(root),
                    (condition, message) => { if (!condition) throw new InvalidOperationException(message); }));
            TraceTests.Register(Test, root, work);
            return Finish();
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
