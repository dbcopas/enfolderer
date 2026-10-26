<#
.SYNOPSIS
    Creates the dedicated AI Gateway preview and connects the real demo model and MCP traffic.
.DESCRIPTION
    Four gateways separate model, geometry, MTG and Pokemon trust domains because preview
    runtime keys can access every asset in a gateway. Credentials remain in memory and are
    written only to Container Apps secrets or encrypted Foundry project connections.
#>
#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $NamePrefix,
    [Parameter(Mandatory)] [string] $SubscriptionId
)
. (Join-Path $PSScriptRoot 'demo-lifecycle.ps1')
. (Join-Path $PSScriptRoot 'ai-gateway-preview.ps1')
if ($NamePrefix -notmatch '^[a-z0-9][a-z0-9-]{1,10}[a-z0-9]$') { throw 'Invalid deployment prefix.' }
$identity = Get-DemoIdentity $SubscriptionId
Assert-DemoOwner $identity $SubscriptionId $NamePrefix
$group = Invoke-DemoAz @('group', 'show', '--subscription', $SubscriptionId, '-n', "$NamePrefix-platform")
if ($group.location -notin @('swedencentral', 'eastus2')) { throw 'Dedicated AI Gateway preview requires Sweden Central or East US 2.' }
$accounts = @(Invoke-DemoAz @('cognitiveservices', 'account', 'list', '--subscription', $SubscriptionId,
    '-g', "$NamePrefix-platform") | Where-Object kind -eq 'AIServices')
if ($accounts.Count -ne 1) { throw 'Expected one dedicated Foundry account in the platform group.' }
$account = $accounts[0]
$models = @(Invoke-DemoAz @('cognitiveservices', 'account', 'deployment', 'list', '--subscription', $SubscriptionId,
    '-g', "$NamePrefix-platform", '-n', $account.name) | Where-Object name -eq 'gpt-4o')
if ($models.Count -ne 1) { throw 'The demo requires its existing gpt-4o deployment.' }
if (-not $PSCmdlet.ShouldProcess("$SubscriptionId / $NamePrefix", 'Provision four dedicated preview gateways, govern models/tools and reconnect the demo')) { return }
$gateways = @{}
$createGateways = $false
foreach ($suffix in @('', '-geo', '-mtg', '-pkm')) {
    $id = Get-PreviewGatewayId $SubscriptionId $NamePrefix $suffix
    Write-Host "Checking dedicated preview gateway $NamePrefix-aigw$suffix"
    $existing = Get-OptionalPreviewGateway $SubscriptionId $id
    if ($existing) { Assert-PreviewGateway $existing $SubscriptionId $NamePrefix $identity.User }
    if (-not $existing) { $createGateways = $true }
}
$insightsId = "/subscriptions/$SubscriptionId/resourceGroups/$NamePrefix-platform/providers/Microsoft.Insights/components/$NamePrefix-gateway-insights"
try { $insights = Invoke-PreviewArm $SubscriptionId "$insightsId`?api-version=2020-02-02" }
catch {
    if (-not $_.Exception.PSObject.Properties['Response'] -or
        $null -eq $_.Exception.Response -or $_.Exception.Response.StatusCode -ne 404) { throw }
    $createGateways = $true
}
if ($createGateways) {
    $null = Invoke-DemoAz @('deployment', 'group', 'create', '--subscription', $SubscriptionId,
        '-g', "$NamePrefix-platform", '-n', 'enfolderer-ai-gateway-preview',
        '--template-file', (Join-Path $PSScriptRoot '..\infra\ai-gateway-preview.bicep'),
        '--parameters', "namePrefix=$NamePrefix", "ownerObjectId=$($identity.User)")
}
foreach ($suffix in @('', '-geo', '-mtg', '-pkm')) {
    $id = Get-PreviewGatewayId $SubscriptionId $NamePrefix $suffix
    $gateway = Invoke-PreviewArm $SubscriptionId "$id`?api-version=$script:GatewayPreviewVersion"
    Assert-PreviewGateway $gateway $SubscriptionId $NamePrefix $identity.User
    if ($gateway.properties.provisioningState -ne 'Succeeded') { throw "Gateway $id is not ready." }
    $gateways[$suffix] = $gateway
}
$insights = Invoke-PreviewArm $SubscriptionId "$insightsId`?api-version=2020-02-02"
if ($insights.tags.enfoldererPrefix -ne $NamePrefix -or $insights.tags.enfoldererOwner -ne $identity.User) {
    throw 'Gateway monitoring resource is not owned by the selected deployment.'
}
foreach ($gateway in $gateways.Values) {
    $null = Set-PreviewAsset $SubscriptionId "$($gateway.id)/workspaces/default/telemetryExporters/appinsights" @{
        kind = 'ApplicationInsights'; payloadCapture = $false
        applicationInsights = @{ resourceId = $insightsId; connectionString = $insights.properties.ConnectionString }
    }
}

$modelGateway = $gateways['']
Write-Host 'Configuring native model catalogue, token/content policies and server-side key.'
$roleName = New-PreviewRoleName $account.id $modelGateway.identity.principalId
$existingRoles = @(Invoke-DemoAz @('role', 'assignment', 'list', '--subscription', $SubscriptionId,
    '--scope', $account.id, '--fill-principal-name', 'false') | Where-Object {
        $_.principalId -eq $modelGateway.identity.principalId -and
        ($_.roleDefinitionId -split '/')[-1] -eq '53ca6127-db72-4b80-b1b0-d745d6d5456d'
    })
if (-not $existingRoles.Count) {
    $null = Invoke-PreviewArm $SubscriptionId "$($account.id)/providers/Microsoft.Authorization/roleAssignments/$roleName`?api-version=2022-04-01" PUT @{
    properties = @{
        roleDefinitionId = "/subscriptions/$SubscriptionId/providers/Microsoft.Authorization/roleDefinitions/53ca6127-db72-4b80-b1b0-d745d6d5456d"
        principalId = $modelGateway.identity.principalId; principalType = 'ServicePrincipal'
        }
    }
}
$providerId = "$($modelGateway.id)/workspaces/default/modelProviders/foundry"
$null = Set-PreviewAsset $SubscriptionId $providerId @{
    kind = 'Foundry'; displayName = 'Enfolderer public card knowledge'
    foundry = @{
        endpoint = "https://$($account.properties.customSubDomainName).openai.azure.com"
        resourceIds = @($account.id)
        authentication = @{ kind = 'ManagedIdentity'; managedIdentity = @{ resource = 'https://cognitiveservices.azure.com' } }
    }
}
$modelId = "$providerId/models/gpt-4o"
$null = Set-PreviewAsset $SubscriptionId $modelId @{
    displayName = 'gpt-4o'; supportedEndpoints = @('/chat/completions', '/responses')
    deployment = @{ resourceId = $models[0].id; modelName = 'gpt-4o'; modelVersion = $models[0].properties.model.version }
    policies = @(
        @{ type = 'tokenLimit'; count = 30000; period = 'minute'; counterKey = @('identity'); scope = 'resource' }
        @{ type = 'contentSafety'; hateSeverity = 'Medium'; violenceSeverity = 'High'; sexualSeverity = 'Medium'; selfHarmSeverity = 'Medium' }
    )
}
$null = Set-PreviewAsset $SubscriptionId "$($modelGateway.id)/workspaces/default/aliases/gpt-4o" @{
    aliasName = 'gpt-4o'; backendModels = @(@{ resourceId = $modelId })
}
$modelKey = Get-PreviewRuntimeKey $SubscriptionId $modelGateway.id 'scan-questions'
$api = Invoke-DemoAz @('containerapp', 'show', '--subscription', $SubscriptionId,
    '-g', "$NamePrefix-platform", '-n', "$NamePrefix-api")
$modelOrigin = "https://$($modelGateway.properties.frontend.defaultHostname)"
Set-PreviewAppConfiguration $SubscriptionId $api @{
    'ScanKnowledge__GatewayEnabled' = 'true'
    'ScanKnowledge__GatewayRequired' = 'true'
    'ScanKnowledge__GatewayEndpoint' = $modelOrigin
    'ScanKnowledge__GatewayModel' = 'gpt-4o'
} @{ 'ScanKnowledge__GatewayApiKey' = @{ name = 'ai-gateway-model-key'; value = $modelKey } }
$modelKey = $null

$toolUrls = @{}
$toolKeys = @{}
$audience = $null
$originalCallers = @()
foreach ($entry in @(
    @{ Suffix = '-geo'; Group = 'cardgeo'; Server = 'mcp-imaging' },
    @{ Suffix = '-mtg'; Group = 'cardid'; Server = 'mcp-cardcatalog-mtg' },
    @{ Suffix = '-pkm'; Group = 'cardid'; Server = 'mcp-cardcatalog-pokemon' }
)) {
    Write-Host "Configuring isolated preview MCP server $($entry.Server)"
    $app = Invoke-DemoAz @('containerapp', 'show', '--subscription', $SubscriptionId,
        '-g', "$NamePrefix-$($entry.Group)", '-n', "$NamePrefix-$($entry.Server)")
    $envs = $app.properties.template.containers[0].env
    $audience = ($envs | Where-Object name -eq 'McpServer__Audience').value
    $caller = ($envs | Where-Object name -eq 'McpServer__AllowedCallerObjectIds').value
    $originalCaller = ($caller -split ',')[0]
    if (-not [guid]::TryParse($originalCaller, [ref]([guid]::Empty)) -or
        -not [guid]::TryParse($audience, [ref]([guid]::Empty)) -or
        $originalCaller -eq [guid]::Empty.ToString() -or $audience -eq [guid]::Empty.ToString()) {
        throw 'MCP identity bootstrap is incomplete.'
    }
    if ($originalCaller -in $originalCallers) { throw 'MCP backends must retain distinct original agent identities, not a shared caller.' }
    $originalCallers += $originalCaller
    $gateway = $gateways[$entry.Suffix]
    Set-PreviewAppConfiguration $SubscriptionId $app @{
        'McpServer__AllowedCallerObjectIds' = "$originalCaller,$($gateway.identity.principalId)"
    } @{}
    $fqdn = $app.properties.configuration.ingress.fqdn
    if ($fqdn -notmatch '^[a-z0-9.-]+\.azurecontainerapps\.io$') { throw 'Unexpected MCP backend host.' }
    $null = Set-PreviewAsset $SubscriptionId "$($gateway.id)/workspaces/default/toolServers/$($entry.Server)" @{
        displayName = $entry.Server
        type = 'mcp'
        failureMode = 'failClosed'
        description = "Dedicated $($entry.Group) trust domain; backend validates this gateway identity."
        endpoints = @(@{
            kind = 'mcp'; required = $true
            mcp = @{ url = "https://$fqdn/mcp"; transport = 'streamableHttp' }
            credentials = @{ type = 'managedIdentity'; managedIdentity = @{ resource = "api://$audience" } }
        })
        policies = @(@{ type = 'requestRateLimit'; callsPerPeriod = 120; periodSeconds = 60; counterKey = @('identity'); scope = 'resource' })
    }
    $toolUrls[$entry.Server] = "https://$($gateway.properties.frontend.defaultHostname)/default/toolservers/$($entry.Server)/mcp"
    $toolKeys[$entry.Server] = Get-PreviewRuntimeKey $SubscriptionId $gateway.id 'foundry-agent'
}
$previous = (Invoke-DemoAz @('account', 'show')).id
try {
    $null = Invoke-DemoAz @('account', 'set', '--subscription', $SubscriptionId)
    $provision = Join-Path $PSScriptRoot '..\agents\provision-foundry.ps1'
    $origin = "https://$($account.properties.customSubDomainName).services.ai.azure.com/api/projects"
    $null = & $provision -ProjectEndpoint "$origin/cardgeo" -Path (Join-Path $PSScriptRoot '..\agents\cardgeo') `
        -Only CardBoundaryAgent -McpServerUrl $toolUrls -McpAudience $audience -McpGatewayApiKeys $toolKeys
    $null = & $provision -ProjectEndpoint "$origin/cardid" -Path (Join-Path $PSScriptRoot '..\agents\cardid') `
        -Only MtgCardIdAgent,PokemonCardIdAgent -McpServerUrl $toolUrls -McpAudience $audience -McpGatewayApiKeys $toolKeys
}
finally {
    $toolKeys.Clear()
    $null = Invoke-DemoAz @('account', 'set', '--subscription', $previous)
}
Write-Output "Dedicated preview configured: $modelOrigin (model synthesis), three isolated MCP gateways. Search planning and named-agent internal model calls remain Foundry-managed."
