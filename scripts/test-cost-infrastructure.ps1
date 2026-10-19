<#
.SYNOPSIS
    Compiles and checks the cost branch's infrastructure contracts without deploying resources.
#>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$temp = [IO.Path]::GetTempFileName()
try {
    az bicep build --file (Join-Path $PSScriptRoot '..\infra\main.bicep') --outfile $temp
    if ($LASTEXITCODE -ne 0) { throw 'Bicep compilation failed.' }
    $template = Get-Content -Raw -LiteralPath $temp | ConvertFrom-Json -AsHashtable
    function Get-Resources([hashtable] $Template) {
        foreach ($resource in $Template.resources) {
            $resource
            if ($resource.type -eq 'Microsoft.Resources/deployments' -and $resource.properties.template) {
                Get-Resources $resource.properties.template
            }
        }
    }
    function Assert([bool] $Condition, [string] $Message) {
        if (-not $Condition) { throw $Message }
    }
    $resources = @(Get-Resources $template)
    $environments = @($resources | Where-Object type -eq 'Microsoft.App/managedEnvironments')
    Assert ($environments.Count -eq 1) 'Expected exactly one ACA environment.'
    Assert (-not $environments[0].properties.ContainsKey('vnetConfiguration')) 'Unexpected customer VNet.'
    Assert (@($environments[0].properties.workloadProfiles).Count -eq 1 `
        -and $environments[0].properties.workloadProfiles[0].workloadProfileType -eq 'Consumption') 'Expected Consumption only.'
    Assert (@($resources | Where-Object type -eq 'Microsoft.OperationalInsights/workspaces').Count -eq 1) 'Expected one workspace.'
    Assert (@($resources | Where-Object { $_.type -like 'Microsoft.Network/*' }).Count -eq 0) 'Private networking must not be deployed.'
    $apps = @($resources | Where-Object type -eq 'Microsoft.App/containerApps')
    # The MCP loop describes two apps across two module instances.
    Assert ($apps.Count -eq 4) 'Expected API, worker and two MCP module definitions.'
    $mcpModules = @($template.resources | Where-Object {
        $_.type -eq 'Microsoft.Resources/deployments' -and $_.properties.parameters.ContainsKey('servers')
    })
    $servers = @($mcpModules | ForEach-Object { $_.properties.parameters.servers.value })
    Assert ($servers.Count -eq 2 -and 'mcp-imaging' -in $servers.name `
        -and 'mcp-cardcatalog-mtg' -in $servers.name) 'Only imaging and MTG MCP servers may be deployed.'
    Assert (-not $template.parameters.ContainsKey('pokemonAgentId') `
        -and -not $template.parameters.ContainsKey('pokemonAgentPrincipalId')) 'Pokemon agents must not be deployable.'
    foreach ($app in $apps) {
        Assert ($app.properties.environmentId -eq '[parameters(''environmentId'')]') 'App is not using the supplied shared environment.'
        Assert ($app.identity.type -eq 'UserAssigned') 'Every app must retain a managed identity.'
    }
    $environmentInputs = @($template.resources | Where-Object {
        $_.type -eq 'Microsoft.Resources/deployments' -and $_.properties.parameters.ContainsKey('environmentId')
    } | ForEach-Object { $_.properties.parameters.environmentId.value } | Select-Object -Unique)
    Assert ($environmentInputs.Count -eq 1) 'Hosting and both MCP modules must receive the same environment.'
    $api = @($apps | Where-Object { $_.name -like '*-api*' })[0]
    $worker = @($apps | Where-Object { $_.name -like '*-worker*' })[0]
    $agentSettings = [regex]::Matches(
        ($worker.properties.template.containers[0].env | ConvertTo-Json -Depth 20),
        'ScanPipeline__IdentificationAgentIds__\w+')
    Assert ($agentSettings.Count -eq 1 -and $agentSettings[0].Value -eq 'ScanPipeline__IdentificationAgentIds__mtg') 'Only the MTG worker agent may be configured.'
    Assert ($api.properties.template.scale.minReplicas -eq 0) 'API must scale to zero.'
    Assert (-not $worker.properties.configuration.ContainsKey('ingress')) 'Worker must not expose ingress.'
    Assert ($worker.properties.template.scale.minReplicas -eq 0 -and $worker.properties.template.scale.maxReplicas -eq 1) 'Worker must scale 0..1.'
    Assert ($worker.properties.template.containers[0].resources.cpu -eq '[json(''1.0'')]' `
        -and $worker.properties.template.containers[0].resources.memory -eq '2Gi') 'Worker must retain validated full-photo memory headroom.'
    $scaler = $worker.properties.template.scale.rules[0].custom
    Assert ($scaler.type -eq 'azure-queue' -and $scaler.identity -eq '[parameters(''workerIdentityId'')]') 'Queue scaler must use the worker identity.'
    Assert ($scaler.metadata.queueName -eq 'scan-jobs' -and $scaler.metadata.queueLength -eq '1' `
        -and $scaler.metadata.queueLengthStrategy -eq 'all') 'Queue scaler must include in-flight leased jobs.'
    $storage = @($resources | Where-Object type -eq 'Microsoft.Storage/storageAccounts')[0]
    Assert ($storage.properties.publicNetworkAccess -eq 'Enabled' -and $storage.properties.allowSharedKeyAccess -eq $false `
        -and $storage.properties.allowBlobPublicAccess -eq $false -and $storage.properties.supportsHttpsTrafficOnly) 'Storage must use authenticated public HTTPS only.'
    foreach ($container in @($resources | Where-Object type -eq 'Microsoft.Storage/storageAccounts/blobServices/containers')) {
        Assert ($container.properties.publicAccess -eq 'None') 'Anonymous container access must remain disabled.'
    }
    $cosmos = @($resources | Where-Object type -eq 'Microsoft.DocumentDB/databaseAccounts')[0]
    Assert ($cosmos.properties.disableLocalAuth -and $cosmos.properties.publicNetworkAccess -eq 'Enabled') 'Cosmos must use Entra-only public access.'
    Assert ($template.parameters.cosmosFreeTier.defaultValue -eq $true) 'Cosmos free tier must be the default.'
    Assert ($cosmos.properties.enableFreeTier -eq '[parameters(''enableFreeTier'')]') 'Free-tier setting must reach Cosmos.'
    $database = @($resources | Where-Object type -eq 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases')[0]
    Assert ($database.properties.options.throughput -eq 400) 'Preserve shared 400 RU/s.'
    Assert (@($resources | Where-Object {
        $_.type -eq 'Microsoft.Authorization/roleAssignments' -and $_.properties.principalType -eq 'Group'
    }).Count -eq 0) 'No separate-team owner groups should be required.'
    Write-Host 'Cost infrastructure checks passed: one Consumption environment/workspace, no private networking, identity-only access, free-tier defaults and queue wakeup.'
}
finally { Remove-Item -LiteralPath $temp }
