<#
.SYNOPSIS
    Permanently clears image containers in an owned Enfolderer deployment without tearing it down.
.DESCRIPTION
    Temporarily pauses the API and worker, deletes/recreates scans, crops, outputs and any
    legacy results container through ARM, restores container configuration/RBAC and resumes
    the original revisions. Collections, Cosmos, credits and other storage accounts are untouched.
    In-flight scans are interrupted; old history references no longer have image files.
    Queued work is discarded to prevent recreating images after cleanup.
    Requires PowerShell 7, az login and effective Owner on the three deployment groups.
    Refuses soft-delete retention, locked or immutable containers rather than claiming a
    permanent purge. Does not enable public storage access, use keys or change retention.
.EXAMPLE
    .\scripts\clear-demo-images.ps1 -WhatIf
.EXAMPLE
    .\scripts\clear-demo-images.ps1 -SubscriptionId <id> -DeploymentName enfolderer-scan
.EXAMPLE
    .\scripts\clear-demo-images.ps1 -ResumePath C:\private\image-cleanup.json
    Repairs containers/permissions and resumes services after an interrupted cleanup.
#>
#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string] $SubscriptionId,
    [string] $DeploymentName,
    [string] $ResumePath,
    [string] $ReceiptPath
)
. (Join-Path $PSScriptRoot 'demo-lifecycle.ps1')

function Invoke-ImagePut {
    param([string] $Url, $Body)
    $file = [IO.Path]::GetTempFileName()
    $errorFile = [IO.Path]::GetTempFileName()
    try {
        $Body | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $file -Encoding utf8
        foreach ($attempt in 1..24) {
            $null = & az rest --method put --url $Url --body "@$file" --only-show-errors -o json 2>$errorFile
            if ($LASTEXITCODE -eq 0) { return }
            $detail = Get-Content -LiteralPath $errorFile -Raw
            if ($detail -notlike '*specified container is being deleted*' -or $attempt -eq 24) {
                throw "ARM restore failed for $Url : $detail"
            }
            Write-Warning "Storage is still deleting the old container; retrying creation ($attempt/24)."
            Start-Sleep -Seconds 5
        }
    }
    finally { Remove-Item -LiteralPath $file, $errorFile -Force }
}

function Restore-ImageContainers {
    param($State)
    foreach ($container in $State.containers) {
        Invoke-ImagePut "https://management.azure.com$($container.id)?api-version=2023-05-01" @{ properties = $container.properties }
        foreach ($grant in $container.grants) {
            Invoke-ImagePut "https://management.azure.com$($grant.id)?api-version=2022-04-01" @{ properties = $grant.properties }
        }
    }
    Invoke-ImagePut "https://management.azure.com$($State.storageId)/queueServices/default/queues/scan-jobs?api-version=2023-05-01" @{ properties = @{} }
}

function Resume-ImageServices {
    param($State)
    # Worker before API: no new intake until its consumer is back.
    foreach ($app in @($State.apps | Sort-Object { $_.name -like '*-api' })) {
        foreach ($revision in $app.revisions) {
            $null = Invoke-DemoAz @('containerapp', 'revision', 'activate', '--subscription', $State.subscriptionId,
                '-g', "$($State.prefix)-platform", '-n', $app.name, '--revision', $revision)
        }
        $null = Invoke-DemoAz @('containerapp', 'revision', 'set-mode', '--subscription', $State.subscriptionId,
            '-g', "$($State.prefix)-platform", '-n', $app.name, '--mode', $app.mode)
        if ($app.url) { Wait-DemoHealth $app.url }
    }
}

$identity = Get-DemoIdentity
if ($ResumePath) {
    $state = Get-Content -LiteralPath $ResumePath -Raw | ConvertFrom-Json -Depth 100
    Assert-DemoManifest $state $identity
    if ($state.kind -ne 'ImageCleanup') { throw 'Not an image-cleanup recovery receipt.' }
    $base = "/subscriptions/$($state.subscriptionId)/resourceGroups/$($state.prefix)-platform/providers/"
    if ($state.storageId -notlike "${base}Microsoft.Storage/storageAccounts/*" -or
        @($state.containers | Where-Object {
            $_.name -notin @('scans', 'crops', 'outputs', 'results') -or
            $_.id -ne "$($state.storageId)/blobServices/default/containers/$($_.name)"
        }).Count -or @($state.apps | Where-Object {
            $_.name -notin @("$($state.prefix)-api", "$($state.prefix)-worker") -or
            $_.id -ne "${base}Microsoft.App/containerApps/$($_.name)"
        }).Count) { throw 'Recovery receipt contains resources outside the selected deployment.' }
    if (-not $PSCmdlet.ShouldProcess("$($state.subscriptionId) / $($state.prefix)", 'Repair image containers/permissions and resume services')) { return }
    Restore-ImageContainers $state
    Resume-ImageServices $state
    $state.status = 'Recovered'
    Save-DemoManifest $state $ResumePath
    Write-Output 'Containers/permissions repaired and original services resumed. An interrupted purge may need rerunning.'
    return
}
$target = Find-DemoDeployment $identity $SubscriptionId $DeploymentName
$subscription = $target.Subscription
$prefix = $target.Prefix
$apps = @(Get-DemoApps $subscription $prefix)
$api = @($apps | Where-Object name -eq "$prefix-api")[0]
$resources = @(Invoke-DemoAz @('resource', 'list', '--subscription', $subscription, '-g', "$prefix-platform"))
$accounts = @($resources | Where-Object type -eq 'Microsoft.Storage/storageAccounts')
if ($accounts.Count -ne 1) { throw 'Expected exactly one deployment storage account; refusing to guess.' }
$account = $accounts[0]
$configured = @($api.properties.template.containers[0].env | Where-Object name -eq 'ScanPlatform__StorageAccountUrl')
if ($configured.Count -ne 1 -or ([uri]$configured[0].value).Host -ne "$($account.name).blob.core.windows.net") {
    throw 'API is not configured for the selected storage account.'
}
$service = Invoke-DemoAz @('storage', 'account', 'blob-service-properties', 'show',
    '--subscription', $subscription, '-g', "$prefix-platform", '-n', $account.name)
foreach ($policy in 'deleteRetentionPolicy', 'containerDeleteRetentionPolicy') {
    if ($service.PSObject.Properties[$policy] -and $service.$policy -and $service.$policy.enabled) {
        throw "$policy is enabled. Permanent cleanup requires an explicitly approved retention change; nothing was changed."
    }
}
$containers = @(Invoke-DemoAz @('storage', 'container-rm', 'list', '--subscription', $subscription,
    '-g', "$prefix-platform", '--storage-account', $account.name))
$selected = @($containers | Where-Object name -in @('scans', 'crops', 'outputs', 'results'))
if (@($selected | Where-Object name -in @('scans', 'crops', 'outputs')).Count -ne 3) {
    throw 'Expected active scans/crops/outputs containers.'
}
$unexpected = @($containers | Where-Object name -notin @('scans', 'crops', 'outputs', 'results', 'collections'))
if ($unexpected.Count) {
    throw "Unclassified containers require review before claiming all images removed: $($unexpected.name -join ', ')."
}
$savedContainers = @(
    foreach ($container in $selected) {
        if ($container.hasImmutabilityPolicy -or $container.hasLegalHold -or
            $container.leaseStatus -ne 'Unlocked' -or
            ($container.immutableStorageWithVersioning -and $container.immutableStorageWithVersioning.enabled)) {
            throw "Container $($container.name) is locked/immutable; nothing was changed."
        }
        $properties = @{ publicAccess = $container.publicAccess }
        foreach ($key in 'metadata', 'defaultEncryptionScope', 'denyEncryptionScopeOverride') {
            if ($container.PSObject.Properties[$key] -and $null -ne $container.$key) {
                $properties[$key] = $container.$key
            }
        }
        $grants = @(Invoke-DemoAz @('role', 'assignment', 'list', '--subscription', $subscription,
            '--scope', $container.id) | Where-Object scope -eq $container.id)
        $savedGrants = @(
            foreach ($grant in $grants) {
                $grantProperties = @{}
                foreach ($key in 'principalId', 'principalType', 'roleDefinitionId', 'condition', 'conditionVersion', 'description') {
                    if ($grant.PSObject.Properties[$key] -and $null -ne $grant.$key) {
                        $grantProperties[$key] = $grant.$key
                    }
                }
                @{ id = $grant.id; properties = $grantProperties }
            }
        )
        @{ id = $container.id; name = $container.name; properties = $properties; grants = $savedGrants; oldEtag = $container.etag }
    }
)
$writers = @(
    foreach ($app in @($apps | Where-Object name -in @("$prefix-api", "$prefix-worker"))) {
        $revisions = @(Invoke-DemoAz @('containerapp', 'revision', 'list', '--subscription', $subscription,
            '-g', "$prefix-platform", '-n', $app.name) | Where-Object { $_.properties.active })
        if (-not $revisions.Count) { throw "$($app.name) has no active revisions; use the hibernation workflow instead." }
        @{
            id = $app.id; name = $app.name; mode = $app.properties.configuration.activeRevisionsMode
            revisions = @($revisions.name)
            url = if ($app.name -eq "$prefix-api") { "https://$($app.properties.configuration.ingress.fqdn)" } else { '' }
        }
    }
)
$state = [ordered]@{
    schemaVersion = 1; kind = 'ImageCleanup'; status = 'Prepared'
    ownerObjectId = $identity.User; tenantId = $identity.Tenant; subscriptionId = $subscription
    prefix = $prefix; storageId = $account.id; containers = $savedContainers; apps = $writers
}
$directory = Join-Path (Get-DemoStateDirectory $identity) 'ImageCleanup'
$receipt = if ($ReceiptPath) { [IO.Path]::GetFullPath($ReceiptPath) } else {
    Join-Path $directory "$subscription-$prefix-$([DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssfff')).json"
}
if (Test-Path -LiteralPath $receipt) { throw "Recovery receipt already exists: $receipt" }
Write-Host "Target: $subscription / $prefix / $($account.name)"
Write-Host "Erase ALL contents of: $($selected.name -join ', '); discard scan-jobs queue."
Write-Host 'API/worker will pause temporarily. Collection snapshots, Cosmos and all other storage accounts are preserved.'
if (-not $PSCmdlet.ShouldProcess($account.id, 'Delete all image containers/queued work, recreate empty and resume original services')) { return }
New-Item -ItemType Directory -Path (Split-Path -Parent $receipt) -Force | Out-Null
Save-DemoManifest $state $receipt
$purgeError = $null
try {
    foreach ($app in $writers) {
        $null = Invoke-DemoAz @('containerapp', 'revision', 'set-mode', '--subscription', $subscription,
            '-g', "$prefix-platform", '-n', $app.name, '--mode', 'multiple')
        foreach ($revision in $app.revisions) {
            $null = Invoke-DemoAz @('containerapp', 'revision', 'deactivate', '--subscription', $subscription,
                '-g', "$prefix-platform", '-n', $app.name, '--revision', $revision)
            $stopped = $false
            foreach ($attempt in 1..30) {
                $replicas = @(Invoke-DemoAz @('containerapp', 'replica', 'list', '--subscription', $subscription,
                    '-g', "$prefix-platform", '-n', $app.name, '--revision', $revision))
                if (-not $replicas.Count) { $stopped = $true; break }
                Start-Sleep -Seconds 5
            }
            if (-not $stopped) { throw "Timed out stopping $revision; no images were deleted." }
        }
    }
    foreach ($container in $savedContainers) {
        $null = Invoke-DemoAz @('resource', 'delete', '--subscription', $subscription, '--ids',
            $container.id, '--api-version', '2023-05-01')
    }
    $null = Invoke-DemoAz @('resource', 'delete', '--subscription', $subscription, '--ids',
        "$($account.id)/queueServices/default/queues/scan-jobs", '--api-version', '2023-05-01')
}
catch { $purgeError = $_ }
try {
    Restore-ImageContainers $state
    if (-not $purgeError) {
        $fresh = @(Invoke-DemoAz @('storage', 'container-rm', 'list', '--subscription', $subscription,
            '-g', "$prefix-platform", '--storage-account', $account.name))
        foreach ($container in $savedContainers) {
            $match = @($fresh | Where-Object name -eq $container.name)
            if ($match.Count -ne 1 -or $match[0].etag -eq $container.oldEtag) {
                throw "Could not verify fresh empty container $($container.name)."
            }
        }
    }
    Resume-ImageServices $state
}
catch {
    $state.status = 'RecoveryRequired'
    Save-DemoManifest $state $receipt
    throw "Cleanup/recovery failed: $($_.Exception.Message). Original purge error: $purgeError. Repair with .\scripts\clear-demo-images.ps1 -ResumePath `"$receipt`""
}
$state.status = if ($purgeError) { 'PurgeFailedServicesResumed' } else { 'Completed' }
Save-DemoManifest $state $receipt
if ($purgeError) { throw $purgeError }
Write-Output "Purged $($selected.name -join ', ') in $($account.name); verified fresh empty containers before resuming API/worker."
Write-Output "Receipt: $receipt"
Write-Output 'Collection snapshots/credits are unchanged. Historical image links no longer work; in-flight/queued scans must be resubmitted.'
