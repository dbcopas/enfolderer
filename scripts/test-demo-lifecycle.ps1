#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) "enfolderer-lifecycle-test-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $root | Out-Null
$global:DemoLifecycleTest = @{
    User = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'; Prefix = 'alice-demo'
    Subscription = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
    Tenant = 'cccccccc-cccc-cccc-cccc-cccccccccccc'
    Mutations = [Collections.Generic.List[string]]::new()
    Deleted = [Collections.Generic.List[string]]::new()
    FailDelete = $false; FailRestore = $false; Shared = $false; ExtraOwner = $false
    Retention = $false; FailPurge = $false; Fresh = $false; CreateCooldown = $false
}
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Invoke-RestMethod { @{ status = 'ok' } }
function az {
    $global:LASTEXITCODE = 0
    $s = $global:DemoLifecycleTest
    $a = @($args)
    function Value([string] $key) {
        $index = [array]::IndexOf($a, $key)
        if ($index -lt 0) { return '' }
        return $a[$index + 1]
    }
    $scope = "/subscriptions/$($s.Subscription)/resourceGroups"
    $platform = "$scope/$($s.Prefix)-platform"
    $storage = "$platform/providers/Microsoft.Storage/storageAccounts/teststorage"
    $cosmos = "$platform/providers/Microsoft.DocumentDB/databaseAccounts/testcosmos"
    $apps = @(
        foreach ($entry in @(
            @('platform', 'api'), @('platform', 'worker'), @('cardgeo', 'mcp-imaging'),
            @('cardid', 'mcp-cardcatalog-mtg'), @('cardid', 'mcp-cardcatalog-pokemon')
        )) {
            @{
                name = "$($s.Prefix)-$($entry[1])"
                id = "$scope/$($s.Prefix)-$($entry[0])/providers/Microsoft.App/containerApps/$($s.Prefix)-$($entry[1])"
                properties = @{
                    environmentId = "$scope/$($s.Prefix)-$($entry[0])/providers/Microsoft.App/managedEnvironments/$($s.Prefix)-$($entry[0])-env"
                    customDomainVerificationId = 'verification'
                    configuration = @{ activeRevisionsMode = 'Single'; ingress = @{
                        fqdn = "$($entry[1]).example.test"
                        customDomains = @(@{ name = 'scan.example.test'; certificateId = 'old-certificate' })
                    } }
                    template = @{ containers = @(@{
                        image = "registry.azurecr.io/enfolderer/$($entry[1]):v1"
                        env = @(@{ name = 'ScanPlatform__StorageAccountUrl'; value = 'https://teststorage.blob.core.windows.net' })
                    }) }
                }
            }
        }
    )
    $result = $null
    switch ($a[0..1] -join ' ') {
        'account show' { $result = @{ user = @{ type = 'user' }; tenantId = $s.Tenant } }
        'account list' { $result = @(@{ id = $s.Subscription; tenantId = $s.Tenant; state = 'Enabled' }) }
        'ad signed-in-user' { $result = @{ id = $s.User } }
        'role assignment' {
            if ((Value '--scope') -like '*/containers/*') {
                $result = @(@{
                    id = "$(Value '--scope')/providers/Microsoft.Authorization/roleAssignments/grant"
                    scope = Value '--scope'; principalId = 'identity'; principalType = 'ServicePrincipal'
                    roleDefinitionId = 'reader'
                })
                break
            }
            $result = @()
            if ((Value '--scope') -notlike '*other-demo*' -or $s.ExtraOwner) {
                $result = @(@{ roleDefinitionId = '/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635' })
            }
        }
        'deployment sub' {
            switch ($a[2]) {
                'list' {
                    $result = @(
                        foreach ($prefix in @($s.Prefix, 'other-demo')) {
                            @{
                                name = "$prefix-deployment"; location = 'swedencentral'
                                properties = @{
                                    timestamp = '2026-10-19T12:00:00Z'; provisioningState = 'Succeeded'
                                    parameters = @{
                                        namePrefix = @{ value = $prefix }; apiClientId = @{ value = 'client' }
                                        imageTag = @{ value = 'v1' }; apiCustomDomains = @{ value = @() }
                                    }
                                }
                            }
                        }
                    )
                }
                'export' {
                    $result = @{ parameters = @{
                        namePrefix = @{ type = 'string' }; apiCustomDomains = @{ type = 'array' }
                    }; resources = @() }
                }
                'create' {
                    $s.Mutations.Add('restore')
                    if ($s.FailRestore) { $global:LASTEXITCODE = 1; break }
                    $s.Deleted.Clear()
                    $result = @{ properties = @{ provisioningState = 'Succeeded' } }
                }
                default { throw "Unexpected deployment command: $a" }
            }
        }
        'containerapp show' { $result = @($apps | Where-Object name -eq (Value '-n'))[0] }
        'containerapp list' {
            $result = @($apps)
            if ($s.Shared) {
                $result += @{ id = 'foreign-app'; properties = @{ environmentId = $apps[0].properties.environmentId } }
            }
        }
        'containerapp delete' {
            $s.Mutations.Add("delete $(Value '--ids')")
            $s.Deleted.Add((Value '--ids'))
            if ($s.FailDelete) { $global:LASTEXITCODE = 1 }
        }
        'containerapp revision' {
            if ($a[2] -eq 'list') {
                $result = @(@{ name = "$(Value '-n')--v1"; properties = @{ active = $true } })
            }
            else { $s.Mutations.Add("$($a[2]) $(Value '-n')") }
        }
        'containerapp replica' { $result = @() }
        'storage account' {
            $result = @{ deleteRetentionPolicy = @{ enabled = $s.Retention }; containerDeleteRetentionPolicy = $null }
        }
        'storage container-rm' {
            $result = @(
                foreach ($name in 'scans', 'crops', 'outputs', 'results', 'collections') {
                    @{
                        id = "$storage/blobServices/default/containers/$name"; name = $name
                        hasImmutabilityPolicy = $false; hasLegalHold = $false; leaseStatus = 'Unlocked'
                        immutableStorageWithVersioning = @{ enabled = $false }; publicAccess = 'None'
                        etag = if ($s.Fresh) { 'new' } else { 'old' }
                    }
                }
            )
        }
        'rest --method' {
            $s.Mutations.Add("put $(Value '--url')")
            if ($s.CreateCooldown -and (Value '--url') -like '*/containers/*?api-version=2023-05-01') {
                $s.CreateCooldown = $false
                $global:LASTEXITCODE = 1
                Write-Error 'The specified container is being deleted. Try operation later.' -ErrorAction Continue
                return
            }
            if ((Value '--url') -like '*/containers/*?api-version=2023-05-01') { $s.Fresh = $true }
            $result = @{}
        }
        'network private-endpoint' {
            $linked = if ((Value '--ids') -like '*cosmos-pe') { $cosmos } else { $storage }
            $result = @{ privateLinkServiceConnections = @(@{ privateLinkServiceId = $linked }) }
        }
        'resource list' {
            if ((Value '--resource-type') -eq 'Microsoft.App/jobs') { $result = @(); break }
            $all = @(
                @{ id = $storage; name = 'teststorage'; type = 'Microsoft.Storage/storageAccounts' },
                @{ id = $cosmos; name = 'testcosmos'; type = 'Microsoft.DocumentDB/databaseAccounts' }
                foreach ($name in 'blob-pe', 'queue-pe', 'cosmos-pe') {
                    @{ id = "$platform/providers/Microsoft.Network/privateEndpoints/$name"; type = 'Microsoft.Network/privateEndpoints' }
                }
                foreach ($app in $apps) { @{ id = $app.id; type = 'Microsoft.App/containerApps' } }
                foreach ($env in @($apps.properties.environmentId | Select-Object -Unique)) {
                    @{ id = $env; type = 'Microsoft.App/managedEnvironments' }
                }
            )
            $result = @($all | Where-Object { $_.id -notin $s.Deleted })
        }
        'resource delete' {
            $s.Mutations.Add("delete $(Value '--ids')")
            $s.Deleted.Add((Value '--ids'))
            if ($s.FailPurge) { $global:LASTEXITCODE = 1 }
        }
        default { throw "Unexpected Azure command: $a" }
    }
    if ($null -ne $result) { ConvertTo-Json -InputObject $result -Depth 100 -Compress }
}

try {
    foreach ($person in 'alice', 'bob') {
        $s = $global:DemoLifecycleTest
        $s.Prefix = "$person-demo"
        $s.User = if ($person -eq 'alice') { 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa' } else { 'dddddddd-dddd-dddd-dddd-dddddddddddd' }
        $s.Mutations.Clear(); $s.Deleted.Clear()
        $path = Join-Path $root "$person.json"
        & (Join-Path $PSScriptRoot 'stop-demo.ps1') -ManifestPath $path -WhatIf
        Assert ($s.Mutations.Count -eq 0 -and -not (Test-Path $path)) 'WhatIf must not mutate Azure or save recovery state.'
        & (Join-Path $PSScriptRoot 'stop-demo.ps1') -ManifestPath $path -Confirm:$false
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -Depth 100
        Assert ($manifest.status -eq 'Stopped' -and $manifest.ownerObjectId -eq $s.User) 'Shutdown must persist per-user recovery state.'
        Assert ($s.Mutations.Count -eq 15) 'Expected five apps, three containers, one queue, three environments and three private endpoints deleted.'
        Assert (-not @($s.Mutations | Where-Object { $_ -match 'other-demo|/collections|/databaseAccounts/|/registries/' }).Count) 'Shutdown touched retained or foreign resources.'
        Assert ($s.Mutations[0] -like "*$person-demo-api") 'API intake must be removed first.'
        $count = $s.Mutations.Count
        & (Join-Path $PSScriptRoot 'restore-demo.ps1') -ManifestPath $path -WhatIf
        Assert ($s.Mutations.Count -eq $count) 'Restore WhatIf must not deploy.'
        $report = & (Join-Path $PSScriptRoot 'restore-demo.ps1') -ManifestPath $path -Confirm:$false
        Assert (($report -join "`n") -match 'Not yet verified demo-ready') 'Restore must report outstanding readiness work.'
        Assert (($report -join "`n") -match 'CNAME -> api.example.test' -and
            ($report -join "`n") -match 'SkipImages') 'Restore must report DNS and agent reconnection steps.'
        $secondReport = & (Join-Path $PSScriptRoot 'stop-demo.ps1') -ManifestPath $path -Confirm:$false
        $secondPath = @(Get-ChildItem -LiteralPath $root -Filter "*-$($s.Prefix)-*.json")[0].FullName
        Assert (Test-Path -LiteralPath $path) 'Repeated cycles must preserve the original completed recovery manifest.'
        $s.FailRestore = $true
        $failedRestore = $false
        try { & (Join-Path $PSScriptRoot 'restore-demo.ps1') -ManifestPath $secondPath -Confirm:$false }
        catch { $failedRestore = $_.Exception.Message -like '*Azure command failed*' }
        Assert ($failedRestore -and (Get-Content $secondPath -Raw | ConvertFrom-Json).status -eq 'RestoreFailed') 'Restore failures must remain retryable, not appear successful.'
        $s.FailRestore = $false
        $null = & (Join-Path $PSScriptRoot 'restore-demo.ps1') -ManifestPath $secondPath -Confirm:$false
        $s.User = 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee'
        $rejected = $false
        try { & (Join-Path $PSScriptRoot 'restore-demo.ps1') -ManifestPath $path -WhatIf }
        catch { $rejected = $_.Exception.Message -like '*another user*' }
        Assert $rejected 'Another user must not replay a saved manifest.'
    }
    $s = $global:DemoLifecycleTest
    $s.Mutations.Clear(); $s.Shared = $true
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'stop-demo.ps1') -ManifestPath (Join-Path $root 'shared.json') -Confirm:$false }
    catch { $rejected = $_.Exception.Message -like '*hosts other apps*' }
    Assert ($rejected -and $s.Mutations.Count -eq 0) 'Shared environments must fail before any destructive operation.'
    $s.Shared = $false; $s.ExtraOwner = $true
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'stop-demo.ps1') -ManifestPath (Join-Path $root 'ambiguous.json') -WhatIf }
    catch { $rejected = $_.Exception.Message -like '*found 2*' }
    Assert $rejected 'Multiple owned environments must require explicit selectors.'
    $s.ExtraOwner = $false; $s.FailDelete = $true
    $path = Join-Path $root 'failure.json'
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'stop-demo.ps1') -ManifestPath $path -Confirm:$false }
    catch { $rejected = $_.Exception.Message -like '*Azure command failed*' }
    Assert $rejected 'Azure deletion errors must propagate.'
    Assert ((Get-Content $path -Raw | ConvertFrom-Json).status -eq 'StopFailed') 'Partial shutdown must retain restorable failure state.'
    $s.FailDelete = $false; $s.Deleted.Clear(); $s.Mutations.Clear()
    $receiptPath = Join-Path $root 'image-receipt.json'
    & (Join-Path $PSScriptRoot 'clear-demo-images.ps1') -ReceiptPath $receiptPath -WhatIf
    Assert ($s.Mutations.Count -eq 0) 'Image purge WhatIf must not mutate services or storage.'
    $s.Retention = $true
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'clear-demo-images.ps1') -ReceiptPath $receiptPath -Confirm:$false }
    catch { $rejected = $_.Exception.Message -like '*Permanent cleanup*' }
    Assert ($rejected -and $s.Mutations.Count -eq 0) 'Retention must fail before any image deletion.'
    $s.Retention = $false; $s.CreateCooldown = $true
    $report = & (Join-Path $PSScriptRoot 'clear-demo-images.ps1') -ReceiptPath $receiptPath -Confirm:$false
    Assert (($report -join "`n") -like '*verified fresh empty containers*') 'Purge must verify newly recreated containers before reporting success.'
    $deletions = @($s.Mutations | Where-Object { $_ -like 'delete *' })
    Assert ($deletions.Count -eq 5 -and -not @($deletions | Where-Object { $_ -match 'collections|other-demo|containerApps' }).Count) 'Image purge must erase only four image containers and the scan queue.'
    Assert (@($s.Mutations | Where-Object { $_ -like 'activate *' }).Count -eq 2) 'Both original API/worker revisions must resume.'
    Assert (@($s.Mutations | Where-Object { $_ -like '*roleAssignments/grant*' }).Count -eq 4) 'Image purge must restore exact container-scoped permissions.'
    $receipt = ($report | Where-Object { $_ -like 'Receipt: *' }).Substring(9)
    $null = & (Join-Path $PSScriptRoot 'clear-demo-images.ps1') -ResumePath $receipt -Confirm:$false
    Assert ((Get-Content $receipt -Raw | ConvertFrom-Json).status -eq 'Recovered') 'Saved cleanup receipts must support explicit service recovery.'
    $s.Mutations.Clear(); $s.FailPurge = $true
    $failed = $false
    try { & (Join-Path $PSScriptRoot 'clear-demo-images.ps1') -ReceiptPath (Join-Path $root 'image-failure.json') -Confirm:$false }
    catch { $failed = $_.Exception.Message -like '*Azure command failed*' }
    Assert ($failed -and @($s.Mutations | Where-Object { $_ -like 'activate *' }).Count -eq 2) 'A failed purge must report failure while repairing containers and resuming services.'
    $s.FailPurge = $false
    Remove-Item -LiteralPath $receipt
    $global:LASTEXITCODE = 0
    Write-Output 'All lifecycle and image-cleanup checks passed: owner isolation, WhatIf, exact deletion scope, repeated recovery, retention refusal, service resumption and permission restoration.'
}
finally {
    Get-ChildItem -LiteralPath $root -File | Remove-Item -Force
    Remove-Item -LiteralPath $root
    Remove-Variable DemoLifecycleTest -Scope Global
}
