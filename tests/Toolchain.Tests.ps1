#requires -Version 7
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
. (Join-Path $root 'scripts\DotNet-Toolchain.ps1')
$work = [IO.Directory]::CreateTempSubdirectory('tlalink-toolchain-tests-').FullName
$passed = 0
function Test([string]$Name, [scriptblock]$Action) {
    & $Action
    $script:passed++
    Write-Host "PASS $Name"
}
function Rejects([scriptblock]$Action, [string]$Message) {
    try { & $Action } catch {
        if ($_.Exception.Message -notlike "*$Message*") { throw }
        return
    }
    throw "Expected rejection: $Message"
}
function Pin([string]$Version, [string]$RollForward = 'disable', [bool]$Prerelease = $false) {
    @{ sdk = @{ version = $Version; rollForward = $RollForward; allowPrerelease = $Prerelease } } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $work 'global.json')
}
try {
    $version = Get-TlaLinkSdkPin $root
    Test 'one exact release SDK is selected' {
        Pin $version
        if ((Get-TlaLinkSdkPin $work) -cne $version) { throw 'SDK selection changed.' }
    }
    Test 'SDK roll-forward is rejected' {
        Pin $version 'latestFeature'
        Rejects { Get-TlaLinkSdkPin $work } 'one exact release SDK'
    }
    Test 'prerelease SDK substitution is rejected' {
        Pin $version 'disable' $true
        Rejects { Get-TlaLinkSdkPin $work } 'one exact release SDK'
    }
    Test 'a missing exact SDK is incomplete, not substituted or installed' {
        Pin '999.0.999'
        Rejects { Get-TlaLinkDotNet $work } 'SDK 999.0.999'
        if (Test-Path (Join-Path $work '.tools')) { throw 'SDK discovery wrote tools.' }
    }
    Test 'the CLI reports a missing SDK as incomplete exit 2 when provisioning is disabled' {
        $null = New-Item -ItemType Directory -Path (Join-Path $work 'scripts')
        Copy-Item -LiteralPath (Join-Path $root 'scripts\DotNet-Toolchain.ps1') -Destination (Join-Path $work 'scripts')
        Copy-Item -LiteralPath (Join-Path $root 'TlaLink.ps1') -Destination $work
        $previous = $env:TLALINK_AUTO_PROVISION
        $env:TLALINK_AUTO_PROVISION = 'false'
        try { $output = @(& (Join-Path $work 'TlaLink.ps1') doctor 2>&1) }
        finally { $env:TLALINK_AUTO_PROVISION = $previous }
        if ($LASTEXITCODE -ne 2 -or ($output -join "`n") -notlike '*SDK 999.0.999*') {
            throw 'Missing SDK did not produce an explicit incomplete CLI result.'
        }
        if (Test-Path (Join-Path $work '.tools')) { throw 'CLI discovery wrote tools.' }
    }
    Test 'an unpublished SDK cannot be provisioned and leaves no installation' {
        $previous = $env:TLALINK_AUTO_PROVISION
        $env:TLALINK_AUTO_PROVISION = $null
        try { $output = @(& (Join-Path $work 'TlaLink.ps1') doctor 2>&1) }
        finally { $env:TLALINK_AUTO_PROVISION = $previous }
        if ($LASTEXITCODE -ne 2 -or ($output -join "`n") -notlike '*SDK 999.0.999*') {
            throw 'Unpublished SDK did not produce an explicit incomplete CLI result.'
        }
        if (Test-Path (Join-Path $work '.tools\dotnet\999.0.999')) { throw 'Failed provisioning left an installation.' }
    }
    Test 'automatic provisioning is on unless explicitly disabled' {
        $previous = $env:TLALINK_AUTO_PROVISION
        try {
            foreach ($value in @($null, '1', 'true')) {
                $env:TLALINK_AUTO_PROVISION = $value
                if (!(Test-TlaLinkAutoProvision)) { throw "Provisioning disabled by '$value'." }
            }
            foreach ($value in @('0', 'false')) {
                $env:TLALINK_AUTO_PROVISION = $value
                if (Test-TlaLinkAutoProvision) { throw "Provisioning enabled by '$value'." }
            }
        }
        finally { $env:TLALINK_AUTO_PROVISION = $previous }
    }
    Test 'missing analysis tools are detected by pinned entrypoint' {
        Copy-Item -LiteralPath (Join-Path $root 'java-toolchain.json'), (Join-Path $root 'analysis-toolchain.json') -Destination $work
        $missing = @(Get-TlaLinkMissingTools $work)
        if (($missing -join ',') -cne 'java,tlc,dafny,apalache,p') { throw "Unexpected missing tools: $($missing -join ',')" }
        $jar = Join-Path $work '.tools\tla\v1.8.0\tla2tools.jar'
        $null = New-Item -ItemType File -Force -Path $jar
        if (@(Get-TlaLinkMissingTools $work) -contains 'tlc') { throw 'Present entrypoint reported missing.' }
    }
    Test 'host discovery preserves the caller environment' {
        $path = $env:PATH
        $runtimeRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT')
        $hostPath = Get-TlaLinkDotNet $root
        if (!(Test-Path -LiteralPath $hostPath -PathType Leaf)) { throw 'Selected host is missing.' }
        if ($env:PATH -cne $path -or [Environment]::GetEnvironmentVariable('DOTNET_ROOT') -cne $runtimeRoot) {
            throw 'Host discovery changed the caller environment.'
        }
        $local = Join-Path $root ".tools\dotnet\$version\distribution"
        if ((Test-Path $local -PathType Container) -and (Split-Path $hostPath) -cne $local) {
            throw 'Existing pinned repository SDK was not selected.'
        }
    }
    Test 'maintained PowerShell setup and launch scripts parse' {
        $scripts = @('TlaLink.ps1', 'scripts\DotNet-Toolchain.ps1', 'scripts\Native-Toolchain.ps1', 'scripts\Initialize-Java.ps1',
            'scripts\Initialize-ExtendedP.ps1', 'examples\Dispatcher\setup\Initialize-Native.ps1',
            'examples\Dispatcher\setup\Initialize-NativeProjects.ps1', 'tests\ToolPackage.Tests.ps1')
        foreach ($script in $scripts) {
            $tokens = $null
            $errors = $null
            $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root $script),
                [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw ($errors -join "`n") }
        }
    }
    Write-Host "$passed/$passed passed."
}
finally {
    [IO.Directory]::Delete($work, $true)
}
exit 0
