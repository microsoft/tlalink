#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('pex', 'psym', 'pobserve', 'pverifier')][string[]]$Engines,
    [Parameter(Mandatory)][string]$Output,
    [string]$DependencyArchive,
    [string]$DependencyArchiveSha256,
    [switch]$Plan,
    [switch]$CheckPrerequisites
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$verification = Join-Path $PSScriptRoot '..\verification'
& (Join-Path $root 'scripts\Initialize-ExtendedP.ps1') -Engines $Engines -Output $Output `
    -PFiles (Join-Path $verification 'P\Dispatcher.p') `
    -PExPom (Join-Path $verification 'P\Artifacts\pex-pom.xml') -PExName DispatcherPEx `
    -PSymPom (Join-Path $verification 'P\Artifacts\psym-pom.xml') -PSymName DispatcherPSym `
    -ObserveFiles (Join-Path $verification 'PObserve\DispatcherMonitor.p') `
    -MonitorInputs (Join-Path $verification 'PObserve\DispatcherParser.java') `
    -ObservePom (Join-Path $verification 'PObserve\monitor-pom.xml') -ObserveName DispatcherMonitor `
    -ObserveRuntimePom (Join-Path $verification 'PObserve\runtime-pom.xml') `
    -DependencyArchive $DependencyArchive -DependencyArchiveSha256 $DependencyArchiveSha256 `
    -Plan:$Plan -CheckPrerequisites:$CheckPrerequisites
