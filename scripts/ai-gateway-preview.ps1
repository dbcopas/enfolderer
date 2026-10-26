#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:GatewayPreviewVersion = '2025-09-01-preview'

function Invoke-PreviewArm {
    param([string] $Subscription, [string] $Path, [string] $Method = 'GET', $Body)
    $access = az account get-access-token --subscription $Subscription --resource 'https://management.azure.com/' -o json | ConvertFrom-Json
    if ($LASTEXITCODE -or -not $access.accessToken) { throw 'Cannot acquire the selected subscription ARM token.' }
    $arguments = @{
        Uri = "https://management.azure.com$Path"
        Method = $Method
        Headers = @{ Authorization = "Bearer $($access.accessToken)" }
        ContentType = 'application/json'
    }
    if ($null -ne $Body) { $arguments.Body = $Body | ConvertTo-Json -Depth 100 -Compress }
    Invoke-RestMethod @arguments
}

function Get-PreviewGatewayId {
    param([string] $Subscription, [string] $Prefix, [string] $Suffix = '')
    "/subscriptions/$Subscription/resourceGroups/$Prefix-platform/providers/Microsoft.ApiManagement/aigateways/$Prefix-aigw$Suffix"
}

function Set-PreviewAsset {
    param([string] $Subscription, [string] $Id, $Properties)
    Invoke-PreviewArm $Subscription "$Id`?api-version=$script:GatewayPreviewVersion" PUT @{ properties = $Properties }
}

function Get-OptionalPreviewGateway {
    param([string] $Subscription, [string] $Id)
    try { Invoke-PreviewArm $Subscription "$Id`?api-version=$script:GatewayPreviewVersion" }
    catch {
        if (-not $_.Exception.PSObject.Properties['Response'] -or
            $null -eq $_.Exception.Response -or $_.Exception.Response.StatusCode -ne 404) { throw }
        return $null
    }
}

function Assert-PreviewGateway {
    param($Gateway, [string] $Subscription, [string] $Prefix, [string] $Owner)
    $ids = @('', '-geo', '-mtg', '-pkm') | ForEach-Object { Get-PreviewGatewayId $Subscription $Prefix $_ }
    if ($Gateway.id -notin $ids -or $Gateway.tags.enfoldererPrefix -ne $Prefix -or
        $Gateway.tags.enfoldererPurpose -ne 'ai-gateway' -or $Gateway.tags.enfoldererOwner -ne $Owner) {
        throw 'Refusing an AI Gateway outside the exact owner-tagged demo scope.'
    }
}

function New-PreviewRoleName {
    param([string] $Scope, [string] $Principal)
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("$Scope/$Principal/preview-model"))
    [guid]::new([byte[]]$hash[0..15]).ToString()
}

function Get-PreviewRuntimeKey {
    param([string] $Subscription, [string] $GatewayId, [string] $Name)
    $null = Set-PreviewAsset $Subscription "$GatewayId/apiKeys/$Name" @{ displayName = $Name }
    $secrets = Invoke-PreviewArm $Subscription "$GatewayId/apiKeys/$Name/listSecrets?api-version=$script:GatewayPreviewVersion" POST @{}
    if (-not $secrets.primaryKey) { throw 'AI Gateway did not return a usable runtime key.' }
    $secrets.primaryKey
}

function Set-PreviewAppConfiguration {
    param([string] $Subscription, $App, [hashtable] $Values, [hashtable] $SecretValues)
    $App = Invoke-PreviewArm $Subscription "$($App.id)?api-version=2026-07-01"
    $current = Invoke-PreviewArm $Subscription "$($App.id)/listSecrets?api-version=2026-07-01" POST @{}
    $secrets = @($current.value)
    $envs = @($App.properties.template.containers[0].env)
    $secretChanged = $false
    foreach ($name in $Values.Keys) {
        $envs = @($envs | Where-Object name -ne $name) + @{ name = $name; value = $Values[$name] }
    }
    foreach ($name in $SecretValues.Keys) {
        $entry = $SecretValues[$name]
        $existing = @($secrets | Where-Object name -eq $entry.name)
        if ($existing.Count -ne 1 -or $existing[0].value -cne $entry.value) { $secretChanged = $true }
        $secrets = @($secrets | Where-Object name -ne $entry.name) + @{ name = $entry.name; value = $entry.value }
        $envs = @($envs | Where-Object name -ne $name) + @{ name = $name; secretRef = $entry.name }
    }
    $App.properties.configuration.secrets = $secrets
    $App.properties.template.containers[0].env = $envs
    if ($secretChanged) {
        # Updating an app secret alone does not restart running revisions.
        $App.properties.template | Add-Member -NotePropertyName revisionSuffix `
            -NotePropertyValue "gateway-$([guid]::NewGuid().ToString('N').Substring(0,12))" -Force
    }
    $null = Invoke-PreviewArm $Subscription "$($App.id)?api-version=2026-07-01" PATCH @{
        properties = @{ configuration = $App.properties.configuration; template = $App.properties.template }
    }
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(10)
    do {
        $state = Invoke-PreviewArm $Subscription "$($App.id)?api-version=2026-07-01"
        if ($state.properties.provisioningState -eq 'Failed') { throw "App configuration failed: $($App.name)" }
        if ($state.properties.provisioningState -eq 'Succeeded' -and
            $state.properties.latestRevisionName -eq $state.properties.latestReadyRevisionName) { return }
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw "App revision readiness timed out: $($App.name)" }
        Start-Sleep -Seconds 5
    } while ($true)
}

function Get-DemoPreviewRecovery {
    param($Identity, [string] $Subscription, [string] $Prefix)
    $resources = @(Invoke-DemoAz @('resource', 'list', '--subscription', $Subscription,
        '-g', "$Prefix-platform") | Where-Object type -eq 'Microsoft.ApiManagement/aigateways')
    if (-not $resources.Count) { return $null }
    $snapshots = @()
    foreach ($resource in $resources) {
        $gateway = Invoke-PreviewArm $Subscription "$($resource.id)?api-version=$script:GatewayPreviewVersion"
        Assert-PreviewGateway $gateway $Subscription $Prefix $Identity.User
        $snapshots += @{ id = $gateway.id; principalId = $gateway.identity.principalId }
    }
    $modelId = Get-PreviewGatewayId $Subscription $Prefix
    $model = @($snapshots | Where-Object id -eq $modelId)
    $roles = @(Invoke-DemoAz @('role', 'assignment', 'list', '--subscription', $Subscription,
        '--all', '--fill-principal-name', 'false') | Where-Object { $_.principalId -in $snapshots.principalId })
    foreach ($role in $roles) {
        if ($model.Count -ne 1 -or $role.principalId -ne $model[0].principalId -or
            $role.scope -notmatch "^/subscriptions/$([regex]::Escape($Subscription))/resourceGroups/$([regex]::Escape($Prefix))-platform/providers/Microsoft.CognitiveServices/accounts/[^/]+$" -or
            ($role.roleDefinitionId -split '/')[-1] -ne '53ca6127-db72-4b80-b1b0-d745d6d5456d') {
            throw 'Preview gateway has unexpected/shared role assignments; refusing automatic teardown.'
        }
    }
    @{ gateways = $snapshots; modelRoles = @($roles | ForEach-Object {
        @{ id = $_.id; principalId = $_.principalId; roleDefinitionId = $_.roleDefinitionId; scope = $_.scope }
    }) }
}

function Assert-DemoPreviewRecovery {
    param($Recovery, [string] $Subscription, [string] $Prefix)
    if (-not $Recovery.gateways.Count -or $Recovery.gateways.Count -gt 4 -or
        @($Recovery.gateways.id | Select-Object -Unique).Count -ne $Recovery.gateways.Count -or
        @($Recovery.gateways.principalId | Select-Object -Unique).Count -ne $Recovery.gateways.Count) { throw 'Invalid preview gateway snapshot.' }
    $ids = @('', '-geo', '-mtg', '-pkm') | ForEach-Object { Get-PreviewGatewayId $Subscription $Prefix $_ }
    foreach ($gateway in $Recovery.gateways) {
        if ($gateway.id -notin $ids -or -not [guid]::TryParse($gateway.principalId, [ref]([guid]::Empty))) {
            throw 'Preview recovery includes a gateway outside the selected deployment or an invalid identity.'
        }
    }
    $model = @($Recovery.gateways | Where-Object id -eq $ids[0])
    foreach ($role in $Recovery.modelRoles) {
        if ($model.Count -ne 1 -or $role.principalId -ne $model[0].principalId -or
            $role.scope -notmatch "^/subscriptions/$([regex]::Escape($Subscription))/resourceGroups/$([regex]::Escape($Prefix))-platform/providers/Microsoft.CognitiveServices/accounts/[^/]+$" -or
            $role.id -notmatch "^$([regex]::Escape($role.scope))/providers/Microsoft.Authorization/roleAssignments/[0-9a-f-]+$" -or
            ($role.roleDefinitionId -split '/')[-1] -ne '53ca6127-db72-4b80-b1b0-d745d6d5456d') {
            throw 'Invalid preview model role snapshot.'
        }
    }
}

function Remove-DemoPreviewGateways {
    param($Recovery, $Identity, [string] $Subscription, [string] $Prefix)
    Assert-DemoPreviewRecovery $Recovery $Subscription $Prefix
    $currentGateways = @()
    foreach ($entry in $Recovery.gateways) {
        $current = Get-OptionalPreviewGateway $Subscription $entry.id
        if (-not $current) { continue }
        Assert-PreviewGateway $current $Subscription $Prefix $Identity.User
        if ($current.identity.principalId -ne $entry.principalId) { throw 'Gateway identity changed since recovery capture; refusing stale teardown.' }
        $currentGateways += $current
    }
    $roles = @(Invoke-DemoAz @('role', 'assignment', 'list', '--subscription', $Subscription,
        '--all', '--fill-principal-name', 'false'))
    foreach ($current in @($roles | Where-Object { $_.principalId -in $Recovery.gateways.principalId })) {
        if ($current.id -notin @($Recovery.modelRoles | ForEach-Object id)) {
            throw 'Preview gateway acquired an uncaptured role assignment; refusing stale teardown.'
        }
    }
    foreach ($saved in $Recovery.modelRoles) {
        foreach ($current in @($roles | Where-Object id -eq $saved.id)) {
            if ($current.principalId -ne $saved.principalId -or $current.scope -ne $saved.scope -or
                $current.roleDefinitionId -ne $saved.roleDefinitionId) { throw 'Saved preview model grant changed; refusing teardown.' }
        }
    }
    foreach ($gateway in $currentGateways) {
        $null = Invoke-PreviewArm $Subscription "$($gateway.id)?api-version=$script:GatewayPreviewVersion" DELETE
    }
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(15)
    foreach ($entry in $Recovery.gateways) {
        while (Get-OptionalPreviewGateway $Subscription $entry.id) {
            if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Preview gateway deletion did not complete; idle cost elimination is not verified.' }
            Start-Sleep -Seconds 5
        }
    }
    foreach ($role in $Recovery.modelRoles) {
        if ($role.id -in @($roles | ForEach-Object id)) {
            $null = Invoke-DemoAz @('role', 'assignment', 'delete', '--subscription', $Subscription, '--ids', $role.id)
        }
    }
}
