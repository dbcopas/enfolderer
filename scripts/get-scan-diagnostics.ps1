<#
.SYNOPSIS
    Downloads a private pilot scan and its diagnostic evidence without pasting attachments.
.DESCRIPTION
    Read-only. Defaults to the latest uploaded scan across approved pilot users in the last 24 hours.
    Requires an explicitly approved diagnostics operator, delegated Scan.Submit, and (for logs)
    Azure CLI sign-in with Log Analytics read access. Never writes tokens or public/SAS URLs.
    Evidence can contain personal photos and user identifiers: keep it outside the repository.
.PARAMETER SignIn
    Perform interactive Microsoft sign-in once. Later calls use the OS-encrypted desktop token cache.
.PARAMETER JobId
    A specific scan job ID. Omit to select the latest uploaded attempt, including failed/running scans.
.EXAMPLE
    .\scripts\get-scan-diagnostics.ps1 -SignIn -OutputDirectory C:\private\scan-diagnostics
.EXAMPLE
    .\scripts\get-scan-diagnostics.ps1 -JobId 249d08db8c97462f8e2c193bd8f97f99
#>
[CmdletBinding()]
param(
    [string] $JobId = 'latest',
    [ValidateRange(1, 168)] [int] $Hours = 24,
    [string] $NamePrefix = 'enf-demo',
    [string] $ConfigFile = (Join-Path $PSScriptRoot '..\Enfolderer.App\aiconfig.defaults.txt'),
    [string] $OutputDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Enfolderer\ScanDiagnostics'),
    [string] $AuthenticationRecord,
    [switch] $SignIn
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($NamePrefix -notmatch '^[a-zA-Z0-9-]{3,12}$') { throw 'Invalid deployment name prefix.' }
if ($JobId -ne 'latest' -and (-not [guid]::TryParse($JobId, [ref]([guid]::Empty)))) { throw 'JobId must be a GUID or latest.' }
$stateDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Enfolderer\DiagnosticsAuth'
$recordFile = Join-Path $stateDirectory 'aiauth.json'
$importRecord = if ($AuthenticationRecord) { $AuthenticationRecord } else { 'none' }
$mode = if ($SignIn) { 'sign-in' } else { 'silent' }
$tool = Join-Path $PSScriptRoot '..\tools\Enfolderer.Ai.ScanDiagnostics'
$output = @(& dotnet run --project $tool -- $ConfigFile $OutputDirectory $JobId $Hours $recordFile $importRecord $mode)
if ($LASTEXITCODE -ne 0) { throw 'Scan evidence download failed; see the error above. Use -SignIn if interactive authentication is required.' }
$directory = $output[-1].Trim()
if (-not (Test-Path -LiteralPath (Join-Path $directory 'manifest.json'))) { throw 'Diagnostic download did not produce a manifest.' }
$job = Get-Content -Raw -LiteralPath (Join-Path $directory 'job.json') | ConvertFrom-Json
Write-Host "Scan $($job.jobId): $($job.status). Evidence: $directory"

$queryFile = [IO.Path]::GetTempFileName()
$resultFile = [IO.Path]::GetTempFileName()
try {
    $worker = & az containerapp show -g "$NamePrefix-platform" -n "$NamePrefix-worker" -o json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the worker; sign in with Azure CLI and verify subscription/read access.' }
    $environment = & az containerapp env show --ids $worker.properties.environmentId -o json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the Container Apps environment.' }
    $workspace = $environment.properties.appLogsConfiguration.logAnalyticsConfiguration.customerId
    if (-not $workspace) { throw 'Log Analytics workspace is not configured.' }
    $start = ([DateTimeOffset]$job.createdAt).AddMinutes(-1).UtcDateTime.ToString('O')
    $now = [DateTimeOffset]::UtcNow
    $terminal = $job.status -in @('Completed', 'Failed')
    $end = if ($terminal) { ([DateTimeOffset]$job.updatedAt).AddMinutes(2) } else { $now }
    if ($end -gt $now) { $end = $now }
    $endText = $end.UtcDateTime.ToString('O')
    # Unscoped Foundry/geometry messages are useful, but must not masquerade as job-correlated logs.
    $query = @"
ContainerAppConsoleLogs_CL
| where TimeGenerated between (datetime($start) .. datetime($endText))
| where ContainerAppName_s startswith '$NamePrefix-'
| project TimeGenerated, ContainerAppName_s, Log_s
| order by TimeGenerated asc
| take 10001
"@
    [IO.File]::WriteAllText($queryFile, (@{ query = $query } | ConvertTo-Json))
    & az rest --method post --url "https://api.loganalytics.azure.com/v1/workspaces/$workspace/query" `
        --resource https://api.loganalytics.io --body "@$queryFile" -o json --output-file $resultFile
    if ($LASTEXITCODE -ne 0) { throw 'Log Analytics query failed; verify workspace read access.' }
    $logs = [IO.File]::ReadAllText($resultFile) | ConvertFrom-Json
    if ($logs.PSObject.Properties['error']) { throw "Log Analytics returned an error: $($logs.error.code)" }
    $rows = @($logs.tables[0].rows)
    if ($rows.Count -gt 10000) { throw 'Log window exceeded 10000 rows. Evidence retained, but logs are incomplete; narrow the investigation.' }
    $context = @($rows | ForEach-Object { [pscustomobject]@{ timestamp = $_[0]; app = $_[1]; message = $_[2] } })
    $correlated = @($context | Where-Object { $_.message -match "(?i)(?<![0-9a-f])$([regex]::Escape($job.jobId))(?![0-9a-f])" })
    ConvertTo-Json -InputObject $context -Depth 8 | Set-Content -LiteralPath (Join-Path $directory 'context-logs.json') -Encoding utf8
    ConvertTo-Json -InputObject $correlated -Depth 8 | Set-Content -LiteralPath (Join-Path $directory 'job-logs.json') -Encoding utf8
    @{ complete = $true; retrievedAt = $now; correlatedLines = $correlated.Count; contextLines = $context.Count;
        warning = 'Context logs may contain overlapping scans; only job-logs.json is directly job-correlated. Log Analytics ingestion may lag.' } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'logs-status.json') -Encoding utf8
    Write-Host "Logs: $($correlated.Count) job-correlated lines; $($context.Count) time-window context lines."
}
catch {
    @{ complete = $false; error = $_.Exception.Message } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $directory 'logs-status.json') -Encoding utf8
    throw
}
finally {
    Remove-Item -LiteralPath $queryFile, $resultFile
}
Write-Output $directory
