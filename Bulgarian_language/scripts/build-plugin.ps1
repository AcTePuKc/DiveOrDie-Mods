param(
    [string]$Configuration = "Release",
    [string]$ProjectPath = "",
    [string]$GameDir = $env:DIVE_OR_DIE_DIR
)

$ErrorActionPreference = "Stop"
$modRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $modRoot "src\DiveOrDieTranslationMod\DiveOrDieTranslationMod.csproj"
}

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    Write-Host "Building Bulgarian Language with GameDir from User.targets"
    dotnet build $ProjectPath -c $Configuration
}
else {
    Write-Host "Building Bulgarian Language for the configured game installation"
    dotnet build $ProjectPath -c $Configuration -p:GameDir="$GameDir"
}
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$outDir = Join-Path $modRoot "build\DiveOrDieTranslationMod"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $modRoot "src\DiveOrDieTranslationMod\bin\$Configuration\net472\DiveOrDieTranslationMod.dll") -Destination (Join-Path $outDir "DiveOrDieTranslationMod.dll") -Force

[pscustomobject]@{
    PluginDir = (Resolve-Path -LiteralPath $outDir).Path
    Dll = (Resolve-Path -LiteralPath (Join-Path $outDir "DiveOrDieTranslationMod.dll")).Path
}
