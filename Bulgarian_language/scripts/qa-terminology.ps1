param(
    [string]$LabelPath = ""
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($LabelPath)) {
    $modRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
    $LabelPath = Join-Path $modRoot "src\DiveOrDieTranslationMod\translations\labels.txt"
}

if (-not (Test-Path -LiteralPath $LabelPath)) {
    throw "Translation file not found: $LabelPath"
}

$lines = Get-Content -LiteralPath $LabelPath
$entries = $lines | Where-Object { $_ -and -not $_.StartsWith('#') -and $_ -match '=' }
$issues = [System.Collections.Generic.List[string]]::new()

$keys = $entries | ForEach-Object { ($_ -split '=', 2)[0] }
$duplicates = $keys | Group-Object | Where-Object Count -gt 1
foreach ($duplicate in $duplicates) {
    $issues.Add("duplicate key: $($duplicate.Name)")
}

$forbiddenTerms = [ordered]@{
    'sanity alternatives' = @('здрав разум', 'здравия разум', 'разсъдък')
    'old offering term' = @('Приношение', 'Приношения', 'приношение', 'приношения')
    'old manning phrase' = @('Докато ръководи', 'докато ръководи')
    'old rebreather spelling' = @('Ребрийдър', 'ребрийдър')
    'formal address' = @('вас', 'сте', 'бихте', 'можете', 'искате')
}

foreach ($line in $lines) {
    foreach ($rule in $forbiddenTerms.GetEnumerator()) {
        foreach ($term in $rule.Value) {
            if ($line -cmatch "(?<![\p{L}])$([regex]::Escape($term))(?![\p{L}])") {
                $issues.Add("$($rule.Key): $line")
            }
        }
    }
}

if ($issues.Count -gt 0) {
    $issues | ForEach-Object { Write-Output "ISSUE: $_" }
    exit 1
}

Write-Output "Terminology QA passed: $($entries.Count) entries, $($keys.Count) keys checked."
