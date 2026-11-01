using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DispatcherExample;

using JsonDocument selection = JsonDocument.Parse(File.ReadAllText(args[1]));
if (selection.RootElement.GetProperty("schema_version").GetInt32() != 4 ||
    selection.RootElement.GetProperty("kind").GetString() != "regression-selection")
    throw new InvalidOperationException("Unsupported selection document.");
string[] tests = selection.RootElement.GetProperty("tests").EnumerateArray().Select(t => t.GetString()!).ToArray();
List<object> results = [];
bool failed = false;
foreach (string test in tests)
{
    try
    {
        if (test == "observation-log")
            DispatcherObservationLog.Write(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
        else DispatcherCases.Run(test);
        results.Add(new { id = test, status = "passed", message = "" });
    }
    catch (InvalidOperationException error)
    {
        failed = true;
        results.Add(new { id = test, status = "failed", message = error.Message });
    }
}
string binary = Assembly.GetEntryAssembly()!.Location;
using FileStream stream = new(args[0], FileMode.CreateNew, FileAccess.Write, FileShare.None);
JsonSerializer.Serialize(stream, new {
    schema_version = 4, kind = "regression-observations", complete = true, binary,
    binary_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(binary))).ToLowerInvariant(),
    selection = tests, results
});
return failed ? 1 : 0;
