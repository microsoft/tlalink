namespace TlaLink.Tests;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--timeout-probe")
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            return 0;
        }
        string root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\.."));
        string work = Directory.CreateTempSubdirectory("tlalink-dotnet-tests-").FullName;
        List<object> results = [];
        int failed = 0;
        void Test(string name, Action action)
        {
            try { action(); Console.WriteLine("PASS " + name); results.Add(new { Name = name, Status = "passed" }); }
            catch (Exception error)
            {
                failed++; Console.WriteLine("FAIL " + name + "\n" + error);
                results.Add(new { Name = name, Status = "failed", Error = error.ToString() });
            }
        }
        try
        {
            DiagnosticTests.Register(Test);
            if (args.Length == 2 && args[1] == "--p")
            {
                PTests.Register(Test, root, work);
                Console.WriteLine($"{results.Count - failed}/{results.Count} passed.");
                return failed == 0 ? 0 : 1;
            }
            if (args.Length == 2 && args[1] == "--diagnostics")
            {
                Console.WriteLine($"{results.Count - failed}/{results.Count} passed.");
                return failed == 0 ? 0 : 1;
            }
            if (args.Length == 2 && args[1] == "--core")
            {
                JavaToolchainTests.RegisterCore(Test, work);
                CoreTests.Register(Test, work);
                Console.WriteLine($"{results.Count - failed}/{results.Count} passed.");
                return failed == 0 ? 0 : 1;
            }
            if (args.Length == 2 && args[1] == "--integrated")
            {
                IntegratedTests.Register(Test, root, work);
                Console.WriteLine($"{results.Count - failed}/{results.Count} passed.");
                return failed == 0 ? 0 : 1;
            }
            if (args.Length == 2 && args[1] == "--workflow")
            {
                ProjectWorkflowTests.Register(Test, root, work);
                EvidenceHardeningTests.Register(Test, root, work);
                EntityTraceTests.Register(Test, root, work);
                Console.WriteLine($"{results.Count - failed}/{results.Count} passed.");
                return failed == 0 ? 0 : 1;
            }
            CoreTests.Register(Test, work);
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
            Console.WriteLine($"{results.Count - failed}/{results.Count} passed.");
            if (args.Length > 1)
                TlaLink.Files.WriteNew(args[1], new { SchemaVersion = Schema.Version, Kind = "test-results", Results = results, Root = root, Failed = failed, Runtime = Environment.Version.ToString() });
            return failed == 0 ? 0 : 1;
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }
}
