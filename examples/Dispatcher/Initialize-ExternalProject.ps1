param(
    [Parameter(Mandatory = $true)][ValidateSet("pex", "psym", "pverifier", "pobserve")][string]$Engine,
    [Parameter(Mandatory = $true)][string]$PCompiler,
    [Parameter(Mandatory = $true)][string]$JavaHome,
    [Parameter(Mandatory = $true)][string[]]$BuildInputs,
    [Parameter(Mandatory = $true)][string]$Output,
    [string]$Artifact,
    [string]$UclidLauncher,
    [string]$UclidDistribution,
    [string]$SolverHome,
    [string]$RuntimeJar,
    [string]$MonitorJar,
    [string]$LombokJar,
    [string]$Log
)
$ErrorActionPreference = "Stop"
$cwd = (Get-Location).ProviderPath
$Output = [IO.Path]::GetFullPath($Output, $cwd)
if (Test-Path -LiteralPath $Output) { throw "Output must be a new directory." }
function Full([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw "A required native input path is missing." }
    [IO.Path]::GetFullPath($Path, $cwd)
}
function Relative([string]$Path) { [IO.Path]::GetRelativePath($Output, (Full $Path)) }
function Hash([string]$Path) { (Get-FileHash -LiteralPath (Full $Path) -Algorithm SHA256).Hash.ToLowerInvariant() }
function Pin([string]$Path) { @{ path = (Relative $Path); sha256 = (Hash $Path) } }
function Write-NewJson([string]$Name, $Value) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 40))
    $stream = [IO.File]::Open([IO.Path]::Combine($Output, $Name), [IO.FileMode]::CreateNew)
    try { $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose() }
}
$comparer = if ([OperatingSystem]::IsWindows()) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
$dependencies = [Collections.Generic.Dictionary[string, object]]::new($comparer)
function Add-Dependency([string]$Path) {
    $path = Full $Path
    if (!$dependencies.ContainsKey($path)) { $dependencies.Add($path, (Pin $path)) }
}
function Add-Distribution([string]$Path) {
    $directory = Full $Path
    if (!(Test-Path -LiteralPath $directory -PathType Container)) { throw "Missing selected distribution: $directory" }
    foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -File) { Add-Dependency $file.FullName }
}
$PCompiler = Full $PCompiler
$JavaHome = Full $JavaHome
$java = [IO.Path]::Combine($JavaHome, "bin", $(if ([OperatingSystem]::IsWindows()) { "java.exe" } else { "java" }))
$reviewInputs = [Collections.Generic.List[string]]::new()
foreach ($input in $BuildInputs) {
    $input = Full $input
    if (!(Test-Path -LiteralPath $input -PathType Leaf)) { throw "BuildInputs must name explicit existing files: $input" }
    $reviewInputs.Add((Relative $input))
}
if ($reviewInputs.Count -eq 0) { throw "Explicit compiler/source/build inputs are required for artifact review." }
if ($Engine -eq "pverifier") {
    Add-Dependency $UclidLauncher
    Add-Distribution ([IO.Path]::GetDirectoryName((Full $UclidLauncher)))
    Add-Distribution $UclidDistribution
    Add-Distribution $SolverHome
}
Add-Dependency $java
Add-Distribution $JavaHome
if ($Engine -eq "pex" -and $LombokJar) { Add-Dependency $LombokJar }
if ($dependencies.Count -gt 1000) { throw "The selected native closure exceeds the supported dependency count." }
$native = if ($Engine -eq "pverifier") { "PVerifier" } elseif ($Engine -eq "pobserve") { "PObserve" } else { "P" }
$model = if ($Engine -eq "pobserve") { "DispatcherMonitor.p" } else { "Dispatcher.p" }
$modelPath = [IO.Path]::Combine($PSScriptRoot, "verification", $native, $model)
$scopePath = [IO.Path]::Combine($PSScriptRoot, "verification", $native, "scope.json")
$sourcePath = [IO.Path]::Combine($PSScriptRoot, "src", "Dispatcher.cs")
$alias = if ($Engine -eq "pverifier") { "dispatcher-pverifier" } elseif ($Engine -eq "pobserve") { "dispatcher-observe" } else { "dispatcher-p" }
$names = if ($Engine -eq "pobserve") { @("admit", "start", "success", "failure", "cancel", "close") } else { @("admit", "start", "complete", "cancel", "close") }
$bindings = @($names | ForEach-Object { @{
    id = "${alias}::$_"
    relation = "Selected dispatcher operation under this backend's explicitly scoped transition semantics."
    assumptions = @("Three job identities, capacity two and at most two attempts.", "Source correspondence, external worker effects and artifact build provenance require separate review.")
} })
$config = [ordered]@{
    schema_version = 4; kind = "p-analysis"; name = "DispatcherExternal"; engine = $Engine
    models = @((Relative $modelPath)); sources = @((Relative $sourcePath)); scope = (Relative $scopePath)
    bindings = $bindings; dependencies = @($dependencies.Values)
}
$claim = switch ($Engine) {
    "pex" { "exhaustive-finite-model" }
    "psym" { "exhaustive-symbolic-model" }
    "pverifier" { "inductive-invariants" }
    "pobserve" { "observed-properties" }
}
if ($Engine -in @("pex", "psym")) {
    $config.artifact = Pin $Artifact
    $config.test_case = "tcFiniteDispatcher"
    $config.schedules = 1000000; $config.max_steps = 3000; $config.seed = 17
    $reviewInputs.Add((Relative $Artifact))
}
if ($Engine -eq "pobserve") {
    $config.observation = @{
        runtime_jar = (Pin $RuntimeJar); monitor_jar = (Pin $MonitorJar)
        specification = "DispatcherMonitor"; parser = "DispatcherParser"
        logs = @((Relative $Log)); input_kind = "text"; replay = 10
    }
    $reviewInputs.Add((Relative $RuntimeJar)); $reviewInputs.Add((Relative $MonitorJar))
    $reviewInputs.Add((Relative $Log))
}
$reviewInputs.Add((Relative $modelPath)); $reviewInputs.Add((Relative $sourcePath))
$reviewInputs.Add((Relative $PCompiler))
[IO.Directory]::CreateDirectory($Output) | Out-Null
Write-NewJson "healthy.json" $config
$reviewInputs.Add("healthy.json")
$stages = @(@{ id = "healthy"; kind = "p-$Engine"; depends_on = @(); timeout_seconds = 240; config = "healthy.json" })
$requirements = @(@{
    id = "healthy"; scenario = "dispatcher"; stage = "healthy"; expected = "passed"; claim = $claim
    bindings = @($bindings | ForEach-Object { $_.id }); reviews = @("external-build")
    config_sha256 = (Hash ([IO.Path]::Combine($Output, "healthy.json")))
})
$config.sources = @(); $config.bindings = @(); $config.Remove("scope") | Out-Null
$controls = if ($Engine -in @("pex", "psym")) { @("leak", "early") } elseif ($Engine -eq "pverifier") { @("admission") } else { @("duplicate-completion") }
foreach ($control in $controls) {
    if ($Engine -in @("pex", "psym")) {
        $config.test_case = if ($control -eq "leak") { "tcLeakedCredit" } else { "tcEarlyRelease" }
        $expectedClaim = "model-counterexample"; $diagnostic = "PL100"
    } elseif ($Engine -eq "pverifier") {
        $config.models = @((Relative $modelPath), (Relative ([IO.Path]::Combine($PSScriptRoot, "verification", "PVerifier", "NoAdmission.p"))))
        $reviewInputs.Add($config.models[1])
        $expectedClaim = "inductive-counterexample"; $diagnostic = "PL102"
    } else {
        $config.observation.logs = @((Relative ([IO.Path]::Combine($PSScriptRoot, "verification", "PObserve", "duplicate-completion.log"))))
        $reviewInputs.Add($config.observation.logs[0])
        $expectedClaim = "observed-property-violation"; $diagnostic = "PL103"
    }
    Write-NewJson "$control.json" $config
    $reviewInputs.Add("$control.json")
    $stages += @{ id = $control; kind = "p-$Engine"; depends_on = @(); timeout_seconds = 240; config = "$control.json" }
    $requirements += @{
        id = $control; scenario = "dispatcher"; stage = $control; expected = "rejected"
        claim = $expectedClaim; diagnostic = $diagnostic; reviews = @("external-build")
        config_sha256 = (Hash ([IO.Path]::Combine($Output, "$control.json")))
    }
}
if ($Engine -eq "pobserve") {
    $config.observation.logs = @((Relative ([IO.Path]::Combine($PSScriptRoot, "verification", "PObserve", "parser-error.log"))))
    Write-NewJson "parser-error.json" $config
}
$uniqueReviewInputs = [Collections.Generic.HashSet[string]]::new($comparer)
foreach ($input in @($reviewInputs) + @($dependencies.Keys | ForEach-Object { Relative $_ })) {
    [void]$uniqueReviewInputs.Add($input)
}
Write-NewJson "project.json" @{
    schema_version = 4; kind = "analysis-project"; annotation_version = 4; id = "dispatcher-$Engine"
    tools = @{ p = (Pin $PCompiler) }
    compiler = @{
        target_framework = "net10.0"; language_version = "14"; nullable = "enable"
        checked = $false; allow_unsafe = $true; defines = @("DEBUG", "TRACE"); references = @()
    }
    requirements = "requirements.json"; review_directory = "reviews"
    obligations = @(@{
        id = "external-build"
        claim = "Review the selected compiler, generated artifact, complete native runtime closure, model/source relation and, for observation, parser/log completeness and single-owner assumptions."
        assumptions = @("Pinned bytes establish consistency, not artifact/source correspondence or authentication.")
        inputs = @($uniqueReviewInputs)
    })
    scenarios = @(@{ id = "dispatcher"; stages = $stages })
}
Write-NewJson "requirements.json" @{
    schema_version = 4; kind = "acceptance-policy"; project = "dispatcher-$Engine"; requirements = $requirements
}
Write-Output "Created dispatcher $Engine project in $Output. External build/source review remains pending."
