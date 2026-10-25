param([string]$Archive)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pin = Get-Content -LiteralPath (Join-Path $root 'java-toolchain.json') -Raw | ConvertFrom-Json
if ($pin.schema_version -ne 4 -or $pin.kind -ne 'java-toolchain') {
    throw 'Expected the current java-toolchain document.'
}
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64' -or (-not $IsWindows -and -not $IsLinux)) {
    throw 'The pinned JDK distributions require Windows or Linux x64 and PowerShell 7.'
}
$platform = if ($IsWindows) { 'win-x64' } else { 'linux-x64' }
$distribution = $pin.platforms.$platform
if (-not $distribution) { throw "No pinned JDK distribution for $platform." }
$cache = Join-Path (Join-Path $root '.tools\java') $platform
$jdkHome = Join-Path $cache $pin.directory
function Test-PinnedFiles([string]$Directory) {
    foreach ($file in $distribution.files.PSObject.Properties) {
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
        $zip = Join-Path $work $(if ($IsWindows) { 'jdk.zip' } else { 'jdk.tar.gz' })
        Invoke-WebRequest -Uri $distribution.archive_url -OutFile $zip
    }
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $distribution.archive_sha256) {
        throw "Pinned JDK archive SHA256 mismatch: $zip"
    }
    $unpacked = Join-Path $work 'unpacked'
    if ($IsWindows) {
        Expand-Archive -LiteralPath $zip -DestinationPath $unpacked
    }
    else {
        $null = New-Item -ItemType Directory -Path $unpacked
        & tar -xzf $zip -C $unpacked
        if ($LASTEXITCODE -ne 0) { throw 'Pinned JDK archive extraction failed.' }
    }
    $selected = Join-Path $unpacked $pin.directory
    Test-PinnedFiles $selected
    [IO.Directory]::Move($selected, $jdkHome)
    Write-Output "Installed pinned Temurin JDK $($pin.version): $jdkHome"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
