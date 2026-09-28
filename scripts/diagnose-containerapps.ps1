<#
.SYNOPSIS
    Explains why the container apps in this deployment will not start.

.DESCRIPTION
    ARM reports a revision that fails to provision as

        ContainerAppOperationError: Failed to provision revision for container app '<name>'.
        Error details: .

    with nothing after "Error details:", and `az deployment operation group list` returns the same
    string with `details: null`. The real reason is never in the deployment record. It is spread
    across the environment, the subnet, the app and the revision, and this script reads all four and
    says which one is at fault.

    It changes nothing. Every call is a read.

    The four causes it can tell apart:

    * The environment came back from a failed deployment reporting Succeeded but with no ingress IP.
      Nothing can start in it. It must be deleted and recreated, which is what step 3b of
      migrate-to-containerapps.ps1 does.

    * Tenant policy attached an NSG or a route table to an apps subnet. A VNet-injected environment
      pulls its own platform images and yours out through that subnet, so a deny-all or a forced
      tunnel stops every revision.

    * Nothing is listening on the ingress target port, so the startup probe never passes.

    * The image could not be pulled, in which case no revision is created at all.

.PARAMETER Prefix
    Resource name prefix used by the deployment, e.g. enf-demo.

.EXAMPLE
    ./scripts/diagnose-containerapps.ps1 -Prefix enf-demo

.NOTES
    Requires an az login with read access, and the containerapp extension:
    az extension add --name containerapp --upgrade.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Prefix
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string] $Text)
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
}

function Write-Note { param([string] $T) Write-Host "  $T" -ForegroundColor DarkGray }
function Write-Bad  { param([string] $T) Write-Host "  $T" -ForegroundColor Red }
function Write-Good { param([string] $T) Write-Host "  $T" -ForegroundColor Green }

# az answers "not found" with a non-zero exit code and a message on stderr. Absence is one of the
# answers this script is looking for, so ask quietly and decide here.
function Invoke-AzJson {
    param([string[]] $Arguments)

    $json = az @Arguments -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) { return $null }
    try { return $json | ConvertFrom-Json } catch { return $null }
}

$platformRg = "$Prefix-platform"
$geoRg      = "$Prefix-cardgeo"
$idRg       = "$Prefix-cardid"
$vnetName   = "$Prefix-vnet"

# Findings are collected rather than printed as they are found, so that the verdict at the end can
# rank them: several of these causes produce each other's symptoms, and the order matters.
$brokenEnvs = @()
$attachedSubnets = @()
$appFindings = @()

# ---------------------------------------------------------------------------------------------
# 1. The environments
# ---------------------------------------------------------------------------------------------

Write-Step '1. Container Apps environments'

foreach ($rg in $platformRg, $geoRg, $idRg) {
    $found = Invoke-AzJson @('containerapp', 'env', 'list', '-g', $rg)
    if (-not $found) {
        Write-Note "$rg : no environment"
        continue
    }
    foreach ($e in @($found)) {
        $staticIp = $e.properties.staticIp
        $internal = $e.properties.vnetConfiguration.internal
        $state = $e.properties.provisioningState
        $profiles = @($e.properties.workloadProfiles | ForEach-Object { $_.name }) -join ','

        # An external environment with no ingress IP never finished building its load balancer,
        # whatever it claims about its provisioning state. This is what a failed environment looks
        # like after something redeployed over it, and it is the only failure here that lies.
        $isBroken = $state -ne 'Succeeded' -or (-not $internal -and [string]::IsNullOrWhiteSpace($staticIp))
        $line = "$($e.name) in $rg : $state, staticIp=$(if ([string]::IsNullOrWhiteSpace($staticIp)) { '(none)' } else { $staticIp }), profiles=$profiles"
        if ($isBroken) {
            Write-Bad $line
            $brokenEnvs += $e.name
        }
        else {
            Write-Good $line
        }
    }
}

# ---------------------------------------------------------------------------------------------
# 2. The subnets the environments run on
# ---------------------------------------------------------------------------------------------

Write-Step '2. Apps subnets'

# infra/modules/network.bicep attaches neither an NSG nor a route table, so anything here arrived
# some other way — in a governed tenant, almost certainly a policy with a Modify or
# DeployIfNotExists effect.
foreach ($subnet in 'platform-apps', 'cardgeo-apps', 'cardid-apps') {
    $s = Invoke-AzJson @('network', 'vnet', 'subnet', 'show', '-g', $platformRg, '--vnet-name', $vnetName, '-n', $subnet)
    if (-not $s) {
        Write-Note "$subnet : not found"
        continue
    }
    $notes = @()
    if ($s.networkSecurityGroup) { $notes += "NSG $(Split-Path $s.networkSecurityGroup.id -Leaf)" }
    if ($s.routeTable) { $notes += "route table $(Split-Path $s.routeTable.id -Leaf)" }
    $delegation = @($s.delegations | ForEach-Object { $_.serviceName }) -join ','

    if ($notes) {
        Write-Bad "$subnet : $($s.addressPrefix), delegated to $delegation, $($notes -join ' and ') attached"
        $attachedSubnets += $subnet
    }
    else {
        Write-Good "$subnet : $($s.addressPrefix), delegated to $delegation, nothing attached"
    }
}

# ---------------------------------------------------------------------------------------------
# 3. The apps and their revisions
# ---------------------------------------------------------------------------------------------

Write-Step '3. Container apps'

$apps = @(
    [pscustomobject]@{ Rg = $platformRg; Name = "$Prefix-api" }
    [pscustomobject]@{ Rg = $platformRg; Name = "$Prefix-worker" }
    [pscustomobject]@{ Rg = $geoRg;      Name = "$Prefix-mcp-imaging" }
    [pscustomobject]@{ Rg = $idRg;       Name = "$Prefix-mcp-cardcatalog-mtg" }
    [pscustomobject]@{ Rg = $idRg;       Name = "$Prefix-mcp-cardcatalog-pokemon" }
)

foreach ($app in $apps) {
    $a = Invoke-AzJson @('containerapp', 'show', '-g', $app.Rg, '-n', $app.Name)
    if (-not $a) {
        Write-Note "$($app.Name) : not deployed"
        continue
    }

    $image = $a.properties.template.containers[0].image
    $port = if ($a.properties.configuration.ingress) { $a.properties.configuration.ingress.targetPort } else { '(no ingress)' }
    Write-Host "  $($app.Name)" -ForegroundColor White
    Write-Note "state $($a.properties.provisioningState), image $image, targetPort $port"
    if ($a.properties.provisioningError) { Write-Bad "provisioningError: $($a.properties.provisioningError)" }

    # The placeholder listens on 80 and our own images on 8080, so a mismatch here means the default
    # TCP startup probe can never connect and the revision is failed by the platform after roughly
    # four minutes of trying.
    if ($port -ne '(no ingress)') {
        $isPlaceholder = $image -like 'mcr.microsoft.com/k8se/quickstart*'
        if ($isPlaceholder -and $port -ne 80) {
            Write-Bad "port mismatch: the placeholder image listens on 80 but targetPort is $port"
            $appFindings += "$($app.Name): targetPort $port against a placeholder that listens on 80"
        }
        elseif (-not $isPlaceholder -and $port -ne 8080) {
            Write-Bad "port mismatch: this image listens on 8080 but targetPort is $port"
            $appFindings += "$($app.Name): targetPort $port against an image that listens on 8080"
        }
    }

    $revisions = Invoke-AzJson @('containerapp', 'revision', 'list', '-g', $app.Rg, '-n', $app.Name)
    if (-not $revisions -or @($revisions).Count -eq 0) {
        # No revision at all means the failure happened before one could be created, which rules out
        # crashes and probes and leaves the image pull.
        Write-Bad 'no revisions — the app never got as far as creating one, so this is an image pull or a template rejection'
        $appFindings += "$($app.Name): no revision was ever created"
        continue
    }

    foreach ($r in @($revisions)) {
        $detail = $r.properties.runningStateDetails
        Write-Note "revision $($r.name): health=$($r.properties.healthState), running=$($r.properties.runningState), provisioning=$($r.properties.provisioningState)"
        if (-not [string]::IsNullOrWhiteSpace($detail)) {
            Write-Bad "runningStateDetails: $detail"
            $appFindings += "$($app.Name)/$($r.name): $detail"
        }
    }
}

# ---------------------------------------------------------------------------------------------
# 4. Verdict
# ---------------------------------------------------------------------------------------------

Write-Step '4. What to do'

if ($brokenEnvs) {
    # Ranked first deliberately: an environment with no ingress IP makes every app in it fail, so
    # anything found in section 3 is a symptom of this and not a separate problem.
    Write-Bad "These environments are unusable: $($brokenEnvs -join ', ')"
    Write-Note 'An external environment with no staticIp never finished building its load balancer.'
    Write-Note 'Redeploying will not repair one. Delete and recreate:'
    Write-Note ''
    Write-Note "  ./scripts/migrate-to-containerapps.ps1 -Prefix $Prefix -Confirm:`$false"
    Write-Note ''
    Write-Note 'Its step 3b deletes the apps, then the environment, then waits for the ME_ resource'
    Write-Note 'group to go too, which az containerapp env delete does not wait for. Ignore anything'
    Write-Note 'reported above about individual apps until this is fixed — it is downstream of this.'
}
elseif ($attachedSubnets) {
    Write-Bad "Policy has attached something to: $($attachedSubnets -join ', ')"
    Write-Note 'These templates attach neither an NSG nor a route table, so this came from tenant'
    Write-Note 'policy. Whatever it does, outbound from these subnets must still allow 443 to the'
    Write-Note 'MicrosoftContainerRegistry, AzureFrontDoor.FirstParty and AzureActiveDirectory'
    Write-Note 'service tags, and 53 to 168.63.129.16, or no revision can ever start. Behind a'
    Write-Note 'firewall the FQDNs are mcr.microsoft.com, *.data.mcr.microsoft.com,'
    Write-Note 'packages.aks.azure.com, acs-mirror.azureedge.net, login.microsoftonline.com and'
    Write-Note '*.identity.azure.net. This needs a policy exemption; it cannot be fixed in the template.'
}
elseif ($appFindings) {
    Write-Bad 'Per-app problems:'
    foreach ($f in $appFindings) { Write-Note "  $f" }
}
else {
    Write-Good 'Nothing conclusive. The apps and environments look healthy from the control plane.'
    Write-Note 'Next, read the platform events for one failing app — these are the messages that the'
    Write-Note 'deployment error omits:'
    Write-Note ''
    Write-Note "  az containerapp logs show -g $platformRg -n $Prefix-api --type system --tail 100"
    Write-Note ''
    Write-Note 'If that fails with KeyError: eventStreamEndpoint there is no revision to stream from,'
    Write-Note 'and the activity log is the only remaining source:'
    Write-Note ''
    Write-Note "  az monitor activity-log list -g $platformRg --offset 2h ``"
    Write-Note '    --query "[?status.value==''Failed''].{op:operationName.value, msg:properties.statusMessage}" -o json'
}

Write-Host ''
