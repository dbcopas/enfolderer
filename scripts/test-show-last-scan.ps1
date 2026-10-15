$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$state = [pscustomobject]@{ Rows = @(); LastQuery = ''; UsedOutputFile = $false }
$global:ScanDiagnosticTestState = $state
function az {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'account') { return '{"id":"test"}' }
    if ($args[0] -eq 'containerapp' -and $args[1] -eq 'show') {
        return '{"properties":{"environmentId":"test-env"}}'
    }
    if ($args[0] -eq 'containerapp' -and $args[1] -eq 'env') {
        return '{"properties":{"appLogsConfiguration":{"logAnalyticsConfiguration":{"customerId":"test-workspace"}}}}'
    }
    if ($args[0] -ne 'rest') { throw "Unexpected az invocation: $args" }
    $bodyIndex = [array]::IndexOf($args, '--body')
    $global:ScanDiagnosticTestState.LastQuery = (Get-Content -Raw -LiteralPath $args[$bodyIndex + 1].Substring(1) | ConvertFrom-Json).query
    $outputIndex = [array]::IndexOf($args, '--output-file')
    if ($outputIndex -lt 0) { throw 'The log query must use UTF-8 file output.' }
    $global:ScanDiagnosticTestState.UsedOutputFile = $true
    $response = @{ tables = @(@{ name = 'PrimaryResult'; rows = $global:ScanDiagnosticTestState.Rows }) } | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($args[$outputIndex + 1], $response, [Text.Encoding]::UTF8)
}

function Assert-Contains($text, $expected) {
    if (-not $text.Contains($expected)) { throw "Missing '$expected' in output:`n$text" }
}
function Run-Diagnostic([switch]$All) {
    (& (Join-Path $PSScriptRoot 'show-last-scan.ps1') -NamePrefix test -Hours 1 -All:$All 6>&1 | Out-String)
}

try {
$state.Rows = @(
    @('2026-10-12T08:30:00Z', 'Job aaaaaaaa started: detecting boundaries.'),
    @('2026-10-12T08:35:00Z', 'Card 0 of job aaaaaaaa: ltr 36 resolved as unplaced'),
    @('2026-10-12T08:40:00Z', 'Job bbbbbbbb failed in state DetectingBoundaries. Agent reply contained an unterminated JSON object.'),
    @('2026-10-12T08:41:00Z', 'Job aaaaaaaa completed: 1 card(s) detected, 1 identified.')
)
$output = Run-Diagnostic
Assert-Contains $output 'Scan bbbbbbbb'
Assert-Contains $output 'FAILED'
if ($output.Contains('Scan aaaaaaaa')) { throw 'An older scan replaced the latest failed attempt.' }
Assert-Contains $state.LastQuery 'failed in state'
Assert-Contains $state.LastQuery 'started|completed'
Assert-Contains $state.LastQuery ': processing card '
Assert-Contains (Run-Diagnostic -All) 'Scan aaaaaaaa'

$unicode = ([char]0x795E).ToString() + [char]0x79D8
$state.Rows = @(
    @('2026-10-12T08:42:00Z', 'Job cccccccc started: detecting boundaries.'),
    @('2026-10-12T08:43:00Z', "Card 0 of job cccccccc: $unicode resolved as confirmed"),
    @('2026-10-12T08:44:00Z', 'Job cccccccc completed: 1 card(s) detected, 1 identified.')
)
Assert-Contains (Run-Diagnostic) $unicode
if (-not $state.UsedOutputFile) { throw 'UTF-8 file output was not exercised.' }
$state.Rows = @()
Assert-Contains (Run-Diagnostic) 'No scan attempt or card resolution'
Write-Output 'All diagnostic checks passed (latest failure, overlapping attempts, -All, Unicode, empty window).'
}
finally {
    Remove-Variable ScanDiagnosticTestState -Scope Global
}
