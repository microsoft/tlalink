using System.Text.Json;

namespace TlaLink;

internal static class AnalysisDocuments
{
    internal static bool MatchesStage(string stage, IAnalysisResult result) => stage switch {
        "tlc" => result is TlcResult, "trace" => result is TraceValidationResult,
        "csharp-regression" => result is CSharpRegressionResult, "dafny-verify" => result is DafnyResult,
        "inputs" => result is ModelInputsResult,
        _ when PInputs.IsStage(stage) => result is PPacket,
        _ when ApalacheInputs.IsStage(stage) => result is ApalachePacket,
        _ => result is IntegratedResult
    };
    internal static IAnalysisResult Read(string path) => Parse(Files.Read(path));

    internal static IAnalysisResult Parse(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowDuplicateProperties = false });
        return Read(document.RootElement);
    }

    internal static IAnalysisResult Read(JsonElement root)
    {
        string selected = Kind(root);
        return selected switch {
            "tlc-result" => Deserialize<TlcResult>(root),
            "trace-validation" => Deserialize<TraceValidationResult>(root),
            "csharp-regression-result" => Deserialize<CSharpRegressionResult>(root),
            "dafny-result" => Deserialize<DafnyResult>(root),
            "p-result" => Deserialize<PPacket>(root),
            "apalache-result" => Deserialize<ApalachePacket>(root),
            "model-inputs" => Deserialize<ModelInputsResult>(root),
            "command-error" => Deserialize<CommandFailure>(root),
            "integrated-result" => Deserialize<IntegratedResult>(root),
            _ => throw new InputException(ErrorCode.InvalidCommandOutput, "Unsupported analysis result kind: " + selected)
        };
    }

    internal static string Kind(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out JsonElement kind) ||
            kind.ValueKind != JsonValueKind.String)
            throw new InputException(ErrorCode.InvalidCommandOutput, "Analysis result requires a document kind.");
        string selected = kind.GetString()!;
        Files.RequireSchema(root, selected);
        return selected;
    }

    internal static T Deserialize<T>(JsonElement root) =>
        root.Deserialize<T>(ProjectInputs.Json)
        ?? throw new InputException(ErrorCode.InvalidCommandOutput, "Empty " + typeof(T).Name + " document.");
}
