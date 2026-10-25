using System.Text.Json;
using System.Text.RegularExpressions;

namespace TlaLink;

public static partial class ProjectWorkflow
{
    private static JsonElement Tlc(string path, AnalysisProject project, AnalysisStage stage, string output, Dictionary<string, string> pins)
    {
        string model = ProjectInputs.Resolve(path, stage.Models![0]);
        string jar = ProjectInputs.Resolve(path, project.Tools.Jar!.Path);
        TlaDocument document = TlaBackend.Parse(model, jar, stage.TimeoutSeconds);
        foreach (var pin in document.DependencyFingerprints) Files.Pin(pins, pin.Key, pin.Value);
        if (document.Diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.StaleTranslation, ErrorCode.ToolFailure)))
            throw new InputException(ErrorCode.WorkflowPrerequisiteMissing, "TLC requires a successfully parsed, fresh model: " +
                string.Join("; ", document.Diagnostics.Select(d => d.Message)));
        var result = Invoke(Executable("java"), ["-Xmx512m", "-cp", jar, "tlc2.TLC", "-tool", "-workers", "1",
            "-metadir", Path.Combine(output, "states"), "-config", ProjectInputs.Resolve(path, stage.Config!), model],
            output, stage.TimeoutSeconds, "tlc", allowFailure: true);
        List<string> codes = TlcErrors().Matches(result.Text).Select(m => "TLC" + m.Groups[1].Value).Distinct().ToList();
        bool rejected = result.ExitCode != 0 && codes.Any(c => c is "TLC2110" or "TLC2111" or "TLC2116" or "TLC2120");
        return JsonSerializer.SerializeToElement(new {
            SchemaVersion = Schema.Version, Kind = "tlc-result",
            Status = result.ExitCode == 0 ? "passed" : rejected ? "rejected" : "incomplete",
            Diagnostics = codes.Select(c => new Issue(c, "error", "See tlc.log for the native diagnostic.")),
            Model = model, Config = ProjectInputs.Resolve(path, stage.Config!), ExitCode = result.ExitCode,
            Boundaries = new[] { "Only the configured finite model; source correspondence and fingerprint collision risk require review." }
        }, Files.Json);
    }

    [GeneratedRegex(@"@!@!@STARTMSG (\d+):1 @!@!@")]
    private static partial Regex TlcErrors();
}
