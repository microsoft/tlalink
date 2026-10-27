using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class SourceInventoryTests
{
    private sealed class HostileIdentity
    {
        public override bool Equals(object? other) => throw new InvalidOperationException("User equality must not run.");
        public override int GetHashCode() => throw new InvalidOperationException("User hashing must not run.");
    }

    public static void Register(Action<string, Action> test, string work)
    {
        string directory = Directory.CreateDirectory(Path.Combine(work, "source-inventory")).FullName;
        string source = Path.Combine(directory, "Fixture.cs");
        File.WriteAllText(source, """
            using System;
            using System.Threading.Tasks;
            interface IChannel { Task Send(int count); }
            sealed class Subject {
                readonly object gate = new();
                int count;
                public async Task Run(IChannel channel) {
                    lock (gate) { count++; }
                    await channel.Send(count);
                    Action callback = () => { count++; GC.KeepAlive(nameof(count)); };
                    callback();
                }
            }
            """);
        test("source inventory resolves calls locks awaits fields and nested-function boundaries", () =>
        {
            SourceInventoryResult result = SourceInventory.Build([source]);
            CoreTests.Equal("ready", result.Status);
            CoreTests.Equal(1, result.SourceCount);
            CoreTests.Equal(1, result.AwaitCount);
            SourceMember member = result.Members.Single(m => m.Symbol.Contains("Subject.Run(", StringComparison.Ordinal));
            CoreTests.Equal(1, member.Locks.Count);
            CoreTests.True(member.Locks.Single().Symbol!.Contains("gate", StringComparison.Ordinal));
            CoreTests.True(member.Calls.Any(c => c.Dispatch == "interface" && c.Target!.Contains("IChannel.Send", StringComparison.Ordinal)));
            CoreTests.True(member.Calls.Any(c => c.Dispatch == "delegate"));
            CoreTests.True(member.Calls.Any(c => c.InNestedFunction && c.Target!.Contains("KeepAlive", StringComparison.Ordinal)));
            CoreTests.Equal(3, member.Calls.Count);
            CoreTests.True(result.Members.Single(m => m.Symbol == "F:Subject.gate").Calls.Count == 1);
            CoreTests.True(member.FieldAccesses.Count(a => a.Name == "count") >= 3);
            CoreTests.Equal(Files.Hash(source), result.Fingerprints[source]);
        });
        test("source inventory CLI retains context and diagnostics without claiming model coverage", () =>
        {
            string output = Path.Combine(directory, "inventory.json");
            var response = TestCli.Run(["inspect-source", "--annotation-version", "2", "--source", source,
                "--output", output, "--format", "summary"]);
            if (response.Code != 0) throw new InvalidOperationException(response.Json);
            using JsonDocument document = JsonDocument.Parse(response.Json);
            CoreTests.Equal("source-inventory", document.RootElement.GetProperty("result_kind").GetString());
            CoreTests.True(!document.RootElement.TryGetProperty("members", out _));
            CoreTests.Equal(2, document.RootElement.GetProperty("annotation_version").GetInt32());
            SourceInventoryResult saved = ProjectInputs.Read<SourceInventoryResult>(output);
            CoreTests.True(saved.Boundaries.Any(b => b.Contains("not model coverage", StringComparison.Ordinal)));
            CoreTests.Equal(2, TestCli.Run(["inspect-source", "--annotation-version", "2", "--source", source, "--output", output]).Code);
            string unresolved = Path.Combine(directory, "Unresolved.cs");
            File.WriteAllText(unresolved, "class Unresolved { MissingType Run() => null; }");
            var bad = TestCli.Run(["inspect-source", "--annotation-version", "2", "--source", unresolved]);
            CoreTests.Equal(2, bad.Code);
            using JsonDocument incomplete = JsonDocument.Parse(bad.Json);
            CoreTests.Equal("incomplete", incomplete.RootElement.GetProperty("status").GetString());
            CoreTests.True(incomplete.RootElement.GetProperty("diagnostics").GetArrayLength() > 0);
        });
        test("source inventory accepts an explicit compiler reference closure without implicit framework additions", () =>
        {
            string path = Path.Combine(directory, "ExplicitContext.cs");
            File.WriteAllText(path, """
                #if CONTEXT
                public class ExplicitContext { public string? Value; }
                #else
                public class ExplicitContext { MissingType Value; }
                #endif
                """);
            string reference = typeof(object).Assembly.Location;
            var response = TestCli.Run(["inspect-source", "--annotation-version", "2", "--source", path,
                "--framework-references", "explicit", "--reference", reference, "--nullable", "disable",
                "--define", "CONTEXT", "--format", "summary"]);
            if (response.Code != 0) throw new InvalidOperationException(response.Json);
            using JsonDocument document = JsonDocument.Parse(response.Json);
            JsonElement compiler = document.RootElement.GetProperty("compiler");
            CoreTests.Equal("explicit", compiler.GetProperty("framework_references").GetString());
            CoreTests.Equal("disable", compiler.GetProperty("nullable").GetString());
            CoreTests.Equal("CONTEXT", compiler.GetProperty("defines")[0].GetString());
            SourceInventoryResult result = SourceInventory.Build([path], [reference], ["CONTEXT"],
                Microsoft.CodeAnalysis.NullableContextOptions.Disable, false);
            CoreTests.Equal("ready", result.Status);
            CSharpScope scope = CSharpScope.Parse([path], [reference], ["CONTEXT"],
                Microsoft.CodeAnalysis.NullableContextOptions.Disable, false);
            CoreTests.Equal(reference, scope.Compilation.References.Single().Display);
            CoreTests.Equal(2, TestCli.Run(["inspect-source", "--annotation-version", "2", "--source", path,
                "--framework-references", "explicit"]).Code);
            CoreTests.Equal(2, TestCli.Run(["inspect-source", "--annotation-version", "2", "--source", path,
                "--nullable", "guess"]).Code);
        });
        test("review-only invocation links identify callbacks without inheriting their creation lock", () =>
        {
            string path = Path.Combine(directory, "Callbacks.cs");
            string text = """
                using System;
                using System.Threading;
                /* @link model Calls; */
                sealed class Calls {
                    readonly object gate = new();
                    Action callback = () => {};
                    void Setup(CancellationToken token) {
                        lock (gate) {
                            callback = () => /* @link step Callback; */ GC.KeepAlive(1);
                        }
                        using (/* @link step Register; */ token.Register(callback)) {}
                    }
                }
                """;
            File.WriteAllText(path, text);
            CSharpScope scope = CSharpScope.Parse([path]);
            CoreTests.True(scope.Diagnostics.All(d => d.Severity != "error"));
            CoreTests.Equal(2, scope.Bindings.Count);
            CoreTests.Equal(0, scope.Bindings.Single(b => b.Tag.Identity == "Callback").Locks.Count);
            File.WriteAllText(path, text.Replace("step Callback;", "step Callback point=commit actor=\"1\" operation=\"1\";"));
            CoreTests.True(CSharpScope.Parse([path]).Diagnostics.Any(d => d.Severity == "error"));
        });
        test("automatic capture cannot claim a lock held only when the callback was created", () =>
        {
            string path = Path.Combine(directory, "CallbackCapture.cs");
            File.WriteAllText(path, """
                using System;
                /* @link model CallbackCapture; */
                class CallbackCapture {
                    readonly object gate = new();
                    /* @link state Value guard=gate type=int; */
                    int value;
                    Action callback = () => {};
                    void Setup() {
                        lock (gate) {
                            callback = () => {
                                /* @link step Change point=commit actor="1" operation="1" classification=transition observe=Value; */
                                value++;
                            };
                        }
                    }
                }
                """);
            CSharpScope scope = CSharpScope.Parse([path]);
            List<ModelBinding> models = scope.Bindings.Select(b => new ModelBinding(b.Model, b.Tag,
                new(b.Tag.Identity, "", b.Tag.Kind, new("fixture.tla", 1, 1), ""), 0)).ToList();
            Packet packet = Review.Build("instrument", scope, models, [], []);
            CoreTests.Equal("passed", packet.LinksStatus);
            CoreTests.ThrowsAny<InputException>(() => Instrumentation.Generate(scope, models, packet, Path.Combine(directory, "invalid-capture")));
        });
        test("enum identities are lowered to lossless integers while UInt64-backed enums are rejected", () =>
        {
            string path = Path.Combine(directory, "EnumIdentity.cs");
            string text = """
                enum Role { Client, Server }
                /* @link model EnumIdentity; */
                class EnumIdentity {
                    readonly object gate = new();
                    readonly Role role = Role.Client;
                    /* @link state Value guard=gate type=int; */
                    int value;
                    void Change() {
                        lock (gate) {
                            /* @link step Change point=commit actor=role entity=role operation="1" classification=transition observe=Value; */
                            value = 1;
                        }
                    }
                }
                """;
            InstrumentManifest Generate(string output)
            {
                CSharpScope scope = CSharpScope.Parse([path]);
                List<ModelBinding> models = scope.Bindings.Select(b => new ModelBinding(b.Model, b.Tag,
                    new(b.Tag.Identity, "", b.Tag.Kind, new("fixture.tla", 1, 1), ""), b.Tag.Kind == "state" ? 1 : 0)).ToList();
                return Instrumentation.Generate(scope, models, Review.Build("instrument", scope, models, [], []), output);
            }
            File.WriteAllText(path, text);
            string generated = Path.Combine(directory, "enum-generated");
            Generate(generated);
            CoreTests.True(Files.Read(Path.Combine(generated, "EnumIdentity.cs")).Contains("((long)(role))", StringComparison.Ordinal));
            File.WriteAllText(path, text.Replace("enum Role {", "enum Role : ulong {"));
            CoreTests.ThrowsAny<InputException>(() => Generate(Path.Combine(directory, "enum-unsupported")));
        });
        test("reference identities are stable and never invoke user equality or hashing", () =>
        {
            object first = new HostileIdentity(), second = new HostileIdentity();
            string id = TlaLink.Runtime.TraceRuntime.ObjectIdentity(first);
            CoreTests.Equal(id, TlaLink.Runtime.TraceRuntime.ObjectIdentity(first));
            CoreTests.True(id != TlaLink.Runtime.TraceRuntime.ObjectIdentity(second));
            CoreTests.True(id.StartsWith("object:", StringComparison.Ordinal));
            CoreTests.ThrowsAny<InvalidOperationException>(() => TlaLink.Runtime.TraceRuntime.ObjectIdentity(1));
        });
        test("ordinary local declarations can be captured without treating using declarations as completed disposal", () =>
        {
            string path = Path.Combine(directory, "LocalCapture.cs");
            string text = """
                /* @link model Locals; */
                class Locals {
                    readonly object gate = new();
                    /* @link state Value guard=gate type=int; */
                    int value;
                    void Change() {
                        lock (gate) {
                            /* @link step Change point=commit actor=this operation="1" classification=transition observe=Value; */
                            int result = value = 1;
                        }
                    }
                }
                """;
            InstrumentManifest Generate(string output)
            {
                CSharpScope scope = CSharpScope.Parse([path]);
                List<ModelBinding> models = scope.Bindings.Select(b => new ModelBinding(b.Model, b.Tag,
                    new(b.Tag.Identity, "", b.Tag.Kind, new("fixture.tla", 1, 1), ""), 0)).ToList();
                return Instrumentation.Generate(scope, models, Review.Build("instrument", scope, models, [], []), output);
            }
            File.WriteAllText(path, text);
            string generated = Path.Combine(directory, "local-generated");
            Generate(generated);
            string rewritten = Files.Read(Path.Combine(generated, "LocalCapture.cs"));
            CoreTests.True(rewritten.Contains("ObjectIdentity(this)", StringComparison.Ordinal));
            CoreTests.True(rewritten.IndexOf("int result = value = 1;", StringComparison.Ordinal) <
                rewritten.IndexOf("TraceRuntime.Emit(", StringComparison.Ordinal));
            File.WriteAllText(path, text.Replace("int result = value = 1;", "using var stream = new System.IO.MemoryStream();"));
            CoreTests.ThrowsAny<InputException>(() => Generate(Path.Combine(directory, "using-unsupported")));
        });
        test("invocation identities are shared within a function and generated locals cannot shadow source names", () =>
        {
            string path = Path.Combine(directory, "CallIdentity.cs");
            File.WriteAllText(path, """
                /* @link model Calls; */
                class Calls {
                    string __tlalinkOperation0 = "";
                    readonly object gate = new();
                    /* @link state Value guard=gate type=int; */
                    int value;
                    void Change() {
                        lock (gate) {
                            /* @link step First actor=this operation="$call" point=commit classification=transition observe=Value; */
                            value = 1;
                            /* @link step Second actor=this operation="$call" point=commit classification=transition observe=Value; */
                            value++;
                        }
                    }
                }
                """);
            CSharpScope scope = CSharpScope.Parse([path]);
            List<ModelBinding> models = scope.Bindings.Select(b => new ModelBinding(b.Model, b.Tag,
                new(b.Tag.Identity, "", b.Tag.Kind, new("fixture.tla", 1, 1), ""), 0)).ToList();
            string generated = Path.Combine(directory, "call-generated");
            Instrumentation.Generate(scope, models, Review.Build("instrument", scope, models, [], []), generated);
            string rewritten = Files.Read(Path.Combine(generated, "CallIdentity.cs"));
            CoreTests.Equal(1, rewritten.Split("NewOperationIdentity()", StringSplitOptions.None).Length - 1);
            CoreTests.True(rewritten.Contains("string __tlalinkOperation0_ =", StringComparison.Ordinal));
            CoreTests.True(TlaLink.Runtime.TraceRuntime.NewOperationIdentity() != TlaLink.Runtime.TraceRuntime.NewOperationIdentity());
        });
        test("control transfers are invoke-only and three-argument actions require an explicit entity", () =>
        {
            string path = Path.Combine(directory, "Routing.cs");
            string text = """
                /* @link model Routes; */
                class Router {
                    void Route(Target target, bool fail) {
                        /* @link step Choose actor=this operation="$call" entity=target point=invoke classification=transition; */
                        switch (fail) {
                            case true:
                                /* @link step Reject actor=this operation="$call" entity=target point=invoke classification=transition; */
                                throw new System.InvalidOperationException();
                            default:
                                /* @link step Return actor=this operation="$call" entity=target point=invoke classification=transition; */
                                return;
                        }
                    }
                }
                class Target {}
                """;
            InstrumentManifest Generate(string output)
            {
                CSharpScope scope = CSharpScope.Parse([path]);
                List<ModelBinding> models = scope.Bindings.Select(b => new ModelBinding(b.Model, b.Tag,
                    new(b.Tag.Identity, "", b.Tag.Kind, new("fixture.tla", 1, 1), ""), 3)).ToList();
                return Instrumentation.Generate(scope, models, Review.Build("instrument", scope, models, [], []), output);
            }
            File.WriteAllText(path, text);
            string generated = Path.Combine(directory, "routing-generated");
            CoreTests.True(Generate(generated).Events.All(e => e.Arity == 3 && e.Entity));
            CoreTests.True(Files.Read(Path.Combine(generated, "Routing.cs")).Contains("ObjectIdentity(target)", StringComparison.Ordinal));
            File.WriteAllText(path, text.Replace("point=invoke", "point=commit"));
            CoreTests.ThrowsAny<InputException>(() => Generate(Path.Combine(directory, "routing-after")));
            File.WriteAllText(path, text.Replace("entity=target ", ""));
            CoreTests.ThrowsAny<InputException>(() => Generate(Path.Combine(directory, "routing-missing-entity")));
        });
        test("blocked instrumentation retains the diagnostic that requires action", () =>
        {
            string path = Path.Combine(directory, "Blocked.cs");
            File.WriteAllText(path, "class Blocked {}");
            CSharpScope scope = CSharpScope.Parse([path]);
            Packet packet = new("instrument") { LinksStatus = "incomplete" };
            packet.Diagnostics.Add(new(ErrorCode.UnboundModelEndpoint, "Specific unresolved correspondence endpoint."));
            try
            {
                Instrumentation.Generate(scope, [], packet, Path.Combine(directory, "blocked-output"));
                throw new InvalidOperationException("Blocked instrumentation was accepted.");
            }
            catch (InputException error)
            {
                CoreTests.True(error.Message.Contains("Specific unresolved correspondence endpoint.", StringComparison.Ordinal));
            }
        });
    }
}
