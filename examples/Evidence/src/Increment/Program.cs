using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

string observations = args[0];
using JsonDocument selection = JsonDocument.Parse(File.ReadAllText(args[1]));
if (selection.RootElement.GetProperty("schema_version").GetInt32() != 4 ||
    selection.RootElement.GetProperty("kind").GetString() != "regression-selection")
    throw new InvalidOperationException("Unsupported selection document.");
string[] tests = selection.RootElement.GetProperty("tests").EnumerateArray().Select(t => t.GetString()!).ToArray();
List<object> results = [];
bool failed = false;
foreach (string test in tests)
{
    bool passed = test switch {
        "increment" => Arithmetic.Increment(3) == 4,
        "limit" => Arithmetic.Increment(10) == (args.Skip(2).Contains("reject") ? 11 : 10),
        _ => throw new InvalidOperationException("Unknown test: " + test)
    };
    failed |= !passed;
    results.Add(new { id = test, status = passed ? "passed" : "failed", message = passed ? "" : "Selected arithmetic assertion failed." });
}
string binary = Assembly.GetEntryAssembly()!.Location;
using FileStream stream = new(observations, FileMode.CreateNew, FileAccess.Write, FileShare.None);
JsonSerializer.Serialize(stream, new {
    schema_version = 4, kind = "regression-observations", complete = true, binary,
    binary_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary))).ToLowerInvariant(),
    selection = tests, results
});
return failed ? 1 : 0;
