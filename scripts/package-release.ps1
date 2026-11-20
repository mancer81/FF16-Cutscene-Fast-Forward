param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$modId = "ff16.qol.cutscenefastforward"
$modConfigPath = Join-Path $repoRoot "src\$modId\ModConfig.json"
$modConfig = Get-Content -Path $modConfigPath -Raw | ConvertFrom-Json
$version = $modConfig.ModVersion

& (Join-Path $PSScriptRoot "build-and-deploy.ps1") -Configuration $Configuration -BuildOnly

$outputDir = Join-Path $repoRoot "src\$modId\bin\$Configuration"
$releaseDir = Join-Path $repoRoot "release"
$stagingRoot = Join-Path $releaseDir "_staging"
$stagingModDir = Join-Path $stagingRoot $modId
$zipName = "FF16-Cutscene-Fast-Forward-v$version.zip"
$zipPath = Join-Path $releaseDir $zipName

New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
if (Test-Path $stagingRoot) {
    Remove-Item -Path $stagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingModDir -Force | Out-Null

Copy-Item -Path (Join-Path $outputDir "*") -Destination $stagingModDir -Recurse -Force

if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}

Compress-Archive -Path $stagingModDir -DestinationPath $zipPath -CompressionLevel Optimal
Remove-Item -Path $stagingRoot -Recurse -Force

Write-Host "Created release archive: $zipPath"
Get-Item $zipPath | Select-Object Name, Length | Format-Table -AutoSize
