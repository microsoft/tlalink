using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TlaLink.Setup;

public sealed record NativeArchivePin(string version, string directory, string archive_url, string archive_sha256);
public sealed record NativeSourcePin(string version, string repository, string commit, string directory, string reason);
public sealed record NativeDotNetPin(string sdk8, string runtime8, string reason);
public sealed record NativeJavaExceptionPin(string version, string directory, string archive_url, string archive_sha256, string reason);
public sealed record NativeLombokPin(string version);
public sealed record NativeJavaPlatformPin(string archive_url, string archive_sha256, Dictionary<string, string> files);
public sealed record NativeSharedJavaDocument(int schema_version, string kind, string version, string directory,
    Dictionary<string, NativeJavaPlatformPin> platforms)
{
    public static NativeSharedJavaDocument Read(string json)
    {
        var pin = JsonSerializer.Deserialize<NativeSharedJavaDocument>(json,
            new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true })
            ?? throw new InvalidOperationException("Missing java-toolchain document.");
        if (pin.schema_version != 4 || pin.kind != "java-toolchain" || pin.platforms is null ||
            string.IsNullOrWhiteSpace(pin.version) || string.IsNullOrWhiteSpace(pin.directory))
            throw new InvalidOperationException("Expected complete schema-4 java-toolchain pins.");
        return pin;
    }
}
public sealed record NativeToolchainDocument(int schema_version, string kind, NativeDotNetPin dotnet,
    NativeArchivePin maven, NativeArchivePin sbt, NativeArchivePin z3, NativeSourcePin p,
    NativeSourcePin psym_generator, NativeSourcePin uclid, NativeJavaExceptionPin uclid_build_java,
    NativeLombokPin lombok, Dictionary<string, string> generation_patches)
{
    public static NativeToolchainDocument Read(string json)
    {
        var options = new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true };
        var pin = JsonSerializer.Deserialize<NativeToolchainDocument>(json, options)
            ?? throw new InvalidOperationException("Missing native-toolchain document.");
        if (pin.schema_version != 4 || pin.kind != "native-toolchain" || pin.dotnet is null ||
            pin.maven is null || pin.sbt is null || pin.z3 is null || pin.p is null ||
            pin.psym_generator is null || pin.uclid is null || pin.uclid_build_java is null ||
            pin.lombok is null || pin.generation_patches is null ||
            string.IsNullOrWhiteSpace(pin.dotnet.reason) || string.IsNullOrWhiteSpace(pin.psym_generator.reason) ||
            string.IsNullOrWhiteSpace(pin.uclid_build_java.reason))
            throw new InvalidOperationException("Expected complete schema-4 native-toolchain pins and explicit exception reasons.");
        return pin;
    }
}
