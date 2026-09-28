<#
.SYNOPSIS
    Deletes the things in your subscription that this repository no longer uses.

.DESCRIPTION
    Two changes left debris behind that a redeploy cannot clear up on its own:

    * Storage went private and the client now uploads through the API, so nothing mints a
      user-delegation SAS any more. The Storage Blob Delegator grants that existed only for that
      are dead. An incremental ARM deployment never deletes a role assignment that was merely
      removed from the template, so they survive every redeploy until something removes them.

    * The agents now receive image bytes through the Foundry Files API instead of a blob URL, so
      Team B's identity no longer reads the crops container, and a run that dies before its own
      cleanup leaves an uploaded file behind in the project.

    It also removes the two role assignments that were granted by hand while debugging the 403:
    Storage Blob Delegator at subscription scope, and Storage Blob Data Contributor at account
    scope. Those matter most. Account-scoped Contributor gives the API write access to the crops
    container, which contradicts the boundary the demo claims, so leaving it in place invites
    exactly the question you cannot answer.

    Nothing here deletes a resource group, an App Service, the storage account, Cosmos or a Foundry
    project. It only removes grants and uploaded files.

    Every deletion goes through ShouldProcess, so -WhatIf lists what would go without touching
    anything, and without -Confirm:$false you are asked about each one.

.PARAMETER Prefix
    Resource name prefix used by the deployment, e.g. enf-demo.

.PARAMETER PlatformResourceGroup
    Resource group holding the storage account and the API and worker identities.
    Defaults to "<Prefix>-platform".

.PARAMETER SubscriptionId
    Subscription to work in. Defaults to the one az is currently set to.

.PARAMETER GeometryProjectEndpoint
    Team A's Foundry project endpoint. Used by -FoundryFiles.

.PARAMETER IdentificationProjectEndpoint
    Team B's Foundry project endpoint. Used by -FoundryFiles. Both projects receive uploads, so
    pass both to clean both.

.PARAMETER FileAgeHours
    Only delete uploaded Foundry files older than this, so a scan running right now is not
    sabotaged. Default 6.

.PARAMETER ApiVersion
    Foundry data-plane API version. Must match ScanPipeline:FoundryApiVersion in the worker.

.PARAMETER RoleAssignments
    Clean up the obsolete role assignments. Implied when no other switch is given.

.PARAMETER FoundryFiles
    Clean up image uploads left behind by failed runs.

.EXAMPLE
    ./cleanup.ps1 -Prefix enf-demo -WhatIf
    Lists the obsolete role assignments and changes nothing.

.EXAMPLE
    ./cleanup.ps1 -Prefix enf-demo -Confirm:$false
    Removes them without prompting.

.EXAMPLE
    ./cleanup.ps1 -Prefix enf-demo -FoundryFiles `
        -GeometryProjectEndpoint $geo -IdentificationProjectEndpoint $id -Confirm:$false
    Also deletes image uploads older than six hours from both projects.

.NOTES
    Requires an az login that can delete role assignments (Owner or User Access Administrator) and,
    for -FoundryFiles, that can author in the projects you name.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)] [string] $Prefix,
    [string] $PlatformResourceGroup,
    [string] $SubscriptionId,
    [string] $GeometryProjectEndpoint,
    [string] $IdentificationProjectEndpoint,
    [int] $FileAgeHours = 6,
    [string] $ApiVersion = 'v1',
    [switch] $RoleAssignments,
    [switch] $FoundryFiles
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $PlatformResourceGroup) { $PlatformResourceGroup = "$Prefix-platform" }
if (-not $SubscriptionId) {
    $SubscriptionId = az account show --query id -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($SubscriptionId)) {
        throw 'Not logged in, or no subscription selected. Run az login first.'
    }
    $SubscriptionId = $SubscriptionId.Trim()
}

# With no switch at all the role assignments are the point, so do those.
if (-not $RoleAssignments -and -not $FoundryFiles) { $RoleAssignments = $true }

$storageName = ($Prefix -replace '-', '').ToLower() + 'stg'
$storageId = "/subscriptions/$SubscriptionId/resourceGroups/$PlatformResourceGroup" +
             "/providers/Microsoft.Storage/storageAccounts/$storageName"

$script:removed = 0
$script:pending = 0

function Get-PrincipalId {
    param([string] $IdentityName)

    $id = az identity show -g $PlatformResourceGroup -n $IdentityName --query principalId -o tsv 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($id)) {
        Write-Warning "Identity $IdentityName not found in $PlatformResourceGroup; skipping its grants."
        return $null
    }
    return $id.Trim()
}

# Deletes by assignment id rather than by --assignee/--role/--scope, because the latter quietly
# matches nothing when the scope string differs only in case, and ARM is inconsistent about
# "resourceGroups" versus "resourcegroups" in the scopes it hands back.
function Remove-Grant {
    param(
        [string] $PrincipalId,
        [string] $RoleName,
        [string] $ScopePrefix,
        [string] $Why
    )

    if (-not $PrincipalId) { return }

    $all = az role assignment list --assignee $PrincipalId --all -o json | ConvertFrom-Json
    $doomed = @($all | Where-Object {
        $_.roleDefinitionName -eq $RoleName -and $_.scope -like "$ScopePrefix*"
    })

    foreach ($grant in $doomed) {
        $what = "$RoleName for $PrincipalId at $($grant.scope)"
        if ($PSCmdlet.ShouldProcess($what, "Delete role assignment - $Why")) {
            az role assignment delete --ids $grant.id --yes -o none
            if ($LASTEXITCODE -eq 0) {
                Write-Host "  removed       $what" -ForegroundColor Yellow
                $script:removed++
            }
            else {
                Write-Warning "  failed to remove $what"
            }
        }
        else {
            Write-Host "  would remove  $what" -ForegroundColor DarkYellow
            $script:pending++
        }
    }
}

if ($RoleAssignments) {
    Write-Host 'Obsolete role assignments' -ForegroundColor Cyan

    $apiPrincipal = Get-PrincipalId "$Prefix-api-id"
    $workerPrincipal = Get-PrincipalId "$Prefix-worker-id"
    $cardidPrincipal = Get-PrincipalId "$Prefix-cardid-id"

    # Nothing mints a user-delegation SAS any more, so Delegator has no purpose. The
    # subscription-scoped one was never in the template to begin with.
    Remove-Grant $apiPrincipal 'Storage Blob Delegator' "/subscriptions/$SubscriptionId" `
        'the API no longer mints a SAS'
    Remove-Grant $workerPrincipal 'Storage Blob Delegator' "/subscriptions/$SubscriptionId" `
        'the worker sends bytes to Foundry instead of a read SAS'

    # Account-scoped Contributor covers both containers. The template grants the API write on the
    # scans container alone, which is the claim the demo actually makes.
    Remove-Grant $apiPrincipal 'Storage Blob Data Contributor' $storageId `
        'account scope also covers crops; the container-scoped grant is the real one'

    # Team B receives the crop as an uploaded file and never reads the container.
    Remove-Grant $cardidPrincipal 'Storage Blob Data Reader' `
        "$storageId/blobServices/default/containers/crops" `
        'Team B receives crops through the Foundry Files API, not from storage'

    if ($script:removed -eq 0 -and $script:pending -eq 0) {
        Write-Host '  nothing to remove' -ForegroundColor Green
    }
}

if ($FoundryFiles) {
    Write-Host 'Leaked Foundry file uploads' -ForegroundColor Cyan

    $endpoints = @($GeometryProjectEndpoint, $IdentificationProjectEndpoint) |
                 Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    if (-not $endpoints) {
        throw 'Pass -GeometryProjectEndpoint and/or -IdentificationProjectEndpoint with -FoundryFiles.'
    }

    $token = az account get-access-token --resource 'https://ai.azure.com' --query accessToken -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
        throw 'Could not obtain a token for https://ai.azure.com. Run az login first.'
    }
    $headers = @{ Authorization = 'Bearer ' + $token.Trim() }
    $cutoff = [DateTimeOffset]::UtcNow.AddHours(-$FileAgeHours)

    foreach ($raw in $endpoints) {
        $ep = $raw.TrimEnd('/')
        Write-Host "  $ep"

        $list = Invoke-RestMethod -Method GET -Uri "$ep/files?api-version=$ApiVersion" -Headers $headers
        $stale = @($list.data | Where-Object {
            [DateTimeOffset]::FromUnixTimeSeconds($_.created_at) -lt $cutoff
        })

        foreach ($file in $stale) {
            $created = [DateTimeOffset]::FromUnixTimeSeconds($file.created_at).ToString('u')
            $what = "$($file.id)  $($file.filename)  $created"
            if ($PSCmdlet.ShouldProcess($what, 'Delete Foundry file')) {
                Invoke-RestMethod -Method DELETE -Uri "$ep/files/$($file.id)?api-version=$ApiVersion" `
                    -Headers $headers | Out-Null
                Write-Host "    removed       $what" -ForegroundColor Yellow
                $script:removed++
            }
            else {
                Write-Host "    would remove  $what" -ForegroundColor DarkYellow
                $script:pending++
            }
        }

        if (-not $stale) { Write-Host "    nothing older than $FileAgeHours h" -ForegroundColor Green }
    }
}

Write-Host ''
Write-Host "$($script:removed) item(s) removed." -ForegroundColor Cyan
if ($script:pending -gt 0) {
    Write-Host "$($script:pending) more would be removed; re-run with -Confirm:`$false." -ForegroundColor Cyan
}
