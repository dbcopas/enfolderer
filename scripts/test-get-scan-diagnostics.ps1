Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ("enfolderer-diagnostics-test-" + [guid]::NewGuid().ToString('N'))
$state = [pscustomobject]@{ Root = $root; FailLogs = $false; Query = ''; Arguments = @(); Rows = @() }
$global:ScanEvidenceTest = $state
New-Item -ItemType Directory -Path $root | Out-Null

function dotnet {
    $global:LASTEXITCODE = 0
    $global:ScanEvidenceTest.Arguments = $args
    $directory = Join-Path $global:ScanEvidenceTest.Root ([guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory | Out-Null
    '{"jobId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","status":"Failed","createdAt":"2026-10-17T10:00:00Z","updatedAt":"2026-10-17T10:05:00Z"}' |
        Set-Content -LiteralPath (Join-Path $directory 'job.json')
    '{}' | Set-Content -LiteralPath (Join-Path $directory 'manifest.json')
    Write-Output $directory
}
function az {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'containerapp' -and $args[1] -eq 'show') {
        return '{"properties":{"environmentId":"test-env"}}'
    }
    if ($args[0] -eq 'containerapp' -and $args[1] -eq 'env') {
        return '{"properties":{"appLogsConfiguration":{"logAnalyticsConfiguration":{"customerId":"test-workspace"}}}}'
    }
    if ($args[0] -ne 'rest') { throw "Unexpected Azure CLI call: $args" }
    $body = [array]::IndexOf($args, '--body')
    $global:ScanEvidenceTest.Query = (Get-Content -Raw -LiteralPath $args[$body + 1].Substring(1) | ConvertFrom-Json).query
    if ($global:ScanEvidenceTest.FailLogs) { $global:LASTEXITCODE = 1; return }
    $output = [array]::IndexOf($args, '--output-file')
    @{ tables = @(@{ rows = $global:ScanEvidenceTest.Rows }) } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $args[$output + 1] -Encoding utf8
}
function Assert($condition, $message) { if (-not $condition) { throw $message } }

try {
    $unicode = ([char]0x795E).ToString() + [char]0x79D8
    $state.Rows = @(
        @('2026-10-17T10:01:00Z', 'test-worker', "Card of job aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa: $unicode"),
        @('2026-10-17T10:02:00Z', 'test-worker', 'Job bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb completed'),
        @('2026-10-17T10:03:00Z', 'test-mcp-imaging', 'Boundary tool returned six regions')
    )
    $directory = & (Join-Path $PSScriptRoot 'get-scan-diagnostics.ps1') -NamePrefix test -OutputDirectory $root
    $status = Get-Content -Raw -LiteralPath (Join-Path $directory 'logs-status.json') | ConvertFrom-Json
    Assert ($status.complete -and $status.correlatedLines -eq 1 -and $status.contextLines -eq 3) 'Context must not be mislabelled as correlated evidence.'
    Assert ((Get-Content -Raw -LiteralPath (Join-Path $directory 'job-logs.json')).Contains($unicode)) 'Unicode log evidence was corrupted.'
    Assert ($state.Arguments -contains 'latest') 'Latest must be the default.'
    Assert ($state.Query.Contains('2026-10-17T09:59:00.0000000Z') -and $state.Query.Contains('2026-10-17T10:07:00.0000000Z')) 'UTC job timestamps must be preserved in bounded log windows.'
    Assert ($state.Query.Contains('take 10001')) 'Log query must detect truncation.'
    $state.Rows = @()
    $directory = & (Join-Path $PSScriptRoot 'get-scan-diagnostics.ps1') -NamePrefix test -OutputDirectory $root -JobId aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
    Assert ($state.Arguments -contains 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa') 'Specific job selection must reach the evidence client.'
    Assert ((Get-Content -Raw -LiteralPath (Join-Path $directory 'job-logs.json')).Trim() -eq '[]') 'No logs should be an explicit empty array.'
    $state.FailLogs = $true
    $failed = $false
    try { & (Join-Path $PSScriptRoot 'get-scan-diagnostics.ps1') -NamePrefix test -OutputDirectory $root }
    catch { $failed = $_.Exception.Message.Contains('Log Analytics query failed') }
    Assert $failed 'Log retrieval failure must not report a successful bundle.'
    $failures = @(Get-ChildItem -LiteralPath $root -Filter logs-status.json -Recurse -File |
        ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json } | Where-Object { -not $_.complete })
    Assert ($failures.Count -eq 1) 'A failed log retrieval must persist explicit status alongside retained scan evidence.'
    Write-Output 'All scan evidence script checks passed (latest/specific selection, UTC, interleaving, Unicode, empty logs, explicit failure).'
}
finally {
    Remove-Variable ScanEvidenceTest -Scope Global
    Get-ChildItem -LiteralPath $root -Directory | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -File | Remove-Item
        Remove-Item -LiteralPath $_.FullName
    }
    Remove-Item -LiteralPath $root
}
