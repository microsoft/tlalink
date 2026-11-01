using System;
using System.Diagnostics;
using System.IO;
using TlaLink.Setup;

string setup = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
NativeToolchainDocument pin = NativeToolchainDocument.Read(File.ReadAllText(Path.Combine(setup, "inputs", "native-toolchain.json")));
NativeSharedJavaDocument javaPin = NativeSharedJavaDocument.Read(File.ReadAllText(Path.Combine(setup, "inputs", "java-toolchain.json")));
string java = Path.Combine(setup, "java", "distribution", javaPin.directory, "bin", "java.exe");
string libraries = Path.Combine(setup, "uclid-current", pin.uclid.directory, "lib", "*");
string solver = Path.Combine(setup, "z3", pin.z3.directory, "bin");
if (!File.Exists(java) || !Directory.Exists(Path.GetDirectoryName(libraries)) || !Directory.Exists(solver))
    throw new InvalidOperationException("The explicitly selected UCLID5 runtime closure is missing.");
ProcessStartInfo start = new(java) { UseShellExecute = false };
start.Environment["PATH"] = solver + Path.PathSeparator + Path.GetDirectoryName(java);
start.ArgumentList.Add("-Djava.library.path=" + solver);
start.ArgumentList.Add("-cp");
start.ArgumentList.Add(libraries);
start.ArgumentList.Add("uclid.UclidMain");
foreach (string argument in args) start.ArgumentList.Add(argument);
using Process process = Process.Start(start) ?? throw new InvalidOperationException("UCLID5 could not start.");
process.WaitForExit();
return process.ExitCode;
