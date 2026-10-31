$ErrorActionPreference = 'Stop'
$taskSource = Join-Path $PSScriptRoot '../src'
$taskViolations = Get-ChildItem -LiteralPath $taskSource -Recurse -Filter '*.cs' -File |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' -and $_.Name -notmatch '^Log(?:\..+)?\.cs$' } |
    Select-String -Pattern '\.Log(?:Trace|Debug|Information|Warning|Error|Critical)\s*\(|\bLog\w*\s*\(\s*\$'
if ($taskViolations) {
    $taskViolations | ForEach-Object { Write-Output ('{0}:{1}: {2}' -f $_.Path, $_.LineNumber, $_.Line.Trim()) }
    exit 1
}
Write-Output 'Logging guard passed.'
