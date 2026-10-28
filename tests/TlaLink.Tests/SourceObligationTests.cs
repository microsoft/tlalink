using TlaLink;

namespace TlaLink.Tests;

public static class SourceObligationTests
{
    internal const string Fixture = """
        using System.Threading;
        struct SignalFlag {
            private int state;
            public bool Raise() => Interlocked.Exchange(ref this.state, 1) == 0;
            public bool Raised => Volatile.Read(ref this.state) != 0;
            public void Mark() { Volatile.Write(ref this.state, 1); }
        }
        sealed class Notification {
            private SignalFlag flag;
            public void Emit() {
                if (this.flag.Raise()) { OnRaised(); }
            }
            private void OnRaised() {}
        }
        """;

    internal static IntegratedManifest Manifest(string source) => new(Schema.Version, [source], ["Claim.dfy"],
        [new("notification", "M:Notification.Emit", "Claim", null, []),
         new("flag", "M:SignalFlag.Raise", "Claim", null, [])], [], [])
    {
        SourceObligations = [
            new("guard", "notification", "guarded-effect", "M:SignalFlag.Raise", "M:Notification.OnRaised",
                "F:Notification.flag", true),
            new("storage", "notification", "stable-storage", Storage: "F:Notification.flag",
                AllowedCalls: ["M:SignalFlag.Raise"]),
            new("exchange", "flag", "atomic-exchange-gate", Storage: "F:SignalFlag.state"),
            new("writes", "flag", "monotonic-int32", Storage: "F:SignalFlag.state")
        ]
    };

    public static void Register(Action<string, Action> test, string work)
    {
        string directory = Directory.CreateDirectory(Path.Combine(work, "source-obligations")).FullName;
        int serial = 0;
        List<SourceObligationResult> Check(string text, Func<IntegratedManifest, IntegratedManifest>? edit = null)
        {
            string source = Path.Combine(directory, "Fixture" + ++serial + ".cs");
            File.WriteAllText(source, text);
            IntegratedManifest manifest = Manifest(source);
            return SourceObligations.Check(CSharpScope.Parse([source]), edit?.Invoke(manifest) ?? manifest);
        }
        void Result(string text, string id, string status, string reason)
        {
            SourceObligationResult result = Check(text).Single(r => r.Obligation.Id == id);
            CoreTests.Equal(status, result.Status);
            CoreTests.True(result.Reason.Contains(reason, StringComparison.Ordinal));
        }
        test("source obligations check a Boolean guard stable storage exchange and every scoped field use", () =>
        {
            List<SourceObligationResult> results = Check(Fixture);
            CoreTests.Equal(4, results.Count);
            foreach (SourceObligationResult result in results)
                if (result.Status != "passed") throw new InvalidOperationException(result.Obligation.Id + ": " + result.Reason);
            CoreTests.Equal("passed", SourceObligations.Status(results));
            CoreTests.True(results.Single(r => r.Obligation.Id == "writes").Evidence.Count >= 4);
            CoreTests.True(results.Single(r => r.Obligation.Id == "guard").Flow != null);
            CoreTests.Equal("not-specified", SourceObligations.Status([]));
        });
        test("source obligations distinguish authorized branch edges from mere gate dominance", () =>
        {
            Result(Fixture.Replace("if (this.flag.Raise())", "if (!this.flag.Raise())", StringComparison.Ordinal),
                "guard", "rejected", "declared gate outcome");
            Result(Fixture.Replace("if (this.flag.Raise()) { OnRaised(); }",
                "if (this.flag.Raise()) { System.GC.KeepAlive(1); } OnRaised();", StringComparison.Ordinal),
                "guard", "rejected", "bypasses");
            List<SourceObligationResult> valid = Check(Fixture.Replace(
                "if (this.flag.Raise()) { OnRaised(); }", "if (!this.flag.Raise()) return; OnRaised();", StringComparison.Ordinal));
            CoreTests.Equal("passed", valid.Single(r => r.Obligation.Id == "guard").Status);
            Result(Fixture.Replace("if (this.flag.Raise()) { OnRaised(); }", "this.flag.Raise(); OnRaised();", StringComparison.Ordinal),
                "guard", "rejected", "conditional outcome");
            Result(Fixture.Replace("OnRaised();", "OnRaised(); OnRaised();", StringComparison.Ordinal),
                "guard", "rejected", "Exactly one");
        });
        test("source obligations reject defensive copies replacement resets and wrong atomic decisions", () =>
        {
            Result(Fixture.Replace("private SignalFlag flag;", "private readonly SignalFlag flag;", StringComparison.Ordinal),
                "guard", "rejected", "readonly");
            Result(Fixture.Replace("if (this.flag.Raise())", "this.flag = default; if (this.flag.Raise())", StringComparison.Ordinal),
                "storage", "rejected", "replaced");
            Result(Fixture.Replace("Volatile.Write(ref this.state, 1)", "Volatile.Write(ref this.state, 0)", StringComparison.Ordinal),
                "writes", "rejected", "resets");
            Result(Fixture.Replace("Volatile.Write(ref this.state, 1);", "this.state = 0;", StringComparison.Ordinal),
                "writes", "rejected", "direct write");
            Result(Fixture.Replace("== 0", "!= 0", StringComparison.Ordinal),
                "exchange", "rejected", "equality");
            Result(Fixture.Replace("Exchange(ref this.state, 1)", "Exchange(ref this.state, 0)", StringComparison.Ordinal),
                "exchange", "rejected", "Exchange");
            Result(Fixture.Replace("public void Mark() { Volatile.Write(ref this.state, 1); }",
                "public void Mark() { this = default; }", StringComparison.Ordinal),
                "writes", "rejected", "instance is replaced");
        });
        test("source obligations leave exception loops aliases nested functions and partial scope incomplete", () =>
        {
            foreach (string body in new[] {
                "try { if (this.flag.Raise()) OnRaised(); } finally {}",
                "while (this.flag.Raise()) OnRaised();",
                "System.Action a = () => OnRaised(); if (this.flag.Raise()) a();",
                "void Helper() {} Helper(); if (this.flag.Raise()) OnRaised();" })
                Result(Fixture.Replace("if (this.flag.Raise()) { OnRaised(); }", body, StringComparison.Ordinal),
                    "guard", "incomplete", "unsupported");
            Result(Fixture.Replace("if (this.flag.Raise())", "ref SignalFlag alias = ref this.flag; if (alias.Raise())", StringComparison.Ordinal),
                "storage", "incomplete", "escapes");
            Result(Fixture.Replace("struct SignalFlag", "partial struct SignalFlag", StringComparison.Ordinal),
                "writes", "incomplete", "Partial");
            Result(Fixture.Replace("struct SignalFlag",
                "[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)] struct SignalFlag",
                StringComparison.Ordinal).Replace("private int state;",
                "[System.Runtime.InteropServices.FieldOffset(0)] private int state;", StringComparison.Ordinal),
                "writes", "incomplete", "layouts");
            Result(Fixture.Replace("public void Mark() { Volatile.Write(ref this.state, 1); }",
                "public void Mark() { Escape(ref this); } static void Escape(ref SignalFlag flag) { flag = default; }",
                StringComparison.Ordinal), "writes", "incomplete", "escapes");
        });
        test("source obligations require explicit unique semantic selectors and binding endpoints", () =>
        {
            var manifest = Manifest("unused.cs");
            CoreTests.ThrowsAny<InputException>(() => SourceObligations.Validate(
                [manifest.SourceObligations[0] with { Gate = "Raise" }], ["notification"]));
            CoreTests.ThrowsAny<InputException>(() => SourceObligations.Validate(manifest.SourceObligations, ["missing"]));
            CoreTests.ThrowsAny<InputException>(() => SourceObligations.Validate(
                [manifest.SourceObligations[0], manifest.SourceObligations[0]], ["notification"]));
            var unresolved = Check(Fixture, m => m with {
                SourceObligations = [m.SourceObligations[0] with { Storage = "F:Notification.missing" }]
            });
            CoreTests.Equal("incomplete", unresolved.Single().Status);
        });
    }
}
