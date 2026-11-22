using System.Diagnostics;

namespace TlaLink.Tests;

public static class PrimitiveKindTests
{
    private sealed record Mutant(string Module, string Invariant);

    private sealed record Kind(string Name, string Harness, string Witness,
        (string Name, int Arity)[] Actions, (string Name, int Arity)[] Waits, Mutant[] Mutants);

    private static readonly Kind[] Kinds =
    [
        new("Register", "MCRegister", "NoLossWitness",
            [("Set", 2), ("TrySet", 3)], [],
            [new("BrokenRegisterSecondWriteWins", "FirstWriteWins")]),
        new("Latch", "MCLatch", "NoFailedWakeWitness",
            [("TrySet", 2), ("TryFail", 2)], [("CanWait", 1)],
            [new("BrokenLatchOverwrite", "OutcomeStable")]),
        new("Once", "MCOnce", "NoLoserResumedWitness",
            [("Publish", 2), ("Start", 1), ("Settle", 1)], [("CanWait", 1)],
            [new("BrokenOnceDoubleWinner", "SingleWinner")]),
        new("AdmissionGate", "MCAdmissionGate", "NoDrainWitness",
            [("Enter", 1), ("EnterRejected", 1), ("TryEnter", 3), ("Exit", 1), ("Close", 1), ("Signal", 1)],
            [("CanWaitDrained", 1)],
            [new("BrokenAdmissionGateEarlySignal", "KindInvariant")]),
        new("CountingResource", "MCCountingResource", "NoClosedWaiterWitness",
            [("TryAcquire", 2), ("Acquire", 2), ("Release", 1), ("Close", 1), ("Resume", 2)],
            [("CanResume", 2)],
            [new("BrokenCountingResourceBarging", "NoBarging"),
             new("BrokenCountingResourceLostWakeup", "KindInvariant")]),
        new("Channel", "MCChannel", "NoDrainedDeliveryWitness",
            [("TryEnqueue", 3), ("Dequeue", 2), ("StartWait", 1), ("Complete", 2)],
            [("CanDequeue", 1)],
            [new("BrokenChannelAbortKeepsItems", "KindInvariant"),
             new("BrokenChannelLateAbortIgnored", "AbortDiscards")]),
        new("CancellationSource", "MCCancellationSource", "NoWakeWitness",
            [("Cancel", 1), ("CancelRejected", 1), ("Dispose", 1)], [("CanWait", 1)],
            [new("BrokenCancellationSourceReset", "RequestStable")]),
    ];

    public static void RegisterNative(Action<string, Action> test, string root, string work)
    {
        string? prepared = null;
        string Prepare()
        {
            if (prepared is not null) return prepared;
            string directory = Directory.CreateDirectory(Path.Combine(work, "primitive-kinds")).FullName;
            string shipped = Path.Combine(root, "src", "tlalink", "Tla", "Primitives");
            string fixtures = Path.Combine(root, "tests", "TlaLink.Tests", "Fixtures", "PrimitiveKinds");
            foreach (string source in Directory.GetFiles(shipped, "*.tla").Concat(Directory.GetFiles(fixtures)))
                File.Copy(source, Path.Combine(directory, Path.GetFileName(source)));
            return prepared = directory;
        }

        test("primitive kind modules parse with SANY and export the kind interface", () =>
        {
            string directory = Prepare(), jar = TestTools.Jar(root);
            foreach (Kind kind in Kinds)
            {
                string module = "TlaLink" + kind.Name;
                var sany = Java(directory, jar, ["tla2sany.SANY", module + ".tla"]);
                Check(sany.Code == 0 && !sany.Output.Contains("rror", StringComparison.Ordinal), module, sany.Output);
                TlaDocument document = TlaBackend.Parse(Path.Combine(directory, module + ".tla"), jar, 120);
                Check(document.ParserSucceeded && document.Diagnostics.All(d => d.Severity != "error"), module,
                    string.Join("\n", document.Diagnostics.Select(d => d.Message)));
                CoreTests.Equal(module, document.Module);
                CoreTests.Equal("state", document.StateVariables.Single());
                Dictionary<string, int> operators = document.Endpoints.Where(e => e.Kind == "operator")
                    .GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First().Arity);
                List<(string Name, int Arity)> expected =
                    [("InitState", 0), ("Init", 0), ("TypeOK", 0), ("Invariant", 0), ("Internal", 1), ("Fairness", 0), ("View", 1)];
                expected.AddRange(kind.Waits);
                foreach (var (name, arity) in kind.Actions)
                    expected.AddRange([(name, arity), (name + "Enabled", arity + 1), (name + "Apply", arity + 1)]);
                foreach (var (name, arity) in expected)
                    Check(operators.TryGetValue(name, out int actual) && actual == arity, module,
                        $"Expected operator {name} with arity {arity}.");
            }
        });
        test("primitive kind harnesses pass TypeOK, kind and harness invariants without deadlock", () =>
        {
            string directory = Prepare(), jar = TestTools.Jar(root);
            foreach (Kind kind in Kinds)
            {
                var run = Tlc(directory, jar, kind.Harness, kind.Harness);
                Check(run.Code == 0 && run.Output.Contains("Model checking completed. No error has been found.",
                    StringComparison.Ordinal), kind.Harness, run.Output);
            }
        });
        test("primitive kind harnesses reach their witness states", () =>
        {
            string directory = Prepare(), jar = TestTools.Jar(root);
            foreach (Kind kind in Kinds)
                ExpectViolation(Tlc(directory, jar, kind.Harness, kind.Harness + "Witness"), kind.Harness, kind.Witness);
        });
        test("primitive kind negative controls are rejected with their defect's invariant", () =>
        {
            string directory = Prepare(), jar = TestTools.Jar(root);
            foreach (Kind kind in Kinds)
            {
                string harness = File.ReadAllText(Path.Combine(directory, kind.Harness + ".tla"));
                foreach (Mutant mutant in kind.Mutants)
                {
                    string control = "MC" + mutant.Module;
                    // A mutant harness differs from its positive harness only in the kind module it instantiates.
                    CoreTests.Equal(harness.Replace($" MODULE {kind.Harness} ", $" MODULE {control} ")
                            .Replace($" INSTANCE TlaLink{kind.Name}", $" INSTANCE {mutant.Module}"),
                        File.ReadAllText(Path.Combine(directory, control + ".tla")));
                    CoreTests.True(File.ReadAllText(Path.Combine(directory, mutant.Module + ".tla"))
                        .Contains($"MODULE {mutant.Module} ", StringComparison.Ordinal));
                    ExpectViolation(Tlc(directory, jar, control, kind.Harness), control, mutant.Invariant);
                }
            }
        });
    }

    private static void ExpectViolation((int Code, string Output) run, string model, string invariant)
    {
        const int SafetyViolation = 12;
        Check(run.Code == SafetyViolation && run.Output.Contains($"Error: Invariant {invariant} is violated.",
            StringComparison.Ordinal), model, run.Output);
    }

    private static (int Code, string Output) Tlc(string directory, string jar, string module, string config)
    {
        string meta = Path.Combine(directory, "states", module + "-" + config);
        return Java(directory, jar, ["tlc2.TLC", "-workers", "1", "-metadir", meta,
            "-config", config + ".cfg", module + ".tla"]);
    }

    private static (int Code, string Output) Java(string directory, string jar, string[] arguments)
    {
        ProcessStartInfo start = new(JavaToolchain.Resolve().Java)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, WorkingDirectory = directory
        };
        foreach (string argument in new[] { "-Djava.io.tmpdir=" + directory, "-cp", jar }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start pinned Java.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(300_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("Java process timed out: " + string.Join(" ", arguments));
        }
        return (process.ExitCode, output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
    }

    private static void Check(bool condition, string model, string detail)
    {
        if (!condition) throw new InvalidOperationException($"{model}: {detail}");
    }
}
