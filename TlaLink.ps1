#requires -Version 7
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$arguments = @($args)
$project = Join-Path $PSScriptRoot 'src\tlalink\TlaLink.csproj'
. (Join-Path $PSScriptRoot 'scripts\DotNet-Toolchain.ps1')
try { $dotnet = Get-TlaLinkDotNet $PSScriptRoot }
catch [IO.InvalidDataException], [InvalidOperationException] {
    Write-Error $_ -ErrorAction Continue
    exit 2
}
$previousPath = $env:PATH
$previousRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT')
try {
    $env:PATH = "$(Split-Path $dotnet)$([IO.Path]::PathSeparator)$env:PATH"
    $env:DOTNET_ROOT = Split-Path $dotnet
    Push-Location $PSScriptRoot
    try {
        $buildOutput = & $dotnet build $project --nologo --verbosity quiet 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Error ($buildOutput -join "`n") -ErrorAction Continue
            exit 2
        }
    }
    finally {
        Pop-Location
    }
    & $dotnet (Join-Path $PSScriptRoot 'src\tlalink\bin\Debug\net10.0\TlaLink.dll') @arguments
    exit $LASTEXITCODE
}
finally {
    $env:PATH = $previousPath
    if ($null -eq $previousRoot) {
        if (Test-Path -LiteralPath Env:DOTNET_ROOT) { Remove-Item -LiteralPath Env:DOTNET_ROOT }
    } else {
        $env:DOTNET_ROOT = $previousRoot
    }
}
