using System.Text.Json;
using TlaLink;

namespace TlaLink.Tests;

public static class SourceInventoryTests
{
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
    }
}
