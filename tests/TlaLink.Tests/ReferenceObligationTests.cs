using TlaLink;

namespace TlaLink.Tests;

public static class ReferenceObligationTests
{
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
    }
}
