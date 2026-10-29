using TlaLink;

namespace TlaLink.Tests;

public static class ReferenceObligationTests
{
    private const string SharedFixture = """
        using System;
        using System.Threading;
        sealed class SharedSignal {
            private object ticket;
            public object Dispatch(Action callback) {
                var seen = Volatile.Read(ref this.ticket);
                if (seen != null) return seen;
                var candidate = new object();
                var old = Interlocked.CompareExchange(ref this.ticket, candidate, null);
                if (old != null) return old;
                callback();
                return candidate;
            }
        }
        sealed class Bell {
            private readonly SharedSignal signal = new SharedSignal();
            public void Ring() { this.signal.Dispatch(this.OnRing); }
            private void OnRing() {
                try { Sound(); }
                catch (InvalidOperationException) {}
            }
            private void Sound() {}
        }
        """;

    private static IntegratedManifest SharedManifest(string source) => new(Schema.Version, [source], ["Claim.dfy"],
        [new("ring", "M:Bell.Ring", "Claim", null, []),
         new("dispatch", "M:SharedSignal.Dispatch(System.Action)", "Claim", null, [])], [], [])
    {
        SourceObligations = [
            new("storage", "ring", "stable-storage", Storage: "F:Bell.signal",
                AllowedCalls: ["M:SharedSignal.Dispatch(System.Action)"]),
            new("wiring", "ring", "callback-effect", Gate: "M:SharedSignal.Dispatch(System.Action)",
                Effect: "M:Bell.Sound", Storage: "F:Bell.signal"),
            new("dispatch", "dispatch", "reference-cas-effect", Effect: "M:System.Action.Invoke",
                Storage: "F:SharedSignal.ticket"),
            new("cell", "dispatch", "reference-cell", Storage: "F:SharedSignal.ticket")
        ]
    };

    private const string AsyncDispatchFixture = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        sealed class DispatchLatch {
            private Task started;
            public Task Run(Func<Task> work) {
                var existing = Volatile.Read(ref this.started);
                if (existing != null) return existing;
                var candidate = new Task(Idle);
                Task previous;
                previous = Interlocked.CompareExchange(ref this.started, candidate, null);
                if (previous != null) return previous;
                Queue(work);
                return candidate;
            }
            private static void Idle() {}
            private static async Task Queue(Func<Task> work) { await work(); }
        }
        sealed class Beacon {
            private readonly DispatchLatch latch = new DispatchLatch();
            public Task Flash() => this.latch.Run(this.OnFlash);
            private Task OnFlash() {
                try {
                    Activate();
                    return Task.CompletedTask;
                }
                catch (InvalidOperationException) { return Task.CompletedTask; }
            }
            private void Activate() {}
        }
        """;

    private const string CompletionDispatchFixture = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        sealed class CompletionDispatch {
            private object ticket;
            public object Run(Action action) {
                var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                var previous = Interlocked.CompareExchange(ref this.ticket, completion.Task, null);
                if (previous != null) return previous;
                action();
                return completion.Task;
            }
        }
        """;

    private const string ReusedCompletionDispatchFixture = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        sealed class CompletionDispatch {
            private Task ticket;
            public Task Run(Func<Task> action) {
                Task previous = Volatile.Read(ref this.ticket);
                if (previous != null) return previous;
                var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                previous = Interlocked.CompareExchange(ref this.ticket, completion.Task, null);
                if (previous != null) return previous;
                _ = action();
                return completion.Task;
            }
        }
        """;

    internal const string Fixture = """
        using System;
        using System.Threading;
        struct Memo<T> where T : class {
            private T value;
            public T Value => Volatile.Read(ref this.value);
            public bool Store(T candidate) => Interlocked.CompareExchange(ref this.value, candidate, null) == null;
        }
        sealed class Announcer {
            private Memo<object> memo;
            public void Publish(object candidate) {
                var existing = this.memo.Value;
                if (existing != null) return;
                if (!this.memo.Store(candidate)) return;
                try { Notify(); }
                catch (InvalidOperationException) { GC.KeepAlive(candidate); }
                finally { GC.KeepAlive(candidate); }
            }
            void Notify() {}
        }
        """;

    internal static IntegratedManifest Manifest(string source) => new(Schema.Version, [source], ["Claim.dfy"],
        [new("publish", "M:Announcer.Publish(System.Object)", "Claim", null, []),
         new("cell", "M:Memo`1.Store(`0)", "Claim", null, []),
         new("read", "P:Memo`1.Value", "Claim", null, [])], [], [])
    {
        SourceObligations = [
            new("effect", "publish", "guarded-try-effect", "M:Memo`1.Store(`0)", "M:Announcer.Notify",
                "F:Announcer.memo", true),
            new("storage", "publish", "stable-storage", Storage: "F:Announcer.memo",
                AllowedCalls: ["M:Memo`1.Store(`0)"], AllowedProperties: ["P:Memo`1.Value"]),
            new("cas", "cell", "reference-cas-gate", Storage: "F:Memo`1.value", Parameter: "candidate"),
            new("writes", "cell", "reference-cell", Storage: "F:Memo`1.value"),
            new("read", "read", "reference-read", Storage: "F:Memo`1.value")
        ]
    };

    public static void Register(Action<string, Action> test, string work)
    {
        string directory = Directory.CreateDirectory(Path.Combine(work, "reference-premises")).FullName;
        List<SourceObligationResult> Check(string text, Func<IntegratedManifest, IntegratedManifest>? edit = null)
        {
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".cs");
            File.WriteAllText(path, text);
            var manifest = Manifest(path);
            return SourceObligations.Check(CSharpScope.Parse([path]), edit?.Invoke(manifest) ?? manifest);
        }
        void Result(string text, string id, string status)
        {
            SourceObligationResult row = Check(text).Single(r => r.Obligation.Id == id);
            if (row.Status != status) throw new InvalidOperationException($"{id}: expected {status}, got {row.Status}: {row.Reason}");
        }
        test("reference premises resolve generic declarations getters and a guarded first try effect", () =>
        {
            var rows = Check(Fixture);
            foreach (var row in rows)
                if (row.Status != "passed") throw new InvalidOperationException(row.Obligation.Id + ": " + row.Reason);
            var call = rows.Single(r => r.Obligation.Id == "effect").Flow!.Calls.Single(c => c.Definition == "M:Memo`1.Store(`0)");
            CoreTests.True(call.Target != call.Definition);
            CoreTests.True(call.Target.Contains("System.Object", StringComparison.Ordinal));
        });
        test("reference premises reject wrong comparisons resets readonly receivers and unauthorized outcomes", () =>
        {
            Result(Fixture.Replace("== null;", "!= null;", StringComparison.Ordinal), "cas", "rejected");
            Result(Fixture.Replace("Volatile.Read(ref this.value)", "null", StringComparison.Ordinal), "read", "rejected");
            Result(Fixture.Replace("candidate, null)", "candidate, candidate)", StringComparison.Ordinal), "writes", "rejected");
            Result(Fixture.Replace("private T value;", "private T value; public void Reset() { this.value = null; }", StringComparison.Ordinal),
                "writes", "rejected");
            Result(Fixture.Replace("private Memo<object> memo;", "private readonly Memo<object> memo;", StringComparison.Ordinal),
                "effect", "rejected");
            Result(Fixture.Replace("if (!this.memo.Store(candidate))", "if (this.memo.Store(candidate))", StringComparison.Ordinal),
                "effect", "rejected");
            Result(Fixture.Replace("private T value;", "private T value; public void ResetOther(Memo<object> other) { other.value = null; }",
                StringComparison.Ordinal), "writes", "rejected");
        });
        test("reference premises leave ref escapes extra try regions later effects and unknown getters incomplete", () =>
        {
            Result(Fixture.Replace("private T value;", "private T value; void Escape() { Pass(ref this.value); } static void Pass(ref T item) {}",
                StringComparison.Ordinal), "writes", "incomplete");
            Result(Fixture.Replace("try { Notify(); }", "try { GC.KeepAlive(candidate); Notify(); }", StringComparison.Ordinal),
                "effect", "incomplete");
            Result(Fixture.Replace("try { Notify(); }", "try { try { Notify(); } finally {} }", StringComparison.Ordinal),
                "effect", "incomplete");
            var rows = Check(Fixture, m => m with {
                SourceObligations = [m.SourceObligations[1] with { AllowedProperties = ["P:Memo`1.Missing"] }]
            });
            CoreTests.Equal("incomplete", rows.Single().Status);
        });
        test("reference premise schemas forbid irrelevant fields and null publication is not a source precondition", () =>
        {
            var manifest = Manifest("ignored.cs");
            CoreTests.ThrowsAny<InputException>(() => SourceObligations.Validate(
                [manifest.SourceObligations[2] with { Parameter = null }], ["cell"]));
            CoreTests.ThrowsAny<InputException>(() => SourceObligations.Validate(
                [manifest.SourceObligations[3] with { When = true }], ["cell"]));
            Result(Fixture.Replace("this.memo.Store(candidate)", "this.memo.Store(null)", StringComparison.Ordinal),
                "effect", "passed");
        });
        List<SourceObligationResult> Shared(string text)
        {
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".cs");
            File.WriteAllText(path, text);
            return SourceObligations.Check(CSharpScope.Parse([path]), SharedManifest(path));
        }
        void SharedResult(string text, string id, string status)
        {
            var row = Shared(text).Single(r => r.Obligation.Id == id);
            if (row.Status != status)
                throw new InvalidOperationException($"{id}: expected {status}, got {row.Status}: {row.Reason}");
        }
        test("reference helper premises check fresh readonly storage CAS dispatch and private callback wiring independently", () =>
        {
            foreach (var row in Shared(SharedFixture))
                if (row.Status != "passed") throw new InvalidOperationException(row.Obligation.Id + ": " + row.Reason);
            SharedResult(SharedFixture.Replace("try { Sound(); }\n        catch (InvalidOperationException) {}", "Sound();",
                StringComparison.Ordinal), "wiring", "passed");
            SharedResult(SharedFixture.Replace("new SharedSignal()", "new()", StringComparison.Ordinal), "storage", "passed");
            SharedResult(SharedFixture.Replace("if (old != null) return old;", "if (null != old) { return old; }",
                StringComparison.Ordinal), "dispatch", "passed");
            SharedResult(SharedFixture.Replace("var seen = Volatile.Read(ref this.ticket);\n        if (seen != null) return seen;\n        ",
                "", StringComparison.Ordinal), "dispatch", "passed");
        });
        test("reference helper storage rejects replacements copies factories mutable fields and unapproved calls", () =>
        {
            SharedResult(SharedFixture.Replace("public void Ring()", "public Bell() { this.signal = new SharedSignal(); } public void Ring()",
                StringComparison.Ordinal), "storage", "rejected");
            SharedResult(SharedFixture.Replace("private readonly SharedSignal", "private SharedSignal",
                StringComparison.Ordinal), "storage", "rejected");
            SharedResult(SharedFixture.Replace("= new SharedSignal();", ";", StringComparison.Ordinal),
                "storage", "incomplete");
            SharedResult(SharedFixture.Replace("= new SharedSignal();",
                "= Create(); private static SharedSignal Create() => new SharedSignal();", StringComparison.Ordinal),
                "storage", "incomplete");
            SharedResult(SharedFixture.Replace("public void Ring()", "private SharedSignal Copy() => this.signal; public void Ring()",
                StringComparison.Ordinal), "storage", "incomplete");
            SharedResult(SharedFixture.Replace("public void Ring()",
                "private void Escape() { Pass(in this.signal); } private static void Pass(in SharedSignal item) {} public void Ring()",
                StringComparison.Ordinal), "storage", "incomplete");
            SharedResult(SharedFixture.Replace("public void Ring()", "private void Extra() { this.signal.ToString(); } public void Ring()",
                StringComparison.Ordinal), "storage", "rejected");
            SharedResult(SharedFixture.Replace("public void Ring()", "private Type Extra() => this.signal.GetType(); public void Ring()",
                StringComparison.Ordinal), "storage", "rejected");
        });
        test("reference CAS effect rejects inverted bypassed duplicate and nonnull comparand authorization", () =>
        {
            SharedResult(SharedFixture.Replace("if (old != null) return old;", "if (old == null) return old;",
                StringComparison.Ordinal), "dispatch", "rejected");
            SharedResult(SharedFixture.Replace("if (old != null) return old;", "", StringComparison.Ordinal),
                "dispatch", "rejected");
            SharedResult(SharedFixture.Replace("if (old != null) return old;", "if (old != null) return candidate;",
                StringComparison.Ordinal), "dispatch", "rejected");
            SharedResult(SharedFixture.Replace("callback();", "callback(); callback();", StringComparison.Ordinal),
                "dispatch", "rejected");
            SharedResult(SharedFixture.Replace("candidate, null)", "candidate, candidate)", StringComparison.Ordinal),
                "dispatch", "rejected");
        });
        test("reference CAS effect leaves nullable candidates aliasing extra calls and complex flow incomplete", () =>
        {
            SharedResult(SharedFixture.Replace("var candidate = new object();", "object candidate = null;",
                StringComparison.Ordinal), "dispatch", "incomplete");
            SharedResult(SharedFixture.Replace("var candidate = new object();",
                "var candidate = Make();", StringComparison.Ordinal).Replace("private object ticket;",
                "private object ticket; private object Make() => new object();", StringComparison.Ordinal),
                "dispatch", "incomplete");
            SharedResult(SharedFixture.Replace("var candidate = new object();",
                "var candidate = new object(); candidate = null;", StringComparison.Ordinal),
                "dispatch", "incomplete");
            SharedResult(SharedFixture.Replace("callback();", "while (true) { callback(); break; }", StringComparison.Ordinal),
                "dispatch", "incomplete");
            SharedResult(SharedFixture.Replace("callback();", "GC.KeepAlive(candidate); callback();", StringComparison.Ordinal),
                "dispatch", "rejected");
            SharedResult(SharedFixture.Replace("callback();", "try { callback(); } catch {}", StringComparison.Ordinal),
                "dispatch", "incomplete");
            SharedResult(SharedFixture.Replace("var candidate = new object();",
                "var candidate = new object(); Use(ref candidate);", StringComparison.Ordinal)
                .Replace("private object ticket;", "private object ticket; private static void Use(ref object value) {}",
                    StringComparison.Ordinal), "dispatch", "incomplete");
            SharedResult(SharedFixture.Replace("var candidate = new object();",
                "var candidate = new object(); var alias = this.ticket;", StringComparison.Ordinal),
                "dispatch", "incomplete");
        });
        test("callback wiring rejects bypasses delegate escapes duplicate delegation and other owner effects", () =>
        {
            SharedResult(SharedFixture.Replace("private void Sound()", "public void Bypass() { OnRing(); } private void Sound()",
                StringComparison.Ordinal), "wiring", "rejected");
            SharedResult(SharedFixture.Replace("private void Sound()", "public Action Escape() => this.OnRing; private void Sound()",
                StringComparison.Ordinal), "wiring", "rejected");
            SharedResult(SharedFixture.Replace("private void Sound()", "public string Name() => nameof(OnRing); private void Sound()",
                StringComparison.Ordinal), "wiring", "rejected");
            SharedResult(SharedFixture.Replace("private void Sound()", "public void Bypass() { Sound(); } private void Sound()",
                StringComparison.Ordinal), "wiring", "rejected");
            SharedResult(SharedFixture.Replace("this.signal.Dispatch(this.OnRing);",
                "this.signal.Dispatch(this.OnRing); this.signal.Dispatch(this.OnRing);", StringComparison.Ordinal),
                "wiring", "incomplete");
            SharedResult(SharedFixture.Replace("private void OnRing()", "public void OnRing()", StringComparison.Ordinal),
                "wiring", "rejected");
            SharedResult(SharedFixture.Replace("try { Sound(); }", "try { GC.KeepAlive(this); Sound(); }",
                StringComparison.Ordinal), "wiring", "incomplete");
            SharedResult(SharedFixture.Replace("try { Sound(); }", "try { Sound(); Sound(); }",
                StringComparison.Ordinal), "wiring", "incomplete");
            SharedResult(SharedFixture.Replace("catch (InvalidOperationException) {}", "catch (InvalidOperationException) { Sound(); }",
                StringComparison.Ordinal), "wiring", "rejected");
            SharedResult(SharedFixture.Replace("private void Sound()",
                "public void Unknown(dynamic target) { target.OnRing(); } private void Sound()", StringComparison.Ordinal),
                "wiring", "incomplete");
        });
        test("reference helper premise contracts reject every irrelevant field", () =>
        {
            var rows = SharedManifest("ignored.cs").SourceObligations;
            foreach (var invalid in new[] {
                rows[1] with { When = true }, rows[1] with { Parameter = "callback" },
                rows[1] with { AllowedCalls = ["M:Bell.Sound"] }, rows[1] with { AllowedProperties = ["P:Bell.X"] },
                rows[1] with { Gate = null }, rows[1] with { Effect = null },
                rows[2] with { Gate = "M:Bell.Sound" }, rows[2] with { When = false },
                rows[2] with { Parameter = "candidate" }, rows[2] with { AllowedCalls = [] },
                rows[2] with { AllowedProperties = [] }, rows[2] with { Effect = null } })
                CoreTests.ThrowsAny<InputException>(() => SourceObligations.Validate([invalid], ["ring", "dispatch"]));
        });
        test("reference helper premises allow predeclared CAS results async dispatch authorization and Task callback wiring", () =>
        {
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".cs");
            File.WriteAllText(path, AsyncDispatchFixture);
            var manifest = new IntegratedManifest(Schema.Version, [path], ["Claim.dfy"],
                [new("flash", "M:Beacon.Flash", "Claim", null, []),
                 new("run", "M:DispatchLatch.Run(System.Func{System.Threading.Tasks.Task})", "Claim", null, [])], [], [])
            {
                SourceObligations = [
                    new("storage", "flash", "stable-storage", Storage: "F:Beacon.latch",
                        AllowedCalls: ["M:DispatchLatch.Run(System.Func{System.Threading.Tasks.Task})"]),
                    new("wiring", "flash", "callback-effect", Storage: "F:Beacon.latch",
                        Gate: "M:DispatchLatch.Run(System.Func{System.Threading.Tasks.Task})", Effect: "M:Beacon.Activate"),
                    new("dispatch", "run", "reference-cas-effect", Storage: "F:DispatchLatch.started",
                        Effect: "M:DispatchLatch.Queue(System.Func{System.Threading.Tasks.Task})"),
                    new("cell", "run", "reference-cell", Storage: "F:DispatchLatch.started")
                ]
            };
            foreach (var row in SourceObligations.Check(CSharpScope.Parse([path], languageVersion: "8"), manifest))
                if (row.Status != "passed") throw new InvalidOperationException(row.Obligation.Id + ": " + row.Reason);
        });
        SourceObligationResult Completion(string text, bool taskDispatch = false)
        {
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".cs");
            File.WriteAllText(path, text);
            var manifest = new IntegratedManifest(Schema.Version, [path], ["Claim.dfy"],
                [new("run", taskDispatch ? "M:CompletionDispatch.Run(System.Func{System.Threading.Tasks.Task})" :
                    "M:CompletionDispatch.Run(System.Action)", "Claim", null, [])], [], [])
            {
                SourceObligations = [
                    new("dispatch", "run", "reference-cas-effect", Storage: "F:CompletionDispatch.ticket",
                        Effect: taskDispatch ? "M:System.Func`1.Invoke" : "M:System.Action.Invoke")
                ]
            };
            return SourceObligations.Check(CSharpScope.Parse([path], languageVersion: "8"), manifest).Single();
        }
        test("reference CAS authorization trusts only the BCL TaskCompletionSource Task contract on a fresh unchanged local", () =>
        {
            foreach (string text in new[] {
                CompletionDispatchFixture,
                CompletionDispatchFixture.Replace("TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously)",
                    "TaskCompletionSource<object>()", StringComparison.Ordinal),
                CompletionDispatchFixture.Replace("return completion.Task;", "return null;", StringComparison.Ordinal) })
            {
                var row = Completion(text);
                if (row.Status != "passed") throw new InvalidOperationException(row.Status + ": " + row.Reason);
            }
        });
        test("reference CAS authorization leaves Task getter lookalikes factories replacement and ref aliases incomplete", () =>
        {
            const string lookalike = """

                sealed class Impostor<T> {
                    public Impostor(TaskCreationOptions options) {}
                    public Task<T> Task => null;
                }
                """;
            const string shadow = """

                namespace System.Threading.Tasks {
                    sealed class TaskCompletionSource<T> {
                        public TaskCompletionSource(TaskCreationOptions options) {}
                        public Task<T> Task => null;
                    }
                }
                """;
            foreach (string text in new[] {
                CompletionDispatchFixture.Replace("TaskCompletionSource<object>", "Impostor<object>", StringComparison.Ordinal) + lookalike,
                CompletionDispatchFixture + shadow,
                CompletionDispatchFixture.Replace(
                    "new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously)", "Create()", StringComparison.Ordinal)
                    .Replace("private object ticket;",
                        "private object ticket; private static TaskCompletionSource<object> Create() => new TaskCompletionSource<object>();",
                        StringComparison.Ordinal) })
            {
                var row = Completion(text);
                CoreTests.Equal("incomplete", row.Status);
                CoreTests.True(row.Reason.Contains("nonnull candidate", StringComparison.Ordinal));
            }
            foreach (string text in new[] {
                CompletionDispatchFixture.Replace("var previous =", "completion = new TaskCompletionSource<object>(); var previous =",
                    StringComparison.Ordinal),
                CompletionDispatchFixture.Replace("var previous =", "Escape(ref completion); var previous =", StringComparison.Ordinal)
                    .Replace("private object ticket;",
                        "private object ticket; private static void Escape(ref TaskCompletionSource<object> value) {}",
                        StringComparison.Ordinal) })
            {
                var row = Completion(text);
                CoreTests.Equal("incomplete", row.Status);
            }
        });
        test("reference CAS authorization overwrites a same-cell Volatile fast-path local before guard and discarded dispatch", () =>
        {
            foreach (string text in new[] {
                ReusedCompletionDispatchFixture,
                ReusedCompletionDispatchFixture.Replace("if (previous != null) return previous;",
                    "if (null != previous) { return previous; }", StringComparison.Ordinal),
                ReusedCompletionDispatchFixture.Replace("_ = action();", "action();", StringComparison.Ordinal) })
            {
                var row = Completion(text, taskDispatch: true);
                if (row.Status != "passed") throw new InvalidOperationException(row.Status + ": " + row.Reason);
            }
        });
        test("reference CAS authorization permits only a throwing BCL parameter-null precondition", () =>
        {
            const string check = "if (action == null) { throw new ArgumentNullException(nameof(action)); } ";
            string guarded = ReusedCompletionDispatchFixture.Replace("Task previous =", check + "Task previous =", StringComparison.Ordinal);
            foreach (string text in new[] { guarded, guarded.Replace("action == null", "null == action", StringComparison.Ordinal) })
            {
                var row = Completion(text, taskDispatch: true);
                if (row.Status != "passed") throw new InvalidOperationException(row.Status + ": " + row.Reason);
            }
            foreach (string text in new[] {
                guarded.Replace("throw new ArgumentNullException(nameof(action));", "return null;", StringComparison.Ordinal),
                guarded.Replace("action == null", "action != null", StringComparison.Ordinal),
                guarded + "\nnamespace System { class ArgumentNullException : Exception { public ArgumentNullException(string name) {} } }" })
                CoreTests.Equal("incomplete", Completion(text, taskDispatch: true).Status);
        });
        test("reference CAS authorization rejects stale results other-cell reads ref aliases and unchecked fast paths", () =>
        {
            foreach (var (text, status) in new[] {
                (ReusedCompletionDispatchFixture.Replace("previous = Interlocked.CompareExchange",
                    "Interlocked.CompareExchange", StringComparison.Ordinal), "incomplete"),
                (ReusedCompletionDispatchFixture.Replace("previous = Interlocked.CompareExchange",
                    "var winner = Interlocked.CompareExchange", StringComparison.Ordinal), "rejected"),
                (ReusedCompletionDispatchFixture.Replace(
                    "previous = Interlocked.CompareExchange(ref this.ticket, completion.Task, null);", "",
                    StringComparison.Ordinal), "rejected"),
                (ReusedCompletionDispatchFixture.Replace("Volatile.Read(ref this.ticket)", "Volatile.Read(ref this.other)",
                    StringComparison.Ordinal).Replace("private Task ticket;", "private Task ticket; private Task other;",
                    StringComparison.Ordinal), "incomplete"),
                (ReusedCompletionDispatchFixture.Replace("Task previous = Volatile.Read(ref this.ticket);",
                    "Task previous = this.ticket;", StringComparison.Ordinal), "incomplete"),
                (ReusedCompletionDispatchFixture.ReplaceLineEndings("\n").Replace(
                    "if (previous != null) return previous;\n        var completion",
                    "if (previous == null) return previous;\n        var completion", StringComparison.Ordinal), "incomplete"),
                (ReusedCompletionDispatchFixture.ReplaceLineEndings("\n").Replace(
                    "if (previous != null) return previous;\n        var completion", "var completion",
                    StringComparison.Ordinal), "incomplete"),
                (ReusedCompletionDispatchFixture.Replace("Task previous = Volatile.Read(ref this.ticket);",
                    "ref Task previous = ref this.ticket;", StringComparison.Ordinal), "incomplete"),
                (ReusedCompletionDispatchFixture.Replace("previous = Interlocked.CompareExchange",
                    "previous = null; previous = Interlocked.CompareExchange", StringComparison.Ordinal), "incomplete"),
                (ReusedCompletionDispatchFixture.Replace("previous = Interlocked.CompareExchange",
                    "Escape(ref previous); previous = Interlocked.CompareExchange", StringComparison.Ordinal)
                    .Replace("private Task ticket;",
                        "private Task ticket; private static void Escape(ref Task value) {}", StringComparison.Ordinal), "incomplete"),
                (ReusedCompletionDispatchFixture.Replace("Volatile.Read", "FakeVolatile.Read", StringComparison.Ordinal) +
                    "\nstatic class FakeVolatile { public static T Read<T>(ref T value) => value; }", "incomplete") })
            {
                var row = Completion(text, taskDispatch: true);
                if (row.Status != status)
                    throw new InvalidOperationException($"Expected {status}, got {row.Status}: {row.Reason}");
            }
        });
    }
}
