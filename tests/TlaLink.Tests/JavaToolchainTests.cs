using System.Diagnostics;
using TlaLink;

namespace TlaLink.Tests;

public static class JavaToolchainTests
{
    public static void RegisterCore(Action<string, Action> test, string work)
    {
        foreach (string value in new[] { "relative-jdk", " ", Path.Combine(work, "missing-jdk") })
            test("Java selection rejects invalid or missing explicit JDK: " + value, () =>
                WithHome(value, () => CoreTests.Throws(() => JavaToolchain.Resolve())));
        test("missing pinned Java is an explicit parser failure without PATH fallback", () =>
        {
            string model = Path.Combine(work, "MissingJava.tla");
            string jar = Path.Combine(work, "placeholder.jar");
            File.WriteAllText(model, "---- MODULE MissingJava ----\nValue == 1\n====\n");
            File.WriteAllText(jar, "not used: Java selection must fail first");
            WithHome(Path.Combine(work, "missing-jdk"), () =>
            {
                TlaDocument parsed = TlaBackend.Parse(model, jar, 30);
                CoreTests.True(!parsed.ParserSucceeded && parsed.Diagnostics.Any(d =>
                    d.Code == "TL900" && d.Message.Contains("Pinned Temurin JDK", StringComparison.Ordinal)));
            });
        });
    }

    public static void RegisterNative(Action<string, Action> test, string work)
    {
        test("pinned java and javac use the same JDK 25 outside the caller directory", () =>
        {
            JavaInstallation java = JavaToolchain.Resolve();
            string previous = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = work;
                CoreTests.Equal(java.Java, JavaToolchain.Resolve().Java);
                CoreTests.Equal(Path.Combine(java.Home, "bin", FilePaths.ExecutableName("java")), java.Java);
                CoreTests.Equal(Path.Combine(java.Home, "bin", FilePaths.ExecutableName("javac")), java.Javac);
                foreach (var pin in java.Fingerprints) CoreTests.Equal(Files.Hash(pin.Key), pin.Value);
                foreach (string executable in new[] { java.Java, java.Javac })
                {
                    ProcessStartInfo start = new(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
                    start.ArgumentList.Add("-version");
                    using Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start pinned Java.");
                    Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(30000))
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit();
                        throw new InvalidOperationException("Pinned Java version query timed out.");
                    }
                    CoreTests.Equal(0, process.ExitCode);
                    CoreTests.True((stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult())
                        .Contains("25.0.4.1", StringComparison.Ordinal));
                }
            }
            finally { Environment.CurrentDirectory = previous; }
        });
        test("relocated JDK cannot substitute changed launcher bytes", () =>
        {
            JavaInstallation java = JavaToolchain.Resolve();
            string relocated = Path.Combine(work, "changed-jdk");
            Directory.CreateDirectory(Path.Combine(relocated, "bin"));
            foreach (string path in java.Fingerprints.Keys)
                File.Copy(path, Path.Combine(relocated, Path.GetRelativePath(java.Home, path)));
            File.AppendAllText(Path.Combine(relocated, "bin", FilePaths.ExecutableName("java")), "changed");
            WithHome(relocated, () => CoreTests.Throws(() => JavaToolchain.Resolve()));
        });
    }

    private static void WithHome(string home, Action action)
    {
        string? previous = Environment.GetEnvironmentVariable("TLALINK_JAVA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("TLALINK_JAVA_HOME", home);
            action();
        }
        finally { Environment.SetEnvironmentVariable("TLALINK_JAVA_HOME", previous); }
    }
}
