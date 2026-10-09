<#
.SYNOPSIS
    Shows what the pipeline decided about each card in the most recent scan. No arguments.

.DESCRIPTION
    A scan that comes back with the wrong edition looks, in the exported CSV, exactly like a scan
    that came back right. The export carries a set and a number; it does not carry how they were
    arrived at, and that is the only thing that says whether to believe them.

    The worker writes one line per card as it resolves it, and those lines do carry it: the
    resolution, the set and number read off the card, each art candidate's distance, and the
    comparison's measured reason for accepting or declining. This script finds the worker, pulls those lines
    out of Log Analytics and prints them in order.

    It reads logs rather than the job document in Cosmos deliberately. Cosmos has local
    authentication disabled, so querying it needs a data-plane role assignment that nobody has by
    default, and granting one is a detour. The logs need nothing beyond the Reader access you
    already have to the resource group, and they say more.

    Reading the output: the `resolution` word is the important one.

      confirmed   name and number agreed. Nothing to doubt - unless both were supplied from
                  memory, which is the one case no text check can see, and why the art check exists.
      named       no number read, but the name is printed in that set and nowhere else, so the set
                  code had nothing to choose between. Equally safe.
      unverified  no number read, the name is printed in several sets, and the set code alone
                  picked this one. Look at the card.
      corrected   the number read named a different card and the name won. The mechanism working.
      relocated   the card was found outside the set code that was read.
      unplaced    no set code and no matching number; the set and number shown are the catalogue's
                  default printing of the name, not this card's.
      ambiguous   the set prints that name more than once and nothing separated them.

.PARAMETER NamePrefix
    The part before "-platform" in your resource group names. Discovered if omitted.

.PARAMETER Hours
    How far back to look. Defaults to 6.

.PARAMETER All
    Show every scan in the window rather than only the most recent one.

.EXAMPLE
    ./scripts/show-last-scan.ps1

    The most recent scan, card by card.

.EXAMPLE
    ./scripts/show-last-scan.ps1 -Hours 48 -All

    Every scan of the last two days.
#>

[CmdletBinding()]
param(
    [string] $NamePrefix,
    [int] $Hours = 6,
    [switch] $All
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step { param([string] $T) Write-Host ''; Write-Host $T -ForegroundColor Cyan }
function Write-Note { param([string] $T) Write-Host "  $T" -ForegroundColor DarkGray }

function Invoke-Az {
    param([Parameter(Mandatory)] [string[]] $Arguments)
    $out = & az @Arguments 2>$null
    if ($LASTEXITCODE -ne 0 -or $null -eq $out) { return $null }
    $text = ($out -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return $text
}

if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
    throw "The Azure CLI (az) is not installed. Install it with: winget install --exact --id Microsoft.AzureCLI"
}
if (-not (Invoke-Az @('account', 'show', '-o', 'json'))) {
    throw "Not signed in to Azure. Run: az login"
}

Write-Step '1. Find the worker'

if (-not $NamePrefix) {
    $groupJson = Invoke-Az @('group', 'list', '-o', 'json')
    $candidates = @()
    if ($groupJson) {
        $candidates = @(($groupJson | ConvertFrom-Json) |
            Where-Object { $_.name.EndsWith('-platform') } | ForEach-Object { $_.name })
    }

    if ($candidates.Count -eq 1) { $NamePrefix = $candidates[0] -replace '-platform$', '' }
    elseif ($candidates.Count -gt 1) {
        throw @"
More than one deployment of this demo is in the subscription:
$($candidates | ForEach-Object { "    $_" } | Out-String)
Say which one to read, using the part of the name before '-platform':

    ./scripts/show-last-scan.ps1 -NamePrefix <prefix>
"@
    }
    else {
        throw "No resource group ending in '-platform' was found, so the demo is not deployed in this subscription."
    }
}

$rg     = "$NamePrefix-platform"
$worker = "$NamePrefix-worker"
Write-Note "$worker in $rg"

# Container Apps writes to the environment's Log Analytics workspace, and the query below runs
# against the workspace rather than the app, so the link has to be followed to find its id.
$workerJson = Invoke-Az @('containerapp', 'show', '-g', $rg, '-n', $worker, '-o', 'json')
if (-not $workerJson) {
    throw @"
No container app named '$worker' in resource group '$rg'.

See what is there:
    az containerapp list -g $rg --query "[].name" -o table
"@
}
$envId = ($workerJson | ConvertFrom-Json).properties.environmentId

$environmentJson = Invoke-Az @('containerapp', 'env', 'show', '--ids', $envId, '-o', 'json')
$customerId = if ($environmentJson) {
    ($environmentJson | ConvertFrom-Json).properties.appLogsConfiguration.logAnalyticsConfiguration.customerId
} else { $null }
if (-not $customerId) {
    throw @"
The Container Apps environment is not sending logs to Log Analytics, so there is nothing to read.

You can still watch the worker live while a scan runs:
    az containerapp logs show -g $rg -n $worker --follow --tail 100
"@
}
Write-Note "log analytics workspace $customerId"

Write-Step "2. Read the resolution lines from the last $Hours hour(s)"

# ContainerAppConsoleLogs_CL is the stream the app's stdout lands in. The worker's resolution line
# always contains " resolved as ", which is specific enough to pick those lines out of everything
# else the worker says while keeping the warnings - a doubtful card is logged at warning level, so
# filtering by severity would drop exactly the cards worth looking at.
$query = @"
ContainerAppConsoleLogs_CL
| where TimeGenerated > ago($($Hours)h)
| where ContainerAppName_s == '$worker'
| where Log_s contains 'resolved as' or Log_s contains 'was not identified'
    or Log_s contains 'was dropped after' or Log_s contains 'Identification failed for card'
    or Log_s contains 'art candidate' or Log_s contains 'art compared' or Log_s contains 'art not checked'
    or Log_s contains 'could not compare candidate' or Log_s contains 'server_error; retry'
| project TimeGenerated, Log_s
| order by TimeGenerated asc
"@

# az.cmd lets cmd.exe interpret KQL pipes and quotes. Send JSON from a file instead.
$queryFile = [System.IO.Path]::GetTempFileName()
try {
    [System.IO.File]::WriteAllText($queryFile, (@{ query = $query } | ConvertTo-Json))
    $raw = az rest --method post --url "https://api.loganalytics.azure.com/v1/workspaces/$customerId/query" `
        --resource https://api.loganalytics.io --body "@$queryFile" -o json
    $queryExitCode = $LASTEXITCODE
}
finally {
    Remove-Item -LiteralPath $queryFile
}
if ($queryExitCode -ne 0 -or -not $raw) {
    throw @"
The log query failed. The usual cause is not having Log Analytics Reader on the workspace; Owner on
the subscription does not include it.

Grant it to yourself:
    `$me = az ad signed-in-user show --query id -o tsv
    `$ws = az monitor log-analytics workspace list -g $rg --query "[0].id" -o tsv
    az role assignment create --assignee `$me --role "Log Analytics Reader" --scope `$ws

Then run this script again. Role assignments can take a minute or two to take effect.
"@
}

# az returns its output a line at a time, and piping that array to ConvertFrom-Json asks it to
# parse each line on its own. Join it back into one document first.
$response = ($raw -join "`n") | ConvertFrom-Json
$table = @($response.tables | Where-Object { $_.name -eq 'PrimaryResult' })
if ($table.Count -ne 1) { throw 'Log Analytics returned no PrimaryResult table.' }
$rows = @($table[0].rows | ForEach-Object {
    [pscustomobject]@{ TimeGenerated = $_[0]; Log_s = $_[1] }
})
if (-not $rows -or $rows.Count -eq 0) {
    Write-Host ''
    Write-Host "No card was resolved in the last $Hours hour(s)." -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Either no scan has run, or it failed before reaching the catalogue. Widen the window:' -ForegroundColor DarkYellow
    Write-Host "    ./scripts/show-last-scan.ps1 -Hours 48"
    Write-Host 'or watch the worker while you scan:' -ForegroundColor DarkYellow
    Write-Host "    az containerapp logs show -g $rg -n $worker --follow --tail 100"
    return
}

# Every line names its job, so the scans separate themselves without needing to be looked up.
$parsed = foreach ($r in $rows) {
    $job = if ($r.Log_s -match 'job ([0-9a-fA-F-]{8,})') { $Matches[1] } else { '(unknown)' }
    [pscustomobject] @{ Time = $r.TimeGenerated; Job = $job; Line = $r.Log_s }
}

$uncorrelated = @($parsed | Where-Object { $_.Job -eq '(unknown)' })
$parsed = @($parsed | Where-Object { $_.Job -ne '(unknown)' })
$jobs = @($parsed | Select-Object -ExpandProperty Job -Unique)
if (-not $All -and $jobs.Count -gt 1) {
    $latest = ($parsed | Select-Object -Last 1).Job
    Write-Note "$($jobs.Count) scans in the window; showing the most recent. Pass -All for the rest."
    $parsed = @($parsed | Where-Object { $_.Job -eq $latest })
    $jobs = @($latest)
}

foreach ($job in $jobs) {
    Write-Host ''
    Write-Host "Scan $job" -ForegroundColor Cyan
    foreach ($p in ($parsed | Where-Object { $_.Job -eq $job })) {
        # The line is already a sentence written for a person. Colour is the only thing added:
        # anything that is not plainly settled is worth the eye stopping on it.
        $doubt = $p.Line -match 'resolved as (unverified|unplaced|ambiguous|fuzzy|relocated|corrected)' `
              -or $p.Line -match 'could not be identified' `
              -or $p.Line -match 'was dropped after' `
              -or $p.Line -match 'then moved to|Identification failed|was not identified|art declined|art not checked|could not compare'
        $colour = if ($doubt) { 'Yellow' } else { 'Gray' }

        # Strip the structured-logging preamble if the runtime added one; the sentence is the point.
        $text = $p.Line -replace '^\s*(?:\S+\s+)?(?:info|warn|fail|trce|dbug|crit):\s*', ''
        Write-Host "  $text" -ForegroundColor $colour
    }
}

if ($uncorrelated.Count -gt 0) {
    Write-Host ''
    Write-Host 'Uncorrelated retry events in this time window (identified by thread, not job):' -ForegroundColor Yellow
    foreach ($event in $uncorrelated) {
        Write-Host "  $($event.Time): $($event.Line)" -ForegroundColor Yellow
    }
}

Write-Host ''
Write-Host 'A yellow line is one the pipeline is not sure about, not necessarily a wrong one.' -ForegroundColor DarkGray
Write-Host 'The words are explained in the help for this script:' -ForegroundColor DarkGray
Write-Host '    Get-Help ./scripts/show-last-scan.ps1 -Detailed' -ForegroundColor DarkGray
