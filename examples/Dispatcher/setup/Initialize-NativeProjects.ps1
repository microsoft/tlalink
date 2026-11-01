#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('pex', 'psym', 'pobserve', 'pverifier')][string[]]$Engines,
    [Parameter(Mandatory)][string]$Setup,
    [Parameter(Mandatory)][string]$Output,
    [Parameter(Mandatory)][string]$PCompiler,
    [string]$Log,
    [string]$RegressionResult
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
. (Join-Path $root 'scripts\DotNet-Toolchain.ps1')
. (Join-Path $root 'scripts\Native-Toolchain.ps1')
$pin = Get-TlaLinkNativeToolchain $root
$sdkVersion = Get-TlaLinkSdkPin $root
$dotnet = Get-TlaLinkDotNet $root
$javaPin = [TlaLink.Setup.NativeSharedJavaDocument]::Read([IO.File]::ReadAllText((Join-Path $root 'java-toolchain.json')))
$cwd = (Get-Location).ProviderPath
$Setup = [IO.Path]::GetFullPath($Setup, $cwd)
$Output = [IO.Path]::GetFullPath($Output, $cwd)
if (Test-Path -LiteralPath $Output) { throw 'Output must be a new directory.' }
if (!(Test-Path -LiteralPath $Setup -PathType Container)) { throw 'Missing explicit completed setup directory.' }
$completion = if (Test-Path -LiteralPath (Join-Path $Setup 'setup.log') -PathType Leaf) {
    [string](Get-Content -LiteralPath (Join-Path $Setup 'setup.log') -Tail 1)
} else { '' }
if (!$completion.StartsWith('Setup completed for the explicitly selected engines only:', [StringComparison]::Ordinal)) {
    throw 'The explicit setup did not complete. Retain partial output and retry in a new directory.'
}
foreach ($file in @('global.json', 'java-toolchain.json', 'native-toolchain.json')) {
    $snapshot = Join-Path $Setup "inputs\$file"
    if (!(Test-Path -LiteralPath $snapshot -PathType Leaf) -or
        (Get-FileHash -LiteralPath $snapshot).Hash -ne (Get-FileHash -LiteralPath (Join-Path $root $file)).Hash) {
        throw "Setup toolchain input is missing or changed: $file. Collect a new setup; never rewrite the existing snapshot."
    }
}
if (@($Engines | Select-Object -Unique).Count -ne $Engines.Count) { throw 'Select each engine only once.' }
if ($Engines -contains 'pobserve' -and (!$Log -or !$RegressionResult)) { throw 'PObserve requires a fresh Log and its RegressionResult.' }
$buildInputs = @((Get-ChildItem -LiteralPath $Setup -Recurse -File).FullName)
# The build SDK/runtime installation is a separate trusted prerequisite, not an inferred artifact guarantee.
$sdkLine = @(& $dotnet --list-sdks | Where-Object { $_ -match ('^' + [regex]::Escape($sdkVersion) + ' ') })
if ($sdkLine.Count -ne 1 -or $sdkLine[0] -notmatch '\[(.+)\]') { throw "Missing unique pinned build SDK $sdkVersion." }
$sdkHome = Join-Path $Matches[1] $sdkVersion
$dotnetHome = Split-Path (Split-Path $sdkHome)
$buildInputs += @((Get-ChildItem -LiteralPath $sdkHome -Recurse -File).FullName)
foreach ($directory in @('packs', 'host', 'shared')) {
    $path = Join-Path $dotnetHome $directory
    if (!(Test-Path -LiteralPath $path -PathType Container)) { throw "Missing compiler/runtime prerequisite closure: $path" }
    $buildInputs += @((Get-ChildItem -LiteralPath $path -Recurse -File).FullName)
}
$buildInputs += $dotnet
$buildInputs += @(
    (Join-Path $root 'global.json'), (Join-Path $root 'native-toolchain.json'),
    (Join-Path $root 'java-toolchain.json'), (Join-Path $root 'scripts\DotNet-Toolchain.ps1'),
    (Join-Path $root 'scripts\Native-Toolchain.ps1'), (Join-Path $root 'src\tlalink\Common\NativeToolchain.cs'))
$buildInputs += $PSCommandPath
$buildInputs += Join-Path $PSScriptRoot 'Initialize-Native.ps1'
$buildInputs += Join-Path $PSScriptRoot '..\Initialize-ExternalProject.ps1'
foreach ($engine in $Engines) {
    $arguments = @{
        Engine = $engine; PCompiler = $PCompiler
        JavaHome = Join-Path $Setup "java\distribution\$($javaPin.directory)"
        BuildInputs = $buildInputs
        Output = Join-Path $Output $engine
    }
    switch ($engine) {
        pex {
            $arguments.Artifact = Join-Path $Setup 'pex-artifact\dispatcher-pex-1.0.jar'
            $arguments.LombokJar = Join-Path $Setup "maven-cache\org\projectlombok\lombok\$($pin.lombok.version)\lombok-$($pin.lombok.version).jar"
        }
        psym { $arguments.Artifact = Join-Path $Setup 'psym-artifact\dispatcher-psym-1.0.jar' }
        pverifier {
            $arguments.UclidLauncher = Join-Path $Setup 'uclid-host\uclid.exe'
            $arguments.UclidDistribution = Join-Path $Setup "uclid-current\$($pin.uclid.directory)"
            $arguments.SolverHome = Join-Path $Setup "z3\$($pin.z3.directory)"
        }
        pobserve {
            $arguments.RuntimeJar = Join-Path $Setup 'observe-runtime\dispatcher-pobserve-runtime-1.0.jar'
            $arguments.MonitorJar = Join-Path $Setup 'observe-monitor\dispatcher-pobserve-monitor-1.0.jar'
            $arguments.Log = $Log
            $arguments.BuildInputs = $buildInputs + @($Log, $RegressionResult)
        }
    }
    & (Join-Path $PSScriptRoot '..\Initialize-ExternalProject.ps1') @arguments
}
