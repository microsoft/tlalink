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
        test("trace wrapper supplies its own arithmetic imports for a boolean-only model", () =>
            Run(root, Path.Combine(work, "boolean"), parallel: false, hidden: false, trajectory: false, expected: "conforms", booleanOnly: true));
        test("reference actor identities survive generated execution and trace matching", () =>
            Run(root, Path.Combine(work, "reference-actor"), parallel: false, hidden: true, trajectory: true, expected: "conforms", referenceActor: true));
        test("overlapping async invocations retain distinct operation identities across awaits", () =>
            ConcurrentCalls(root, Path.Combine(work, "concurrent-calls")));
        test("explicit reference entities are passed to three-argument actions", () =>
            ConcurrentCalls(root, Path.Combine(work, "reference-entities"), entity: true));
    }

    private static void ConcurrentCalls(string root, string output, bool entity = false)
    {
        Directory.CreateDirectory(output);
        string source = Path.Combine(output, "Subject.cs"), model = Path.Combine(output, "Calls.tla");
        File.WriteAllText(source, """
            using System.Threading.Tasks;

            class Subject {
                public async Task Run(Task release) {
                    /* @link {"bind":[{"id":"BeginCall","target":"Calls::BeginCall"}],"capture":[{"binding":"BeginCall","actor":"this","operation":"$call","point":"commit","classification":"transition"}]} */
                    System.GC.KeepAlive(0);
                    await release;
                    /* @link {"bind":[{"id":"EndCall","target":"Calls::EndCall"}],"capture":[{"binding":"EndCall","actor":"this","operation":"$call","point":"commit","classification":"transition"}]} */
                    System.GC.KeepAlive(0);
                }
            }
            """);
        File.WriteAllText(model, """
            ---- MODULE Calls ----

            Ops == {"call:1", "call:2"}
            VARIABLE pc
            vars == <<pc>>
            Init == pc = [op \in Ops |-> "New"]

            BeginCall(actor, op) == pc[op] = "New" /\ pc' = [pc EXCEPT ![op] = "Pending"]

            EndCall(actor, op) == pc[op] = "Pending" /\ pc' = [pc EXCEPT ![op] = "Done"]
            Next == \E op \in Ops : BeginCall(1, op) \/ EndCall(1, op)
            Spec == Init /\ [][Next]_vars
            ====
            """);
        if (entity)
        {
            File.WriteAllText(source, Files.Read(source).Replace("Run(Task release)", "Run(Task release, Subject target)")
                .Replace("\"actor\":\"this\"", "\"actor\":\"this\",\"entity\":\"target\""));
            File.WriteAllText(model, """
                ---- MODULE Calls ----

                Ops == {"call:1", "call:2"}
                VARIABLE pc, target
                vars == <<pc, target>>
                Init == pc = [op \in Ops |-> "New"] /\ target = [op \in Ops |-> ""]

                BeginCall(actor, op, entity) ==
                  /\ pc[op] = "New" /\ pc' = [pc EXCEPT ![op] = "Pending"]
                  /\ target' = [target EXCEPT ![op] = entity]

                EndCall(actor, op, entity) ==
                  /\ pc[op] = "Pending" /\ target[op] = entity
                  /\ pc' = [pc EXCEPT ![op] = "Done"] /\ UNCHANGED target
                Next == \E op \in Ops : BeginCall(1, op, "object:2") \/ EndCall(1, op, "object:2")
                Spec == Init /\ [][Next]_vars
                ====
                """);
        }
        string generated = Path.Combine(output, "generated"), jar = TestTools.Jar(root);
        var instrument = TestCli.Tla(["instrument", "--annotation-version", "3", "--source", source,
            "--model", model, "--jar", jar, "--output", generated]);
        if (instrument.Code != 0) throw new InvalidOperationException(instrument.Json);
        File.WriteAllText(Path.Combine(generated, "Program.cs"), """
            using System.Threading.Tasks;
            using TlaLink.Runtime;
            class Program {
                static async Task Main(string[] args) {
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    var subject = new Subject();
                    var first = subject.Run(release.Task);
                    var second = subject.Run(release.Task);
                    release.SetResult();
                    await Task.WhenAll(first, second);
                    TraceRuntime.Complete(args[0]);
                }
            }
            """);
        if (entity)
        {
            string program = Path.Combine(generated, "Program.cs");
            File.WriteAllText(program, Files.Read(program).Replace("var subject = new Subject();",
                "var subject = new Subject(); var target = new Subject();").Replace("subject.Run(release.Task)", "subject.Run(release.Task, target)"));
        }
        string project = Path.Combine(generated, "Fixture.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>");
        TestCli.Restore(project, output, Path.Combine(root, "NuGet.Config"));
        string manifest = Path.Combine(generated, "bindings.json"), trace = Path.Combine(output, "trace.json"), execution = Path.Combine(output, "execution");
        var run = TestCli.Run(["execute", "--manifest", manifest, "--project", project, "--trace", trace, "--output", execution]);
        if (run.Code != 0) throw new InvalidOperationException(run.Json);
        using JsonDocument captured = JsonDocument.Parse(Files.Read(trace));
        JsonElement[] events = captured.RootElement.GetProperty("events").EnumerateArray().ToArray();
        CoreTests.Equal(4, events.Length);
        CoreTests.True(events.Take(2).All(e => e.GetProperty("step").GetString() == "BeginCall"));
        CoreTests.Equal(2, events.GroupBy(e => e.GetProperty("operation").GetString()).Count());
        string config = Path.Combine(output, "calls.cfg");
        File.WriteAllText(config, "SPECIFICATION Spec\n");
        var check = TestCli.Tla(["validate-trace", "--annotation-version", "3", "--model", model, "--jar", jar,
            "--manifest", manifest, "--trace", trace, "--execution", Path.Combine(execution, "execution.json"),
            "--config", config, "--output", Path.Combine(output, "validation")]);
        if (check.Code != 0) throw new InvalidOperationException(check.Json);
    }

    private static void Run(string root, string output, bool parallel, bool hidden, bool trajectory, string expected,
        bool booleanOnly = false, bool referenceActor = false)
    {
        Directory.CreateDirectory(output);
        string model = Path.Combine(output, "TraceFixture.tla"), source = Path.Combine(output, "Subject.cs");
        string state = trajectory ? """
            readonly object gate = new();
            /* @link {"bind":[{"id":"Value","target":"TraceFixture::Value"}],"capture":[{"binding":"Value","guard":"gate","type":"int"}]} */
            int value;
            """ : "";
        string Statement(string id, int value, int actor)
        {
            AnnotationPayload annotations = new() {
                Bind = [new(id, new("TraceFixture", id))],
                Capture = [new(id, CapturePoint.Commit, CaptureClassification.Transition,
                    referenceActor ? "this" : actor.ToString(System.Globalization.CultureInfo.InvariantCulture), "1",
                    Observe: trajectory ? ["Value"] : null)]
            };
            return (trajectory ? "lock (gate) {\n" : "") + "/* @link " + Markup.Encode(annotations) + " */\n" +
                (trajectory ? $"this.value = {value};\n}}" : "System.GC.KeepAlive(0);");
        }
        File.WriteAllText(source, "\nclass Subject {\n" + state +
            "\npublic void A() { " + Statement("A", 1, 1) + " }\npublic void B() { " +
            Statement("B", 4, trajectory ? 1 : 2) + " }\n}");
        string body = trajectory ? """
            VARIABLES x, h, phase
            vars == <<x,h,phase>>
            Init == /\ x = 0 /\ h \in 0..1 /\ phase = 0

            Value == x

            A == /\ phase = 0 /\ phase' = 1
                 /\ x' = (IF h = 0 THEN 1 ELSE 2) /\ UNCHANGED h

            B == /\ phase = 1 /\ phase' = 2
                 /\ x' = (IF h = 0 THEN 3 ELSE 4) /\ UNCHANGED h
            HIDDEN_ANNOTATION
            Flip == /\ h' = 1-h /\ UNCHANGED <<x,phase>>
            Next == A \/ B \/ Flip
            """ : """
            VARIABLE x
            vars == <<x>>
            Init == x = 0

            A == x = 0 /\ x' = 1

            B == x = 1 /\ x' = 2
            Next == A \/ B
            """;
        body = body.Replace("HIDDEN_ANNOTATION", hidden ? "" : "", StringComparison.Ordinal);
        if (booleanOnly) body = """
            VARIABLE flag
            vars == <<flag>>
            Init == flag = FALSE

            A == flag /\ flag' = FALSE

            B == ~flag /\ flag' = TRUE
            Next == A \/ B
            """;
        File.WriteAllText(model, "---- MODULE TraceFixture ----\n" + (booleanOnly ? "" : "EXTENDS Integers\n") +
            "\n" + body + "\nSpec == Init /\\ [][Next]_vars\n====\n");
        string config = Path.Combine(output, "fixture.cfg");
        File.WriteAllText(config, "SPECIFICATION Spec\n");
        string generated = Path.Combine(output, "generated"), jar = TestTools.Jar(root);
        var instrument = TestCli.Tla(["instrument", "--annotation-version", "3", "--source", source, "--model", model, "--jar", jar, "--output", generated],
            hidden ? [new("Flip", "Explicit intervening-state control.")] : null);
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
        var check = TestCli.Tla(["validate-trace", "--annotation-version", "3", "--model", model, "--jar", jar, "--manifest", manifest, "--trace", trace,
            "--execution", Path.Combine(execution, "execution.json"), "--config", config, "--output", Path.Combine(output, "validation")]);
        using JsonDocument result = JsonDocument.Parse(check.Json);
        if (result.RootElement.GetProperty("status").GetString() != expected) throw new InvalidOperationException(check.Json);
        CoreTests.Equal(expected == "rejected" ? 1 : 0, check.Code);
        if (expected == "rejected")
            CoreTests.True(result.RootElement.GetProperty("diagnostics").EnumerateArray().Any(d =>
                d.GetProperty("code").GetString() == ErrorCode.TraceRejected.Id()));
    }
}
