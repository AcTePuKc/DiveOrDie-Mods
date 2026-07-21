param(
    [string]$Configuration = "Release",
    [string]$ProjectPath = ""
)

$ErrorActionPreference = "Stop"
$modRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
    $ProjectPath = Join-Path $modRoot "src\DiveOrDieSkipIntroPatcher\DiveOrDieSkipIntroPatcher.csproj"
}

Write-Host "Building standalone Skip Intro patcher"
dotnet build $ProjectPath -c $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$outDir = Join-Path $modRoot "build\DiveOrDieSkipIntro"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $modRoot "src\DiveOrDieSkipIntroPatcher\bin\$Configuration\net472\DiveOrDieSkipIntroPatcher.dll") -Destination (Join-Path $outDir "DiveOrDieSkipIntroPatcher.dll") -Force

[pscustomobject]@{
    PatcherDir = (Resolve-Path -LiteralPath $outDir).Path
    Dll = (Resolve-Path -LiteralPath (Join-Path $outDir "DiveOrDieSkipIntroPatcher.dll")).Path
}
