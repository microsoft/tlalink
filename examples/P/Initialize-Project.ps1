param(
    [Parameter(Mandatory = $true)][string]$PCompiler,
    [Parameter(Mandatory = $true)][string]$Output
)
$ErrorActionPreference = "Stop"
$PCompiler = [IO.Path]::GetFullPath($PCompiler, (Get-Location).ProviderPath)
$Output = [IO.Path]::GetFullPath($Output, (Get-Location).ProviderPath)
if (!(Test-Path -LiteralPath $PCompiler -PathType Leaf)) { throw "PCompiler must select an installed P 3.1.0 p.dll." }
if (Test-Path -LiteralPath $Output) { throw "Output must be a new directory." }
[IO.Directory]::CreateDirectory($Output) | Out-Null

function Write-NewJson([string]$Name, $Value) {
    $json = $Value | ConvertTo-Json -Depth 30
    $stream = [IO.File]::Open([IO.Path]::Combine($Output, $Name), [IO.FileMode]::CreateNew)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
        $stream.Write($bytes, 0, $bytes.Length)
    }
    finally { $stream.Dispose() }
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$config = Get-Content -LiteralPath ([IO.Path]::Combine($PSScriptRoot, "credit.json")) -Raw | ConvertFrom-Json
$config.models = @([IO.Path]::Combine($PSScriptRoot, "Credit.p"))
$config.sources = @([IO.Path]::Combine($PSScriptRoot, "Credit.cs"))
Write-NewJson "credit.json" $config
$config.engine = "inspect"
Write-NewJson "inspect.json" $config
$config.engine = "pchecker"
$config.models = @([IO.Path]::Combine($PSScriptRoot, "ExtraCredit.p"))
Write-NewJson "extra-credit.json" $config
Write-NewJson "project.json" @{
    schema_version = 4; kind = "analysis-project"; annotation_version = 2; id = "p-credit"
    tools = @{ dafny = $null; jar = $null; p = @{ path = $PCompiler; sha256 = (Hash $PCompiler) } }
    compiler = @{
        target_framework = "net10.0"; language_version = "14"; nullable = "enable"
        checked = $false; allow_unsafe = $true; defines = $null; references = @()
    }
    requirements = "requirements.json"; obligations = @(); review_directory = "reviews"
    scenarios = @(@{ id = "credit"; stages = @(
        @{ id = "links"; kind = "p-inspect"; depends_on = @(); timeout_seconds = 60; config = "inspect.json" },
        @{ id = "healthy"; kind = "p-pchecker"; depends_on = @("links"); timeout_seconds = 60; config = "credit.json" },
        @{ id = "extra-credit"; kind = "p-pchecker"; depends_on = @(); timeout_seconds = 60; config = "extra-credit.json" }
    ) })
}
Write-NewJson "requirements.json" @{
    schema_version = 4; kind = "acceptance-policy"; project = "p-credit"
    requirements = @(
        @{ id = "links"; scenario = "credit"; stage = "links"; expected = "passed"
           claim = "structural-links"; bindings = @("credit-return"); config_sha256 = (Hash ([IO.Path]::Combine($Output, "inspect.json"))) },
        @{ id = "healthy"; scenario = "credit"; stage = "healthy"; expected = "passed"
           claim = "explored-schedules"; bindings = @("credit-return"); config_sha256 = (Hash ([IO.Path]::Combine($Output, "credit.json"))) },
        @{ id = "extra-credit"; scenario = "credit"; stage = "extra-credit"; expected = "rejected"
           diagnostic = "PL100"; claim = "model-counterexample"; config_sha256 = (Hash ([IO.Path]::Combine($Output, "extra-credit.json"))) }
    )
}
Write-Output "Created example project in $Output. Review its independently pinned requirements before running."
