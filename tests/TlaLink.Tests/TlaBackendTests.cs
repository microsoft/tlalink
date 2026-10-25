using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using TlaLink;

namespace TlaLink.Tests;

public static class TlaBackendTests
{
    public static int Run(string jar, Action<bool, string> assert)
    {
        string directory = Path.Combine(Environment.CurrentDirectory,
            ".tlalink-backend-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        int assertions = 0;
        void Check(bool condition, string message) { assertions++; assert(condition, message); }
        try
        {
            var fixture = new Fixture(Path.GetFullPath(jar), directory, Check);
            Imports(fixture);
            Translations(fixture);
            OptionsAndScopes(fixture);
            MetadataAndOffsets(fixture);
            Failures(fixture);
            return assertions;
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void Imports(Fixture fixture)
    {
        string leaf = fixture.Write("Leaf", "Value == 1");
        string support = fixture.Write("Support", "EXTENDS Leaf, Naturals\nOther == Value + 1");
        string root = fixture.Write("Root", "S == INSTANCE Support\nLocal == S!Other");
        var parsed = fixture.Parse(root);
        fixture.Check(parsed.DependencyFingerprints.ContainsKey(leaf), "Transitive dependency");
        fixture.Check(parsed.DependencyFingerprints.ContainsKey(support), "INSTANCE dependency");
        fixture.Check(parsed.DependencyFingerprints.Keys.Any(k => k.StartsWith("library:", StringComparison.Ordinal)
            && k.Contains("Naturals.tla", StringComparison.Ordinal)), "Stable library origin");
        fixture.Check(parsed.Module == "Root", "SANY root module name");
        var local = parsed.Endpoints.Single(e => e.Name == "Local");
        fixture.Check(parsed.Text[local.Start..local.End].StartsWith("Local ==", StringComparison.Ordinal),
            "End-exclusive operator extent");
    }

    private static void Translations(Fixture fixture)
    {
        string flow = fixture.Write("Flow", Algorithm);
        var missing = fixture.Parse(flow);
        fixture.Check(missing.Diagnostics.Any(d => d.Code == "TL400" && d.Message.Contains("missing")),
            "Missing translation remains incomplete");
        fixture.Check(missing.Endpoints.Where(e => e.Kind == "label").Select(e => e.Name).Order()
            .SequenceEqual(new[] { "Explicit", "Finish" }), "Explicit labels without translation");
        fixture.Check(missing.Endpoints.Where(e => e.Kind == "label")
            .All(e => e.Scope == "Flow.Algorithm"), "Algorithm scope");
        fixture.Translate(flow);
        byte[] before = File.ReadAllBytes(flow);
        var current = fixture.Parse(flow);
        fixture.Check(current.Diagnostics.Count == 0, "Fresh translation");
        fixture.Check(current.Variables.SequenceEqual(new[] { "value", "pc" }), "Generated state variables");
        fixture.Check(before.SequenceEqual(File.ReadAllBytes(flow)), "Read-only parsing");
        File.WriteAllText(flow, File.ReadAllText(flow).Replace("value := value + 1", "value := value + 2"));
        var stale = fixture.Parse(flow);
        fixture.Check(stale.Diagnostics.Any(d => d.Code == "TL400" && d.Message.Contains("stale")),
            "Actual body freshness, not recorded checksums");
        string importing = fixture.Write("Importing", "EXTENDS Flow\nLocal == 1");
        var imported = fixture.Parse(importing);
        fixture.Check(imported.Diagnostics.Any(d => d.Code == "TL400" && d.Path == flow),
            "Imported PlusCal freshness");
        fixture.Check(imported.Endpoints.Count == 1 && imported.Endpoints[0].Name == "Local",
            "Imported PlusCal endpoints remain outside explicit scope");
    }

    private static void OptionsAndScopes(Fixture fixture)
    {
        string options = fixture.Write("Options",
            Algorithm.Replace("EXTENDS Naturals", "EXTENDS Naturals\n(* PlusCal options (-wf) *)"));
        fixture.Translate(options);
        fixture.Check(fixture.Parse(options).Diagnostics.Count == 0, "Recorded translator options");
        File.WriteAllText(options, File.ReadAllText(options).Replace("PlusCal options (-wf)", "options removed"));
        fixture.Check(fixture.Parse(options).Diagnostics.Any(d => d.Code == "TL400"),
            "Removed translator options invalidate freshness");
        string parallel = fixture.Write("Parallel", """
            EXTENDS Naturals
            (* --fair algorithm ParallelAlgorithm
            variables value = 0;
            process Worker \in 1..2
            begin
            Increment:
                value := value + 1;
            Finish:
                skip;
            end process;
            end algorithm; *)
            """);
        fixture.Translate(parallel);
        var process = fixture.Parse(parallel);
        fixture.Check(process.Diagnostics.Count == 0, "Fresh fair process");
        fixture.Check(process.Endpoints.Where(e => e.Kind == "label")
            .All(e => e.Scope == "Parallel.ParallelAlgorithm.Worker"), "Process scope");
        string braces = fixture.Write("Braces", """
            EXTENDS Naturals
            (* PlusCal options (-label) *)
            (* --algorithm BracesAlgorithm {
            variables value = 0;
            {
                value := 1;
            Explicit:
                value := 2;
            }
            } *)
            """);
        fixture.Translate(braces);
        var parsed = fixture.Parse(braces);
        fixture.Check(parsed.Diagnostics.Count == 0, "C-syntax fresh translation");
        fixture.Check(parsed.Endpoints.Where(e => e.Kind == "label").Select(e => e.Name)
            .SequenceEqual(new[] { "Explicit" }), "Synthetic labels excluded");
        File.WriteAllText(braces, File.ReadAllText(braces).Replace("\r\n", "\n").Replace("\n", "\r\n"),
            new UTF8Encoding(true));
        var bom = fixture.Parse(braces);
        fixture.Check(bom.Diagnostics.Count == 0 && !bom.Text.StartsWith('\ufeff'), "BOM/CRLF freshness");
    }

    private static void MetadataAndOffsets(Fixture fixture)
    {
        var parsed = fixture.Parse(fixture.Write("Arity",
            "VARIABLE x\nApply(F(_), arg) == F(arg)\nPair(a,b) == <<a,b>>"));
        fixture.Check(parsed.Variables.SequenceEqual(new[] { "x" }), "Declared state variable");
        fixture.Check(parsed.Endpoints.Single(e => e.Name == "Apply").Arity == 2, "Higher-order arity");
        fixture.Check(parsed.Endpoints.Single(e => e.Name == "Pair").Arity == 2, "Operator arity");
        var tabs = fixture.Parse(fixture.Write("Tabs", "\tValue == 1"));
        fixture.Check(tabs.Endpoints.Single().Column == 9
            && tabs.Endpoints.Single().Start == tabs.Text.IndexOf("Value", StringComparison.Ordinal),
            "Tab-expanded SANY columns");
        var unicode = fixture.Parse(fixture.Write("Unicode", "Text == \"\U0001f600\"\nOther == 1"));
        fixture.Check(unicode.Endpoints.Single(e => e.Name == "Other").Start
            == unicode.Text.IndexOf("Other", StringComparison.Ordinal), "UTF-16 normalized offsets");
    }

    private static void Failures(Fixture fixture)
    {
        foreach (string option in new[] { "-writeAST", "-spec", "-unknown-option" })
        {
            string bad = fixture.Write("Bad", Algorithm.Replace("EXTENDS Naturals",
                $"EXTENDS Naturals\n(* PlusCal options ({option}) *)"));
            var result = TlaBackend.Parse(bad, fixture.Jar, 30);
            fixture.Check(!result.ParserSucceeded && result.Diagnostics.Any(d => d.Code == "TL900"),
                "Explicit unsafe/invalid translator option failure: " + option);
        }
        string invalid = fixture.Write("Invalid", "Value == Undefined");
        fixture.Check(!TlaBackend.Parse(invalid, fixture.Jar, 30).ParserSucceeded, "SANY rejection explicit");
        fixture.Check(!TlaBackend.Parse(invalid, fixture.Jar + ".absent", 30).ParserSucceeded,
            "Missing JAR explicit");
        fixture.Check(!TlaBackend.Parse(invalid, fixture.Jar, 0).ParserSucceeded, "Invalid timeout explicit");
        string utf16 = fixture.Write("Utf16", "Value == 1");
        File.WriteAllText(utf16, File.ReadAllText(utf16), Encoding.Unicode);
        fixture.Check(!TlaBackend.Parse(utf16, fixture.Jar, 30).ParserSucceeded, "UTF-16 adapter rejection");
    }

    private sealed class Fixture(string jar, string directory, Action<bool, string> check)
    {
        public string Jar => jar;
        public void Check(bool condition, string message) => check(condition, message);
        public string Write(string module, string body)
        {
            string path = Path.Combine(directory, module + ".tla");
            File.WriteAllText(path, $"---- MODULE {module} ----\n{body}\n====\n", new UTF8Encoding(false));
            return path;
        }
        public TlaDocument Parse(string path)
        {
            var parsed = TlaBackend.Parse(path, jar, 30);
            Check(parsed.ParserSucceeded, string.Join("\n", parsed.Diagnostics.Select(d => d.Message)));
            return parsed;
        }
        public void Translate(string path)
        {
            var info = new ProcessStartInfo(JavaToolchain.Resolve().Java)
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, WorkingDirectory = directory
            };
            foreach (string argument in new[] { "-Djava.io.tmpdir=" + directory, "-cp", jar,
                "pcal.trans", "-nocfg", "-unixEOL", path })
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                throw new Exception("Fixture translation timed out.");
            }
            Check(process.ExitCode == 0 && output.Result.Contains("Translation completed"),
                output.Result + error.Result);
        }
    }

    private const string Algorithm = """
        EXTENDS Naturals
        (* --algorithm Algorithm
        variables value = 0;
        begin
        Explicit:
            value := value + 1;
        Finish:
            skip;
        end algorithm; *)
        """;
}
