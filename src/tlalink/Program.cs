using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TlaLink;

public static class Program
{
    public static int Main(string[] args)
    {
        CliOptions? options = null;
        try
        {
            options = new(args);
            if (options.Command == "symbols") return SourceSymbols.Run(options);
            if (options.Command is "prepare-sources" or "check-prepared-sources") return SourcePreparation.Run(options);
            if (CliOptions.AnnotationCommands.Contains(options.Command, StringComparer.Ordinal))
                Markup.RequireVersion(options.AnnotationVersion);
            if (options.Command == "links") return Links.Run(options);
            if (options.Command == "inspect-source") return SourceInventory.Run(options);
            if (options.Command == "report") return EvidenceReport.Run(options);
            if (options.Command == "project-schema") return ProjectEditorSchema.Run(options);
            if (options.Command == "gc") return EvidenceGarbageCollection.Run(options);
            if (options.Command is "check-dafny" or "read-dafny") return DafnyAnalysisBackend.Run(options);
            if (options.Command is "check-regression" or "read-regression") return CSharpRegression.Run(options);
            if (options.Command is "check-apalache" or "read-apalache") return ApalacheBackend.Run(options);
            if (options.Command is "inspect-p" or "check-p" or "read-p") return PBackend.Run(options);
            if (options.Command is "expand" or "doctor" or "plan" or "run" or "check-stage" or "accept" or "explain" or "record-review") return ProjectWorkflow.Run(options);
            if (options.Command == "read-integrated") return IntegratedEvidence.Read(options);
            if (options.Command is "inspect-integrated" or "verify-integrated" or "test-integrated")
                return IntegratedReview.Run(options);
            if (options.Command == "execute")
            {
                ExecutionReceipt receipt = Execution.Run(options.Required("manifest"), options.Required("project"), options.Required("trace"),
                    options.Required("output"), options.Timeout, options.Many("arg"));
                Render(receipt, options);
                return 0;
            }
            string jar = FilePaths.Full(options.Required("jar"));
            Dictionary<string, string> scopePins = new(FilePaths.Comparer);
            CorrespondenceConfiguration? correspondence = options.Command == "inputs" ? null :
                CorrespondenceInputs.Load(options.Required("scope"), scopePins);
            string? alias = correspondence == null ? null : options.Required("target");
            FormalTarget? target = correspondence == null ? null : CorrespondenceInputs.Target(correspondence, alias!, "tla");
            List<string> modelPaths = target?.Inputs ?? Files.Discover(options.Many("model"), ".tla");
            if (target != null && options.Many("model").Count != 0 &&
                !Files.Discover(options.Many("model"), ".tla").SequenceEqual(target.Inputs, FilePaths.Comparer))
                throw new InputException(ErrorCode.InvalidModelBinding, "--model contradicts the selected correspondence target.");
            ModelScope models = ModelScope.Load(modelPaths, jar, options.Timeout, annotations: options.Command != "inputs");
            foreach (var pin in scopePins) Files.Pin(models.Fingerprints, pin.Key, pin.Value);
            PinTool(models.Fingerprints);
            Files.Pin(models.Fingerprints, jar, Files.Hash(jar));
            string adapter = Path.Combine(AppContext.BaseDirectory, "TlaIndex.java");
            Files.Pin(models.Fingerprints, adapter, Files.Hash(adapter));
            if (options.Command == "inputs")
            {
                bool ready = models.Diagnostics.All(d => d.Severity != "error" && !d.Is(ErrorCode.StaleTranslation, ErrorCode.ToolFailure));
                Render(new ModelInputsResult(Schema.Version, "model-inputs", "inputs", ready ? "ready" : "incomplete",
                    models.Fingerprints, models.Diagnostics,
                    new(args, Environment.CurrentDirectory, typeof(Program).Assembly.Location)), options);
                return ready ? 0 : 2;
            }
            if (options.Command == "validate-trace")
            {
                InstrumentManifest manifest = Instrumentation.ReadManifest(options.Required("manifest"));
                string scopePath = FilePaths.Full(options.Required("scope"));
                if (!manifest.Fingerprints.TryGetValue(scopePath, out string? scopeHash) || scopeHash != Files.Hash(scopePath))
                    throw new InputException(ErrorCode.InvalidTrace, "Trace validation must reuse the instrumented correspondence scope and its pinned bytes.");
                if (manifest.Events.Any(e => e.Model != alias))
                    throw new InputException(ErrorCode.InvalidTrace, "Manifest events disagree with the selected target alias.");
                models.BindManifest(manifest, correspondence!);
                if (models.Documents.Count != 1 || models.Diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.StaleTranslation, ErrorCode.ToolFailure)))
                    throw new InputException(ErrorCode.InvalidTrace, "Trace validation requires one successfully parsed, fresh root model.");
                TlaDocument document = models.Documents.Single();
                TraceValidationResult validation = TraceValidation.Validate(options.Required("manifest"), options.Required("trace"),
                    options.Required("config"), document.Path, document.Module, document.Variables.ToList(),
                    models.Bindings, models.Fingerprints, jar, options.Required("output"), options.Timeout,
                    options.Get("init", "Init"), options.Get("next", "Next"), options.Required("execution"), TlcResources.FromCli(options));
                Render(validation, options);
                return validation.Status switch { "conforms" => 0, "rejected" => 1, _ => 2 };
            }
            CSharpScope source = CSharpScope.Parse(Files.Discover(options.Many("source"), ".cs"),
                options.Many("reference"), options.Has("define") ? options.Many("define") : null,
                languageVersion: options.Get("language-version", CSharpLanguages.Default));
            source.ApplyCorrespondence(correspondence!);
            source.SelectedTargets = new(StringComparer.Ordinal) { alias! };
            models.BindSources(source, correspondence!, alias);
            Packet packet = Review.Build(options.Command, source, models.Bindings, models.Diagnostics, models.Fingerprints,
                options.Command == "review" ? options.Required("step") : null, models.Inventory);
            if (options.Optional("baseline") is string baseline) Baseline(baseline, packet);
            if (options.Optional("snapshot") is string snapshot) Files.WriteNew(snapshot, packet);
            if (options.Command == "instrument")
                Render(Instrumentation.Generate(source, models.Bindings, packet, options.Required("output")), options);
            else Render(packet, options);
            return packet.LinksStatus switch { "passed" => 0, "failed" => 1, _ => 2 };
        }
        catch (Exception error) when (error is InputException or IOException or UnauthorizedAccessException or JsonException
            or DecoderFallbackException or Win32Exception or TimeoutException or KeyNotFoundException or BadImageFormatException ||
            error is (InvalidOperationException or FormatException) && error.TargetSite?.DeclaringType?.Assembly == typeof(JsonDocument).Assembly)
        {
            CommandFailure failure = new(Schema.Version, "command-error", options?.Command, "incomplete",
                [new Issue(error is InputException { Code: { } code } ? code : ErrorCode.ToolFailure, error.Message)]);
            if (options == null) Console.WriteLine(JsonSerializer.Serialize(failure, Files.Json));
            else Render(failure, options);
            return 2;
        }
    }

    public static void PinTool(Dictionary<string, string> pins)
    {
        string[] tools = [typeof(Program).Assembly.Location, typeof(Microsoft.CodeAnalysis.Compilation).Assembly.Location,
            typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly.Location,
            Path.Combine(AppContext.BaseDirectory, "TraceRuntime.source.cs")];
        foreach (string tool in tools) Files.Pin(pins, tool, Files.Hash(tool));
        pins["runtime:dotnet"] = RuntimeInformation.FrameworkDescription;
        pins["runtime:architecture"] = RuntimeInformation.ProcessArchitecture.ToString();
        pins["tool:tlalink-schema"] = Schema.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        pins["tool:annotation-version"] = Markup.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static void Baseline(string path, Packet packet)
    {
        Packet previous = ProjectInputs.Read<Packet>(path);
        if (previous.SchemaVersion != Schema.Version || previous.Kind != "source-review")
            throw new InputException(ErrorCode.ReviewInputChanged, "Baseline needs a current source-review fingerprint object; older schemas are not accepted.");
        Dictionary<string, string> fingerprints = previous.Fingerprints;
        if (fingerprints.Count == 0) throw new InputException(ErrorCode.ReviewInputChanged, "Baseline fingerprints are empty.");
        foreach (string key in fingerprints.Keys.Union(packet.Fingerprints.Keys).Order(StringComparer.Ordinal))
            if (fingerprints.GetValueOrDefault(key) != packet.Fingerprints.GetValueOrDefault(key))
                packet.Diagnostics.Add(new(ErrorCode.ReviewInputChanged, "Changed/added/removed input; prior review evidence requires reconsideration: " + key));
        if (packet.Diagnostics.Any(d => d.Code == ErrorCode.ReviewInputChanged.Id()) && packet.LinksStatus == "passed") packet.LinksStatus = "incomplete";
    }

    private static void Render(object value, CliOptions options)
    {
        if (options.Get("format", "json") == "summary") Console.WriteLine(CommandSummary.Create(value, options).ToJsonString(Files.Json));
        else if (options.Get("format", "json") == "json") Console.WriteLine(JsonSerializer.Serialize(value, Files.Json));
        else if (value is Packet packet)
        {
            Console.WriteLine($"Links: {packet.LinksStatus}; coverage: {packet.CoverageStatus}; conformance: {packet.ConformanceStatus}");
            foreach (Link link in packet.Links) Console.WriteLine($"{link.Key}: {link.Implementation.Location.Path}:{link.Implementation.Location.Line} -> {link.Specification.Location.Path}:{link.Specification.Location.Line}");
            foreach (Issue issue in packet.Diagnostics) Console.WriteLine($"{issue.Code} {issue.Severity}: {issue.Message}");
        }
        else Console.WriteLine(JsonSerializer.Serialize(value, Files.Json));
    }
}
