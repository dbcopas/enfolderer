Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-DemoAz {
    param([string[]] $Arguments)
    $text = & az @Arguments --only-show-errors -o json
    if ($LASTEXITCODE -ne 0) { throw "Azure command failed: az $($Arguments -join ' ')" }
    if ($text) { return ($text -join "`n") | ConvertFrom-Json -Depth 100 }
}

function Get-DemoIdentity {
    param([string] $SubscriptionId)
    $selector = @()
    if ($SubscriptionId) { $selector = @('--subscription', $SubscriptionId) }
    $account = Invoke-DemoAz (@('account', 'show') + $selector)
    if ($account.user.type -ne 'user') { throw 'Sign in as a human deployment owner with az login.' }
    $token = Invoke-DemoAz (@('account', 'get-access-token', '--resource', 'https://management.azure.com/') + $selector)
    try {
        $payload = $token.accessToken.Split('.')[1].Replace('-', '+').Replace('_', '/')
        $payload = $payload.PadRight($payload.Length + ((4 - $payload.Length % 4) % 4), '=')
        $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
        if (-not [guid]::TryParse($claims.oid, [ref]([guid]::Empty)) -or $claims.tid -ne $account.tenantId) {
            throw 'ARM token identity does not match the selected account tenant.'
        }
        [pscustomobject]@{ Tenant = $claims.tid; User = $claims.oid }
    }
    finally { $token = $null; $payload = $null; $claims = $null }
}

function Test-DemoOwner {
    param($Identity, [string] $Subscription, [string] $Prefix)
    foreach ($suffix in 'platform', 'cardgeo', 'cardid') {
        $scope = "/subscriptions/$Subscription/resourceGroups/$Prefix-$suffix"
        $grants = @(Invoke-DemoAz @('role', 'assignment', 'list', '--subscription', $Subscription,
            '--fill-principal-name', 'false', '--include-inherited', '--scope', $scope))
        if (@($grants | Where-Object {
            $_.principalId -eq $Identity.User -and
            ($_.roleDefinitionId -split '/')[-1] -eq '8e3af657-a8ff-443c-a75c-2fe8c4bcb635'
        }).Count) { continue }
        # Group expansion can require Graph; direct owners never need that fallback.
        $grants = @(Invoke-DemoAz @('role', 'assignment', 'list', '--subscription', $Subscription,
            '--assignee', $Identity.User, '--include-groups', '--fill-principal-name', 'false',
            '--include-inherited', '--scope', $scope))
        if (-not @($grants | Where-Object {
            ($_.roleDefinitionId -split '/')[-1] -eq '8e3af657-a8ff-443c-a75c-2fe8c4bcb635'
        }).Count) { return $false }
    }
    return $true
}

function Assert-DemoOwner {
    param($Identity, [string] $Subscription, [string] $Prefix)
    if (-not (Test-DemoOwner $Identity $Subscription $Prefix)) {
        throw "Current user must have effective Owner on all three $Prefix deployment groups."
    }
}

function Find-DemoDeployment {
    param($Identity, [string] $SubscriptionId, [string] $DeploymentName)
    $subscriptions = @(Invoke-DemoAz @('account', 'list') | Where-Object {
        $_.state -eq 'Enabled' -and $_.tenantId -eq $Identity.Tenant -and
        (-not $SubscriptionId -or $_.id -eq $SubscriptionId)
    })
    $found = @(
        foreach ($subscription in $subscriptions) {
            $deployments = @(Invoke-DemoAz @('deployment', 'sub', 'list', '--subscription', $subscription.id))
            foreach ($deployment in $deployments) {
                if ($DeploymentName -and $deployment.name -ne $DeploymentName) { continue }
                $parameters = $deployment.properties.parameters
                if (-not $parameters -or -not $parameters.PSObject.Properties['namePrefix'] -or
                    -not $parameters.PSObject.Properties['apiClientId'] -or
                    -not $parameters.PSObject.Properties['imageTag']) { continue }
                $prefix = [string]$parameters.namePrefix.value
                if ($prefix -notmatch '^[a-z0-9][a-z0-9-]{1,10}[a-z0-9]$') { continue }
                if ($deployment.properties.provisioningState -ne 'Succeeded') { continue }
                [pscustomobject]@{
                    Subscription = $subscription.id; Prefix = $prefix; Deployment = $deployment
                }
            }
        }
    )
    # Repeated successful deployments of one environment are one candidate, not separate demos.
    $candidates = @($found | Group-Object { "$($_.Subscription)/$($_.Prefix)" } | ForEach-Object {
        $_.Group | Sort-Object { $_.Deployment.properties.timestamp } -Descending | Select-Object -First 1
    } | Where-Object { Test-DemoOwner $Identity $_.Subscription $_.Prefix })
    if ($candidates.Count -ne 1) {
        $choices = ($candidates | ForEach-Object {
            "$($_.Subscription) / $($_.Deployment.name) / $($_.Prefix)"
        }) -join "`n"
        throw "Expected one discoverable deployment; found $($candidates.Count). Specify -SubscriptionId and -DeploymentName.`n$choices"
    }
    Assert-DemoOwner $Identity $candidates[0].Subscription $candidates[0].Prefix
    $candidates[0]
}

function Get-DemoApps {
    param([string] $Subscription, [string] $Prefix)
    foreach ($entry in @(
        @('platform', 'api'), @('platform', 'worker'), @('cardgeo', 'mcp-imaging'),
        @('cardid', 'mcp-cardcatalog-mtg'), @('cardid', 'mcp-cardcatalog-pokemon')
    )) {
        Invoke-DemoAz @('containerapp', 'show', '--subscription', $Subscription,
            '-g', "$Prefix-$($entry[0])", '-n', "$Prefix-$($entry[1])")
    }
}

function Get-DemoStateDirectory {
    param($Identity)
    Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Enfolderer\DemoLifecycle\$($Identity.Tenant)\$($Identity.User)"
}

function Save-DemoManifest {
    param($Manifest, [string] $Path)
    $temporary = "$Path.tmp"
    $Manifest | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Assert-DemoManifest {
    param($Manifest, $Identity)
    if ($Manifest.schemaVersion -ne 1 -or $Manifest.ownerObjectId -ne $Identity.User -or
        $Manifest.tenantId -ne $Identity.Tenant) { throw 'Manifest belongs to another user/tenant or has an unsupported format.' }
    if ($Manifest.prefix -notmatch '^[a-z0-9][a-z0-9-]{1,10}[a-z0-9]$' -or
        -not [guid]::TryParse($Manifest.subscriptionId, [ref]([guid]::Empty))) {
        throw 'Invalid manifest deployment scope.'
    }
    Assert-DemoOwner $Identity $Manifest.subscriptionId $Manifest.prefix
}

function Wait-DemoHealth {
    param([string] $Url, [int] $Attempts = 30)
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $health = Invoke-RestMethod "$($Url.TrimEnd('/'))/healthz" -TimeoutSec 15
            if ($health.status -eq 'ok') { return }
            $failure = 'health response did not report ok'
        }
        catch { $failure = $_.Exception.Message }
        if ($attempt -lt $Attempts) { Start-Sleep -Seconds 10 }
    }
    throw "Health check failed for $Url : $failure"
}

function Get-DemoProperty {
    param($Object, [string] $Name, $Default = $null)
    if ($Object -is [Collections.IDictionary] -and $Object.Contains($Name)) { return $Object[$Name] }
    if ($null -ne $Object -and $Object.PSObject.Properties[$Name]) { return $Object.$Name }
    return $Default
}

function Invoke-DemoSearchRead {
    param([string] $Subscription, [string] $Endpoint, [string] $Path)
    $token = Invoke-DemoAz @('account', 'get-access-token', '--subscription', $Subscription,
        '--scope', 'https://search.azure.com/.default')
    try {
        Invoke-RestMethod "$($Endpoint.TrimEnd('/'))/$Path`?api-version=2026-08-01-preview" `
            -Headers @{ Authorization = "Bearer $($token.accessToken)" } -TimeoutSec 30
    }
    finally { $token = $null }
}

function Assert-DemoSearchManifest {
    param($Search, [string] $Subscription, [string] $Prefix)
    $base = "/subscriptions/$Subscription/resourceGroups/"
    if ($Search.id -notmatch "^$([regex]::Escape($base))$([regex]::Escape($Prefix))-(platform|cardgeo|cardid)/providers/Microsoft.Search/searchServices/($([regex]::Escape($Prefix))-[a-z0-9-]+)$" -or
        $Search.endpoint.TrimEnd('/') -ne "https://$($Matches[2]).search.windows.net") {
        throw 'Search recovery scope is not a dedicated deployment resource.'
    }
    if ($Search.resource.tags.enfoldererPrefix -ne $Prefix -or
        $Search.resource.tags.enfoldererPurpose -ne 'scan-knowledge' -or
        -not $Search.resource.properties.disableLocalAuth -or
        $Search.resource.name -ne ($Search.id -split '/')[-1] -or
        $Search.resource.type -ne 'Microsoft.Search/searchServices') {
        throw 'Search recovery ownership/configuration is invalid.'
    }
    foreach ($grant in $Search.modelRoles) {
        if ($grant.scope -ne $Search.modelId -or $grant.principalId -ne $Search.principalId -or
            $grant.roleDefinitionId -notmatch '/roleDefinitions/(a001fd3d-188f-4b5d-821b-7da978bf7442|a97b65f3-24c7-4388-baec-2e87135dc908)$' -or
            $grant.id -notmatch "^$([regex]::Escape($Search.modelId))/providers/Microsoft.Authorization/roleAssignments/[a-f0-9-]+$") {
            throw 'Search model grant does not exactly match the saved model scope and Search principal.'
        }
    }
    if ($Search.modelId -notmatch "^/subscriptions/$([regex]::Escape($Subscription))/resourceGroups/[^/]+/providers/Microsoft.CognitiveServices/accounts/[^/]+$") {
        throw 'Search synthesis model must be in the selected subscription.'
    }
}

function Get-DemoSearchRecovery {
    param($Identity, [string] $Subscription, [string] $Prefix, $Parameters, $Apps, $KnowledgeConfig)
    $entry = Get-DemoProperty $Parameters 'scanKnowledgeEndpoint'
    if (-not $entry -or -not $entry.value) { return $null }
    $endpoint = [string]$entry.value
    $uri = [uri]$endpoint
    if ($uri.Scheme -ne 'https' -or $uri.Host -notmatch '^([a-z0-9-]+)\.search\.windows\.net$' -or
        $uri.AbsolutePath -ne '/' -or -not $uri.IsDefaultPort -or $uri.Query -or $uri.Fragment -or $uri.UserInfo) {
        throw 'scanKnowledgeEndpoint must be an Azure Search HTTPS origin.'
    }
    $name = $uri.Host.Split('.')[0]
    $services = @(Invoke-DemoAz @('resource', 'list', '--subscription', $Subscription,
        '--resource-type', 'Microsoft.Search/searchServices') | Where-Object name -eq $name)
    if ($services.Count -ne 1) { throw 'Configured Search service cannot be uniquely resolved.' }
    $service = Invoke-DemoAz @('resource', 'show', '--subscription', $Subscription,
        '--ids', $services[0].id, '--api-version', '2025-05-01')
    if ($service.identity.type -ne 'SystemAssigned' -or
        @(Get-DemoProperty $service.properties 'privateEndpointConnections' @()).Count) {
        throw 'Search requires a dedicated system identity without private endpoint consumers.'
    }
    $resource = [ordered]@{
        type = 'Microsoft.Search/searchServices'; apiVersion = '2025-05-01'
        name = $service.name; location = $service.location; tags = $service.tags; sku = $service.sku
        identity = @{ type = 'SystemAssigned' }; properties = [ordered]@{}
    }
    # Copy only writable service properties, not ARM provisioning/status fields.
    foreach ($key in 'replicaCount', 'partitionCount', 'hostingMode', 'disableLocalAuth',
        'publicNetworkAccess', 'networkRuleSet', 'semanticSearch',
        'authOptions', 'computeType', 'dataExfiltrationProtections') {
        if ($service.properties.PSObject.Properties[$key]) {
            $resource.properties[$key] = $service.properties.$key
        }
    }
    $cmk = Get-DemoProperty $service.properties 'encryptionWithCmk'
    if ($cmk -and $cmk.PSObject.Properties['enforcement']) {
        $resource.properties['encryptionWithCmk'] = @{ enforcement = $cmk.enforcement }
    }
    $baseName = 'scan-cards'; $sourceName = 'scryfall'; $indexName = 'scryfall'
    foreach ($mapping in @(@('scanKnowledgeBase', 'baseName'), @('scanKnowledgeSource', 'sourceName'), @('scanKnowledgeIndex', 'indexName'))) {
        $setting = Get-DemoProperty $Parameters $mapping[0]
        if ($setting -and $setting.value) {
            switch ($mapping[1]) {
                'baseName' { $baseName = [string]$setting.value }
                'sourceName' { $sourceName = [string]$setting.value }
                'indexName' { $indexName = [string]$setting.value }
            }
        }
    }
    foreach ($objectName in $baseName, $sourceName, $indexName) {
        if ($objectName -notmatch '^[a-z0-9][a-z0-9-]{1,127}$') { throw 'Invalid Search knowledge object name.' }
    }
    $recovery = [pscustomobject]@{
        id = $service.id; endpoint = $endpoint.TrimEnd('/'); resource = [pscustomobject]$resource
        principalId = $service.identity.principalId; modelId = ''; modelRoles = @(); searchRoles = @()
        knowledgeBase = $baseName; knowledgeSource = $sourceName; index = $indexName; config = $null
    }
    # Validate ownership before inspecting the data plane. A tagged, prefix-named resource
    # in an owner-checked app group is required; an endpoint alone is never deletion authority.
    $recovery.modelId = "/subscriptions/$Subscription/resourceGroups/placeholder/providers/Microsoft.CognitiveServices/accounts/placeholder"
    Assert-DemoSearchManifest $recovery $Subscription $Prefix
    foreach ($path in 'indexes', 'knowledgesources', 'knowledgebases', 'indexers', 'datasources', 'skillsets') {
        $objects = @( (Invoke-DemoSearchRead $Subscription $endpoint $path).value )
        $expected = switch ($path) {
            'indexes' { $indexName }; 'knowledgesources' { $sourceName }; 'knowledgebases' { $baseName }; default { $null }
        }
        if (@($objects | Where-Object { $_.name -ne $expected }).Count) {
            throw "Search has other $path consumers; refusing deletion."
        }
    }
    if ($KnowledgeConfig) {
        $config = $KnowledgeConfig
    }
    else {
        $kb = Invoke-DemoSearchRead $Subscription $endpoint "knowledgebases/$baseName"
        if (@($kb.models).Count -ne 1) { throw 'Expected exactly one synthesis model. Supply explicit -KnowledgeConfigPath for a damaged knowledge base.' }
        $model = $kb.models[0].azureOpenAIParameters
        $config = [pscustomobject]@{ ModelEndpoint = $model.resourceUri; ModelDeployment = $model.deploymentId; ModelName = $model.modelName }
    }
    $modelUri = [uri]$config.ModelEndpoint
    if ($modelUri.Scheme -ne 'https' -or $modelUri.Host -notmatch '^([a-z0-9-]+)\.(openai\.azure\.com|cognitiveservices\.azure\.com|services\.ai\.azure\.com)$' -or
        $modelUri.AbsolutePath -ne '/' -or -not $modelUri.IsDefaultPort -or $modelUri.Query -or $modelUri.Fragment -or $modelUri.UserInfo -or
        -not $config.ModelDeployment -or -not $config.ModelName) { throw 'Invalid saved synthesis model configuration.' }
    $modelAccounts = @(Invoke-DemoAz @('resource', 'list', '--subscription', $Subscription,
        '--resource-type', 'Microsoft.CognitiveServices/accounts') | Where-Object name -eq $modelUri.Host.Split('.')[0])
    if ($modelAccounts.Count -ne 1) { throw 'Cannot resolve the synthesis model account in this subscription.' }
    $recovery.modelId = $modelAccounts[0].id
    $recovery.config = $config
    $recovery.modelRoles = @(Invoke-DemoAz @('role', 'assignment', 'list', '--subscription', $Subscription,
        '--scope', $recovery.modelId, '--fill-principal-name', 'false') | Where-Object {
        $_.scope -eq $recovery.modelId -and $_.principalId -eq $recovery.principalId -and
        ($_.roleDefinitionId -split '/')[-1] -in @('a001fd3d-188f-4b5d-821b-7da978bf7442', 'a97b65f3-24c7-4388-baec-2e87135dc908')
    })
    if (-not $recovery.modelRoles.Count) { throw 'Expected an exact scoped Search-to-model inference grant.' }
    $api = @($Apps | Where-Object name -eq "$Prefix-api")[0]
    $allowed = @($Identity.User, $recovery.principalId)
    $apiIdentity = Get-DemoProperty $api 'identity'
    $systemPrincipal = Get-DemoProperty $apiIdentity 'principalId'
    if ($systemPrincipal) { $allowed += $systemPrincipal }
    $userIdentities = Get-DemoProperty $apiIdentity 'userAssignedIdentities'
    if ($userIdentities) {
        foreach ($property in $userIdentities.PSObject.Properties) {
            $assigned = Invoke-DemoAz @('resource', 'show', '--subscription', $Subscription,
                '--ids', $property.Name, '--api-version', '2023-01-31')
            $allowed += $assigned.properties.principalId
        }
    }
    $recovery.searchRoles = @(Invoke-DemoAz @('role', 'assignment', 'list', '--subscription', $Subscription,
        '--scope', $service.id, '--fill-principal-name', 'false') | Where-Object { $_.scope -eq $service.id })
    if (@($recovery.searchRoles | Where-Object { $_.principalId -notin $allowed }).Count) {
        throw 'Search grants access to other consumers; refusing deletion.'
    }
    $allApps = @(Invoke-DemoAz @('containerapp', 'list', '--subscription', $Subscription))
    foreach ($app in $allApps) {
        if ($app.id -notin $Apps.id -and ($app.properties | ConvertTo-Json -Depth 100) -match [regex]::Escape($uri.Host)) {
            throw 'Search endpoint is referenced by another app; refusing deletion.'
        }
    }
    Assert-DemoSearchManifest $recovery $Subscription $Prefix
    return $recovery
}

function Restore-DemoSearch {
    param($Search, [string] $Subscription, [string] $Work)
    $resources = @($Search.resource)
    foreach ($grant in $Search.searchRoles) {
        $principal = $grant.principalId
        if ($principal -eq $Search.principalId) {
            $principal = "[reference(resourceId('Microsoft.Search/searchServices', '$($Search.resource.name)'), '2025-05-01', 'Full').identity.principalId]"
        }
        $resources += @{
            type = 'Microsoft.Authorization/roleAssignments'; apiVersion = '2022-04-01'
            name = ($grant.id -split '/')[-1]; scope = "[resourceId('Microsoft.Search/searchServices', '$($Search.resource.name)')]"
            dependsOn = @("[resourceId('Microsoft.Search/searchServices', '$($Search.resource.name)')]")
            properties = @{ principalId = $principal; principalType = $grant.principalType; roleDefinitionId = $grant.roleDefinitionId }
        }
    }
    $path = Join-Path $Work 'search.json'
    @{ '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#'
        contentVersion = '1.0.0.0'; resources = $resources } |
        ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $path -Encoding utf8
    $null = Invoke-DemoAz @('deployment', 'group', 'create', '--subscription', $Subscription,
        '-g', ($Search.id -split '/')[4], '-n', "$($Search.resource.name)-restore", '--template-file', $path)
    $service = Invoke-DemoAz @('resource', 'show', '--subscription', $Subscription,
        '--ids', $Search.id, '--api-version', '2025-05-01')
    $modelResources = @(
        foreach ($grant in $Search.modelRoles) {
            $salt = if (($grant.roleDefinitionId -split '/')[-1] -eq 'a97b65f3-24c7-4388-baec-2e87135dc908') {
                'iq-model-access'
            } else { 'iq-openai-model-access' }
            @{
                type = 'Microsoft.Authorization/roleAssignments'; apiVersion = '2022-04-01'
                name = "[guid('$($Search.modelId)', '$($service.identity.principalId)', '$salt')]"
                scope = $Search.modelId
                properties = @{ principalId = $service.identity.principalId; principalType = 'ServicePrincipal'
                    roleDefinitionId = $grant.roleDefinitionId }
            }
        }
    )
    $modelPath = Join-Path $Work 'search-model.json'
    @{ '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#'
        contentVersion = '1.0.0.0'; resources = $modelResources } |
        ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $modelPath -Encoding utf8
    $null = Invoke-DemoAz @('deployment', 'group', 'create', '--subscription', $Subscription,
        '-g', ($Search.modelId -split '/')[4], '-n', "$($Search.resource.name)-model-restore", '--template-file', $modelPath)
    Wait-DemoSearchAccess $Subscription $Search.endpoint
    & (Join-Path $PSScriptRoot 'configure-scan-knowledge.ps1') -SearchEndpoint $Search.endpoint `
        -ModelEndpoint $Search.config.ModelEndpoint -ModelDeployment $Search.config.ModelDeployment `
        -ModelName $Search.config.ModelName -Index $Search.index -KnowledgeSource $Search.knowledgeSource `
        -KnowledgeBase $Search.knowledgeBase -SubscriptionId $Subscription -OracleBulk -Confirm:$false
}

function Wait-DemoSearchAccess {
    param([string] $Subscription, [string] $Endpoint, [int] $Attempts = 30)
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $null = Invoke-DemoSearchRead $Subscription $Endpoint 'indexes'
            return
        }
        catch {
            $response = Get-DemoProperty $_.Exception 'Response'
            $status = Get-DemoProperty $response 'StatusCode'
            if ($null -eq $status -or [int]$status -notin @(401, 403) -or $attempt -eq $Attempts) { throw }
            Start-Sleep -Seconds 10
        }
    }
}
