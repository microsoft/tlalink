#requires -Version 7.0
function Get-TlaLinkNativeToolchain([string]$Root) {
    if (!('TlaLink.Setup.NativeToolchainDocument' -as [type])) {
        Add-Type -Path (Join-Path $Root 'src\tlalink\Common\NativeToolchain.cs') `
            -ReferencedAssemblies (@([IO.Directory]::GetFiles((Join-Path $PSHOME 'ref'), '*.dll')) +
                @([System.Text.Json.JsonSerializer].Assembly.Location))
    }
    return [TlaLink.Setup.NativeToolchainDocument]::Read(
        [IO.File]::ReadAllText((Join-Path $Root 'native-toolchain.json')))
}
