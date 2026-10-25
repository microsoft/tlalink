param([Parameter(Mandatory)][string]$Destination)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$target = [IO.Path]::GetFullPath($Destination, (Get-Location).ProviderPath)
if ($target.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
    $target.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The export destination must be outside the working tree.'
}
if (Test-Path -LiteralPath $target) { throw 'The export destination must be new.' }

$files = @(
    '.gitignore', 'AGENTS.md', 'DESIGN.md', 'global.json', 'NuGet.Config',
    'README.md', 'TlaLink.ps1', 'TlaLink.sln', 'TlaLink.code-workspace', 'java-toolchain.json'
)
$extensions = @('.cs', '.csproj', '.java', '.json', '.tla', '.cfg', '.dfy', '.p', '.ps1', '.md')
foreach ($directory in 'src', 'tests', 'examples', 'scripts') {
    $base = Join-Path $root $directory
    $files += @(Get-ChildItem -LiteralPath $base -Recurse -File | Where-Object {
        $relative = $_.FullName.Substring($root.Length + 1)
        $relative -notmatch '\\(?:bin|obj|__pycache__|Results|reviews)\\' -and
        $_.Extension -in $extensions
    } | ForEach-Object { $_.FullName.Substring($root.Length + 1) })
}
foreach ($relative in $files) {
    $source = Join-Path $root $relative
    if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Export inputs must not be reparse points: $relative"
    }
}
$null = New-Item -ItemType Directory -Path $target
foreach ($relative in $files) {
    $destinationFile = Join-Path $target $relative
    $null = New-Item -ItemType Directory -Path (Split-Path $destinationFile -Parent) -Force
    Copy-Item -LiteralPath (Join-Path $root $relative) -Destination $destinationFile
}
Write-Output "Exported $($files.Count) public source files to $target"
