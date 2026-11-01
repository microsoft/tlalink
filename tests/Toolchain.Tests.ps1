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
    Test 'the CLI reports a missing SDK as incomplete exit 2' {
        $null = New-Item -ItemType Directory -Path (Join-Path $work 'scripts')
        Copy-Item -LiteralPath (Join-Path $root 'scripts\DotNet-Toolchain.ps1') -Destination (Join-Path $work 'scripts')
        Copy-Item -LiteralPath (Join-Path $root 'TlaLink.ps1') -Destination $work
        $output = @(& (Join-Path $work 'TlaLink.ps1') doctor 2>&1)
        if ($LASTEXITCODE -ne 2 -or ($output -join "`n") -notlike '*SDK 999.0.999*') {
            throw 'Missing SDK did not produce an explicit incomplete CLI result.'
        }
        if (Test-Path (Join-Path $work '.tools')) { throw 'CLI discovery wrote tools.' }
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
            'examples\Dispatcher\setup\Initialize-NativeProjects.ps1')
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
