#requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [string]$Jar,
    [string]$JavaHome
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
. (Join-Path $root 'scripts\DotNet-Toolchain.ps1')
$dotnet = Get-TlaLinkDotNet $root
$packagePath = (Resolve-Path -LiteralPath $Package).Path
if ([bool]$Jar -ne [bool]$JavaHome) { throw 'Native smoke tests require both -Jar and -JavaHome; setup is separate.' }
if ($Jar) {
    $Jar = (Resolve-Path -LiteralPath $Jar).Path
    $JavaHome = (Resolve-Path -LiteralPath $JavaHome).Path
}
$work = [IO.Directory]::CreateTempSubdirectory('tlalink-package-tests-').FullName
$previous = @{}
$variables = @('PATH', 'DOTNET_ROOT', 'DOTNET_CLI_HOME', 'DOTNET_CLI_TELEMETRY_OPTOUT',
    'DOTNET_NOLOGO', 'NUGET_PACKAGES', 'TLALINK_JAVA_HOME')
foreach ($name in $variables) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
$passed = 0
function Test([string]$Name, [scriptblock]$Action) {
    & $Action
    $script:passed++
    Write-Host "PASS $Name"
}
function Invoke-DotNet([string[]]$Arguments, [int]$ExpectedExit = 0) {
    $output = @(& $dotnet @Arguments 2>&1)
    if ($LASTEXITCODE -ne $ExpectedExit) {
        throw "Expected exit $ExpectedExit from dotnet $($Arguments -join ' '):`n$($output -join "`n")"
    }
    return ($output -join "`n")
}
function Invoke-Tool([string[]]$Arguments, [int]$ExpectedExit = 0) {
    $text = Invoke-DotNet (@('tlalink') + $Arguments) $ExpectedExit
    return ($text | ConvertFrom-Json)
}
try {
    $env:DOTNET_ROOT = Split-Path $dotnet
    $env:PATH = "$env:DOTNET_ROOT$([IO.Path]::PathSeparator)$env:PATH"
    $env:DOTNET_CLI_HOME = Join-Path $work 'home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $env:NUGET_PACKAGES = Join-Path $work 'packages'
    if ($JavaHome) { $env:TLALINK_JAVA_HOME = $JavaHome }
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $nuspec = @($archive.Entries | Where-Object FullName -like '*.nuspec')
        if ($nuspec.Count -ne 1) { throw 'Expected exactly one package specification.' }
        $reader = [IO.StreamReader]::new($nuspec[0].Open())
        try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $id = [string]$spec.package.metadata.id
        $version = [string]$spec.package.metadata.version
        Test 'package declares a standalone TlaLink .NET tool' {
            if ($id -cne 'TlaLink' -or
                @($spec.package.metadata.packageTypes.packageType | Where-Object name -eq 'DotnetTool').Count -ne 1 -or
                $spec.SelectNodes("//*[local-name()='dependency']").Count -ne 0) {
                throw 'Expected a TlaLink DotnetTool package with no external package dependencies.'
            }
            foreach ($path in @('LICENSE', 'README.md', 'licenses/dotnet-sdk-LICENSE.txt',
                'licenses/dotnet-sdk-ThirdPartyNotices.txt', 'tools/net10.0/any/TlaLink.deps.json',
                'tools/net10.0/any/TlaLink.runtimeconfig.json')) {
                if (!$archive.GetEntry($path)) { throw "Missing package asset: $path" }
            }
            if (@($archive.Entries | Where-Object FullName -match '(^|/)(local|tests|examples|\.tools)/').Count) {
                throw 'Consumer material, tests, examples and native distributions must not be packaged.'
            }
        }
    } finally { $archive.Dispose() }

    $consumer = [IO.Directory]::CreateDirectory((Join-Path $work 'consumer')).FullName
    $feed = [IO.Directory]::CreateDirectory((Join-Path $work 'feed')).FullName
    Copy-Item -LiteralPath $packagePath -Destination $feed
    Copy-Item -LiteralPath (Join-Path $root 'global.json') -Destination $consumer
    Copy-Item -LiteralPath (Join-Path $root 'examples\Components\src\Counter\Counter.cs') -Destination $consumer
    $config = Join-Path $consumer 'NuGet.Config'
    $escapedFeed = [Security.SecurityElement]::Escape($feed)
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="package-test" value="$escapedFeed" /></packageSources></configuration>
"@ | Set-Content -LiteralPath $config -Encoding utf8NoBOM
    Push-Location $consumer
    try {
        Test 'a consumer restores an exact local-tool manifest from an isolated offline feed' {
            $null = Invoke-DotNet @('new', 'tool-manifest', '--output', '.config')
            $null = Invoke-DotNet @('tool', 'install', $id, '--version', $version, '--configfile', $config)
            $manifest = Get-Content -LiteralPath '.config\dotnet-tools.json' -Raw | ConvertFrom-Json
            $selection = $manifest.tools.PSObject.Properties | Where-Object Name -eq $id.ToLowerInvariant()
            if ($selection.Count -ne 1 -or $selection.Value.version -cne $version -or
                @($selection.Value.commands).Count -ne 1 -or $selection.Value.commands[0] -cne 'tlalink') {
                throw 'Installed local tool manifest differs from the selected package.'
            }
            $null = Invoke-DotNet @('tool', 'restore', '--configfile', $config)
        }
        $installed = Join-Path $env:NUGET_PACKAGES "$($id.ToLowerInvariant())\$version\tools\net10.0\any"
        Test 'installed package retains the flat bridge assets and SDK Roslyn bytes' {
            $assets = @{
                'TlaIndex.java' = 'src\tlalink\Tla\TlaIndex.java'
                'DafnyIndex.source.cs' = 'src\tlalink\Dafny\DafnyIndex.source.cs'
                'PIndex.source.cs' = 'src\tlalink\PLang\PIndex.source.cs'
                'TraceRuntime.source.cs' = 'src\tlalink\Common\Runtime\TraceRuntime.cs'
                'NuGet.Config' = 'NuGet.Config'
            }
            foreach ($asset in $assets.GetEnumerator()) {
                if ((Get-FileHash -LiteralPath (Join-Path $installed $asset.Key)).Hash -cne
                    (Get-FileHash -LiteralPath (Join-Path $root $asset.Value)).Hash) {
                    throw "Installed asset differs from its maintained input: $($asset.Key)"
                }
            }
            $sdk = Get-TlaLinkSdkPin $root
            foreach ($name in @('Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll')) {
                if ((Get-FileHash -LiteralPath (Join-Path $installed $name)).Hash -cne
                    (Get-FileHash -LiteralPath (Join-Path $env:DOTNET_ROOT "sdk\$sdk\Roslyn\bincore\$name")).Hash) {
                    throw "Installed Roslyn dependency differs from the selected SDK: $name"
                }
            }
            [xml]$settings = Get-Content -LiteralPath (Join-Path $installed 'DotnetToolSettings.xml') -Raw
            if ($settings.DotNetCliTool.Commands.Command.Name -cne 'tlalink' -or
                $settings.DotNetCliTool.Commands.Command.EntryPoint -cne 'TlaLink.dll' -or
                $settings.DotNetCliTool.Commands.Command.Runner -cne 'dotnet') {
                throw 'Incorrect installed tool command/entrypoint.'
            }
        }
        Test 'installed tool resolves C# symbols with its packaged Roslyn dependencies' {
            $result = Invoke-Tool @('symbols', 'find', '--source', '.\Counter.cs', '--name', 'Step')
            if ($result.status -cne 'found' -or $result.match_count -ne 1 -or
                $result.symbols[0].id -cne 'M:ComponentsExample.Counter.Step') {
                throw 'Installed Roslyn source query did not resolve the selected declaration.'
            }
            if ($result.fingerprints.PSObject.Properties.Name -notcontains (Join-Path $installed 'TlaLink.dll')) {
                throw 'Query did not run from the isolated installed tool.'
            }
        }
        Test 'installed tool generates the typed project schema without a checkout' {
            $result = Invoke-Tool @('project-schema', '--output', '.\project.schema.json')
            $schema = Get-Content -LiteralPath '.\project.schema.json' -Raw | ConvertFrom-Json
            if ($result.status -cne 'ready' -or
                $schema.'$schema' -cne 'https://json-schema.org/draft/2020-12/schema') {
                throw 'Installed schema generation failed.'
            }
        }
        Test 'installed tool retains incomplete exit semantics for missing sources' {
            $result = Invoke-Tool @('symbols', 'find', '--source', '.\Missing.cs', '--name', 'Step') 2
            if ($result.status -cne 'incomplete' -or @($result.diagnostics).Count -eq 0) {
                throw 'Missing source did not produce explicit incomplete diagnostics.'
            }
        }
        Test 'installed tool carries pinned native setup descriptors without needing a checkout' {
            $result = Invoke-Tool @('toolchain', 'restore', '--directory', '.\native-tools',
                '--tool', 'tlc', '--offline', 'true') 2
            if ($result.status -cne 'incomplete' -or
                $result.diagnostics[0].message -notlike '*pinned archive in offline mode*') {
                throw 'Expected a missing pinned archive, not a missing embedded toolchain resource.'
            }
        }
        if ($Jar) {
            Test 'installed tool restores and repairs the pinned TLC file idempotently offline' {
                $pin = (Get-Content -LiteralPath (Join-Path $root 'analysis-toolchain.json') -Raw |
                    ConvertFrom-Json).tools | Where-Object id -eq 'tlc'
                $cache = Join-Path $consumer 'native-tools'
                $archives = [IO.Directory]::CreateDirectory((Join-Path $cache '.archives')).FullName
                Copy-Item -LiteralPath $Jar -Destination (Join-Path $archives $pin.sha256.ToLowerInvariant())
                $arguments = @('toolchain', 'restore', '--directory', $cache, '--tool', 'tlc', '--offline', 'true')
                $first = Invoke-Tool $arguments
                $second = Invoke-Tool ($arguments + @('--format', 'summary'))
                if ($first.kind -cne 'toolchain-restore-result' -or $first.tools[0].status -cne 'installed' -or
                    $second.result_kind -cne 'toolchain-restore-result' -or $second.tools[0].status -cne 'current') {
                    throw 'Expected installed then current from the installed setup command.'
                }
                [IO.File]::WriteAllText($first.tools[0].entrypoint, 'damaged synthetic test copy')
                $null = Invoke-Tool $arguments 2
                $repaired = Invoke-Tool ($arguments + @('--exclusive', 'true'))
                if ($repaired.tools[0].status -cne 'repaired' -or
                    !(Test-Path -LiteralPath $repaired.tools[0].replaced_directory) -or
                    (Get-FileHash -LiteralPath $repaired.tools[0].entrypoint).Hash -cne $pin.sha256) {
                    throw 'Guarded repair did not retain the old copy and restore pinned bytes.'
                }
            }
            Test 'installed Java bridge and trace runtime support actual instrumentation' {
                foreach ($name in @('Demo.cs', 'Demo.tla', 'demo-scope.json')) {
                    Copy-Item -LiteralPath (Join-Path $root "examples\$name") -Destination $consumer
                }
                $result = Invoke-Tool @('instrument', '--annotation-version', '4',
                    '--source', '.\Demo.cs', '--scope', '.\demo-scope.json', '--target', 'demo',
                    '--jar', $Jar, '--output', '.\instrumented')
                if ($result.kind -cne 'instrumentation-manifest' -or @($result.events).Count -ne 2) {
                    throw 'Installed native bridge did not produce both selected observation points.'
                }
                $runtime = Join-Path $consumer 'instrumented\TlaLink.TraceRuntime.cs'
                if ((Get-FileHash -LiteralPath $runtime).Hash -cne
                    (Get-FileHash -LiteralPath (Join-Path $installed 'TraceRuntime.source.cs')).Hash) {
                    throw 'Instrumentation did not deploy the packaged trace runtime.'
                }
            }
        }
        Write-Host "$passed/$passed package tests passed."
    } finally { Pop-Location }
} finally {
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    [IO.Directory]::Delete($work, $true)
}
exit 0
