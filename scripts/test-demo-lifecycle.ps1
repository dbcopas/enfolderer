#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot ".demo-lifecycle-test-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $root | Out-Null
$global:DemoLifecycleTest = @{
    User = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'; Prefix = 'alice-demo'
    Subscription = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
    Tenant = 'cccccccc-cccc-cccc-cccc-cccccccccccc'
    Mutations = [Collections.Generic.List[string]]::new()
    Deleted = [Collections.Generic.List[string]]::new()
    FailDelete = $false; FailRestore = $false; Shared = $false; ExtraOwner = $false
    Retention = $false; FailPurge = $false; Fresh = $false; CreateCooldown = $false
    Search = $false; SearchUnsafe = ''; SearchPrincipal = '11111111-1111-1111-1111-111111111111'
    FailImport = $false; OracleImports = 0
    Reads = [Collections.Generic.List[string]]::new()
    CorpDefault = $false; GroupOwner = $false; GroupFallbacks = 0
    SearchAuthFailures = 0; SearchProbeStatus = 403; SearchProbes = 0; Sleeps = 0
    NondefaultNames = $false
}
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Start-Sleep { $global:DemoLifecycleTest.Sleeps++ }
function Invoke-RestMethod {
    param($Uri, $Headers, $TimeoutSec)
    $s = $global:DemoLifecycleTest
    if ($Uri -like '*search.windows.net/indexes?*') {
        $s.SearchProbes++
        if ($s.SearchAuthFailures -gt 0) {
            $s.SearchAuthFailures--
            $exception = [Exception]::new('Mock Search HTTP error.')
            $exception | Add-Member -NotePropertyName Response -NotePropertyValue ([pscustomobject]@{ StatusCode = $s.SearchProbeStatus })
            throw $exception
        }
    }
    if ($Uri -like '*search.windows.net/*') {
        Assert ($Headers.Authorization -like 'Bearer header.*.signature') 'Search reads must use the explicit CLI token.'
        $s.Reads.Add($Uri)
        $kb = if ($s.NondefaultNames) { 'custom-cards' } else { 'scan-cards' }
        $source = if ($s.NondefaultNames) { 'custom-source' } else { 'scryfall' }
        $index = if ($s.NondefaultNames) { 'custom-index' } else { 'scryfall' }
        $result = switch -Regex ($Uri) {
            '/knowledgebases/[^/]+\?' {
                Assert ($Uri -match "/knowledgebases/$kb\?") 'Knowledge base read must use configured nondefault name even with WhatIf.'
                [pscustomobject]@{ models = @([pscustomobject]@{ azureOpenAIParameters = [pscustomobject]@{
                    resourceUri = 'https://demo-model.openai.azure.com'; deploymentId = 'synthesis'; modelName = 'gpt-4.1-mini'
                } }) }
            }
            '/indexes\?' { [pscustomobject]@{ value = @([pscustomobject]@{ name = $index }) } }
            '/knowledgesources\?' { [pscustomobject]@{ value = @([pscustomobject]@{ name = $source }) } }
            '/knowledgebases\?' { [pscustomobject]@{ value = @([pscustomobject]@{ name = $kb }) } }
            '/servicestats\?' { [pscustomobject]@{ counters = @{ storageSize = @{ usage = 123; quota = 456 } } } }
            '/indexes/[^/]+/stats\?' { [pscustomobject]@{ documentCount = 42; storageSize = 123 } }
            default { [pscustomobject]@{ value = @() } }
        }
        if ($s.SearchUnsafe -eq 'objects' -and $Uri -match '/indexes\?') {
            $result.value += [pscustomobject]@{ name = 'foreign-index' }
        }
        return $result
    }
    @{ status = 'ok' }
}
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
    $search = "$platform/providers/Microsoft.Search/searchServices/$($s.Prefix)-knowledge"
    $model = "$platform/providers/Microsoft.CognitiveServices/accounts/demo-model"
    $modelRole = "$model/providers/Microsoft.Authorization/roleAssignments/22222222-2222-2222-2222-222222222222"
    $apps = @(
        foreach ($entry in @(
            @('platform', 'api'), @('platform', 'worker'), @('cardgeo', 'mcp-imaging'),
            @('cardid', 'mcp-cardcatalog-mtg'), @('cardid', 'mcp-cardcatalog-pokemon')
        )) {
            @{
                name = "$($s.Prefix)-$($entry[1])"
                identity = @{ type = 'SystemAssigned'; principalId = 'api-principal' }
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
        'account show' {
            if ($s.CorpDefault) { Assert ((Value '--subscription') -eq $s.Subscription) 'Explicit demo selector must override corporate default account.' }
            $result = @{ user = @{ type = 'user' }; tenantId = $s.Tenant }
        }
        'account get-access-token' {
            if ($s.CorpDefault) { Assert ((Value '--subscription') -eq $s.Subscription) 'Token must use explicit demo subscription.' }
            if (Value '--scope') {
                Assert ((Value '--scope') -eq 'https://search.azure.com/.default' -and
                    (Value '--subscription') -eq $s.Subscription) 'Search token must use explicit subscription and Search default scope.'
            }
            $claims = @{ oid = $s.User; tid = $s.Tenant } | ConvertTo-Json -Compress
            $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($claims)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            $result = @{ accessToken = "header.$encoded.signature" }
        }
        'account list' { $result = @(@{ id = $s.Subscription; tenantId = $s.Tenant; state = 'Enabled' }) }
        'ad signed-in-user' { throw 'Identity must not call Graph.' }
        'role assignment' {
            if ($a[2] -eq 'list') {
                Assert (-not ($a -contains '--scope' -and $a -contains '--all')) 'Azure CLI rejects role assignment list with both --scope and --all.'
            }
            if ($a[2] -eq 'delete') {
                Assert ((Value '--ids') -eq $modelRole) 'Only the exact old Search model role may be deleted.'
                $s.Mutations.Add("delete-role $(Value '--ids')")
                break
            }
            if ($a[2] -eq 'create') {
                throw 'Model grant must use deterministic ARM deployment, not a random CLI assignment.'
            }
            if ((Value '--scope') -eq $model) {
                $result = @(
                    @{ id = $modelRole; scope = $model; principalId = $s.SearchPrincipal
                        roleDefinitionId = '/providers/Microsoft.Authorization/roleDefinitions/a97b65f3-24c7-4388-baec-2e87135dc908' }
                    @{ id = "$model/foreign"; scope = $model; principalId = 'foreign-principal'
                        roleDefinitionId = '/providers/Microsoft.Authorization/roleDefinitions/a97b65f3-24c7-4388-baec-2e87135dc908' }
                    @{ id = "$model/deployments/shared/grant"; scope = "$model/deployments/shared"; principalId = $s.SearchPrincipal
                        roleDefinitionId = '/providers/Microsoft.Authorization/roleDefinitions/a97b65f3-24c7-4388-baec-2e87135dc908' }
                )
                break
            }
            if ((Value '--scope') -eq $search) {
                $result = @(
                    foreach ($principal in @($s.User, 'api-principal', $s.SearchPrincipal)) {
                        @{ id = "$search/providers/Microsoft.Authorization/roleAssignments/33333333-3333-3333-3333-33333333333$([array]::IndexOf(@($s.User, 'api-principal', $s.SearchPrincipal), $principal))"
                            scope = $search; principalId = $principal; principalType = 'ServicePrincipal'
                            roleDefinitionId = '/providers/Microsoft.Authorization/roleDefinitions/1407120a-92aa-4202-b7e9-c0e197c71c8f' }
                    }
                )
                if ($s.SearchUnsafe -eq 'rbac') {
                    $result += @{ scope = $search; principalId = 'foreign-principal' }
                }
                break
            }
            if ((Value '--scope') -like '*/containers/*') {
                $result = @(@{
                    id = "$(Value '--scope')/providers/Microsoft.Authorization/roleAssignments/grant"
                    scope = Value '--scope'; principalId = 'identity'; principalType = 'ServicePrincipal'
                    roleDefinitionId = 'reader'
                })
                break
            }
            $result = @()
            if ($a -contains '--include-groups') { $s.GroupFallbacks++ }
            if ((Value '--scope') -notlike '*other-demo*' -or $s.ExtraOwner) {
                $result = @(@{ principalId = $s.User; roleDefinitionId = '/providers/Microsoft.Authorization/roleDefinitions/8e3af657-a8ff-443c-a75c-2fe8c4bcb635' })
                if ($s.GroupOwner -and $a -notcontains '--include-groups') { $result[0].principalId = 'group-owner' }
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
                    if ($s.Search) {
                        foreach ($deployment in $result) {
                            $deployment.properties.parameters.scanKnowledgeEndpoint = @{ value = "https://$($s.Prefix)-knowledge.search.windows.net" }
                            if ($s.NondefaultNames) {
                                $deployment.properties.parameters.scanKnowledgeBase = @{ value = 'custom-cards' }
                                $deployment.properties.parameters.scanKnowledgeSource = @{ value = 'custom-source' }
                                $deployment.properties.parameters.scanKnowledgeIndex = @{ value = 'custom-index' }
                            }
                        }
                    }
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
        'deployment group' {
            if ((Value '-n') -like '*-model-restore') {
                $template = Get-Content -LiteralPath (Value '--template-file') -Raw | ConvertFrom-Json -Depth 100
                Assert ($template.resources[0].properties.principalId -eq $s.SearchPrincipal) 'Model ARM grant must use the recreated principal.'
                Assert ($template.resources[0].name -eq "[guid('$model', '$($s.SearchPrincipal)', 'iq-model-access')]") 'Model grant name must match scan-knowledge.bicep deterministic guid.'
                $s.Mutations.Add('restore-model-role')
                break
            }
            $s.Mutations.Add('restore-search')
            $template = Get-Content -LiteralPath (Value '--template-file') -Raw | ConvertFrom-Json -Depth 100
            $cmk = $template.resources[0].properties.encryptionWithCmk
            Assert ($cmk.enforcement -eq 'Unspecified' -and -not $cmk.PSObject.Properties['encryptionComplianceStatus']) 'Search restore must preserve writable CMK enforcement without read-only compliance status.'
            $s.SearchPrincipal = '44444444-4444-4444-4444-444444444444'
            $result = @{ properties = @{ provisioningState = 'Succeeded' } }
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
            if ((Value '--method') -eq 'get') {
                throw 'Search reads must not use az rest.'
            }
            if ((Value '--method') -eq 'post') {
                $result = @{ properties = @{ rows = @(@(1.23, 'USD')) } }
                break
            }
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
            if ((Value '--resource-type') -eq 'Microsoft.Search/searchServices') {
                $result = @(@{ id = $search; name = "$($s.Prefix)-knowledge" }); break
            }
            if ((Value '--resource-type') -eq 'Microsoft.CognitiveServices/accounts') {
                $result = @(@{ id = $model; name = 'demo-model' }); break
            }
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
                if ($s.Search) { @{ id = $search; type = 'Microsoft.Search/searchServices' } }
            )
            $result = @($all | Where-Object { $_.id -notin $s.Deleted })
        }
        'resource show' {
            Assert ((Value '--ids') -eq $search) 'Only configured Search should be read here.'
            $result = @{
                id = $search; name = "$($s.Prefix)-knowledge"; location = 'swedencentral'
                tags = @{ enfoldererPrefix = $s.Prefix; enfoldererPurpose = 'scan-knowledge' }
                sku = @{ name = 'basic' }; identity = @{ type = 'SystemAssigned'; principalId = $s.SearchPrincipal }
                properties = @{ replicaCount = 1; partitionCount = 1; hostingMode = 'Default'; disableLocalAuth = $true; semanticSearch = 'free'
                    encryptionWithCmk = @{ enforcement = 'Unspecified'; encryptionComplianceStatus = 'NonCompliant' } }
            }
            if ($s.SearchUnsafe -eq 'tags') { $result.tags.enfoldererPurpose = 'shared' }
            if ($s.SearchUnsafe -eq 'keys') { $result.properties.disableLocalAuth = $false }
            if ($s.SearchUnsafe -eq 'scope') { $result.id = $search.Replace("$($s.Prefix)-platform", 'foreign-platform') }
        }
        'monitor metrics' {
            if ($a[2] -eq 'list-definitions') {
                $result = @(@{ name = @{ value = 'SearchQueriesPerSecond' }; supportedAggregationTypes = @('Average', 'Maximum') })
            }
            else { $s.Reads.Add(($a -join ' ')); $result = @{ value = @() } }
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
    # Run the real recovery code with only the public corpus importer substituted.
    foreach ($file in 'demo-lifecycle.ps1', 'stop-demo.ps1', 'restore-demo.ps1') {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $root $file)
    }
    @'
[CmdletBinding(SupportsShouldProcess)]
param($SearchEndpoint, $ModelEndpoint, $ModelDeployment, $ModelName, $Index, $KnowledgeSource, $KnowledgeBase, $SubscriptionId, [switch]$OracleBulk)
if (-not $OracleBulk -or $SubscriptionId -ne $global:DemoLifecycleTest.Subscription -or
    $ModelDeployment -ne 'synthesis' -or $ModelName -ne 'gpt-4.1-mini' -or
    $Index -ne 'scryfall' -or $KnowledgeSource -ne 'scryfall' -or $KnowledgeBase -ne 'scan-cards') {
    throw 'Incorrect public corpus rebuild configuration.'
}
$global:DemoLifecycleTest.OracleImports++
if ($global:DemoLifecycleTest.FailImport) { throw 'Mock corpus import failed.' }
'@ | Set-Content -LiteralPath (Join-Path $root 'configure-scan-knowledge.ps1')
    $s.Search = $true; $s.Mutations.Clear(); $s.Deleted.Clear()
    $searchPath = Join-Path $root 'search.json'
    & (Join-Path $root 'stop-demo.ps1') -ManifestPath $searchPath -WhatIf
    Assert ($s.Mutations.Count -eq 0 -and -not (Test-Path $searchPath)) 'Search WhatIf must not save or mutate anything.'
    foreach ($unsafe in 'tags', 'keys', 'scope', 'objects', 'rbac') {
        $s.SearchUnsafe = $unsafe
        $rejected = $false
        try { & (Join-Path $root 'stop-demo.ps1') -ManifestPath $searchPath -Confirm:$false }
        catch { $rejected = $true }
        Assert ($rejected -and $s.Mutations.Count -eq 0) "Unsafe Search $unsafe must fail before deleting any app or data."
    }
    $s.SearchUnsafe = ''
    & (Join-Path $root 'stop-demo.ps1') -ManifestPath $searchPath -Confirm:$false
    $saved = Get-Content $searchPath -Raw | ConvertFrom-Json -Depth 100
    Assert ($saved.search.config.ModelDeployment -eq 'synthesis' -and $saved.search.resource.sku.name -eq 'basic') 'Search template and synthesis configuration must be recoverable.'
    Assert ($s.Mutations.Count -eq 17 -and @($s.Mutations | Where-Object { $_ -like 'delete-role *' }).Count -eq 1) 'Shutdown must remove only dedicated Search and its exact model grant in addition to existing resources.'
    Assert (-not @($s.Mutations | Where-Object { $_ -match '/accounts/demo-model$|foreign|/deployments/shared' }).Count) 'Model and foreign grants must be retained.'
    $count = $s.Mutations.Count
    & (Join-Path $root 'restore-demo.ps1') -ManifestPath $searchPath -WhatIf
    Assert ($s.Mutations.Count -eq $count -and $s.OracleImports -eq 0) 'Search restore WhatIf must not provision or import.'
    $s.FailImport = $true
    $s.SearchAuthFailures = 2
    $probeCount = $s.SearchProbes
    $failed = $false
    try { & (Join-Path $root 'restore-demo.ps1') -ManifestPath $searchPath -Confirm:$false }
    catch { $importFailure = $_.Exception.Message; $failed = $importFailure -like '*Mock corpus import failed*' }
    Assert ($failed -and (Get-Content $searchPath -Raw | ConvertFrom-Json).status -eq 'RestoreFailed') "Corpus rebuild failures must preserve retryable recovery state: $importFailure"
    Assert ($s.SearchProbes -eq $probeCount + 3 -and $s.OracleImports -eq 1) 'Retry only readiness 403 responses; invoke failed importer exactly once.'
    $s.FailImport = $false
    $null = & (Join-Path $root 'restore-demo.ps1') -ManifestPath $searchPath -Confirm:$false
    Assert ($s.OracleImports -eq 2 -and (Get-Content $searchPath -Raw | ConvertFrom-Json).status -eq 'InfrastructureRestored') 'Retry must rebuild the full public corpus before marking restoration complete.'
    $legacy = Get-Content $searchPath -Raw | ConvertFrom-Json -Depth 100
    $legacy.PSObject.Properties.Remove('search')
    $legacyPath = Join-Path $root 'legacy-search.json'
    $legacy | ConvertTo-Json -Depth 100 | Set-Content $legacyPath
    $count = $s.Mutations.Count
    & (Join-Path $root 'restore-demo.ps1') -ManifestPath $legacyPath -WhatIf
    Assert ($s.Mutations.Count -eq $count) 'Old manifests without a Search field must remain valid for WhatIf.'
    $legacyWarnings = @()
    $null = & (Join-Path $root 'restore-demo.ps1') -ManifestPath $legacyPath -Confirm:$false -WarningVariable legacyWarnings
    Assert ($s.OracleImports -eq 2 -and ($legacyWarnings -join ' ') -match 'Legacy manifest') 'Old manifests must not invent Search recovery details or silently claim Search restored.'
    $saved.search.modelRoles[0].principalId = 'foreign'
    $saved | ConvertTo-Json -Depth 100 | Set-Content $searchPath
    $count = $s.Mutations.Count
    $rejected = $false
    try { & (Join-Path $root 'restore-demo.ps1') -ManifestPath $searchPath -Confirm:$false }
    catch { $rejected = $true }
    Assert ($rejected -and $s.Mutations.Count -eq $count) 'Tampered model grant must fail before restore mutation.'
    $s.Mutations.Clear()
    $report = & (Join-Path $PSScriptRoot 'measure-scan-knowledge.ps1') -StartTime '2026-10-01T00:00:00Z' -EndTime '2026-10-02T00:00:00Z' -IncludeActualCost
    Assert ($s.Mutations.Count -eq 0 -and $report.indexStatistics[0].statistics.documentCount -eq 42) 'Monitoring must be read-only and report actual index/storage statistics.'
    Assert (@($s.Reads | Where-Object { $_ -match '--start-time 2026-10-01T00:00:00Z --end-time 2026-10-02T00:00:00Z' }).Count -gt 0) 'Metrics must query the exact explicit time range.'
    $null = & (Join-Path $PSScriptRoot 'measure-scan-knowledge.ps1') -StartTime '2026-10-01T00:00:00Z' -EndTime '2026-10-02T00:00:00Z' -BudgetAmount 25 -BudgetContact 'owner@example.org' -WhatIf
    Assert ($s.Mutations.Count -eq 0) 'Budget WhatIf must not deploy an alert.'
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'measure-scan-knowledge.ps1') -StartTime '2026-10-01T00:00:00Z' -EndTime '2026-10-02T00:00:00Z' -BudgetAmount 25 }
    catch { $rejected = $true }
    Assert ($rejected -and $s.Mutations.Count -eq 0) 'No budget may be invented without an explicit amount and contact.'
    $null = & (Join-Path $PSScriptRoot 'measure-scan-knowledge.ps1') -StartTime '2026-10-01T00:00:00Z' -EndTime '2026-10-02T00:00:00Z' -BudgetAmount 25 -BudgetContact 'owner@example.org' -Confirm:$false
    Assert ($s.Mutations.Count -eq 1 -and $s.Mutations[0] -match '/budgets/scan-knowledge') 'Only explicit budget deployment may mutate monitoring scope.'
    $s.CorpDefault = $true
    $s.Mutations.Clear()
    $null = & (Join-Path $PSScriptRoot 'measure-scan-knowledge.ps1') -SubscriptionId $s.Subscription -StartTime '2026-10-01T00:00:00Z' -EndTime '2026-10-02T00:00:00Z'
    & (Join-Path $root 'stop-demo.ps1') -SubscriptionId $s.Subscription -ManifestPath (Join-Path $root 'explicit.json') -WhatIf
    $s.NondefaultNames = $true
    & (Join-Path $root 'stop-demo.ps1') -SubscriptionId $s.Subscription -ManifestPath (Join-Path $root 'nondefault.json') -WhatIf
    Assert (@($s.Reads | Where-Object { $_ -match '/knowledgebases/custom-cards\?' }).Count -gt 0) 'WhatIf must retain nondefault knowledge names for safety validation.'
    $s.NondefaultNames = $false
    & (Join-Path $root 'restore-demo.ps1') -ManifestPath $legacyPath -WhatIf
    Assert ($s.Mutations.Count -eq 0) 'Explicit subscription identity must work independently of CLI default without mutation.'
    . (Join-Path $PSScriptRoot 'demo-lifecycle.ps1')
    $ownerIdentity = Get-DemoIdentity -SubscriptionId $s.Subscription
    $fallbacks = $s.GroupFallbacks
    Assert (Test-DemoOwner $ownerIdentity $s.Subscription $s.Prefix) 'Direct Owner check should succeed.'
    Assert ($s.GroupFallbacks -eq $fallbacks) 'Direct Owner must not use Graph-dependent group expansion.'
    $s.GroupOwner = $true
    Assert (Test-DemoOwner $ownerIdentity $s.Subscription $s.Prefix) 'Group ownership fallback should remain supported.'
    Assert ($s.GroupFallbacks -eq $fallbacks + 3) 'Group expansion should run only where no direct Owner exists.'
    $s.GroupOwner = $false
    foreach ($status in 401, 403, 429, 500) {
        $s.SearchProbeStatus = $status; $s.SearchAuthFailures = 2
        $probes = $s.SearchProbes
        $failed = $false
        try { Wait-DemoSearchAccess $s.Subscription "https://$($s.Prefix)-knowledge.search.windows.net" -Attempts 2 }
        catch { $failed = $true }
        $expected = if ($status -in @(401, 403)) { 2 } else { 1 }
        Assert ($failed -and $s.SearchProbes -eq $probes + $expected) "Search $status readiness retry must be bounded and limited to authentication errors."
    }
    $s.CorpDefault = $false; $s.SearchAuthFailures = 0
    $global:LASTEXITCODE = 0
    Write-Output 'All lifecycle, Search recovery, monitoring/budget and image-cleanup checks passed.'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force
    Remove-Variable DemoLifecycleTest -Scope Global
}
