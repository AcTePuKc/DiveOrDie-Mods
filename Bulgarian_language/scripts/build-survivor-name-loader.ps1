param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\DIVE or DIE - Children of Rain",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$modRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $modRoot "src\DiveOrDieSurvivorNameLoader\DiveOrDieSurvivorNameLoader.csproj"
$output = Join-Path $modRoot "build\DiveOrDieTranslationMod"

dotnet build $project -c $Configuration -p:GameDir="$GameDir"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
New-Item -ItemType Directory -Force -Path $output | Out-Null
Copy-Item -LiteralPath (Join-Path $modRoot "src\DiveOrDieSurvivorNameLoader\bin\$Configuration\net472\DiveOrDieSurvivorNameLoader.dll") `
    -Destination (Join-Path $output "DiveOrDieSurvivorNameLoader.dll") -Force
Copy-Item -LiteralPath (Join-Path $modRoot "src\DiveOrDieTranslationMod\translations\survivor-names.json") `
    -Destination (Join-Path $output "survivor-names.json") -Force

[pscustomobject]@{
    Output = $output
    Dll = Join-Path $output "DiveOrDieSurvivorNameLoader.dll"
}
