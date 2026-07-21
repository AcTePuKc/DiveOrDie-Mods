param(
    [ValidateRange(0, 9)]
    [int]$LanguageIndex = 0,
    [string]$SettingsPath = $(Join-Path $env:USERPROFILE "AppData\LocalLow\DropRateStudio\Dive or Die\Settings.json")
)

$ErrorActionPreference = "Stop"

$runningGame = Get-Process -Name "Dive or Die" -ErrorAction SilentlyContinue
if ($runningGame) {
    throw "Close DIVE or DIE before changing Settings.json."
}
if (-not (Test-Path -LiteralPath $SettingsPath)) {
    throw "Game settings file not found: $SettingsPath"
}

$content = Get-Content -LiteralPath $SettingsPath -Raw
try {
    $null = $content | ConvertFrom-Json
}
catch {
    throw "Settings.json is not valid JSON; no change was made: $($_.Exception.Message)"
}

$pattern = '("languageIndex"\s*:\s*)(-?\d+)'
$match = [regex]::Match($content, $pattern)
if (-not $match.Success) {
    throw "languageIndex was not found in: $SettingsPath"
}

$currentIndex = [int]$match.Groups[2].Value
if ($currentIndex -eq $LanguageIndex) {
    Write-Output "languageIndex is already $LanguageIndex; no change was needed."
    exit 0
}

$backupPath = "$SettingsPath.bulgarian-language-backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
Copy-Item -LiteralPath $SettingsPath -Destination $backupPath

$regex = [regex]::new($pattern)
$updated = $regex.Replace($content, ('${1}' + $LanguageIndex), 1)
try {
    $validated = $updated | ConvertFrom-Json
}
catch {
    throw "The proposed settings update was not valid JSON; the original file was left unchanged: $($_.Exception.Message)"
}
if ([int]$validated.languageIndex -ne $LanguageIndex) {
    throw "The proposed settings update did not produce languageIndex=$LanguageIndex; the original file was left unchanged."
}

$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($SettingsPath, $updated, $utf8WithoutBom)

Write-Output "Reset languageIndex from $currentIndex to $LanguageIndex."
Write-Output "Backup: $backupPath"
