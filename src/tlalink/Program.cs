using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TlaLink;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            CliOptions options = new(args);
            if (options.Command is "inspect-p" or "check-p" or "read-p") return PBackend.Run(options);
            if (options.Command is "doctor" or "plan" or "run" or "accept" or "explain" or "record-review") return ProjectWorkflow.Run(options);
            if (options.Command == "read-integrated") return IntegratedEvidence.Read(options);
            if (options.Command is "inspect-integrated" or "verify-integrated" or "test-integrated")
                return IntegratedReview.Run(options);
            if (options.Command == "execute")
            {
                object receipt = Execution.Run(options.Required("manifest"), options.Required("project"), options.Required("trace"),
                    options.Required("output"), options.Timeout, options.Many("arg"));
                Render(receipt, options);
                return 0;
            }
            string jar = Path.GetFullPath(options.Required("jar"));
            List<string> modelPaths = Files.Discover(options.Many("model"), ".tla");
            ModelScope models = ModelScope.Load(modelPaths, jar, options.Timeout, annotations: options.Command != "inputs");
            PinTool(models.Fingerprints);
            Files.Pin(models.Fingerprints, jar, Files.Hash(jar));
            string adapter = Path.Combine(AppContext.BaseDirectory, "TlaIndex.java");
            Files.Pin(models.Fingerprints, adapter, Files.Hash(adapter));
            if (options.Command == "inputs")
            {
                bool ready = models.Diagnostics.All(d => d.Severity != "error" && !d.Is(ErrorCode.StaleTranslation, ErrorCode.ToolFailure));
                Render(new { SchemaVersion = Schema.Version, Kind = "model-inputs", Command = "inputs", Status = ready ? "ready" : "incomplete",
                    Fingerprints = models.Fingerprints, Diagnostics = models.Diagnostics,
                    Invocation = new { Arguments = args, WorkingDirectory = Environment.CurrentDirectory,
                        ToolAssembly = typeof(Program).Assembly.Location } }, options);
                return ready ? 0 : 2;
            }
            if (options.Command == "validate-trace")
            {
                if (models.Documents.Count != 1 || models.Diagnostics.Any(d => d.Severity == "error" || d.Is(ErrorCode.StaleTranslation, ErrorCode.ToolFailure)))
                    throw new InputException(ErrorCode.InvalidTrace, "Trace validation requires one successfully parsed, fresh root model.");
                TlaDocument document = models.Documents.Single();
                object validation = TraceValidation.Validate(options.Required("manifest"), options.Required("trace"),
                    options.Required("config"), document.Path, document.Module, document.Variables.ToList(),
                    models.Bindings, models.Fingerprints, jar, options.Required("output"), options.Timeout,
                    options.Get("init", "Init"), options.Get("next", "Next"), options.Required("execution"));
                Render(validation, options);
                using JsonDocument result = JsonDocument.Parse(JsonSerializer.Serialize(validation, Files.Json));
                return result.RootElement.GetProperty("status").GetString() switch { "conforms" => 0, "rejected" => 1, _ => 2 };
            }
            CSharpScope source = CSharpScope.Parse(Files.Discover(options.Many("source"), ".cs"),
                options.Many("reference"), options.Has("define") ? options.Many("define") : null);
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
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                SchemaVersion = Schema.Version, Kind = "command-error", Status = "incomplete",
                Diagnostics = new[] { new Issue(ErrorCode.ToolFailure, error.Message) }
            }, Files.Json));
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
    }

    public static void Baseline(string path, Packet packet)
    {
        using JsonDocument snapshot = JsonDocument.Parse(Files.Read(path));
        JsonElement root = snapshot.RootElement;
        if (!root.TryGetProperty("schema_version", out JsonElement schema) || schema.GetInt32() != Schema.Version ||
            !root.TryGetProperty("kind", out JsonElement kind) || kind.GetString() != "source-review" ||
            !root.TryGetProperty("fingerprints", out JsonElement previous) || previous.ValueKind != JsonValueKind.Object)
            throw new InputException(ErrorCode.ReviewInputChanged, "Baseline needs a current source-review fingerprint object; older schemas are not accepted.");
        Dictionary<string, string> fingerprints = [];
        foreach (JsonProperty property in previous.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String || !fingerprints.TryAdd(property.Name, property.Value.GetString()!))
                throw new InputException(ErrorCode.ReviewInputChanged, "Baseline fingerprints must be unique strings.");
        }
        if (fingerprints.Count == 0) throw new InputException(ErrorCode.ReviewInputChanged, "Baseline fingerprints are empty.");
        foreach (string key in fingerprints.Keys.Union(packet.Fingerprints.Keys).Order(StringComparer.Ordinal))
            if (fingerprints.GetValueOrDefault(key) != packet.Fingerprints.GetValueOrDefault(key))
                packet.Diagnostics.Add(new(ErrorCode.ReviewInputChanged, "Changed/added/removed input; prior review evidence requires reconsideration: " + key));
        if (packet.Diagnostics.Any(d => d.Code == ErrorCode.ReviewInputChanged.Id()) && packet.LinksStatus == "passed") packet.LinksStatus = "incomplete";
    }

    private static void Render(object value, CliOptions options)
    {
        if (options.Get("format", "json") == "json") Console.WriteLine(JsonSerializer.Serialize(value, Files.Json));
        else if (value is Packet packet)
        {
            Console.WriteLine($"Links: {packet.LinksStatus}; coverage: {packet.CoverageStatus}; conformance: {packet.ConformanceStatus}");
            foreach (Link link in packet.Links) Console.WriteLine($"{link.Key}: {link.Implementation.Location.Path}:{link.Implementation.Location.Line} -> {link.Specification.Location.Path}:{link.Specification.Location.Line}");
            foreach (Issue issue in packet.Diagnostics) Console.WriteLine($"{issue.Code} {issue.Severity}: {issue.Message}");
        }
        else Console.WriteLine(JsonSerializer.Serialize(value, Files.Json));
    }
}
