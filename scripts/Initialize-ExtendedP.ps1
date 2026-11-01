#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('pex', 'psym', 'pobserve', 'pverifier')][string[]]$Engines,
    [Parameter(Mandatory)][string]$Output,
    [string[]]$PFiles,
    [string]$PExPom,
    [string]$PSymPom,
    [string]$PExName = 'NativePEx',
    [string]$PSymName = 'NativePSym',
    [string[]]$ObserveFiles,
    [string[]]$MonitorInputs,
    [string]$ObservePom,
    [string]$ObserveRuntimePom,
    [string]$ObserveName = 'NativeMonitor',
    [string]$DependencyArchive,
    [string]$DependencyArchiveSha256,
    [switch]$Plan,
    [switch]$CheckPrerequisites
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'DotNet-Toolchain.ps1')
. (Join-Path $PSScriptRoot 'Native-Toolchain.ps1')
$pin = Get-TlaLinkNativeToolchain $root
$sdk = Get-TlaLinkSdkPin $root
$javaPin = [TlaLink.Setup.NativeSharedJavaDocument]::Read([IO.File]::ReadAllText((Join-Path $root 'java-toolchain.json')))
$cwd = (Get-Location).ProviderPath
$Output = [IO.Path]::GetFullPath($Output, $cwd)
if (!$IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'Extended-P setup currently requires Windows x64 and PowerShell 7.'
}
if (Test-Path -LiteralPath $Output) { throw 'Output must be a new directory; selected installations are never overwritten.' }
if ($DependencyArchive) {
    $DependencyArchive = [IO.Path]::GetFullPath($DependencyArchive, $cwd)
    if ($DependencyArchiveSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or
        (Get-FileHash -LiteralPath $DependencyArchive).Hash -ne $DependencyArchiveSha256) {
        throw 'The explicit dependency archive requires its reviewed SHA256 pin and matching bytes.'
    }
} elseif ($DependencyArchiveSha256) { throw 'DependencyArchiveSha256 requires DependencyArchive.' }
if (@($Engines | Select-Object -Unique).Count -ne $Engines.Count) { throw 'Select each engine only once.' }
foreach ($name in @($PExName, $PSymName, $ObserveName)) {
    if ($name -cnotmatch '^[A-Za-z][A-Za-z0-9_]*$') { throw 'Native project names must be Java identifiers.' }
}
$inputs = [Collections.Generic.List[string]]::new()
function Require-Input([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'The selected engine requires explicit model/POM input files.' }
    $path = [IO.Path]::GetFullPath($Path, $cwd)
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing setup input: $path" }
    $inputs.Add($path)
    return $path
}
if ($Engines -contains 'pex' -or $Engines -contains 'psym') {
    if (!$PFiles.Count) { throw 'PEx/PSym require explicit PFiles.' }
    $PFiles = @($PFiles | ForEach-Object { Require-Input $_ })
}
if ($Engines -contains 'pex') { $PExPom = Require-Input $PExPom }
if ($Engines -contains 'psym') { $PSymPom = Require-Input $PSymPom }
if ($Engines -contains 'pobserve') {
    if (!$ObserveFiles.Count -or !$MonitorInputs.Count) { throw 'PObserve requires ObserveFiles and explicit parser MonitorInputs.' }
    $ObserveFiles = @($ObserveFiles | ForEach-Object { Require-Input $_ })
    $MonitorInputs = @($MonitorInputs | ForEach-Object { Require-Input $_ })
    $ObservePom = Require-Input $ObservePom
    $ObserveRuntimePom = Require-Input $ObserveRuntimePom
}
$setup = Join-Path $root 'examples\Dispatcher\setup'
$patches = $pin.generation_patches
foreach ($patch in $patches.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $setup $patch)).Hash -ne $patches[$patch]) {
        throw "Maintained generation patch pin mismatch: $patch"
    }
}
Write-Output "Explicit setup: $($Engines -join ', ') -> $Output"
Write-Output "Pins: SDK $sdk; .NET 8 runtime $($pin.dotnet.runtime8); shared Temurin $($javaPin.version); Maven $($pin.maven.version); P $($pin.p.version)/$($pin.psym_generator.version) exact commits."
Write-Output "PVerifier builds UCLID5 $($pin.uclid.commit) with SBT $($pin.sbt.version), build-only Java $($pin.uclid_build_java.version), and Z3 $($pin.z3.version)."
Write-Output 'Fresh local dependency caches, source snapshots, command logs and complete published closures are retained for human review. No approval is granted.'
if ($Plan -and !$CheckPrerequisites) { return }
foreach ($command in @('git')) {
    if (!(Get-Command $command -ErrorAction SilentlyContinue)) { throw "Missing prerequisite: $command. Install separately, then invoke setup again." }
}
$dotnet = Get-TlaLinkDotNet $root
$runtimes = & $dotnet --list-runtimes
if (!($runtimes | Where-Object { $_ -match "^Microsoft\.NETCore\.App $([regex]::Escape($pin.dotnet.runtime8)) " })) {
    throw "Missing pinned .NET runtime $($pin.dotnet.runtime8). Provision it separately in the selected .NET host."
}
if ($CheckPrerequisites) { Write-Output 'Prerequisites available; no downloads/builds performed.'; return }

[IO.Directory]::CreateDirectory($Output) | Out-Null
$oldEnvironment = @{}
foreach ($variable in @('JAVA_HOME', 'PATH', 'NUGET_PACKAGES', 'DOTNET_CLI_HOME', 'DOTNET_ROLL_FORWARD', 'MAVEN_OPTS', 'COURSIER_CACHE', 'JAVA_TOOL_OPTIONS', '_JAVA_OPTIONS', 'JDK_JAVA_OPTIONS')) {
    $oldEnvironment[$variable] = [Environment]::GetEnvironmentVariable($variable)
}
function Run([string]$Executable, [string[]]$Arguments) {
    $description = "$Executable $($Arguments -join ' ')"
    Add-Content -LiteralPath (Join-Path $Output 'setup.log') -Value $description
    & $Executable @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $Output 'setup.log') -Append | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Setup command failed (exit $LASTEXITCODE): $description. Partial output retained; use a new directory on retry." }
}
function Download([string]$Url, [string]$Name, [string]$Hash) {
    $file = Join-Path $Output $Name
    Invoke-WebRequest -Uri $Url -OutFile $file
    if ((Get-FileHash -LiteralPath $file).Hash -ne $Hash) { throw "Archive pin mismatch: $Name. Partial output retained." }
    return $file
}
function Clone([string]$Url, [string]$Commit, [string]$Name) {
    $directory = Join-Path $Output $Name
    Run 'git' @('init', $directory)
    Run 'git' @('-C', $directory, 'remote', 'add', 'origin', $Url)
    Run 'git' @('-C', $directory, 'fetch', '--depth', '1', 'origin', $Commit)
    Run 'git' @('-C', $directory, 'checkout', '--detach', 'FETCH_HEAD')
    $actual = & git -C $directory rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $actual -cne $Commit) { throw "Source commit mismatch: $Name" }
    return $directory
}
function Snapshot([string]$Path, [string]$Directory) {
    [IO.Directory]::CreateDirectory($Directory) | Out-Null
    $target = Join-Path $Directory ([IO.Path]::GetFileName($Path))
    if (Test-Path -LiteralPath $target) { throw "Duplicate setup input filename: $target" }
    [IO.File]::Copy($Path, $target, $false)
    return $target
}
try {
    $recipe = Join-Path $Output 'inputs'
    [IO.Directory]::CreateDirectory($recipe) | Out-Null
    [IO.File]::Copy($PSCommandPath, (Join-Path $recipe 'Initialize-ExtendedP.ps1'), $false)
    foreach ($file in @('global.json', 'java-toolchain.json', 'native-toolchain.json')) {
        [IO.File]::Copy((Join-Path $root $file), (Join-Path $recipe $file), $false)
    }
    foreach ($file in @('DotNet-Toolchain.ps1', 'Native-Toolchain.ps1')) {
        [IO.File]::Copy((Join-Path $PSScriptRoot $file), (Join-Path $recipe $file), $false)
    }
    $null = Snapshot (Join-Path $root 'src\tlalink\Common\NativeToolchain.cs') (Join-Path $recipe 'contracts')
    [IO.File]::Copy((Join-Path $root 'NuGet.Config'), (Join-Path $Output 'NuGet.Config'), $false)
    $nugetConfig = Join-Path $Output 'NuGet.Config'
    if ($DependencyArchive) {
        $nugetConfig = Join-Path $Output 'NuGet.Offline.Config'
        [IO.File]::WriteAllText($nugetConfig, '<configuration><packageSources><clear /></packageSources></configuration>')
    }
    [IO.File]::WriteAllText((Join-Path $Output 'maven-settings.xml'), '<settings xmlns="http://maven.apache.org/SETTINGS/1.2.0"/>')
    if ($DependencyArchive) {
        # Only dependency caches are allowed; never replace sources, recipes or selected distributions.
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($DependencyArchive)
        try {
            foreach ($entry in $archive.Entries) {
                $name = $entry.FullName.Replace('\', '/')
                if ($name -notmatch '^(maven-cache|nuget-cache|sbt-global|sbt-boot|ivy-cache|coursier-cache)/' -or
                    $name.Split('/') -contains '..' -or $name.Split('/') -contains '.') {
                    throw "Dependency archive contains an unsupported entry: $name"
                }
            }
        } finally { $archive.Dispose() }
        [IO.File]::Copy($DependencyArchive, (Join-Path $Output 'dependencies.zip'), $false)
        Expand-Archive -LiteralPath (Join-Path $Output 'dependencies.zip') -DestinationPath $Output
    }
    [IO.File]::Copy((Join-Path $root 'global.json'), (Join-Path $Output 'global.json'), $false)
    $jdk = Download $javaPin.platforms.'win-x64'.archive_url 'java.zip' $javaPin.platforms.'win-x64'.archive_sha256
    Expand-Archive -LiteralPath $jdk -DestinationPath (Join-Path $Output 'java\distribution')
    $env:JAVA_HOME = Join-Path $Output "java\distribution\$($javaPin.directory)"
    foreach ($file in $javaPin.platforms.'win-x64'.files.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $env:JAVA_HOME $file)).Hash -ne $javaPin.platforms.'win-x64'.files[$file]) {
            throw "Shared JDK extracted file mismatch: $file"
        }
    }
    $mavenZip = Download $pin.maven.archive_url 'maven.zip' $pin.maven.archive_sha256
    Expand-Archive -LiteralPath $mavenZip -DestinationPath (Join-Path $Output 'maven')
    $maven = Join-Path $Output "maven\$($pin.maven.directory)\bin\mvn.cmd"
    $java = Join-Path $env:JAVA_HOME 'bin\java.exe'
    $env:PATH = "$(Split-Path $dotnet);$(Join-Path $env:JAVA_HOME 'bin');$env:PATH"
    $env:NUGET_PACKAGES = Join-Path $Output 'nuget-cache'
    $env:DOTNET_CLI_HOME = Join-Path $Output 'dotnet-home'
    $env:DOTNET_ROLL_FORWARD = 'LatestPatch'
    $env:MAVEN_OPTS = ''
    $env:COURSIER_CACHE = Join-Path $Output 'coursier-cache'
    foreach ($variable in @('JAVA_TOOL_OPTIONS', '_JAVA_OPTIONS', 'JDK_JAVA_OPTIONS')) {
        if (Test-Path -LiteralPath "Env:$variable") { Remove-Item -LiteralPath "Env:$variable" }
    }
    Push-Location $Output
    try {
        # Retain actual compiler SDK and runtime provenance without copying a shared installation.
        Run $dotnet @('--info')
        if ((& $dotnet --version) -cne $sdk) { throw "The isolated setup did not select SDK $sdk." }
        $compiler = @{}
        foreach ($compilerPin in @($pin.p, $pin.psym_generator)) {
            $version = $compilerPin.version
            if ($version -eq $pin.psym_generator.version -and $Engines -notcontains 'psym') { continue }
            $source = Clone $compilerPin.repository $compilerPin.commit "p-$version-source"
            [IO.File]::Copy((Join-Path $root 'global.json'), (Join-Path $source 'global.json'), $true)
            $patch = Snapshot (Join-Path $setup $(if ($version -eq $pin.p.version) { 'p3-generation.patch' } else { 'p24-generation.patch' })) $recipe
            Run 'git' @('-C', $source, 'apply', '--check', $patch)
            Run 'git' @('-C', $source, 'apply', $patch)
            $project = Join-Path $source 'Src\PCompiler\PCommandLine\PCommandLine.csproj'
            $destination = Join-Path $Output "p-$version-compiler"
            Run $dotnet @('publish', $project, '-c', 'Release', '--output', $destination, '--configfile', $nugetConfig, "-p:Antlr4JavaExecutable=$java", "-p:Version=$version", "-p:AssemblyVersion=$version.0", "-p:FileVersion=$version.0")
            $compiler[$version] = Join-Path $destination 'p.dll'
            if (!(Test-Path -LiteralPath $compiler[$version])) { throw "Compiler publication missing for P $version." }
        }
        function Maven([string]$Pom, [string]$Sources, [string]$Destination, [string[]]$Extra = @()) {
            $offline = if ($DependencyArchive) { @('-o') } else { @() }
            Run $maven (@('-B', '-ntp', '--strict-checksums', '-s', (Join-Path $Output 'maven-settings.xml'), '-gs', (Join-Path $Output 'maven-settings.xml'), '-f', $Pom, "-Dmaven.repo.local=$(Join-Path $Output 'maven-cache')", "-Duser.home=$(Join-Path $Output 'maven-home')", "-Dnative.sources=$Sources", "-Dartifact.output=$Destination", "-Dlombok.version=$($pin.lombok.version)", 'package') + $offline + $Extra)
            if (!(Get-ChildItem -LiteralPath $Destination -Filter '*.jar' -File -ErrorAction SilentlyContinue)) {
                throw "Native artifact publication missing: $Destination"
            }
        }
        foreach ($engine in @('pex', 'psym')) {
            if ($Engines -notcontains $engine) { continue }
            $engineInputs = Join-Path $recipe $engine
            $selectedFiles = @($PFiles | ForEach-Object { Snapshot $_ $engineInputs })
            $pom = Snapshot $(if ($engine -eq 'pex') { $PExPom } else { $PSymPom }) $engineInputs
            $generated = Join-Path $Output "$engine-generated"
            $mode = if ($engine -eq 'pex') { 'pex' } else { 'verification' }
            $name = if ($engine -eq 'pex') { $PExName } else { $PSymName }
            $selectedCompiler = if ($engine -eq 'pex') { $compiler[$pin.p.version] } else { $compiler[$pin.psym_generator.version] }
            Run $dotnet (@('--fx-version', $pin.dotnet.runtime8, $selectedCompiler, 'compile', '--mode', $mode, '--projname', $name, '--outdir', $generated, '--pfiles') + $selectedFiles)
            Maven $pom (Join-Path $generated $(if ($engine -eq 'pex') { 'PEx' } else { 'Symbolic' })) (Join-Path $Output "$engine-artifact")
        }
        if ($Engines -contains 'pobserve') {
            $monitorInputsDirectory = Join-Path $recipe 'monitor'
            $selectedFiles = @($ObserveFiles | ForEach-Object { Snapshot $_ (Join-Path $recipe 'observation-model') })
            foreach ($file in $MonitorInputs) { $null = Snapshot $file $monitorInputsDirectory }
            $monitorPom = Snapshot $ObservePom $monitorInputsDirectory
            $runtimePom = Snapshot $ObserveRuntimePom (Join-Path $recipe 'observation-runtime')
            $generated = Join-Path $Output 'observe-generated'
            Run $dotnet (@('--fx-version', $pin.dotnet.runtime8, $compiler[$pin.p.version], 'compile', '--mode', 'pobserve', '--projname', $ObserveName, '--outdir', $generated, '--pfiles') + $selectedFiles)
            Maven $runtimePom (Join-Path $Output "p-$($pin.p.version)-source\Src\PObserve\PObserve\src\main\java") (Join-Path $Output 'observe-runtime') @("-Dnative.resources=$(Join-Path $Output "p-$($pin.p.version)-source\Src\PObserve\PObserve\src\resources")")
            Maven $monitorPom (Join-Path $generated 'PObserve') (Join-Path $Output 'observe-monitor')
        }
        if ($Engines -contains 'pverifier') {
            $sbt = Download $pin.sbt.archive_url "sbt-launch-$($pin.sbt.version).jar" $pin.sbt.archive_sha256
            $z3 = Download $pin.z3.archive_url 'z3.zip' $pin.z3.archive_sha256
            Expand-Archive -LiteralPath $z3 -DestinationPath (Join-Path $Output 'z3')
            $buildJdk = Download $pin.uclid_build_java.archive_url 'uclid-build-java.zip' $pin.uclid_build_java.archive_sha256
            Expand-Archive -LiteralPath $buildJdk -DestinationPath (Join-Path $Output 'uclid-build-java\distribution')
            $buildJava = Join-Path $Output "uclid-build-java\distribution\$($pin.uclid_build_java.directory)\bin\java.exe"
            $uclid = Clone $pin.uclid.repository $pin.uclid.commit 'uclid-source'
            Push-Location $uclid
            try {
                $offline = if ($DependencyArchive) { @('set offline := true') } else { @() }
                Run $buildJava (@('-Dsbt.supershell=false', '-Dsbt.log.noformat=true', "-Dsbt.offline=$(([bool]$DependencyArchive).ToString().ToLowerInvariant())", "-Dsbt.global.base=$(Join-Path $Output 'sbt-global')", "-Dsbt.boot.directory=$(Join-Path $Output 'sbt-boot')", "-Dsbt.ivy.home=$(Join-Path $Output 'ivy-cache')", "-Dcoursier.cache=$(Join-Path $Output 'coursier-cache')", '-jar', $sbt) + $offline + @('universal:packageBin'))
            } finally { Pop-Location }
            Expand-Archive -LiteralPath (Join-Path $uclid "target\universal\$($pin.uclid.directory).zip") -DestinationPath (Join-Path $Output 'uclid-current')
            $launcherProject = Snapshot (Join-Path $setup 'UclidLauncher.csproj') (Join-Path $recipe 'launcher')
            $null = Snapshot (Join-Path $setup 'UclidLauncher.cs') (Join-Path $recipe 'launcher')
            $null = Snapshot (Join-Path $root 'src\tlalink\Common\NativeToolchain.cs') (Join-Path $recipe 'launcher')
            Run $dotnet @('publish', $launcherProject, '--output', (Join-Path $Output 'uclid-host'), '--configfile', $nugetConfig)
            Run (Join-Path $Output 'uclid-host\uclid.exe') @('--help')
        }
    } finally { Pop-Location }
    $completed = "Setup completed for the explicitly selected engines only: $($Engines -join ', '). Retain $Output unchanged; source/runtime/build reviews remain pending."
    Add-Content -LiteralPath (Join-Path $Output 'setup.log') -Value $completed
    Write-Output $completed
} finally {
    foreach ($variable in $oldEnvironment.Keys) {
        if ($null -eq $oldEnvironment[$variable]) {
            if (Test-Path -LiteralPath "Env:$variable") { Remove-Item -LiteralPath "Env:$variable" }
        } else {
            Set-Item -LiteralPath "Env:$variable" -Value $oldEnvironment[$variable]
        }
    }
}
