<#
.SYNOPSIS
    Migrates a deployment from the old App Service hosting to Container Apps.

.DESCRIPTION
    This repository used to host the API, the worker and the three MCP servers on App Service. It
    now runs all five as container apps. An existing deployment cannot simply be redeployed onto
    the new templates, for two reasons:

    * ARM incremental deployments never delete a resource that was merely removed from a template,
      so the old sites and plans survive every redeploy, keep billing, and keep holding their VNet
      integration subnets.

    * The subnets changed size, name and delegation. A Container Apps environment needs a /27
      delegated to Microsoft.App/environments; App Service needed a /28 delegated to
      Microsoft.Web/serverFarms. The new ranges overlap the old ones, and a subnet cannot be
      resized or re-delegated while anything sits in it.

    So the old compute and the old VNet have to go before the deployment, not as part of it. That
    ordering is the whole reason this script exists: each step is individually a one-liner, but
    getting them out of order produces errors that describe the symptom and not the cause.

    Everything else is left alone and survives: the resource groups, the four managed identities
    and their role assignments, storage, Cosmos, the Foundry account, both projects, the agents and
    their asst_ ids, the private DNS zones, and both app registrations.

    Steps that need a value only you have — re-provisioning the agents, and repointing the desktop
    app's aiconfig.txt — are not done here. They are printed at the end with the values filled in.

    Re-running is safe. Every step checks whether it is still needed and says so if it is not.

.PARAMETER Prefix
    Resource name prefix used by the deployment, e.g. enf-demo.

.PARAMETER Location
    Location of the subscription-scope deployment record. Must match the location the deployment
    was first created with, because Azure pins it to the deployment name and rejects a later
    mismatch outright. Read from the existing deployment if you leave it unset.

.PARAMETER ParametersFile
    Bicep parameters file. Defaults to infra/main.parameters.json relative to the repository root.

.PARAMETER ImageTag
    Tag to build and deploy, e.g. v1.

.PARAMETER DeploymentName
    Name of the subscription-scope deployment. Defaults to enfolderer-scan.

.PARAMETER SkipImageBuild
    Do everything except build the images. Useful if `az acr build` is blocked by policy in your
    tenant and you intend to push the images another way: the script stops before the redeploy that
    would point the apps at tags that do not exist.

.PARAMETER ImagesAlreadyPushed
    Skip the build and carry on to the redeploy regardless, because the images are already in the
    registry under -ImageTag. This is how you finish a migration that -SkipImageBuild stopped: a
    plain re-run would try to build again and fail at exactly the same point.

.EXAMPLE
    ./scripts/migrate-to-containerapps.ps1 -Prefix enf-demo -WhatIf
    Surveys the deployment and prints every action it would take, changing nothing.

.EXAMPLE
    ./scripts/migrate-to-containerapps.ps1 -Prefix enf-demo -Confirm:$false
    Runs the migration.

.NOTES
    Run from the repository root: the image build uses the repository as its build context.

    Requires an az login with rights to delete resources and to create role assignments (Owner or
    User Access Administrator — Contributor is not enough, because the deployment grants RBAC), and
    the containerapp extension: az extension add --name containerapp --upgrade.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory = $true)] [string] $Prefix,
    [string] $Location,
    [string] $ParametersFile = 'infra/main.parameters.json',
    [string] $ImageTag = 'v1',
    [string] $DeploymentName = 'enfolderer-scan',
    [switch] $SkipImageBuild,
    [switch] $ImagesAlreadyPushed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$platformRg = "$Prefix-platform"
$geoRg      = "$Prefix-cardgeo"
$idRg       = "$Prefix-cardid"
$vnetName   = "$Prefix-vnet"

# Keyed by image repository, valued by the project the Dockerfile's PROJECT argument selects.
$images = [ordered]@{
    'api'                     = 'Enfolderer.Ai.Api'
    'worker'                  = 'Enfolderer.Ai.Worker'
    'mcp-imaging'             = 'Enfolderer.Ai.Mcp.Imaging'
    'mcp-cardcatalog-mtg'     = 'Enfolderer.Ai.Mcp.CardCatalog.Mtg'
    'mcp-cardcatalog-pokemon' = 'Enfolderer.Ai.Mcp.CardCatalog.Pokemon'
}

# The sites and plans the old templates created, in the resource groups they were created in.
$oldSites = @(
    @{ Rg = $platformRg; Name = "$Prefix-api" }
    @{ Rg = $platformRg; Name = "$Prefix-worker" }
    @{ Rg = $geoRg;      Name = "$Prefix-mcp-imaging" }
    @{ Rg = $idRg;       Name = "$Prefix-mcp-cardcatalog-mtg" }
    @{ Rg = $idRg;       Name = "$Prefix-mcp-cardcatalog-pokemon" }
)

$oldPlans = @(
    @{ Rg = $platformRg; Name = "$Prefix-plan" }
    @{ Rg = $geoRg;      Name = "$Prefix-cardgeo-mcp-plan" }
    @{ Rg = $idRg;       Name = "$Prefix-cardid-mcp-plan" }
)

$oldPrivateEndpoints = @("$Prefix-stg-blob-pe", "$Prefix-stg-queue-pe")

function Write-Step {
    param([string] $Text)
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
}

function Write-Did  { param([string] $T) Write-Host "  $T" -ForegroundColor Yellow }
function Write-Skip { param([string] $T) Write-Host "  $T" -ForegroundColor DarkGray }
function Write-Would { param([string] $T) Write-Host "  would $T" -ForegroundColor DarkYellow }

# az reports "not found" on stderr and a non-zero exit code, which under $ErrorActionPreference =
# 'Stop' would end the run. Existence is a question, not an error, so ask it quietly.
function Test-AzResource {
    param([string] $ResourceGroup, [string] $Name, [string] $Namespace, [string] $Type)

    $id = az resource show -g $ResourceGroup -n $Name `
        --resource-type "$Namespace/$Type" --query id -o tsv 2>$null
    return ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($id))
}

# Runs a deployment and returns what Azure said if it failed. az writes the error JSON to stderr,
# and redirecting it into the success stream is not safe under $ErrorActionPreference = 'Stop' —
# Windows PowerShell turns native stderr into ErrorRecords and would throw before we could read it.
# A temporary file keeps the text out of the pipeline entirely.
function Invoke-Deployment {
    param([string[]] $ExtraParameters = @())

    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        az deployment sub create --name $DeploymentName --location $Location `
            --template-file infra/main.bicep --parameters $ParametersFile @ExtraParameters -o none `
            2>$errFile
        $exit = $LASTEXITCODE
        $text = ''
        if (Test-Path $errFile) { $text = (Get-Content $errFile -Raw) }
        if ($null -eq $text) { $text = '' }
        return [pscustomobject]@{ Succeeded = ($exit -eq 0); Output = $text }
    }
    finally {
        Remove-Item $errFile -Force -ErrorAction SilentlyContinue
    }
}

# Turns the one error code that is not our fault into advice. Everything else the deployment can
# say is already covered by docs/azure-setup.md, so point there rather than guessing.
function Show-DeploymentFailureHelp {
    param([string] $Output)

    Write-Host ''
    if ($Output -match 'NoAvailableCapacityInRegion') {
        Write-Host "$Location has no spare Container Apps capacity right now." -ForegroundColor Red
        Write-Host ''
        Write-Host 'This is not a fault in the template or in your subscription. Each environment'
        Write-Host 'is backed by a managed cluster, and Azure could not allocate one in this region'
        Write-Host 'at this moment. It is transient and usually clears within a few hours.'
        Write-Host ''
        Write-Host 'Wait, then re-run this same script:' -ForegroundColor DarkYellow
        Write-Host ''
        Write-Host "  ./scripts/migrate-to-containerapps.ps1 -Prefix $Prefix -Confirm:`$false"
        Write-Host ''
        Write-Host 'Re-run the script rather than the deployment on its own. A capacity failure'
        Write-Host 'leaves the environment behind in a Failed state, and ARM will not replace it;'
        Write-Host 'step 3b above is what deletes it so the retry has somewhere to build.'
        Write-Host ''
        Write-Host 'Moving to a region with capacity is a bigger job than changing one parameter:'
        Write-Host 'an environment must sit in the same region as its subnet, so storage, Cosmos,'
        Write-Host 'Foundry and the registry would all have to move too. See "The region is out of'
        Write-Host 'capacity" in docs/azure-setup.md before going that way.'
    }
    else {
        Write-Host 'The deployment failed. The error Azure returned is above.' -ForegroundColor Red
        Write-Host ''
        Write-Host 'To see which resource failed and why:' -ForegroundColor DarkYellow
        Write-Host ''
        Write-Host "  ./scripts/diagnose-containerapps.ps1 -Prefix $Prefix"
        Write-Host ''
        Write-Host '"If the deployment fails" in docs/azure-setup.md lists every cause seen so far.'
    }
    Write-Host ''
}

function Get-DeploymentOutput {
    param([string] $Name)

    $value = az deployment sub show --name $DeploymentName `
        --query "properties.outputs.$Name.value" -o tsv 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($value)) { return $null }
    return $value.Trim()
}

# ---------------------------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------------------------

Write-Step 'Preflight'

# Capture before converting: a failed az writes nothing to stdout, and ConvertFrom-Json on an
# empty string throws under $ErrorActionPreference = 'Stop', losing the message that explains it.
$accountJson = az account show -o json 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($accountJson)) {
    throw 'Not logged in. Run az login first.'
}
$account = $accountJson | ConvertFrom-Json
Write-Skip "subscription  $($account.name) ($($account.id))"

if (-not (Test-Path $ParametersFile)) {
    throw "Parameters file '$ParametersFile' not found. Run this from the repository root."
}
if (-not (Test-Path 'infra/main.bicep') -or -not (Test-Path 'src/Dockerfile')) {
    throw 'Run this from the repository root: infra/main.bicep and src/Dockerfile must be present.'
}

# The containerapp extension is needed by the follow-up commands this script prints, and its
# absence is much easier to explain now than halfway through.
az extension show --name containerapp -o none 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Warning 'The containerapp CLI extension is not installed. Run: az extension add --name containerapp --upgrade'
}

# A subscription-scope deployment pins its location to its name on the first run, so a later
# mismatch is rejected outright with InvalidDeploymentLocation and nothing is deployed. Take the
# location from the existing deployment rather than making the caller remember it.
$existingLocation = az deployment sub show --name $DeploymentName --query location -o tsv 2>$null
if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($existingLocation)) {
    $existingLocation = $existingLocation.Trim()
    if (-not $Location) {
        $Location = $existingLocation
        Write-Skip "location      $Location (from the existing '$DeploymentName' deployment)"
    }
    elseif ($Location -ne $existingLocation) {
        throw ("Deployment '$DeploymentName' already exists in '$existingLocation', but -Location " +
               "is '$Location'. Azure pins a subscription-scope deployment's location to its name " +
               'and would reject this. Pass the existing location, or use a different ' +
               '-DeploymentName.')
    }
}
elseif (-not $Location) {
    throw "No existing '$DeploymentName' deployment to read the location from. Pass -Location."
}

# The parameters file is committed with placeholder GUIDs. Deploying with those produces role
# assignments against principals that do not exist, which fails deep inside the deployment.
$parameters = Get-Content $ParametersFile -Raw | ConvertFrom-Json
foreach ($name in 'teamAGroupObjectId', 'teamBGroupObjectId', 'apiClientId') {
    $value = $parameters.parameters.$name.value
    if ($value -eq '00000000-0000-0000-0000-000000000000') {
        throw "$ParametersFile still has the placeholder value for '$name'. Fill it in first."
    }
}
Write-Skip "parameters    $ParametersFile"

# A Container Apps environment on a custom VNet creates a public IP and a load balancer in *your*
# subscription, in a managed resource group prefixed ME_. That is a bring-your-own-public-IP
# operation as far as Microsoft.Network is concerned, and it is gated on a subscription feature.
#
# The reason to check it here rather than let the deployment find out: the gate is not enforced at
# ARM admission. The deployment is accepted, runs for twenty minutes, allocates a cluster, and only
# then fails inside ConfigureAllocatedClusterHandler with a message about a feature. Worse, the
# environment is left in a state that a later retry cannot repair.
$featureState = az feature show --namespace Microsoft.Network `
    --name AllowBringYourOwnPublicIpAddress --query properties.state -o tsv 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($featureState)) { $featureState = 'Unknown' }
$featureState = $featureState.Trim()

if ($featureState -ne 'Registered') {
    # Printed rather than thrown: PowerShell reflows a multi-line exception message and the
    # commands below are the whole point of it.
    Write-Host ''
    Write-Host "Microsoft.Network/AllowBringYourOwnPublicIpAddress is $featureState." -ForegroundColor Red
    Write-Host 'Every Container Apps environment on a custom VNet will fail without it. Register it:' -ForegroundColor Red
    Write-Host ''
    Write-Host '  az feature register --namespace Microsoft.Network --name AllowBringYourOwnPublicIpAddress'
    Write-Host '  az feature show --namespace Microsoft.Network --name AllowBringYourOwnPublicIpAddress -o table'
    Write-Host '  # once it reads Registered:'
    Write-Host '  az provider register --namespace Microsoft.Network --wait'
    Write-Host ''
    Write-Host 'Registration is self-service and usually takes seconds. The provider re-registration is'
    Write-Host 'not optional: the feature has no effect until Microsoft.Network is refreshed. If the'
    Write-Host 'state is still Registering after a day, raise a support request.'
    Write-Host ''
    Write-Host 'If registration succeeds but the deployment is then refused with RequestDisallowedByPolicy,'
    Write-Host 'a tenant policy is blocking public IP creation outright. That needs a policy exemption,'
    Write-Host 'not a change in this repository. Prove it cheaply before redeploying:'
    Write-Host ''
    Write-Host "  az network public-ip create -g $platformRg -n byoip-probe --sku Standard --allocation-method Static"
    Write-Host "  az network public-ip delete -g $platformRg -n byoip-probe"
    Write-Host ''
    throw 'Subscription is not registered for Microsoft.Network/AllowBringYourOwnPublicIpAddress.'
}
Write-Skip "byo public ip $featureState"

# ---------------------------------------------------------------------------------------------
# 1. Survey
# ---------------------------------------------------------------------------------------------

Write-Step '1. What is there now'

$sitesToDelete = @($oldSites | Where-Object {
    Test-AzResource $_.Rg $_.Name 'Microsoft.Web' 'sites'
})
$plansToDelete = @($oldPlans | Where-Object {
    Test-AzResource $_.Rg $_.Name 'Microsoft.Web' 'serverfarms'
})
# The old and new VNets share a name, so existence alone cannot tell them apart. The subnet names
# can: the App Service templates made 'platform', the Container Apps templates make 'platform-apps'.
# Getting this wrong is expensive in both directions — deleting the new VNet throws away private
# endpoints that are already correct, and keeping the old one makes the deployment fail on a subnet
# it cannot resize.
$subnetNames = @()
$vnetExists = Test-AzResource $platformRg $vnetName 'Microsoft.Network' 'virtualNetworks'
if ($vnetExists) {
    $json = az network vnet show -g $platformRg -n $vnetName --query "subnets[].name" -o json 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($json)) {
        $subnetNames = @($json | ConvertFrom-Json)
    }
}
$vnetIsCurrent = $vnetExists -and ($subnetNames -contains 'platform-apps')

Write-Skip "App Service sites   $($sitesToDelete.Count) of $($oldSites.Count)"
Write-Skip "App Service plans   $($plansToDelete.Count) of $($oldPlans.Count)"
Write-Skip ("VNet                " + $(
    if (-not $vnetExists) { 'none' }
    elseif ($vnetIsCurrent) { "$vnetName (already migrated, keeping)" }
    else { "$vnetName (App Service era, replacing)" }))

if (-not $vnetExists) {
    # Worth saying plainly: an account with public access disabled and no private path refuses
    # everything, and it looks exactly like a missing role assignment.
    Write-Skip ''
    Write-Skip 'No VNet yet, so storage has had no private path at all. That alone would explain'
    Write-Skip 'every 403 so far: the account is publicNetworkAccess: Disabled.'
}
elseif ($vnetIsCurrent) {
    # An environment pulls its own system images, and the app images, out through this subnet, and
    # it needs AAD to use a managed identity. The template attaches neither an NSG nor a route
    # table, so anything here was put there by tenant policy — and a deny-all or a 0.0.0.0/0 route
    # to a firewall stops every revision from provisioning, with an empty ARM error to show for it.
    foreach ($subnet in 'platform-apps', 'cardgeo-apps', 'cardid-apps') {
        $json = az network vnet subnet show -g $platformRg --vnet-name $vnetName -n $subnet `
            --query "{nsg:networkSecurityGroup.id, routeTable:routeTable.id}" -o json 2>$null
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) { continue }
        $attached = $json | ConvertFrom-Json
        $notes = @()
        if ($attached.nsg) { $notes += "NSG $(Split-Path $attached.nsg -Leaf)" }
        if ($attached.routeTable) { $notes += "route table $(Split-Path $attached.routeTable -Leaf)" }
        if ($notes) {
            Write-Skip "subnet $subnet has $($notes -join ' and ') attached, which this template did not add."
            Write-Skip '  Outbound to MicrosoftContainerRegistry, AzureFrontDoor.FirstParty,'
            Write-Skip '  AzureActiveDirectory (443) and 168.63.129.16 (53) must survive it, or no'
            Write-Skip '  revision will start. See docs/azure-setup.md, "If the deployment fails".'
        }
    }
}

# ---------------------------------------------------------------------------------------------
# 2. Delete the App Service tier
# ---------------------------------------------------------------------------------------------

Write-Step '2. Delete the App Service tier'

if (-not $sitesToDelete -and -not $plansToDelete) {
    Write-Skip 'already gone'
}

# Sites before plans: a plan will not delete while it still hosts a site.
foreach ($site in $sitesToDelete) {
    $what = "$($site.Name) in $($site.Rg)"
    if ($PSCmdlet.ShouldProcess($what, 'Delete App Service site')) {
        # --keep-empty-plan makes the plan loop below authoritative. Without it az decides for
        # itself whether to take the plan with the site, and the plan this run already surveyed
        # would then fail to delete because it was removed underneath us.
        az webapp delete -g $site.Rg -n $site.Name --keep-empty-plan -o none
        if ($LASTEXITCODE -ne 0) { throw "Failed to delete site $what." }
        Write-Did "deleted site  $what"
    }
    else {
        Write-Would "delete site   $what"
    }
}

foreach ($plan in $plansToDelete) {
    $what = "$($plan.Name) in $($plan.Rg)"
    if ($PSCmdlet.ShouldProcess($what, 'Delete App Service plan')) {
        az appservice plan delete -g $plan.Rg -n $plan.Name --yes -o none
        if ($LASTEXITCODE -ne 0) { throw "Failed to delete plan $what." }
        Write-Did "deleted plan  $what"
    }
    else {
        Write-Would "delete plan   $what"
    }
}

# ---------------------------------------------------------------------------------------------
# 3. Delete the old VNet
# ---------------------------------------------------------------------------------------------

Write-Step '3. Delete the old VNet'

# A plan under a name the old templates never used still holds its integration subnet, and the
# VNet delete would fail on it with a message about the subnet rather than about the plan. Name
# the real obstacle instead.
$knownPlanNames = @($oldPlans | ForEach-Object { $_.Name })
$strayPlans = @()
foreach ($rg in $platformRg, $geoRg, $idRg) {
    $json = az appservice plan list -g $rg --query "[].name" -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) { continue }
    $strayPlans += @(($json | ConvertFrom-Json) |
        Where-Object { $_ -notin $knownPlanNames } |
        ForEach-Object { "$_ in $rg" })
}
if ($strayPlans) {
    # Under -WhatIf the plans above have not actually been deleted, so this is a warning rather
    # than a stop: the point is to surface it while there is still time to look.
    $message = ('App Service plans this script does not know about are still present and will ' +
                'hold their integration subnets: ' + ($strayPlans -join ', ') +
                '. Delete them, then re-run.')
    if ($WhatIfPreference) { Write-Warning $message } else { throw $message }
}

if (-not $vnetExists) {
    Write-Skip 'nothing to delete'
}
elseif ($vnetIsCurrent) {
    Write-Skip 'already has the Container Apps subnets; leaving it and its private endpoints alone'
}
else {
    # Private endpoints first; the VNet will not delete while they are in it. The private DNS
    # zones are deliberately left: a VNet's resource id is path-based, so when the template
    # recreates the VNet under the same name the existing zone links still point at it.
    foreach ($pe in $oldPrivateEndpoints) {
        if (-not (Test-AzResource $platformRg $pe 'Microsoft.Network' 'privateEndpoints')) {
            continue
        }
        if ($PSCmdlet.ShouldProcess($pe, 'Delete private endpoint')) {
            az network private-endpoint delete -g $platformRg -n $pe -o none
            if ($LASTEXITCODE -ne 0) { throw "Failed to delete private endpoint $pe." }
            Write-Did "deleted pe    $pe"
        }
        else {
            Write-Would "delete pe     $pe"
        }
    }

    if ($PSCmdlet.ShouldProcess($vnetName, 'Delete virtual network')) {
        az network vnet delete -g $platformRg -n $vnetName -o none
        if ($LASTEXITCODE -ne 0) {
            throw ("Failed to delete $vnetName. Something is still using a subnet. As well as " +
                   'resources the old templates did not create, look for a Container Apps ' +
                   'environment: a failed one still holds a service association link on its ' +
                   "subnet. az network vnet subnet show -g $platformRg --vnet-name $vnetName " +
                   "-n platform-apps --query serviceAssociationLinks")
        }
        Write-Did "deleted vnet  $vnetName"
    }
    else {
        Write-Would "delete vnet   $vnetName"
    }
}

# ---------------------------------------------------------------------------------------------
# 3b. Remove Container Apps environments left in a failed state
# ---------------------------------------------------------------------------------------------

Write-Step '3b. Clear failed Container Apps environments'

# A failed environment cannot be repaired by redeploying over it. It can come back reporting
# Succeeded while its staticIp is null and its managed resource group holds no public IP or load
# balancer, at which point every app in it fails to start for reasons that have nothing to do with
# the app. It also keeps a service association link on its subnet, so the subnet cannot be reused.
# Delete it and wait for it to actually be gone.
$failedEnvs = @()
foreach ($rg in $platformRg, $geoRg, $idRg) {
    $json = az containerapp env list -g $rg `
        --query "[].{Name:name, State:properties.provisioningState, Group:properties.infrastructureResourceGroup, StaticIp:properties.staticIp, Internal:properties.vnetConfiguration.internal}" `
        -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) { continue }
    $failedEnvs += @(($json | ConvertFrom-Json) |
        Where-Object {
            # Outright failed, or the quieter version: reporting Succeeded after a repair attempt
            # but with no ingress IP, which is what a redeployed-over failure looks like. The
            # environment is unusable either way, and only the second kind looks healthy.
            $_.State -notin 'Succeeded', 'Waiting', 'InProgress' -or
            ($_.State -eq 'Succeeded' -and -not $_.Internal -and [string]::IsNullOrWhiteSpace($_.StaticIp))
        } |
        ForEach-Object {
            $state = if ($_.State -eq 'Succeeded') { 'Succeeded but no staticIp' } else { $_.State }
            [pscustomobject]@{ Rg = $rg; Name = $_.Name; State = $state; Group = $_.Group }
        })
}

if (-not $failedEnvs) {
    Write-Skip 'none'
}
foreach ($failed in $failedEnvs) {
    $what = "$($failed.Name) in $($failed.Rg) ($($failed.State))"
    if (-not $PSCmdlet.ShouldProcess($what, 'Delete failed Container Apps environment')) {
        Write-Would "delete env    $what"
        continue
    }

    # An environment cannot be deleted while container apps still reference it, and after a failed
    # deployment there are always some — they exist as resources even though their revisions never
    # provisioned. Clear them first, or the delete below fails silently and we spend 20 minutes
    # polling for something that was never going to happen.
    $appsJson = az containerapp list -g $failed.Rg `
        --query "[?ends_with(properties.environmentId, '/$($failed.Name)')].name" -o json 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($appsJson)) {
        foreach ($appName in @($appsJson | ConvertFrom-Json)) {
            az containerapp delete -g $failed.Rg -n $appName --yes -o none 2>$null
            Write-Did "deleted app   $appName in $($failed.Rg)"
        }
    }

    az containerapp env delete -g $failed.Rg -n $failed.Name --yes -o none 2>$null
    # The CLI returns before the deletion finishes, and recreating over the top fails with
    # ManagedEnvironmentScheduledForDelete. Poll rather than trust the exit code.
    $deadline = (Get-Date).AddMinutes(20)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-AzResource $failed.Rg $failed.Name 'Microsoft.App' 'managedEnvironments')) { break }
        Start-Sleep -Seconds 15
    }
    if (Test-AzResource $failed.Rg $failed.Name 'Microsoft.App' 'managedEnvironments') {
        throw "Timed out waiting for $what to delete. Check it in the portal before re-running."
    }

    # The ME_ group outlives the environment by a little, and its leftover load balancer is enough
    # to keep the subnet's service association link alive.
    if ($failed.Group) {
        $deadline = (Get-Date).AddMinutes(20)
        while ((Get-Date) -lt $deadline) {
            az group show -n $failed.Group -o none 2>$null
            if ($LASTEXITCODE -ne 0) { break }
            Start-Sleep -Seconds 15
        }
    }
    Write-Did "deleted env   $what"
}

# ---------------------------------------------------------------------------------------------
# 4. Deploy the infrastructure
# ---------------------------------------------------------------------------------------------

Write-Step '4. Deploy the infrastructure'

# The first deployment deliberately passes no imageTag: the registry it creates has no images yet,
# so every container app starts on a placeholder and is pointed at the real images in step 6.
if ($PSCmdlet.ShouldProcess($DeploymentName, 'Deploy infra/main.bicep')) {
    $result = Invoke-Deployment
    if (-not $result.Succeeded) {
        if ($result.Output) { Write-Host $result.Output }
        Show-DeploymentFailureHelp $result.Output
        throw 'Deployment failed.'
    }
    Write-Did 'deployed (apps on the placeholder image)'
}
else {
    Write-Would "deploy infra/main.bicep as '$DeploymentName' in $Location"
    Write-Host ''
    Write-Host 'Stopping here: the remaining steps all read outputs from that deployment.' -ForegroundColor DarkYellow
    Write-Host "Re-run with -Confirm:`$false to carry them out." -ForegroundColor DarkYellow
    return
}

$registry = Get-DeploymentOutput 'registryName'
if (-not $registry) { throw 'Deployment produced no registryName output.' }
Write-Skip "registry      $registry"

# ---------------------------------------------------------------------------------------------
# 5. Build the images
# ---------------------------------------------------------------------------------------------

Write-Step '5. Build the images'

if ($ImagesAlreadyPushed) {
    Write-Skip "-ImagesAlreadyPushed was given; assuming enfolderer/*:$ImageTag are in $registry"
}
elseif ($SkipImageBuild) {
    Write-Skip '-SkipImageBuild was given; not building'
    Write-Host ''
    Write-Host "Push images tagged '$ImageTag' to $registry, then finish with:" -ForegroundColor DarkYellow
    # Echo back the values this run was given, not the defaults: resuming against a different
    # deployment or parameters file would deploy something other than what was just torn down.
    $resume = "  ./scripts/migrate-to-containerapps.ps1 -Prefix $Prefix -ImageTag $ImageTag " +
              "-Location $Location -DeploymentName $DeploymentName " +
              "-ParametersFile '$ParametersFile' -ImagesAlreadyPushed -Confirm:`$false"
    Write-Host $resume -ForegroundColor DarkYellow
    return
}
else {
    foreach ($image in $images.Keys) {
        $project = $images[$image]
        if ($PSCmdlet.ShouldProcess("$image`:$ImageTag", 'Build image in ACR')) {
            az acr build --registry $registry --image "enfolderer/$image`:$ImageTag" `
                --build-arg "PROJECT=$project" --file src/Dockerfile .
            if ($LASTEXITCODE -ne 0) {
                throw ("Image build failed for $image. If the registry refused the connection, " +
                       'your tenant may block its public endpoint the same way it blocks storage; ' +
                       'see docs/azure-setup.md.')
            }
            Write-Did "built         enfolderer/$image`:$ImageTag"
        }
    }
}

# ---------------------------------------------------------------------------------------------
# 6. Redeploy with the tag
# ---------------------------------------------------------------------------------------------

Write-Step '6. Point the apps at the images'

# Not replaceable by `az containerapp update --image`: as well as the image, this deployment adds
# the registry configuration that lets each app pull as its own managed identity.
if ($PSCmdlet.ShouldProcess($DeploymentName, "Redeploy with imageTag=$ImageTag")) {
    $result = Invoke-Deployment -ExtraParameters @('--parameters', "imageTag=$ImageTag")
    if (-not $result.Succeeded) {
        if ($result.Output) { Write-Host $result.Output }
        Show-DeploymentFailureHelp $result.Output
        throw 'Redeployment with the image tag failed.'
    }
    Write-Did "redeployed with imageTag=$ImageTag"
}

$placeholders = @()
foreach ($rg in $platformRg, $geoRg, $idRg) {
    $json = az containerapp list -g $rg `
        --query "[].{Name:name, Image:properties.template.containers[0].image}" -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) { continue }
    $placeholders += @(($json | ConvertFrom-Json) |
        Where-Object { $_.Image -like 'mcr.microsoft.com/k8se/*' } |
        ForEach-Object { "$($_.Name) in $rg" })
}
if ($placeholders) {
    Write-Warning ('Still on the placeholder image: ' +
        (($placeholders | Select-Object -Unique) -join ', ') +
        '. The redeploy did not reach them; check that the images exist in the registry.')
}

# ---------------------------------------------------------------------------------------------
# What is left, with the values filled in
# ---------------------------------------------------------------------------------------------

$apiUrl = Get-DeploymentOutput 'apiUrl'
$geo    = Get-DeploymentOutput 'geometryProjectEndpoint'
$ident  = Get-DeploymentOutput 'identificationProjectEndpoint'

Write-Step 'Done. Three things are left, and they need values only you have.'

Write-Host @"

  1. Re-provision the agents. CardBoundaryAgent no longer has any tools, and every MCP URL
     changed, so this is not optional. provision.ps1 updates in place by name, so the asst_ ids
     you already recorded survive.

     `$mcp = @{}
     foreach (`$o in 'geometryMcpServerUrls','identificationMcpServerUrls') {
       (az deployment sub show --name $DeploymentName --query "properties.outputs.`$o.value" -o json |
          ConvertFrom-Json) | ForEach-Object { `$mcp[`$_.name] = `$_.url }
     }

     ./agents/provision.ps1 -ProjectEndpoint '$geo' ``
       -Path ./agents/cardgeo -McpServerUrl `$mcp

     ./agents/provision.ps1 -ProjectEndpoint '$ident' ``
       -Path ./agents/cardid -McpServerUrl `$mcp ``
       -ConnectedAgentId @{ 'cardgeo/CardBoundaryAgent' = '<boundary agent asst_ id>' }

  2. Tell the worker the agent ids, if they are not the agent names:

     az containerapp update -g $platformRg -n "$Prefix-worker" --set-env-vars ``
       ScanPipeline__BoundaryAgentId='<boundary asst_ id>' ``
       ScanPipeline__IdentificationAgentIds__mtg='<mtg asst_ id>' ``
       ScanPipeline__IdentificationAgentIds__pokemon='<pokemon asst_ id>'

  3. Repoint the desktop app. The API hostname has changed:

     api_base_url=$apiUrl

     in aiconfig.txt beside Enfolderer.App.exe. Nothing else in that file changes.

  Then watch a scan go through:

     Invoke-RestMethod '$apiUrl/healthz'
     az containerapp logs show -g $platformRg -n "$Prefix-worker" --follow --tail 50

  Worth doing separately: ./scripts/cleanup.ps1 -Prefix $Prefix -WhatIf sweeps the role
  assignments left behind by the old topology, including an account-scoped grant that gives the
  API more than the demo claims it has.

"@
