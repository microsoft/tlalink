# Separate extended-P setup

These are explicit **Windows x64 / PowerShell 7** setup/build steps for the
public dispatcher campaign, not analysis commands or machine-wide installations.
Keep selected tools immutable while collecting evidence. No automatic engine
substitution, unpinned Maven goal or human approval is performed.

## One current toolchain

The recipes consume three authoritative root pins:

- [`global.json`](../../../global.json): the exact common .NET build SDK, with
  roll-forward disabled. The shared host selector prefers an already provisioned
  `.tools\dotnet\<version>\distribution` installation; otherwise it requires the
  exact SDK in the selected PATH host.
- [`java-toolchain.json`](../../../java-toolchain.json): the common Temurin JDK
  for P generation, Maven builds and **all native execution**, as well as the
  other Java-backed TlaLink engines. Setup extracts the reviewed Windows archive
  into its new directory and checks the pinned release/launcher/compiler files.
- [`native-toolchain.json`](../../../native-toolchain.json): Maven, SBT, Z3,
  native source commits, generation-patch hashes, shared Lombok and narrowly
  justified native exceptions. The named C# setup contracts reject unknown,
  missing and null fields. Setup snapshots these documents and contracts once;
  it does not infer versions from caches or generated filenames.

There are three explicit exceptions, not alternative defaults:

1. **.NET 8 execution:** upstream P compiler/checker targets `net8.0`.
   The common SDK builds it, but the manifest-pinned .NET 8 runtime must be
   provisioned separately in the selected .NET host. CI installs the exact
   `dotnet.sdk8` pin to supply that runtime; it does not select `8.x`.
2. **PSym generation:** supported PSym Maven runtime 1.1.4 requires the pinned
   P 2.4.0 generator. The other native profiles use the common pinned P 3.1.0
   source. Runtime and compiler version numbers are not interchangeable.
3. **UCLID build only:** upstream UCLID's pinned SBT 1.4.9 invokes
   `System.setSecurityManager`; a fresh copied-source build on Java 25 fails
   with `UnsupportedOperationException`. Only that SBT build uses the separately
   pinned Temurin 17 distribution. The published UCLID launcher runs on the
   **shared JDK**, not the build exception.

PEx/PSym generation artifacts compile on the shared JDK. PObserve's runtime
requires Lombok processing; its POM explicitly selects the same current Lombok
processor that the other POMs use as an annotation dependency. The version is
supplied from `native-toolchain.json` as `-Dlombok.version`, not repeated in
POMs. Java `--release 17` in upstream-compatible artifacts is a bytecode/API
target, not selection of a Java 17 build host.

## Explicit setup

Preview first, then provision missing prerequisites separately:

```powershell
.\examples\Dispatcher\setup\Initialize-Native.ps1 -Engines pex,psym,pobserve,pverifier `
  -Output .\.tools\dispatcher-native-new -Plan
.\examples\Dispatcher\setup\Initialize-Native.ps1 -Engines pex,psym,pobserve,pverifier `
  -Output .\.tools\dispatcher-native-new -CheckPrerequisites
# Only after explicit authorization for network setup/build:
.\examples\Dispatcher\setup\Initialize-Native.ps1 -Engines pex,psym,pobserve,pverifier `
  -Output .\.tools\dispatcher-native-new
```

The [generic command](../../../scripts/README.md#extended-p-windows-x64--powershell-7)
requires explicit model/POM inputs; this wrapper selects only the maintained
synthetic dispatcher files. `-Plan` performs no writes/downloads/builds.
Missing prerequisites, pin mismatches and failed engines stop setup, retaining
partial output. Retry in a **new** directory; partial output is not an installation.

For offline dependency reuse supply `-DependencyArchive` and its independently
reviewed `-DependencyArchiveSha256`. Only the documented cache directories may
be extracted; no sources, recipes or tool installations may be replaced.
The archive must contain the **current** dependencies, including the common
Lombok version. Old dependency archives are not silently upgraded or substituted.
Tool/source downloads remain separately explicit even with offline dependencies.

Setup retains input snapshots, exact source clones, generation patches,
generated files, private dependency caches and command logs for review.
Published compiler directories derive from the pinned versions. Runtime JDK
layout is `java\distribution\<shared directory>`; the UCLID build-only JDK is
under `uclid-build-java\distribution\<exception directory>`. Maven, SBT, Z3 and
UCLID paths derive from their manifest pins. Cross-host byte-identical JARs are
not promised, and source hashes are not authentication or source/build approval.

## Ordinary project initialization and validation

After **completed** setup, create ordinary manifests using the full retained
closure rather than a short illustrative `BuildInputs` list:

```powershell
.\examples\Dispatcher\setup\Initialize-NativeProjects.ps1 -Engines pex,psym,pverifier `
  -Setup .\.tools\dispatcher-native-new -Output .\Results\dispatcher-native-projects-new `
  -PCompiler .\.tools\dispatcher-native-new\p-3.1.0-compiler\p.dll
```

The initializer pins the complete retained setup and selected SDK/runtime
closure through the existing human review obligation. An independently selected
installed P compiler may instead be supplied explicitly. PObserve also requires
a fresh regression `-Log` and `-RegressionResult`. Build/source reviews and
independent requirements remain pending; native outcomes do not approve them.

[Manually opted-in native CI](../../../.github/workflows/extended-p.yml) uses
the same setup, explicit engine selection and reviewed dependency archive.
It records healthy/defect controls and preserves backend-specific outcomes.
Solver-unknown PVerifier remains incomplete, not a successful rejection.
PObserve's malformed parser remains incomplete. CI artifacts are presentation/
build provenance, not permanent retention roots or human approval.

Focused `--p` tests consume explicit ordinary `p-analysis` selections through
`TLALINK_P_TRANSLATION_COMPILER`, `TLALINK_P_VERIFIER_CONFIG`,
`TLALINK_P_OBSERVE_CONFIG` and `TLALINK_P_EX_CONFIG`. Tests do not install tools.
PSym keeps its separate exhaustion campaign; these translation tests do not
claim PSym generation coverage.

### Measured common-pin validation

A fresh all-engine setup with the shared Java 25 distribution completed.
Its PEx and PSym healthy campaigns passed with their respective finite/
symbolic exhaustion claims; early-return and credit-leak controls rejected.
PObserve monitored the fresh serial C# log successfully and rejected duplicate
completion. UCLID ran through the shared-JDK launcher: healthy induction passed;
the admission control remained incomplete (`PL104`, solver unknown), not a
successful rejection. Selected P translation tests passed **39/39 with no
skips**, including native generation, verification and monitoring controls.
Core tests including strict pin contracts passed **455/455**.

These are measured local outcomes, not hosted CI results, C# refinement or
human source/build approval. Historical runs and requirements were not rewritten.
