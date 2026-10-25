using System.Reflection;
using System.Text.Json;

namespace TlaLink.Tests;

public static class DiagnosticTests
{
    public static void Register(Action<string, Action> test)
    {
        test("diagnostic enum metadata is complete, unique and cached", () =>
        {
            HashSet<string> ids = new(StringComparer.Ordinal);
            foreach (ErrorCode code in Enum.GetValues<ErrorCode>())
            {
                DiagnosticInfoAttribute metadata = code.GetInfo();
                DiagnosticInfoAttribute declared = typeof(ErrorCode).GetField(code.ToString())!
                    .GetCustomAttribute<DiagnosticInfoAttribute>()!;
                CoreTests.True(ids.Add(metadata.Id));
                CoreTests.Equal(declared.Id, metadata.Id);
                CoreTests.Equal(declared.Severity, metadata.Severity);
                CoreTests.True(ReferenceEquals(metadata, code.GetInfo()));
                CoreTests.True(code.Severity() is "error" or "warning");
            }
            CoreTests.Equal(Enum.GetValues<ErrorCode>().Length, ids.Count);
        });
        test("undefined diagnostic codes fail explicitly", () =>
        {
            CoreTests.ThrowsAny<ArgumentOutOfRangeException>(() => ((ErrorCode)(-1)).GetInfo());
            CoreTests.ThrowsAny<ArgumentOutOfRangeException>(() => new Issue((ErrorCode)(-1), "invalid"));
            CoreTests.ThrowsAny<ArgumentOutOfRangeException>(() => new InputException((ErrorCode)(-1), "invalid"));
        });
        test("typed input exceptions retain code and existing message prefix", () =>
        {
            InputException error = new(ErrorCode.InvalidWorkflowInput, "Unknown scenario: missing");
            CoreTests.Equal<ErrorCode?>(ErrorCode.InvalidWorkflowInput, error.Code);
            CoreTests.Equal("WF001: Unknown scenario: missing", error.Message);
            CoreTests.Equal<ErrorCode?>(null, new InputException("Uncoded input error").Code);
        });
        test("typed issues preserve exact public JSON shape and severity", () =>
        {
            Issue error = new(ErrorCode.WorkflowStageFailed, "Failed");
            CoreTests.Equal(
                JsonSerializer.Serialize(new Issue("WF900", "error", "Failed"), Files.Json),
                JsonSerializer.Serialize(error, Files.Json));
            Issue warning = new(ErrorCode.ReviewInputChanged, "Changed", new("input.cs", 2, 3));
            CoreTests.Equal(
                JsonSerializer.Serialize(new Issue("TL300", "warning", "Changed", new("input.cs", 2, 3)), Files.Json),
                JsonSerializer.Serialize(warning, Files.Json));
            CoreTests.Equal(warning, JsonSerializer.Deserialize<Issue>(JsonSerializer.Serialize(warning, Files.Json), Files.Json));
            CoreTests.True(warning.Is(ErrorCode.ToolFailure, ErrorCode.ReviewInputChanged));
            CoreTests.True(!warning.Is(ErrorCode.ToolFailure));
        });
        test("native diagnostic IDs remain strings and round trip unchanged", () =>
        {
            Issue native = new("TLC2110", "error", "Native diagnostic");
            CoreTests.Equal(native, JsonSerializer.Deserialize<Issue>(JsonSerializer.Serialize(native, Files.Json), Files.Json));
            CoreTests.True(!native.Is(ErrorCode.ToolFailure));
            TlaBackendDiagnostic warning = new(ErrorCode.StaleTranslation, "Stale", "Model.tla", 5);
            CoreTests.Equal(new TlaBackendDiagnostic("TL400", "warning", "Stale", "Model.tla", 5), warning);
            CoreTests.Equal(warning, JsonSerializer.Deserialize<TlaBackendDiagnostic>(
                JsonSerializer.Serialize(warning, Files.Json), Files.Json));
            CoreTests.True(warning.Is(ErrorCode.StaleTranslation));
        });
    }
}
