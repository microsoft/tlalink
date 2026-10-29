using TlaLink;

namespace TlaLink.Tests;

internal sealed record AsyncGateCase(string Name, string Source, string Status, string Reason,
    string? Gate = null, int? AwaitCount = null);
internal sealed record AsyncGateCases(int SchemaVersion, string Kind, List<AsyncGateCase> Cases);
internal sealed record AsyncGateFixture(string Directory, string ManifestPath, IntegratedManifest Manifest);

internal static class AsyncGateFixtures
{
    private static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures", "AsyncGate");

    internal static List<AsyncGateCase> Cases()
    {
        AsyncGateCases catalog = ProjectInputs.Read<AsyncGateCases>(Path.Combine(Root, "cases.json"));
        if (catalog.SchemaVersion != Schema.Version || catalog.Kind != "async-gate-test-cases" ||
            catalog.Cases.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != catalog.Cases.Count)
            throw new InvalidOperationException("Invalid async gate test catalog.");
        return catalog.Cases;
    }

    internal static AsyncGateFixture Copy(string directory, string name)
    {
        AsyncGateCase sample = Cases().Single(c => c.Name == name);
        Directory.CreateDirectory(directory);
        foreach (string file in new[] { "Work.cs", "Claim.dfy", "scope.json", "bindings.json" })
            File.Copy(Path.Combine(Root, file), Path.Combine(directory, file));
        File.Copy(sample.Gate == null ? Path.Combine(Root, "AdmissionGate.cs") :
            Path.Combine(Root, "Gates", sample.Gate), Path.Combine(directory, "AdmissionGate.cs"));
        File.Copy(Path.Combine(Root, "Cases", sample.Source), Path.Combine(directory, "Service.cs"));
        string manifestPath = Path.Combine(directory, "bindings.json");
        IntegratedManifest manifest = ProjectInputs.Read<IntegratedManifest>(manifestPath);
        List<BindAnnotation> selectors = FormalCorrespondence.Comments(Files.Read(Path.Combine(directory, "Claim.dfy")), nested: true)
            .SelectMany(c => Markup.Parse(c.Content, c.Start, c.End)).Select(a => a.Declaration).OfType<BindAnnotation>().ToList();
        manifest = manifest with {
            Sources = manifest.Sources.Select(p => FilePaths.Full(p, directory)).ToList(),
            Bindings = manifest.Bindings.Select(b => b with {
                Source = selectors.Single(s => s.Id == b.Id).Source!.Symbol
            }).ToList()
        };
        return new(directory, manifestPath, manifest);
    }
}

public static class AsyncGateObligationTests
{
    public static void Register(Action<string, Action> test, string work)
    {
        string directory = Directory.CreateDirectory(Path.Combine(work, "async-gate")).FullName;
        foreach (AsyncGateCase sample in AsyncGateFixtures.Cases())
            test("async gate lifetime source contract: " + sample.Name, () =>
            {
                AsyncGateFixture fixture = AsyncGateFixtures.Copy(Path.Combine(directory, sample.Name), sample.Name);
                SourceObligationResult result = SourceObligations.Check(
                    CSharpScope.Parse(fixture.Manifest.Sources, languageVersion: "8"), fixture.Manifest).Single();
                if (result.Status != sample.Status || !result.Reason.Contains(sample.Reason, StringComparison.Ordinal))
                    throw new InvalidOperationException(sample.Name + ": " + result.Status + ": " + result.Reason);
                if (sample.Status != "passed") return;
                CoreTests.True(result.Flow != null);
                CoreTests.True(result.AsyncGate != null);
                CoreTests.Equal(sample.AwaitCount, result.AsyncGate!.Awaits.Count);
                CoreTests.True(result.Evidence.Contains(result.AsyncGate.Acquisition));
                CoreTests.True(result.Evidence.Contains(result.AsyncGate.Release));
                CoreTests.True(result.AsyncGate.Boundary.Contains("successful release", StringComparison.Ordinal));
                CoreTests.True(result.AsyncGate.Boundary.Contains("eventual completion", StringComparison.Ordinal));
            });
        test("async gate lifetime validates exact fields rather than accepting unrelated selectors", () =>
        {
            AsyncGateFixture fixture = AsyncGateFixtures.Copy(Path.Combine(directory, "schema"), "balanced");
            SourceObligation obligation = fixture.Manifest.SourceObligations.Single();
            foreach (SourceObligation invalid in new[] {
                obligation with { Gate = obligation.Effect }, obligation with { Gate = "Enter" },
                obligation with { When = true }, obligation with { Parameter = "token" },
                obligation with { AllowedCalls = ["M:AdmissionGate.Close"] }
            })
                CoreTests.ThrowsAny<InputException>(() => SourceObligations.Validate([invalid], ["proof::work"]));
        });
    }
}
