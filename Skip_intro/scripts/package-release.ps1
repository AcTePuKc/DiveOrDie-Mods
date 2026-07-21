param(
    [string]$Version = "0.1.0",
    [string]$OutputDir = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$modRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $modRoot "dist"
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

& (Join-Path $PSScriptRoot "build-patcher.ps1") -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$releaseName = "DiveOrDieSkipIntro-$Version"
$stagingRoot = [System.IO.Path]::GetFullPath((Join-Path $OutputDir $releaseName))
if (-not $stagingRoot.StartsWith($OutputDir + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Release staging path escaped the configured output directory: $stagingRoot"
}

$patcherDir = Join-Path $stagingRoot "BepInEx\patchers"
if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $patcherDir | Out-Null
Copy-Item -LiteralPath (Join-Path $modRoot "build\DiveOrDieSkipIntro\DiveOrDieSkipIntroPatcher.dll") -Destination $patcherDir -Force
Copy-Item -LiteralPath (Join-Path $modRoot "README.md") -Destination $stagingRoot -Force
Copy-Item -LiteralPath (Join-Path (Split-Path $modRoot) "LICENSE") -Destination $stagingRoot -Force

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$zipPath = Join-Path $OutputDir "$releaseName.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $zipPath -Force
Write-Output (Resolve-Path -LiteralPath $zipPath).Path
