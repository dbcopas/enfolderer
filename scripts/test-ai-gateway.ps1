<#
.SYNOPSIS
    Tests live gateway authentication, published agent wiring and actual MCP calls.
.DESCRIPTION
    Uses only synthetic prompts/public card facts. Invokes the same named agent endpoints as
    the worker and explicitly approves only the expected tool label. Incurs model/tool usage.
    No private scans, bearer tokens or response bodies are logged.
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SubscriptionId,
    [Parameter(Mandatory)] [string] $NamePrefix
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ai-gateway-preview.ps1')
function Read-AzJson {
    param([string[]] $Arguments)
    $text = & az @Arguments --subscription $SubscriptionId --only-show-errors -o json
    if ($LASTEXITCODE) { throw "Azure read failed: $($Arguments -join ' ')" }
    ($text -join "`n") | ConvertFrom-Json -Depth 100
}
function Get-Token([string] $Resource) {
    (Read-AzJson @('account', 'get-access-token', '--resource', $Resource)).accessToken
}
$worker = Read-AzJson @('containerapp', 'show', '-g', "$NamePrefix-platform", '-n', "$NamePrefix-worker")
$envs = $worker.properties.template.containers[0].env
$geo = ($envs | Where-Object name -eq 'ScanPipeline__GeometryProjectEndpoint').value
$idn = ($envs | Where-Object name -eq 'ScanPipeline__IdentificationProjectEndpoint').value
$version = ($envs | Where-Object name -eq 'ScanPipeline__FoundryApiVersion').value
$token = Get-Token 'https://ai.azure.com'
$armToken = Get-Token 'https://management.azure.com/'
$cases = @(
    @{ Agent = 'CardBoundaryAgent'; Endpoint = $geo; Server = 'mcp-imaging'; Suffix = '-geo';
        Prompt = 'Gateway connectivity check only. No photograph or blob is supplied. Return a JSON object saying that an image is required. Do not invent coordinates.' },
    @{ Agent = 'MtgCardIdAgent'; Endpoint = $idn; Server = 'mcp-cardcatalog-mtg'; Suffix = '-mtg';
        Prompt = 'Gateway integration test with public synthetic card readings, not a photograph: readName=Black Lotus, readSet=lea, readCollectorNumber=232, language=en. Call resolve_printing to verify these facts and return the normal identification JSON.' },
    @{ Agent = 'PokemonCardIdAgent'; Endpoint = $idn; Server = 'mcp-cardcatalog-pokemon'; Suffix = '-pkm';
        Prompt = 'Gateway connectivity check only. No photograph is supplied. Return a JSON object saying that an image is required. Do not invent a card.' }
)
$start = [DateTimeOffset]::UtcNow
foreach ($case in $cases) {
    $gatewayId = Get-PreviewGatewayId $SubscriptionId $NamePrefix $case.Suffix
    $gateway = Invoke-PreviewArm $SubscriptionId "$gatewayId`?api-version=$script:GatewayPreviewVersion"
    $url = "https://$($gateway.properties.frontend.defaultHostname)/default/toolservers/$($case.Server)/mcp"
    foreach ($authorization in @('', 'Bearer invalid', "Bearer $armToken")) {
        $headers = @{}
        if ($authorization) { $headers.Authorization = $authorization }
        $response = Invoke-WebRequest $url -Method Post -Headers $headers -ContentType application/json `
            -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' -SkipHttpErrorCheck
        if ($response.StatusCode -notin @(401, 403)) {
            throw "Gateway authentication probe failed for $($case.Server): $($response.StatusCode)."
        }
    }
    $headers = @{ Authorization = "Bearer $token"; 'Foundry-Features' = 'AgentEndpoints=V1Preview' }
    $versions = Invoke-RestMethod "$($case.Endpoint)/agents/$($case.Agent)/versions?api-version=$version" -Headers $headers
    $agent = @($versions.data | Sort-Object { [int]$_.version } -Descending)[0]
    $tool = @($agent.definition.tools | Where-Object type -eq mcp)
    if ($tool.Count -ne 1 -or $tool[0].server_url -ne $url) { throw "$($case.Agent) is not wired to its gateway." }
    # Use the deployed label, not an independently maintained test convention.
    $label = $tool[0].server_label
    $path = "$($case.Endpoint)/agents/$($case.Agent)/endpoint/protocols/openai/responses"
    $body = @{ input = $case.Prompt; store = $true; background = $true }
    $ids = [Collections.Generic.List[string]]::new()
    $listed = $false
    $called = $false
    try {
        for ($round = 0; $round -lt 8; $round++) {
            $result = Invoke-RestMethod "$path`?api-version=$version" -Method Post -Headers $headers `
                -ContentType application/json -Body ($body | ConvertTo-Json -Depth 30)
            $ids.Add($result.id)
            $deadline = [DateTimeOffset]::UtcNow.AddMinutes(3)
            while ($result.status -in @('queued', 'in_progress')) {
                if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Agent probe timed out.' }
                Start-Sleep -Seconds 2
                $result = Invoke-RestMethod "$path/$($result.id)?api-version=$version" -Headers $headers
            }
            if ($result.status -ne 'completed') { throw "Agent probe failed for $($case.Agent): $($result | ConvertTo-Json -Depth 8 -Compress)" }
            $decisions = @()
            foreach ($item in $result.output) {
                if ($item.type -eq 'mcp_list_tools') {
                    if ($item.PSObject.Properties['error'] -and $item.error) { throw "Tool listing failed for $($case.Agent)." }
                    if ($item.server_label -eq $label -and @($item.tools).Count) { $listed = $true }
                }
                if ($item.type -eq 'mcp_call') {
                    if ($item.PSObject.Properties['error'] -and $item.error) { throw "Tool call failed for $($case.Agent)." }
                    if ($item.server_label -eq $label) { $called = $true }
                }
                if ($item.type -eq 'mcp_approval_request') {
                    if ($item.server_label -ne $label) { throw 'Agent requested an unapproved tool server.' }
                    $decisions += @{ type = 'mcp_approval_response'; approval_request_id = $item.id; approve = $true }
                }
            }
            if (-not $decisions.Count) { break }
            $body = @{ previous_response_id = $result.id; input = $decisions; store = $true; background = $true }
        }
        if ($decisions.Count) { throw "Agent probe exceeded the approval-round limit for $($case.Agent)." }
        if (-not $listed) { throw "No successful gateway tool listing from $($case.Agent)." }
        if ($case.Agent -eq 'MtgCardIdAgent' -and -not $called) { throw 'MTG probe did not execute a catalogue tool.' }
        Write-Output "PASS $($case.Agent): gateway auth denials, published URL, live agent MCP listing$(if ($called) { ' and tool call' })."
    }
    finally {
        foreach ($responseId in $ids) {
            $null = Invoke-RestMethod "$path/$responseId`?api-version=$version" -Method Delete -Headers $headers
        }
    }
}
Write-Output "Dedicated preview agent probes began $($start.ToString('o')); all three agents use isolated native toolservers endpoints."
