<#
.SYNOPSIS
    Discovers an owned Enfolderer deployment and hibernates its recreatable infrastructure.
.DESCRIPTION
    Deletes ALL active scans, crops and generated outputs, cancels work, removes the job queue,
    container apps, their exclusive environments, data private endpoints and validated
    dedicated Search when configured, and owner-tagged dedicated AI Gateway preview resources. Preserves
    collections, Cosmos (including credit balances), registry images, identities, Foundry and security.
    Storage soft-delete/version retention can delay physical reclamation and billing savings.
    Does not open private storage to the internet or use account keys.
    Requires effective Owner on all three deployment groups, Azure CLI and PowerShell 7.
    Discovery is confined to the signed-in tenant; multiple deployments require explicit selectors.
    Save the manifest on durable private storage: it contains the exact deployed template/config.
    Only tagged, prefix-named, RBAC-only Search explicitly configured by scanKnowledgeEndpoint
    in an owned deployment group is eligible. Shared consumers/indexes cause a safe refusal.
    KnowledgeConfigPath optionally supplies ModelEndpoint/ModelDeployment/ModelName as JSON
    if the knowledge-base model settings are unavailable. Restore rebuilds the public corpus.
.EXAMPLE
    .\scripts\stop-demo.ps1 -WhatIf
.EXAMPLE
    .\scripts\stop-demo.ps1 -SubscriptionId <id> -DeploymentName enfolderer-scan
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string] $SubscriptionId,
    [string] $DeploymentName,
    [string] $ManifestPath,
    # Optional recovery configuration: ModelEndpoint, ModelDeployment and ModelName.
    [string] $KnowledgeConfigPath
)
#Requires -Version 7.0
. (Join-Path $PSScriptRoot 'demo-lifecycle.ps1')
. (Join-Path $PSScriptRoot 'ai-gateway-preview.ps1')
$identity = Get-DemoIdentity -SubscriptionId $SubscriptionId
$target = Find-DemoDeployment $identity $SubscriptionId $DeploymentName
$subscription = $target.Subscription
$prefix = $target.Prefix
if (-not $ManifestPath) {
    $ManifestPath = Join-Path (Get-DemoStateDirectory $identity) "$subscription-$prefix.json"
}
$ManifestPath = [IO.Path]::GetFullPath($ManifestPath)
if (Test-Path -LiteralPath $ManifestPath) {
    $previous = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -Depth 100
    Assert-DemoManifest $previous $identity
    if ($previous.status -ne 'InfrastructureRestored') {
        throw "Manifest already exists: $ManifestPath. Restore it first; refusing to overwrite recovery state."
    }
    $ManifestPath = Join-Path (Split-Path -Parent $ManifestPath) "$subscription-$prefix-$([DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfff')).json"
}
$apps = @(Get-DemoApps $subscription $prefix)
$images = @($apps | ForEach-Object { $_.properties.template.containers[0].image })
$tags = @($images | ForEach-Object { ($_ -split ':')[-1] } | Select-Object -Unique)
if ($tags.Count -ne 1 -or $images.Where({ $_ -notmatch '/enfolderer/' }).Count) {
    throw 'Expected five Enfolderer services on one image tag; refusing unsupported topology.'
}
$template = Invoke-DemoAz @('deployment', 'sub', 'export', '--subscription', $subscription,
    '-n', $target.Deployment.name)
if (-not $template.parameters.PSObject.Properties['apiCustomDomains'] -or
    ($template | ConvertTo-Json -Depth 100) -match '"templateLink"\s*:') {
    throw 'Recovery requires a self-contained template with explicit custom-domain parameters.'
}
foreach ($parameter in $template.parameters.PSObject.Properties) {
    if ($parameter.Value.type -in @('secureString', 'secureObject')) {
        throw "Cannot persist secure parameter $($parameter.Name). This deployment needs a dedicated recovery workflow."
    }
}
$parameters = @{}
foreach ($parameter in $target.Deployment.properties.parameters.PSObject.Properties) {
    $parameters[$parameter.Name] = @{ value = $parameter.Value.value }
}
$parameters['imageTag'] = @{ value = $tags[0] }
$api = @($apps | Where-Object name -eq "$prefix-api")[0]
$domains = @()
if ($api.properties.configuration.ingress.PSObject.Properties['customDomains']) {
    $domains = @($api.properties.configuration.ingress.customDomains | Where-Object { $_ })
}
# Certificates belong to the deleted environment. Never replay their old IDs.
$parameters['apiCustomDomains'] = @{ value = @() }
$knowledgeConfig = $null
if ($KnowledgeConfigPath) { $knowledgeConfig = Get-Content -LiteralPath $KnowledgeConfigPath -Raw | ConvertFrom-Json -Depth 100 }
$search = Get-DemoSearchRecovery $identity $subscription $prefix $parameters $apps $knowledgeConfig
$previewGateway = Get-DemoPreviewRecovery $identity $subscription $prefix
$environments = @($apps | ForEach-Object { $_.properties.environmentId } | Select-Object -Unique)
if ($environments.Count -ne 3) { throw 'Expected three dedicated environments. Shared/consolidated layouts need a dedicated plan.' }
foreach ($environment in $environments) {
    $hosted = @(Invoke-DemoAz @('containerapp', 'list', '--subscription', $subscription) |
        Where-Object { $_.properties.environmentId -eq $environment })
    if (@($hosted | Where-Object { $_.id -notin $apps.id }).Count) {
        throw "Environment $environment hosts other apps; refusing deletion."
    }
    $jobs = @(Invoke-DemoAz @('resource', 'list', '--subscription', $subscription,
        '--resource-type', 'Microsoft.App/jobs'))
    foreach ($job in $jobs) {
        $detail = Invoke-DemoAz @('resource', 'show', '--subscription', $subscription,
            '--ids', $job.id, '--api-version', '2024-03-01')
        if ($detail.properties.environmentId -eq $environment) {
            throw "Environment $environment hosts a Container Apps job; refusing deletion."
        }
    }
    if ($environment -notmatch "/resourceGroups/$([regex]::Escape($prefix))-(platform|cardgeo|cardid)/") {
        throw "Environment outside deployment groups: $environment"
    }
}
$resources = @(Invoke-DemoAz @('resource', 'list', '--subscription', $subscription, '-g', "$prefix-platform"))
$storage = @($resources | Where-Object { $_.type -eq 'Microsoft.Storage/storageAccounts' })
$cosmos = @($resources | Where-Object { $_.type -eq 'Microsoft.DocumentDB/databaseAccounts' })
if ($storage.Count -ne 1 -or $cosmos.Count -ne 1) { throw 'Expected one storage account and one Cosmos account.' }
$storageSetting = @($api.properties.template.containers[0].env | Where-Object name -eq 'ScanPlatform__StorageAccountUrl')
if ($storageSetting.Count -ne 1 -or
    ([uri]$storageSetting[0].value).Host -ne "$($storage[0].name).blob.core.windows.net") {
    throw 'API storage configuration does not match the selected storage account.'
}
$endpoints = @()
foreach ($resource in @($resources | Where-Object type -eq 'Microsoft.Network/privateEndpoints')) {
    $endpoint = Invoke-DemoAz @('network', 'private-endpoint', 'show', '--subscription', $subscription, '--ids', $resource.id)
    $connections = @($endpoint.privateLinkServiceConnections)
    if (@($connections | Where-Object {
        $_.privateLinkServiceId -in @($storage[0].id, $cosmos[0].id)
    }).Count) { $endpoints += $resource.id }
}
if ($endpoints.Count -ne 3) { throw 'Expected three data private endpoints; refusing unsupported topology.' }
$manifest = [ordered]@{
    schemaVersion = 1; ownerObjectId = $identity.User; tenantId = $identity.Tenant
    subscriptionId = $subscription; prefix = $prefix; deploymentName = $target.Deployment.name
    location = $target.Deployment.location; status = 'Prepared'; savedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    template = $template; parameters = $parameters; apps = $apps; environments = $environments
    privateEndpoints = $endpoints; storageId = $storage[0].id; customDomains = $domains
    search = $search
    previewGateway = $previewGateway
}
Write-Host "Selected owned deployment: $subscription / $($target.Deployment.name) / $prefix"
Write-Host 'Deletion plan:'
foreach ($id in @($apps.id) + $environments + $endpoints) { Write-Host "  $id" }
if ($search) {
    Write-Host "  $($search.id): dedicated public knowledge corpus (rebuild on restore)"
    foreach ($grant in $search.modelRoles) { Write-Host "  Exact old Search-to-model role: $($grant.id)" }
}
Write-Host "  $($storage[0].id): ALL scans, crops, outputs and queued work"
if ($previewGateway) {
    foreach ($gateway in $previewGateway.gateways) { Write-Host "  $($gateway.id): dedicated preview gateway (rebuild with fresh runtime credentials)" }
}
Write-Host 'Preserved/billable: collection snapshots, Cosmos/credits, registry, Foundry, logs, DNS, Consumption APIM (no fixed capacity fee) and security settings.'
$action = 'IRREVERSIBLY delete all scans/crops/outputs and queued work; delete apps, exclusive environments, private endpoints, dedicated preview gateways and validated dedicated Search (if configured)'
if (-not $PSCmdlet.ShouldProcess("$subscription / $prefix; recovery manifest $ManifestPath", $action)) { return }
New-Item -ItemType Directory -Path (Split-Path -Parent $ManifestPath) -Force | Out-Null
Save-DemoManifest $manifest $ManifestPath
try {
    # Delete the API first to stop intake; stopping this demo intentionally discards in-flight scans.
    foreach ($app in $apps) {
        $null = Invoke-DemoAz @('containerapp', 'delete', '--subscription', $subscription,
            '--ids', $app.id, '--yes')
    }
    if ($previewGateway) { Remove-DemoPreviewGateways $previewGateway $identity $subscription $prefix }
    foreach ($container in 'scans', 'crops', 'outputs') {
        $null = Invoke-DemoAz @('resource', 'delete', '--subscription', $subscription, '--ids',
            "$($storage[0].id)/blobServices/default/containers/$container", '--api-version', '2023-05-01')
    }
    $null = Invoke-DemoAz @('resource', 'delete', '--subscription', $subscription, '--ids',
        "$($storage[0].id)/queueServices/default/queues/scan-jobs", '--api-version', '2023-05-01')
    foreach ($environment in $environments) {
        $null = Invoke-DemoAz @('resource', 'delete', '--subscription', $subscription,
            '--ids', $environment, '--api-version', '2024-03-01')
    }
    foreach ($endpoint in $endpoints) {
        $null = Invoke-DemoAz @('resource', 'delete', '--subscription', $subscription,
            '--ids', $endpoint, '--api-version', '2023-11-01')
    }
    if ($search) {
        foreach ($grant in $search.modelRoles) {
            $null = Invoke-DemoAz @('role', 'assignment', 'delete', '--subscription', $subscription, '--ids', $grant.id)
        }
        $null = Invoke-DemoAz @('resource', 'delete', '--subscription', $subscription,
            '--ids', $search.id, '--api-version', '2025-05-01')
    }
    $remaining = @(Invoke-DemoAz @('resource', 'list', '--subscription', $subscription) |
        Where-Object { $_.id -in @($apps.id) + $environments + $endpoints + $(if ($search) { $search.id }) })
    if ($remaining.Count) { throw 'Some selected compute/network resources still exist; shutdown is incomplete.' }
    $manifest.status = 'Stopped'
    Save-DemoManifest $manifest $ManifestPath
}
catch {
    $manifest.status = 'StopFailed'
    Save-DemoManifest $manifest $ManifestPath
    throw
}
Write-Output "Stopped $prefix. Restore using: .\scripts\restore-demo.ps1 -ManifestPath `"$ManifestPath`""
Write-Output 'Retained: collections, Cosmos/credits/job metadata, registry images, Foundry, identities, DNS zones and security settings.'
Write-Output 'These retained services can still bill. Deleted blob containers can remain billable during configured soft-delete retention.'
