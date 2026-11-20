param(
    [string]$Configuration = "Release",
    [string]$ReloadedModsDir = $env:RELOADEDIIMODS,
    [switch]$NoRestore,
    [switch]$BuildOnly
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$modId = "ff16.qol.cutscenefastforward"
$projectPath = Join-Path $repoRoot "src\$modId\$modId.csproj"

$localDotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
$dotnet = if (Test-Path $localDotnet) { $localDotnet } else { "dotnet" }

$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$buildArgs = @("build", $projectPath, "-c", $Configuration)
if ($NoRestore) {
    $buildArgs += "--no-restore"
}

Write-Host "Building $modId ($Configuration) with $dotnet..."
& $dotnet @buildArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}

$outputDir = Join-Path $repoRoot "src\$modId\bin\$Configuration"
if (-not (Test-Path (Join-Path $outputDir "$modId.dll"))) {
    throw "Expected build output missing: $outputDir\$modId.dll"
}

if ($BuildOnly) {
    Write-Host "Build succeeded: $outputDir"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ReloadedModsDir)) {
    throw "Reloaded-II Mods directory not specified. Pass -ReloadedModsDir 'C:\Path\To\Reloaded-II\Mods' or set the RELOADEDIIMODS environment variable."
}

$deployDir = Join-Path $ReloadedModsDir $modId
Write-Host "Deploying to $deployDir..."
if (Test-Path $deployDir) {
    Remove-Item -Path (Join-Path $deployDir "*") -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $deployDir -Force | Out-Null
}

Copy-Item -Path (Join-Path $outputDir "*") -Destination $deployDir -Recurse -Force

Write-Host "Deployed successfully to $deployDir"
Get-ChildItem -Path $deployDir | Select-Object Name, Length | Format-Table -AutoSize
