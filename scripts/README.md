# Explicit setup commands

Setup is separate from `doctor`, `plan`, `run`, `check-p` and acceptance.
Those analysis commands never provision or refresh tools.

## Common .NET host

[global.json](../global.json) is the exact SDK authority, with roll-forward
disabled. [DotNet-Toolchain.ps1](DotNet-Toolchain.ps1) discovers the already
installed `.tools\dotnet\<version>\distribution` SDK or the selected PATH host;
it never downloads or installs. The root [launcher](../TlaLink.ps1) uses that
same host for child builds. Direct build/test commands can select it explicitly
as shown in [the reference](../REFERENCE.md#requirements-and-build).
[Toolchain.Tests.ps1](../tests/Toolchain.Tests.ps1) tests exact pin enforcement,
missing-SDK rejection, nonmutating discovery and PowerShell syntax.

## Extended P (Windows x64 / PowerShell 7)

`Initialize-ExtendedP.ps1` translates the maintained
[source-pinned recipes](../examples/Dispatcher/setup/README.md) into reusable
explicit setup/build steps. Select one or more `-Engines pex,psym,pobserve,pverifier`
and a **new** `-Output` directory. There is no automatic fallback or
overwrite/reuse of a selected installation.

| Parameter | Contract |
|---|---|
| `-Engines`, `-Output` | Required explicit engine list and new output directory |
| `-Plan` | Validate local input paths and generation-patch pins; describe setup without writing, downloading or building |
| `-CheckPrerequisites` | Also check pinned installed SDK/runtime and Git; no writes/downloads/builds |
| `-PFiles`, `-PExPom`, `-PSymPom` | Explicit P models and authored version-pinned Java build recipes for selected artifact engines |
| `-PExName`, `-PSymName` | Generated Java project names (default `NativePEx`, `NativePSym`) |
| `-ObserveFiles`, `-MonitorInputs` | Explicit P monitor models and Java parser source files |
| `-ObservePom`, `-ObserveRuntimePom`, `-ObserveName` | Authored monitor/runtime recipes and generated monitor name |
| `-DependencyArchive`, `-DependencyArchiveSha256` | Optional independently reviewed ZIP pin for immutable dependency-cache reuse |

The command requires the separately provisioned SDK from
[global.json](../global.json), .NET 8 runtime from
[native-toolchain.json](../native-toolchain.json) and Git.
It does not install these machine prerequisites. Actual SDK/
runtime host information and commands are logged. Source commits, JDK/Maven/
SBT/Z3 archives and maintained generation patches have exact pins. Builds use
separate source clones and publish complete compiler/launcher closures; they
do not modify an official installed P compiler or generated Java afterwards.
PVerifier uses source-built UCLID5 and the truthful Windows launcher; PObserve
uses source-built runtime Java and explicit parser inputs.

Without a dependency archive, the explicit setup invocation provisions
version-selected build dependencies into fresh private caches. A dependency
archive may contain only `maven-cache/`, `nuget-cache/`, `sbt-global/`,
`sbt-boot/`, `ivy-cache/` and `coursier-cache/`. Supply its reviewed SHA256;
NuGet has no online sources, Maven uses offline package builds and UCLID uses
SBT offline dependency resolution. Pinned upstream source/tool archives still
require network access.
Missing dependencies or pin mismatches fail clearly, retain partial output,
and require a new directory on retry. Never derive an approval pin merely to
clear a failed setup or update dependencies with a latest-version goal.

Retain the **whole** setup directory, generated inputs, dependency caches and
logs unchanged. The files are setup provenance, not a new evidence format,
acceptance receipt, source correspondence approval or byte-reproducibility
promise. Before analysis, select the full build/compiler/runtime closure as
ordinary project review inputs. SDK/runtime installations are separately
trusted prerequisites, not authenticated by a version string.

The [Dispatcher wrapper](../examples/Dispatcher/setup/Initialize-Native.ps1)
supplies maintained public models/POMs. Its
[project initializer](../examples/Dispatcher/setup/Initialize-NativeProjects.ps1)
expands retained files plus the selected SDK/runtime closure into the existing
schema-4 human-review obligation. It never grants review. For ordinary pinned
Java setup see `Initialize-Java.ps1` and the root `java-toolchain.json`.
