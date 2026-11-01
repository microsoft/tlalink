param([Parameter(Mandatory)][string]$Destination)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$target = [IO.Path]::GetFullPath($Destination, (Get-Location).ProviderPath)
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if ($target.Equals($root, $comparison) -or
    $target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, $comparison)) {
    throw 'The export destination must be outside the working tree.'
}
if (Test-Path -LiteralPath $target) { throw 'The export destination must be new.' }

$files = @(
    '.gitignore', '.gitattributes', 'AGENTS.md', 'DESIGN.md', 'global.json', 'NuGet.Config',
    'README.md', 'REFERENCE.md', 'SECURITY.md', 'TlaLink.ps1', 'TlaLink.sln', 'TlaLink.code-workspace', 'java-toolchain.json', 'native-toolchain.json',
    ([IO.Path]::Combine([string[]]@('examples', 'Dispatcher', 'verification', 'P', 'Artifacts', 'pex-pom.xml'))),
    ([IO.Path]::Combine([string[]]@('examples', 'Dispatcher', 'verification', 'P', 'Artifacts', 'psym-pom.xml'))),
    ([IO.Path]::Combine([string[]]@('examples', 'Dispatcher', 'verification', 'PObserve', 'runtime-pom.xml'))),
    ([IO.Path]::Combine([string[]]@('examples', 'Dispatcher', 'verification', 'PObserve', 'monitor-pom.xml'))),
    ([IO.Path]::Combine([string[]]@('examples', 'Dispatcher', 'verification', 'PObserve', 'duplicate-completion.log'))),
    ([IO.Path]::Combine([string[]]@('examples', 'Dispatcher', 'verification', 'PObserve', 'parser-error.log'))),
    ([IO.Path]::Combine([string[]]@('examples', 'Dispatcher', 'setup', 'p3-generation.patch'))),
    ([IO.Path]::Combine([string[]]@('examples', 'Dispatcher', 'setup', 'p24-generation.patch'))),
    ([IO.Path]::Combine([string[]]@('tests', 'TlaLink.Tests', 'Fixtures', 'NativeProofDiagnostics', 'PVerifierGoals.stdout.txt'))),
    ([IO.Path]::Combine([string[]]@('tests', 'TlaLink.Tests', 'Fixtures', 'NativeProofDiagnostics', 'Apalache.stdout.txt'))),
    ([IO.Path]::Combine([string[]]@('tests', 'TlaLink.Tests', 'Fixtures', 'NativeProofDiagnostics', 'DafnyPostcondition.jsonl'))),
    ([IO.Path]::Combine([string[]]@('tests', 'TlaLink.Tests', 'Fixtures', 'NativeProofDiagnostics', 'DafnyTiming.csv'))),
    ([IO.Path]::Combine([string[]]@('tests', 'TlaLink.Tests', 'Fixtures', 'PTranslation', 'healthy.log'))),
    ([IO.Path]::Combine([string[]]@('tests', 'TlaLink.Tests', 'Fixtures', 'PTranslation', 'wrong-state.log'))),
    ([IO.Path]::Combine([string[]]@('tests', 'TlaLink.Tests', 'Fixtures', 'PTranslation', 'parser-error.log')))
)
$extensions = @('.cs', '.csproj', '.java', '.json', '.tla', '.cfg', '.dfy', '.p', '.ps1', '.md', '.yml', '.yaml')
foreach ($directory in 'src', 'tests', 'examples', 'schemas', 'scripts', '.github') {
    $base = Join-Path $root $directory
    $files += @(Get-ChildItem -LiteralPath $base -Recurse -File | Where-Object {
        $relative = $_.FullName.Substring($root.Length + 1)
        $relative -notmatch '[/\\](?:bin|obj|__pycache__|Results|reviews)[/\\]' -and
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
