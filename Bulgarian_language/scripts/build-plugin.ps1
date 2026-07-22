param(
    [string]$Configuration = "Release",
    [string]$ProjectPath = "",
    [string]$GameDir = $env:DIVE_OR_DIE_DIR,
    [switch]$UsePrebuilt
)

$ErrorActionPreference = "Stop"
$modRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$outDir = Join-Path $modRoot "build\DiveOrDieTranslationMod"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
$verifiedCore = Join-Path $modRoot "artifacts\DiveOrDieTranslationMod.dll"
Copy-Item -LiteralPath $verifiedCore -Destination (Join-Path $outDir "DiveOrDieTranslationMod.dll") -Force
Copy-Item -LiteralPath (Join-Path $modRoot "src\DiveOrDieTranslationMod\translations\labels.txt") `
    -Destination (Join-Path $outDir "labels.txt") -Force
Copy-Item -LiteralPath (Join-Path $modRoot "src\DiveOrDieTranslationMod\translations\survivor-names.json") `
    -Destination (Join-Path $outDir "survivor-names.json") -Force

if ($UsePrebuilt) {
    Copy-Item -LiteralPath (Join-Path $modRoot "artifacts\DiveOrDieSurvivorNameLoader.dll") `
        -Destination (Join-Path $outDir "DiveOrDieSurvivorNameLoader.dll") -Force
}
else {
    $loaderBuild = Join-Path $PSScriptRoot "build-survivor-name-loader.ps1"
    if ([string]::IsNullOrWhiteSpace($GameDir)) {
        & $loaderBuild -Configuration $Configuration
    }
    else {
        & $loaderBuild -Configuration $Configuration -GameDir $GameDir
    }
    if (-not $?) { throw "Survivor name loader build failed." }
}

[pscustomobject]@{
    PluginDir = (Resolve-Path -LiteralPath $outDir).Path
    Dll = (Resolve-Path -LiteralPath (Join-Path $outDir "DiveOrDieTranslationMod.dll")).Path
}
