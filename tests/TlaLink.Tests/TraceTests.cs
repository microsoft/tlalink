using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class TraceTests
{
    public static void Register(Action<string, Action> test, string root, string work)
    {
        test("partial order does not impose global logging order", () => Run(root, Path.Combine(work, "partial"), parallel: true, hidden: false, trajectory: false, expected: "conforms"));
        test("same-thread program order is enforced", () => Run(root, Path.Combine(work, "thread"), parallel: false, hidden: false, trajectory: false, expected: "rejected"));
        test("one hidden-state trajectory must explain the entire trace", () => Run(root, Path.Combine(work, "trajectory"), parallel: false, hidden: false, trajectory: true, expected: "rejected"));
        test("explicit justified hidden steps can explain an intervening change", () => Run(root, Path.Combine(work, "hidden"), parallel: false, hidden: true, trajectory: true, expected: "conforms"));
    }

    private static void Run(string root, string output, bool parallel, bool hidden, bool trajectory, string expected)
    {
        Directory.CreateDirectory(output);
        string model = Path.Combine(output, "TraceFixture.tla"), source = Path.Combine(output, "Subject.cs");
        string state = trajectory ? """
            readonly object gate = new();
            /* @tla 2 state Value guard=gate type=int; */
            int value;
            """ : "";
        string Statement(string id, int value, int actor) =>
            (trajectory ? "lock (gate) {\n" : "") +
            $"/* @tla 2 step {id} point=commit classification=transition actor=\"{actor}\" operation=\"1\"" +
            (trajectory ? " observe=Value" : "") + "; */\n" +
            (trajectory ? $"this.value = {value};\n}}" : "System.GC.KeepAlive(0);");
        File.WriteAllText(source, "/* @tla 2 model Fixture; */\nclass Subject {\n" + state +
            "\npublic void A() { " + Statement("A", 1, 1) + " }\npublic void B() { " +
            Statement("B", 4, trajectory ? 1 : 2) + " }\n}");
        string body = trajectory ? """
            VARIABLES x, h, phase
            vars == <<x,h,phase>>
            Init == /\ x = 0 /\ h \in 0..1 /\ phase = 0
            (* @tla 2 state Value type=int; *)
            Value == x
            (* @tla 2 step A; *)
            A == /\ phase = 0 /\ phase' = 1
                 /\ x' = (IF h = 0 THEN 1 ELSE 2) /\ UNCHANGED h
            (* @tla 2 step B; *)
            B == /\ phase = 1 /\ phase' = 2
                 /\ x' = (IF h = 0 THEN 3 ELSE 4) /\ UNCHANGED h
            HIDDEN_ANNOTATION
            Flip == /\ h' = 1-h /\ UNCHANGED <<x,phase>>
            Next == A \/ B \/ Flip
            """ : """
            VARIABLE x
            vars == <<x>>
            Init == x = 0
            (* @tla 2 step A; *)
            A == x = 0 /\ x' = 1
            (* @tla 2 step B; *)
            B == x = 1 /\ x' = 2
            Next == A \/ B
            """;
        body = body.Replace("HIDDEN_ANNOTATION", hidden ? "(* @tla 2 step Flip visibility=hidden justification=\"Internal unobserved choice for this synthetic control\"; *)" : "", StringComparison.Ordinal);
        File.WriteAllText(model, "---- MODULE TraceFixture ----\nEXTENDS Integers\n(* @tla 2 model Fixture; *)\n" + body + "\nSpec == Init /\\ [][Next]_vars\n====\n");
        string config = Path.Combine(output, "fixture.cfg");
        File.WriteAllText(config, "SPECIFICATION Spec\n");
        string generated = Path.Combine(output, "generated"), jar = TestTools.Jar(root);
        var instrument = TestCli.Run(["instrument", "--source", source, "--model", model, "--jar", jar, "--output", generated]);
        if (instrument.Code != 0) throw new InvalidOperationException(instrument.Json);
        string workload = trajectory ? "subject.A(); subject.B();" : parallel ? """
            using var started = new System.Threading.ManualResetEventSlim();
            var a = new System.Threading.Thread(() => { started.Set(); System.Threading.Thread.Sleep(200); subject.A(); });
            var b = new System.Threading.Thread(subject.B);
            a.Start(); started.Wait(); b.Start(); a.Join(); b.Join();
            """ : "subject.B(); subject.A();";
        File.WriteAllText(Path.Combine(generated, "Program.cs"), """
            using TlaLink.Runtime;
            class Program { static void Main(string[] args) { var subject = new Subject();
            """ + workload + "\nTraceRuntime.Complete(args[0]); } }");
        string project = Path.Combine(generated, "Fixture.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>");
        TestCli.Restore(project, output, Path.Combine(root, "NuGet.Config"));
        string manifest = Path.Combine(generated, "bindings.json"), trace = Path.Combine(output, "trace.json"), execution = Path.Combine(output, "execution");
        var run = TestCli.Run(["execute", "--manifest", manifest, "--project", project, "--trace", trace, "--output", execution]);
        if (run.Code != 0) throw new InvalidOperationException(run.Json);
        if (parallel)
        {
            using JsonDocument captured = JsonDocument.Parse(Files.Read(trace));
            CoreTests.Equal("B", captured.RootElement.GetProperty("events")[0].GetProperty("step").GetString());
        }
        var check = TestCli.Run(["validate-trace", "--model", model, "--jar", jar, "--manifest", manifest, "--trace", trace,
            "--execution", Path.Combine(execution, "execution.json"), "--config", config, "--output", Path.Combine(output, "validation")]);
        using JsonDocument result = JsonDocument.Parse(check.Json);
        if (result.RootElement.GetProperty("status").GetString() != expected) throw new InvalidOperationException(check.Json);
    }
}
