#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ai-gateway-preview.ps1')
function Assert($condition, [string] $message) { if (-not $condition) { throw $message } }
function Reject([scriptblock] $action, [string] $message) {
    $rejected = $false
    try { & $action } catch { $rejected = $true }
    Assert $rejected $message
}
$subscription = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
$owner = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
$prefix = 'alice-demo'
$script:gateways = @{}
$script:deletes = @()
$script:sleeps = 0
$script:failure = ''
$script:patch = $null
$script:readyReads = 0
$script:pending = @{}
$scope = "/subscriptions/$subscription/resourceGroups/$prefix-platform/providers/Microsoft.CognitiveServices/accounts/model"
$script:roles = @(@{
    id = "$scope/providers/Microsoft.Authorization/roleAssignments/eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"
    scope = $scope; principalId = '11111111-1111-1111-1111-111111111111'
    roleDefinitionId = "/subscriptions/$subscription/providers/Microsoft.Authorization/roleDefinitions/53ca6127-db72-4b80-b1b0-d745d6d5456d"
})
foreach ($suffix in @('', '-geo', '-mtg', '-pkm')) {
    $id = Get-PreviewGatewayId $subscription $prefix $suffix
    $principal = if (-not $suffix) { $roles[0].principalId } else { [guid]::NewGuid().ToString() }
    $gateways[$id] = [pscustomobject]@{
        id = $id; type = 'Microsoft.ApiManagement/aigateways'; name = "$prefix-aigw$suffix"; location = 'swedencentral'
        tags = @{ enfoldererPrefix = $prefix; enfoldererPurpose = 'ai-gateway'; enfoldererOwner = $owner }
        identity = @{ principalId = $principal }
    }
}
function Start-Sleep { param($Seconds) $script:sleeps++ }
function Invoke-DemoAz {
    param([array] $Arguments)
    Assert ($Arguments -contains $subscription) 'ARM/CLI operations must retain explicit subscription.'
    if ($Arguments[0] -eq 'resource') { return @($script:gateways.Values) }
    if ($Arguments[2] -eq 'list') { return $script:roles }
    if ($Arguments[2] -eq 'delete') {
        $script:deletes += $Arguments[-1]
        $script:roles = @($script:roles | Where-Object id -ne $Arguments[-1])
        return
    }
    throw 'Unexpected CLI operation.'
}
function Invoke-PreviewArm {
    param($Subscription, [string] $Path, [string] $Method = 'GET', $Body)
    Assert ($Subscription -eq $script:subscription) 'REST operation must retain explicit subscription.'
    if ($script:failure -eq 'network') { throw 'Original network error' }
    if ($script:failure -eq 'forbidden') {
        $error = [Exception]::new('Forbidden')
        $error | Add-Member -NotePropertyName Response -NotePropertyValue ([pscustomobject]@{ StatusCode = 403 })
        throw $error
    }
    $id = ($Path -split '\?')[0]
    if ($id -like '*/containerApps/*') {
        if ($Method -eq 'PATCH') { $script:patch = $Body; return }
        if ($id.EndsWith('/listSecrets')) { return @{ value = @(@{ name = 'existing'; value = 'fixture-only' }) } }
        $script:readyReads++
        return [pscustomobject]@{
            id = $id; name = 'api'
            properties = @{
                provisioningState = 'Succeeded'; latestRevisionName = 'new'
                latestReadyRevisionName = $(if ($script:readyReads -gt 2) { 'new' } else { 'old' })
                configuration = @{ secrets = @() }
                template = [pscustomobject]@{ containers = @(@{ env = @(@{ name = 'keep'; value = 'preserved' }) }) }
            }
        }
    }
    if ($Method -eq 'DELETE') { $script:deletes += $id; $script:pending[$id] = 2; return }
    if ($script:pending.ContainsKey($id)) {
        if ($script:pending[$id] -gt 0) { $script:pending[$id]-- }
        else { $script:gateways.Remove($id); $script:pending.Remove($id) }
    }
    if ($script:gateways.ContainsKey($id)) { return $script:gateways[$id] }
    $error = [Exception]::new('Not found')
    $error | Add-Member -NotePropertyName Response -NotePropertyValue ([pscustomobject]@{ StatusCode = 404 })
    throw $error
}
$identity = @{ User = $owner }
$recovery = Get-DemoPreviewRecovery $identity $subscription $prefix
Assert-DemoPreviewRecovery $recovery $subscription $prefix
Assert ($recovery.gateways.Count -eq 4 -and $recovery.modelRoles.Count -eq 1) 'Capture all four domains and exact model grant.'
Assert (($recovery | ConvertTo-Json -Depth 10) -notmatch 'primaryKey|secondaryKey|accessToken|fixture-only') 'Recovery must exclude credentials.'
$modelId = Get-PreviewGatewayId $subscription $prefix
$gateways[$modelId].tags.enfoldererOwner = [guid]::NewGuid().ToString()
Reject { Get-DemoPreviewRecovery $identity $subscription $prefix } 'Reject foreign ownership before teardown.'
$gateways[$modelId].tags.enfoldererOwner = $owner
$savedPrincipal = $gateways[$modelId].identity.principalId
$gateways[$modelId].identity.principalId = [guid]::NewGuid().ToString()
Reject { Remove-DemoPreviewGateways $recovery $identity $subscription $prefix } 'Reject a recreated identity with a stale manifest.'
Assert ($deletes.Count -eq 0) 'Stale identity must cause no deletion.'
$gateways[$modelId].identity.principalId = $savedPrincipal
$savedRolePrincipal = $roles[0].principalId
$roles[0].principalId = $gateways[(Get-PreviewGatewayId $subscription $prefix '-geo')].identity.principalId
Reject { Get-DemoPreviewRecovery $identity $subscription $prefix } 'Reject unexpected grants to tool gateways.'
Reject { Remove-DemoPreviewGateways $recovery $identity $subscription $prefix } 'Reject changed saved model grant.'
Assert ($deletes.Count -eq 0) 'Changed role must cause no deletion.'
$roles[0].principalId = $savedRolePrincipal
$foreign = $recovery | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$foreign.gateways[0].id = $foreign.gateways[0].id.Replace($prefix, 'other-demo')
Reject { Assert-DemoPreviewRecovery $foreign $subscription $prefix } 'Reject cross-deployment recovery IDs.'
$foreign = $recovery | ConvertTo-Json -Depth 20 | ConvertFrom-Json
$foreign.modelRoles[0].principalId = [guid]::NewGuid().ToString()
Reject { Assert-DemoPreviewRecovery $foreign $subscription $prefix } 'Reject forged role principal.'
$script:failure = 'network'
Reject { Get-OptionalPreviewGateway $subscription $modelId } 'A non-HTTP failure must propagate, not become absent.'
$script:failure = 'forbidden'
Reject { Get-OptionalPreviewGateway $subscription $modelId } '403 must propagate, not become absent.'
$script:failure = ''
Set-PreviewAppConfiguration $subscription @{ id = "/subscriptions/$subscription/resourceGroups/$prefix-platform/providers/Microsoft.App/containerApps/api" } `
    @{ 'GatewayEnabled' = 'true' } @{ GatewayApiKey = @{ name = 'model-key'; value = 'new-fixture-only' } }
Assert ($patch.properties.configuration.secrets.Count -eq 2) 'Preserve existing secrets while updating gateway key.'
Assert ($patch.properties.template.containers[0].env.Count -eq 3) 'Preserve unrelated environment and use a secretRef.'
Assert (@($patch.properties.template.containers[0].env | Where-Object name -eq 'GatewayApiKey')[0].secretRef -eq 'model-key') 'Never put runtime key in literal environment.'
Assert (($patch | ConvertTo-Json -Depth 20) -match '"revisionSuffix": "gateway-') 'Rotated secrets must force a fresh revision to load the new environment value.'
Assert ($sleeps -gt 0) 'Wait for latest ready revision, not just ARM provisioning.'
$script:readyReads = 0
Set-PreviewAppConfiguration $subscription @{ id = "/subscriptions/$subscription/resourceGroups/$prefix-platform/providers/Microsoft.App/containerApps/api" } `
    @{} @{ GatewayApiKey = @{ name = 'existing'; value = 'FIXTURE-ONLY' } }
Assert (($patch | ConvertTo-Json -Depth 20) -match '"revisionSuffix": "gateway-') 'Secret comparison must be case-sensitive when deciding whether to restart.'
Remove-DemoPreviewGateways $recovery $identity $subscription $prefix
Assert ($gateways.Count -eq 0 -and $deletes.Count -eq 5) 'Delete exactly four gateways and the captured model grant.'
Assert ($sleeps -ge 9) 'Poll asynchronous deletions until all four roots return 404.'
Remove-DemoPreviewGateways $recovery $identity $subscription $prefix
Assert ($deletes.Count -eq 5) 'An already deleted snapshot must be safely idempotent.'
Assert ($null -eq (Get-OptionalPreviewGateway $subscription $modelId)) 'Confirmed 404 is the only absence result.'
$global:GatewayPreviewWhatIf = @{ Subscription = $subscription; Owner = $owner; Reads = 0 }
function Invoke-RestMethod { throw 'WhatIf must not call the gateway REST client.' }
function az {
    $global:LASTEXITCODE = 0
    $a = @($args)
    Assert ($a -contains $global:GatewayPreviewWhatIf.Subscription) 'WhatIf discovery must be explicitly subscription-scoped.'
    $global:GatewayPreviewWhatIf.Reads++
    $result = switch ($a[0..1] -join ' ') {
        'account show' { @{ user = @{ type = 'user' }; tenantId = 'cccccccc-cccc-cccc-cccc-cccccccccccc' } }
        'account get-access-token' {
            $claims = @{ oid = $global:GatewayPreviewWhatIf.Owner; tid = 'cccccccc-cccc-cccc-cccc-cccccccccccc' } | ConvertTo-Json -Compress
            $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($claims)).TrimEnd('=')
            @{ accessToken = "header.$payload.signature" }
        }
        'role assignment' {
            Assert ($a[2] -eq 'list') 'WhatIf cannot mutate a role assignment.'
            @(@{ principalId = $global:GatewayPreviewWhatIf.Owner; roleDefinitionId = '/roles/8e3af657-a8ff-443c-a75c-2fe8c4bcb635' })
        }
        'group show' { @{ location = 'swedencentral' } }
        'cognitiveservices account' {
            if ($a[2] -eq 'list') { @(@{ kind = 'AIServices'; name = 'model' }) }
            elseif ($a[2] -eq 'deployment' -and $a[3] -eq 'list') { @(@{ name = 'gpt-4o' }) }
            else { throw 'Unexpected WhatIf model mutation.' }
        }
        default { throw "Unexpected WhatIf command: $($a[0..1] -join ' ')" }
    }
    ConvertTo-Json -InputObject $result -Depth 10 -Compress
}
& (Join-Path $PSScriptRoot 'configure-ai-gateway-preview.ps1') -NamePrefix $prefix -SubscriptionId $subscription -WhatIf
Assert ($global:GatewayPreviewWhatIf.Reads -gt 0) 'WhatIf must perform real read-only ownership/model discovery.'
Remove-Variable -Name GatewayPreviewWhatIf -Scope Global
Write-Output 'PASS dedicated preview lifecycle: secret-free capture, ownership, stale identities/grants, foreign IDs, HTTP failures, secretRef/readiness, exact deletion and confirmed absence.'
