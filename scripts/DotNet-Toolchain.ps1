#requires -Version 7
function Get-TlaLinkSdkPin([string]$Root) {
    $pin = Get-Content -LiteralPath (Join-Path $Root 'global.json') -Raw | ConvertFrom-Json
    if ($pin.sdk.version -notmatch '^\d+\.\d+\.\d+$' -or
        $pin.sdk.rollForward -cne 'disable' -or $pin.sdk.allowPrerelease -ne $false) {
        throw [IO.InvalidDataException]::new('global.json must select one exact release SDK with rollForward disable and allowPrerelease false.')
    }
    return [string]$pin.sdk.version
}

function Get-TlaLinkDotNet([string]$Root) {
    $version = Get-TlaLinkSdkPin $Root
    $executable = if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' }
    $local = Join-Path $Root ".tools\dotnet\$version\distribution\$executable"
    $selected = if (Test-Path -LiteralPath $local -PathType Leaf) {
        $local
    } else {
        $command = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if (!$command) { throw [InvalidOperationException]::new("Missing SDK $version. Provision it separately; analyses never install tools.") }
        $command.Source
    }
    $sdks = @(& $selected --list-sdks)
    if ($LASTEXITCODE -ne 0 -or !($sdks | Where-Object { $_ -match ('^' + [Regex]::Escape($version) + ' ') })) {
        throw [InvalidOperationException]::new("Selected host lacks SDK ${version}: $selected. Provision the exact SDK separately; no roll-forward substitution.")
    }
    return [IO.Path]::GetFullPath($selected)
}
