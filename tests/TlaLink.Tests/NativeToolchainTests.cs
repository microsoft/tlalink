using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using TlaLink.Setup;

namespace TlaLink.Tests;

public static class NativeToolchainTests
{
    public static void Register(Action<string, Action> test, string root)
    {
        string path = Path.Combine(root, "native-toolchain.json");
        test("native setup toolchain is one strict schema-4 contract with narrow justified exceptions", () =>
        {
            string json = Files.Read(path);
            using JsonDocument document = JsonDocument.Parse(json);
            Files.RequireSchema(document.RootElement, "native-toolchain");
            var strict = new JsonSerializerOptions(Files.Json) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
            NativeToolchainDocument pin = JsonSerializer.Deserialize<NativeToolchainDocument>(json, strict)!;
            CoreTests.Equal(pin.dotnet, NativeToolchainDocument.Read(json).dotnet);
            CoreTests.Equal("8.0.424", pin.dotnet.sdk8);
            CoreTests.Equal("8.0.30", pin.dotnet.runtime8);
            CoreTests.True(pin.dotnet.reason.Contains("net8.0", StringComparison.Ordinal));
            CoreTests.True(pin.psym_generator.reason.Contains("PSym 1.1.4", StringComparison.Ordinal));
            CoreTests.True(pin.uclid_build_java.reason.Contains("System.setSecurityManager", StringComparison.Ordinal));
            CoreTests.True(pin.uclid_build_java.reason.Contains("Only this build", StringComparison.Ordinal));
            CoreTests.True(!document.RootElement.TryGetProperty("sdk", out _) && !document.RootElement.TryGetProperty("java", out _));
            NativeSharedJavaDocument java = NativeSharedJavaDocument.Read(Files.Read(Path.Combine(root, "java-toolchain.json")));
            CoreTests.True(java.version.StartsWith("25.", StringComparison.Ordinal));
            foreach (NativeArchivePin archive in new[] { pin.maven, pin.sbt, pin.z3 })
            {
                CoreTests.True(Uri.TryCreate(archive.archive_url, UriKind.Absolute, out Uri? uri) && uri.Scheme == "https");
                CoreTests.True(archive.archive_sha256.Length == 64 && archive.archive_sha256.All(Uri.IsHexDigit));
            }
            foreach ((string patch, string hash) in pin.generation_patches)
                CoreTests.Equal(hash.ToLowerInvariant(), Files.Hash(Path.Combine(root, "examples", "Dispatcher", "setup", patch)));
            using JsonDocument sdk = JsonDocument.Parse(Files.Read(Path.Combine(root, "global.json")));
            CoreTests.Equal("disable", sdk.RootElement.GetProperty("sdk").GetProperty("rollForward").GetString());
        });
        test("native setup toolchain rejects unknown missing and null properties and absent exception reasons", () =>
        {
            JsonObject document = JsonNode.Parse(Files.Read(path))!.AsObject();
            document["surprise"] = true;
            CoreTests.ThrowsAny<JsonException>(() => NativeToolchainDocument.Read(document.ToJsonString()));
            document.Remove("surprise");
            JsonNode? lombok = document["lombok"]?.DeepClone();
            document.Remove("lombok");
            CoreTests.ThrowsAny<JsonException>(() => NativeToolchainDocument.Read(document.ToJsonString()));
            document["lombok"] = null;
            CoreTests.ThrowsAny<JsonException>(() => NativeToolchainDocument.Read(document.ToJsonString()));
            document["lombok"] = lombok;
            document["uclid_build_java"]!["reason"] = "";
            CoreTests.ThrowsAny<InvalidOperationException>(() => NativeToolchainDocument.Read(document.ToJsonString()));
        });
        test("native Maven Lombok versions come from common pins and PObserve explicitly selects its processor", () =>
        {
            NativeToolchainDocument pin = NativeToolchainDocument.Read(Files.Read(path));
            CoreTests.Equal("1.18.42", pin.lombok.version);
            XNamespace ns = "http://maven.apache.org/POM/4.0.0";
            foreach (string relative in new[] {
                Path.Combine("P", "Artifacts", "pex-pom.xml"),
                Path.Combine("P", "Artifacts", "psym-pom.xml"),
                Path.Combine("PObserve", "runtime-pom.xml") })
            {
                XDocument pom = XDocument.Load(Path.Combine(root, "examples", "Dispatcher", "verification", relative));
                XElement[] lombok = pom.Descendants(ns + "dependency")
                    .Where(d => (string?)d.Element(ns + "artifactId") == "lombok").ToArray();
                CoreTests.Equal(1, lombok.Length);
                CoreTests.Equal("${lombok.version}", (string?)lombok[0].Element(ns + "version"));
                if (relative.Contains("runtime-pom", StringComparison.Ordinal))
                    CoreTests.Equal("${lombok.version}", (string?)pom.Descendants(ns + "annotationProcessorPaths")
                        .Single().Element(ns + "path")!.Element(ns + "version"));
            }
        });
    }
}
