param(
    [string]$Version = "0.1.0",
    [string]$OutputDir = "",
    [string]$Configuration = "Release",
    [string]$GameDir = $env:DIVE_OR_DIE_DIR,
    [switch]$UsePrebuilt
)

$ErrorActionPreference = "Stop"
$modRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $modRoot "dist"
}
$OutputDir = [System.IO.Path]::GetFullPath($OutputDir)

& (Join-Path $PSScriptRoot "build-plugin.ps1") -Configuration $Configuration -GameDir $GameDir -UsePrebuilt:$UsePrebuilt
if (-not $?) { throw "Bulgarian Language build failed." }

$releaseName = "DiveOrDieBulgarianLanguage-$Version"
$stagingRoot = [System.IO.Path]::GetFullPath((Join-Path $OutputDir $releaseName))
if (-not $stagingRoot.StartsWith($OutputDir + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Release staging path escaped the configured output directory: $stagingRoot"
}

$pluginDir = Join-Path $stagingRoot "BepInEx\plugins\DiveOrDieTranslationMod"
$configDir = Join-Path $stagingRoot "BepInEx\config"
if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pluginDir, $configDir | Out-Null
Copy-Item -LiteralPath (Join-Path $modRoot "build\DiveOrDieTranslationMod\DiveOrDieTranslationMod.dll") -Destination $pluginDir -Force
Copy-Item -LiteralPath (Join-Path $modRoot "build\DiveOrDieTranslationMod\DiveOrDieSurvivorNameLoader.dll") -Destination $pluginDir -Force
Copy-Item -LiteralPath (Join-Path $modRoot "src\DiveOrDieTranslationMod\translations\labels.txt") -Destination (Join-Path $pluginDir "labels.txt") -Force
Copy-Item -LiteralPath (Join-Path $modRoot "src\DiveOrDieTranslationMod\translations\survivor-names.json") -Destination (Join-Path $pluginDir "survivor-names.json") -Force
Copy-Item -LiteralPath (Join-Path $modRoot "config\actepukc.diveordie.translationbulgarian.cfg") -Destination $configDir -Force
Copy-Item -LiteralPath (Join-Path $modRoot "scripts\reset-language-before-disable.ps1") -Destination $stagingRoot -Force
Copy-Item -LiteralPath (Join-Path $modRoot "UNINSTALL.md") -Destination $stagingRoot -Force

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$zipPath = Join-Path $OutputDir "$releaseName.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $zipPath -Force
Write-Output (Resolve-Path -LiteralPath $zipPath).Path
