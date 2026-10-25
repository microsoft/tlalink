param([string]$Archive)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pin = Get-Content -LiteralPath (Join-Path $root 'java-toolchain.json') -Raw | ConvertFrom-Json
if ($pin.schema_version -ne 4 -or $pin.kind -ne 'java-toolchain') {
    throw 'Expected the current java-toolchain document.'
}
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'The pinned JDK distribution requires Windows x64 and PowerShell 7.'
}
$cache = Join-Path $root '.tools\java'
$jdkHome = Join-Path $cache $pin.directory
function Test-PinnedFiles([string]$Directory) {
    foreach ($file in $pin.files.PSObject.Properties) {
        $path = Join-Path $Directory $file.Name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Value) {
            throw "Pinned JDK file missing or changed: $path"
        }
    }
}
if (Test-Path -LiteralPath $jdkHome) {
    Test-PinnedFiles $jdkHome
    Write-Output "Pinned JDK already available: $jdkHome"
    return
}
$null = New-Item -ItemType Directory -Path $cache -Force
$work = Join-Path $cache ('.install-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $work
try {
    if ($Archive) {
        $zip = [IO.Path]::GetFullPath($Archive, (Get-Location).ProviderPath)
    }
    else {
        $zip = Join-Path $work 'jdk.zip'
        Invoke-WebRequest -Uri $pin.archive_url -OutFile $zip
    }
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $pin.archive_sha256) {
        throw "Pinned JDK archive SHA256 mismatch: $zip"
    }
    $unpacked = Join-Path $work 'unpacked'
    Expand-Archive -LiteralPath $zip -DestinationPath $unpacked
    $selected = Join-Path $unpacked $pin.directory
    Test-PinnedFiles $selected
    [IO.Directory]::Move($selected, $jdkHome)
    Write-Output "Installed pinned Temurin JDK $($pin.version): $jdkHome"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
