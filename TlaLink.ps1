$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$arguments = @($args)
$project = Join-Path $PSScriptRoot 'src\TlaLink\TlaLink.csproj'
Push-Location $PSScriptRoot
try {
    $buildOutput = & dotnet build $project --nologo --verbosity quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error ($buildOutput -join "`n") -ErrorAction Continue
        exit 2
    }
}
finally {
    Pop-Location
}
& dotnet (Join-Path $PSScriptRoot 'src\TlaLink\bin\Debug\net10.0\TlaLink.dll') @arguments
exit $LASTEXITCODE
