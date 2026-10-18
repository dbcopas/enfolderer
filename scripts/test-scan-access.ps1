<#
.SYNOPSIS
    Checks live scan endpoint authentication and exact per-agent MCP allow-lists.
.DESCRIPTION
    Read-only except for MCP initialize requests that must be denied. Does not create scan jobs,
    change consent, display tokens, or claim to test live ownership with two human users.
#>
[CmdletBinding()]
param([string] $NamePrefix = 'enf-demo')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$registrations = @(az ad app list --display-name "$NamePrefix-mcp" -o json | ConvertFrom-Json |
    Where-Object displayName -eq "$NamePrefix-mcp")
if ($LASTEXITCODE -ne 0 -or $registrations.Count -ne 1) { throw 'Expected one existing MCP resource registration.' }
$audience = $registrations[0].appId
$token = az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv
if ($LASTEXITCODE -ne 0 -or -not $token) { throw 'Could not obtain Foundry token for the wrong-audience test.' }
$initialize = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"scan-access-check","version":"1"}}}'
$projects = az resource list --resource-type Microsoft.CognitiveServices/accounts/projects -o json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Could not read Foundry projects.' }

foreach ($entry in @(
    @{ Group = "$NamePrefix-cardgeo"; App = "$NamePrefix-mcp-imaging"; Project = 'cardgeo'; Agent = 'CardBoundaryAgent'; Connection = 'mcp-imaging-agent-identity' },
    @{ Group = "$NamePrefix-cardid"; App = "$NamePrefix-mcp-cardcatalog-mtg"; Project = 'cardid'; Agent = 'MtgCardIdAgent'; Connection = 'mcp-cardcatalog-mtg-agent-identity' },
    @{ Group = "$NamePrefix-cardid"; App = "$NamePrefix-mcp-cardcatalog-pokemon"; Project = 'cardid'; Agent = 'PokemonCardIdAgent'; Connection = 'mcp-cardcatalog-pokemon-agent-identity' }
)) {
    $app = az containerapp show -g $entry.Group -n $entry.App -o json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Could not read $($entry.App)." }
    $url = "https://$($app.properties.configuration.ingress.fqdn)"
    $health = Invoke-RestMethod "$url/healthz" -TimeoutSec 30
    if ($health.status -ne 'ok') { throw "$($entry.App) is unhealthy." }
    foreach ($headers in @(
        @{ Accept = 'application/json, text/event-stream' },
        @{ Accept = 'application/json, text/event-stream'; Authorization = "Bearer $token" }
    )) {
        $response = Invoke-WebRequest "$url/mcp" -Method Post -Headers $headers `
            -ContentType application/json -Body $initialize -SkipHttpErrorCheck -TimeoutSec 30
        if ($response.StatusCode -ne 401) { throw "$($entry.App): expected 401, received $($response.StatusCode)." }
    }
    $project = @($projects | Where-Object {
        $_.resourceGroup -in @("$NamePrefix-platform", $entry.Group) -and ($_.name -split '/')[-1] -eq $entry.Project
    })
    if ($project.Count -ne 1) { throw "Expected one $($entry.Project) project." }
    $account = ($project[0].name -split '/')[0]
    $subdomain = az cognitiveservices account show -g $project[0].resourceGroup -n $account --query properties.customSubDomainName -o tsv
    if ($LASTEXITCODE -ne 0) { throw "Could not read account $account." }
    $endpoint = "https://$subdomain.services.ai.azure.com/api/projects/$($entry.Project)"
    $agent = Invoke-RestMethod "$endpoint/agents/$($entry.Agent)?api-version=2025-11-15-preview" `
        -Headers @{ Authorization = "Bearer $token"; 'Foundry-Features' = 'AgentEndpoints=V1Preview' }
    $envs = $app.properties.template.containers[0].env
    $allowed = ($envs | Where-Object name -eq McpServer__AllowedCallerObjectIds).value
    $configuredAudience = ($envs | Where-Object name -eq McpServer__Audience).value
    if (-not $agent.instance_identity.principal_id -or $allowed -ne $agent.instance_identity.principal_id `
        -or $configuredAudience -ne $audience) {
        throw "$($entry.App): caller/audience differs from the distinct named agent identity."
    }
    $connection = az rest --method get --url "https://management.azure.com$($project[0].id)/connections/$($entry.Connection)?api-version=2025-04-01-preview" -o json |
        ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect $($entry.Connection)." }
    if ($connection.properties.authType -ne 'AgenticIdentityToken' `
        -or $connection.properties.audience -ne "api://$audience" `
        -or $connection.properties.target.TrimEnd('/') -ne "$url/mcp") {
        throw "$($entry.App): expected an agent-identity connection for this exact endpoint and audience."
    }
    Write-Host "$($entry.App): healthy; anonymous/wrong-audience 401; exact agent principal; secretless connection"
}
$api = az containerapp show -g "$NamePrefix-platform" -n "$NamePrefix-api" --query properties.configuration.ingress.fqdn -o tsv
if ($LASTEXITCODE -ne 0) { throw 'Could not read API hostname.' }
$response = Invoke-WebRequest "https://$api/jobs/" -Method Post -ContentType application/json `
    -Body '{"fileName":"access-check.jpg"}' -SkipHttpErrorCheck -TimeoutSec 30
if ($response.StatusCode -ne 401) { throw "Anonymous API create: expected 401, received $($response.StatusCode)." }
foreach ($path in '/diagnostics/scans/', '/diagnostics/scans/anonymous-check/original',
    '/allowance/', '/admin/allowances/00000000-0000-0000-0000-000000000001') {
    foreach ($headers in @(@{}, @{ Authorization = "Bearer $token" })) {
        $denied = Invoke-WebRequest "https://$api$path" -Headers $headers -SkipHttpErrorCheck -TimeoutSec 30
        if ($denied.StatusCode -ne 401) { throw "Anonymous/wrong-audience $path must return 401." }
    }
}
Write-Host 'Scan diagnostics and allowances: anonymous and wrong-audience reads return 401.'
$webConfig = Invoke-RestMethod "https://$api/web-config" -TimeoutSec 30
if ($webConfig.enabled) {
    $website = Invoke-WebRequest "https://$api/" -TimeoutSec 30
    if ($website.Content -notmatch 'Pocket scanner') { throw 'Mobile website was not served by the API.' }
    foreach ($path in '/collection/', '/jobs/anonymous-check/cards.csv') {
        $denied = Invoke-WebRequest "https://$api$path" -SkipHttpErrorCheck -TimeoutSec 30
        if ($denied.StatusCode -ne 401) { throw "Anonymous $path must return 401." }
    }
    Write-Host "Mobile website: HTTPS landing page available; collection and CSV endpoints reject anonymous requests."
}
$worker = az identity show -g "$NamePrefix-platform" -n "$NamePrefix-worker-id" --query principalId -o tsv
if ($LASTEXITCODE -ne 0) { throw 'Could not read worker identity.' }
$roles = @(az role assignment list --assignee $worker --all -o json | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect worker roles.' }
if ($roles | Where-Object { $_.roleDefinitionId.EndsWith('/53ca6127-db72-4b80-b1b0-d745d6d5456d') }) {
    throw 'Worker still holds authoring-capable Foundry User. Remove obsolete classic grants after verifying Consumer access.'
}
Write-Host 'API: anonymous job creation 401. Two-user ownership and end-to-end scan remain separate checks.'
