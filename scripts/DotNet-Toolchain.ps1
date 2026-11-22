#requires -Version 7
function Get-TlaLinkSdkPin([string]$Root) {
    $pin = Get-Content -LiteralPath (Join-Path $Root 'global.json') -Raw | ConvertFrom-Json
    if ($pin.sdk.version -notmatch '^\d+\.\d+\.\d+$' -or
        $pin.sdk.rollForward -cne 'disable' -or $pin.sdk.allowPrerelease -ne $false) {
        throw [IO.InvalidDataException]::new('global.json must select one exact release SDK with rollForward disable and allowPrerelease false.')
    }
    return [string]$pin.sdk.version
}

function Get-TlaLinkRid {
    if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) { return '' }
    if ($IsWindows) { return 'win-x64' }
    if ($IsLinux) { return 'linux-x64' }
    return ''
}

# Launcher provisioning is on unless TLALINK_AUTO_PROVISION is 0/false; discovery alone never installs.
function Test-TlaLinkAutoProvision {
    return [Environment]::GetEnvironmentVariable('TLALINK_AUTO_PROVISION') -notin @('0', 'false', 'False', 'FALSE')
}

function Test-TlaLinkSdk([string]$DotNet, [string]$Version) {
    $sdks = @(& $DotNet --list-sdks)
    return $LASTEXITCODE -eq 0 -and [bool]($sdks | Where-Object { $_ -match ('^' + [Regex]::Escape($Version) + ' ') })
}

function Get-TlaLinkDotNet([string]$Root, [switch]$Provision) {
    $version = Get-TlaLinkSdkPin $Root
    $executable = if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' }
    $local = Join-Path $Root ".tools\dotnet\$version\distribution\$executable"
    $selected = if (Test-Path -LiteralPath $local -PathType Leaf) {
        $local
    } else {
        $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($command -and (Test-TlaLinkSdk $command.Source $version)) { return [IO.Path]::GetFullPath($command.Source) }
        if ($Provision) {
            Install-TlaLinkDotNet $Root $version
            $local
        } elseif (!$command) {
            throw [InvalidOperationException]::new("Missing SDK $version. Use TlaLink.ps1 provisioning or provision it separately; analyses never install tools.")
        } else {
            $command.Source
        }
    }
    if (!(Test-TlaLinkSdk $selected $version)) {
        throw [InvalidOperationException]::new("Selected host lacks SDK ${version}: $selected. Provision the exact SDK separately; no roll-forward substitution.")
    }
    return [IO.Path]::GetFullPath($selected)
}

# Installs the exact pinned SDK into the checkout's ignored .tools cache. The archive is checked against the
# SHA512 published in the official .NET release metadata before it is extracted; nothing machine-wide changes.
function Install-TlaLinkDotNet([string]$Root, [string]$Version, [int]$TimeoutSeconds = 600) {
    $rid = Get-TlaLinkRid
    if (!$rid) { throw [InvalidOperationException]::new("Automatic SDK $Version provisioning supports Windows and Linux x64.") }
    $tools = Join-Path $Root '.tools\dotnet'
    $destination = Join-Path $tools "$Version\distribution"
    if (Test-Path -LiteralPath $destination) {
        throw [InvalidOperationException]::new("Incomplete SDK $Version installation: $destination. Remove it and retry.")
    }
    $extension = if ($rid -eq 'win-x64') { 'zip' } else { 'tar.gz' }
    $channel = ($Version -split '\.')[0..1] -join '.'
    try {
        $metadata = Invoke-RestMethod "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$channel/releases.json" -TimeoutSec $TimeoutSeconds
    } catch {
        throw [InvalidOperationException]::new("Cannot provision SDK ${Version}: release metadata unavailable. $($_.Exception.Message)")
    }
    $sdk = @($metadata.releases | ForEach-Object { @($_.sdks) + @($_.sdk) } |
        Where-Object { $_ -and $_.version -eq $Version }) | Select-Object -First 1
    $file = @($sdk.files | Where-Object { $_.rid -eq $rid -and $_.name -eq "dotnet-sdk-$rid.$extension" }) | Select-Object -First 1
    if (!$file -or !$file.url.StartsWith('https://') -or $file.hash -notmatch '^[0-9a-fA-F]{128}$') {
        throw [InvalidOperationException]::new("Cannot provision SDK ${Version}: no published $rid archive.")
    }
    $archives = Join-Path $tools '.archives'
    $archive = Join-Path $archives "dotnet-sdk-$Version-$rid.$extension"
    $null = New-Item -ItemType Directory -Force -Path $archives
    if (!(Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $file.hash) {
        $download = "$archive.$([Guid]::NewGuid().ToString('N')).download"
        [Console]::Error.WriteLine("Downloading pinned .NET SDK $Version ($rid)...")
        Invoke-WebRequest $file.url -OutFile $download -TimeoutSec $TimeoutSeconds
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA512).Hash -ne $file.hash) {
            throw [InvalidOperationException]::new("Downloaded SDK $Version SHA512 mismatch; retained for diagnosis: $download")
        }
        Move-Item -LiteralPath $download -Destination $archive -Force
    }
    $stage = Join-Path $tools ".staging\$([Guid]::NewGuid().ToString('N'))"
    $null = New-Item -ItemType Directory -Force -Path $stage
    if ($extension -eq 'zip') { Expand-Archive -LiteralPath $archive -DestinationPath $stage }
    else {
        tar -xzf $archive -C $stage
        if ($LASTEXITCODE -ne 0) { throw [InvalidOperationException]::new("Could not extract SDK archive; staging retained: $stage") }
    }
    $null = New-Item -ItemType Directory -Force -Path (Split-Path $destination)
    try { Move-Item -LiteralPath $stage -Destination $destination }
    catch {
        # A concurrent launcher may have promoted the same verified archive first.
        if (!(Test-Path -LiteralPath $destination)) { throw }
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}

# Returns the ids of pinned analysis tools whose entrypoint is absent from the checkout's .tools cache.
function Get-TlaLinkMissingTools([string]$Root) {
    $rid = Get-TlaLinkRid
    if (!$rid) { return @() }
    $tools = Join-Path $Root '.tools'
    $java = Get-Content -LiteralPath (Join-Path $Root 'java-toolchain.json') -Raw | ConvertFrom-Json
    $launcher = if ($rid -eq 'win-x64') { 'bin/java.exe' } else { 'bin/java' }
    $entries = @([pscustomobject]@{ id = 'java'; path = "java/$rid/$($java.directory)/$launcher" })
    $catalogue = Get-Content -LiteralPath (Join-Path $Root 'analysis-toolchain.json') -Raw | ConvertFrom-Json
    $entries += $catalogue.tools | Where-Object { $_.platform -in @('any', $rid) } |
        ForEach-Object { [pscustomobject]@{ id = $_.id; path = "$($_.directory)/$($_.entrypoint)" } }
    return @($entries | Where-Object { !(Test-Path -LiteralPath (Join-Path $tools $_.path) -PathType Leaf) } |
        ForEach-Object id | Select-Object -Unique)
}
