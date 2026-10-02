<#
.SYNOPSIS
    Explains why the Foundry agents data plane is refusing a project.

.DESCRIPTION
    agents/provision.ps1 talks to the Foundry Agent Service over HTTP. When that service cannot
    resolve the project behind the endpoint it answers

        HTTP 500
        { "error": { "code": "InternalServerError", "message": "Unable to get resource information." } }

    which says nothing about which of the several things it needed was missing. The control plane
    does know, so this script reads it: the account, its subdomain and settings, any soft-deleted
    account squatting on the same name, the project, the model deployments and your own role
    assignments. Then it repeats the data-plane call and reports what came back.

    It changes nothing. Every call is a read.

    Causes it can tell apart:

    * The account or the project does not exist, or is not in a Succeeded state. The endpoint is
      built from names, so a typo or a half-finished deployment gives a perfectly well-formed URL
      that points at nothing.

    * A soft-deleted account of the same name still holds the subdomain. Cognitive Services accounts
      are soft-deleted for 48 hours, and the DNS name resolves to the dead one until it is purged.

    * The account is missing customSubDomainName or allowProjectManagement, so projects cannot be
      addressed through it.

    * You have no data-plane role on the project. Subscription Owner does not grant one, because
      agent authoring is a data action.

    * The token is fine and the resources are fine, in which case the fault is the service's and the
      script says so rather than inventing a cause.

.PARAMETER Prefix
    Resource name prefix used by the deployment, e.g. enf-demo.

.PARAMETER AccountName
    Foundry account name. Defaults to <Prefix>-ai, which is what infra/main.bicep creates under the
    default single-account layout. Pass it explicitly if you deployed with singleAccount=false.

.PARAMETER ResourceGroup
    Resource group holding the account. Defaults to <Prefix>-platform.

.PARAMETER Project
    Project names to check. Defaults to cardgeo and cardid.

.PARAMETER ApiVersion
    Data-plane api-version to probe with. Matches provision.ps1's default.

.EXAMPLE
    ./scripts/diagnose-foundry.ps1 -Prefix enf-demo

.NOTES
    Requires an az login with read access to the resource group.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Prefix,
    [string] $AccountName,
    [string] $ResourceGroup,
    [string[]] $Project = @('cardgeo', 'cardid'),
    [string] $ApiVersion = 'v1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $AccountName)   { $AccountName = "$Prefix-ai" }
if (-not $ResourceGroup) { $ResourceGroup = "$Prefix-platform" }

function Write-Step {
    param([string] $Text)
    Write-Host ''
    Write-Host $Text -ForegroundColor Cyan
}

function Write-Note { param([string] $T) Write-Host "  $T" -ForegroundColor DarkGray }
function Write-Bad  { param([string] $T) Write-Host "  $T" -ForegroundColor Red }
function Write-Good { param([string] $T) Write-Host "  $T" -ForegroundColor Green }
# Amber, for something that deserves attention but is not established as broken. Keeping it apart
# from Write-Bad matters: red next to "delete this" is how a diagnostic talks someone into
# destroying working resources.
function Write-Warn { param([string] $T) Write-Host "  $T" -ForegroundColor Yellow }

# az answers "not found" with a non-zero exit code and a message on stderr. Absence is one of the
# answers this script is looking for, so ask quietly and decide here.
function Invoke-AzJson {
    param([string[]] $Arguments)

    $json = az @Arguments -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) { return $null }
    # A native command's output arrives as an array of lines, and Windows PowerShell's
    # ConvertFrom-Json parses each pipeline item separately and throws on the first line of a
    # multi-line document. Join before converting.
    try { return ($json -join "`n") | ConvertFrom-Json } catch { return $null }
}

# The CLI omits a property entirely when its value is null, so on a healthy resource most optional
# fields are absent rather than empty. Under Set-StrictMode reading one directly throws, which would
# end the run at the moment it had something to report.
function Get-Prop {
    param([object] $Object, [string] $Name)

    if ($null -eq $Object) { return $null }
    $member = $Object.PSObject.Properties[$Name]
    if ($null -eq $member) { return $null }
    return $member.Value
}

# Pulls the agent names out of a list response. Different routes use different envelopes, so both
# documented keys are accepted.
#
# Returns $null when the reply carries no recognisable list at all, and an empty array when it
# carries a list that is genuinely empty. Those two mean opposite things -- "this route does not
# answer the question I asked" versus "this project has no agents" -- and collapsing them into a
# count of zero is what previously made every healthy agent look like it had gone missing.
function Get-AgentNames {
    param([object] $Response)

    $key = $null
    foreach ($candidate in 'data', 'value') {
        if ($Response -and $Response.PSObject.Properties[$candidate]) { $key = $candidate; break }
    }
    if ($null -eq $key) { return $null }

    $names = @()
    foreach ($item in @(Get-Prop $Response $key)) {
        if ($null -eq $item) { continue }
        $agentName = Get-Prop $item 'name'
        if ([string]::IsNullOrWhiteSpace($agentName)) { $agentName = Get-Prop $item 'id' }
        if (-not [string]::IsNullOrWhiteSpace($agentName)) { $names += [string] $agentName }
    }
    # A bare @() unrolls to $null on return, which the caller would read as "no list", so the
    # comma keeps an empty list an empty list.
    return ,@($names | Sort-Object)
}

# Reads the agent names this repository expects a project to have. provision.ps1 creates one agent
# per YAML file, skipping the mcp-* files, which describe tool servers rather than agents.
function Get-ExpectedAgentNames {
    param([string] $Project)

    $dir = Join-Path (Split-Path -Parent $PSScriptRoot) "agents/$Project"
    if (-not (Test-Path $dir)) { return ,@() }

    $names = @()
    foreach ($file in Get-ChildItem -Path $dir -Filter '*.yaml' | Where-Object { $_.Name -notlike 'mcp-*' }) {
        # The name is a top-level scalar, so a line match avoids taking a YAML parser dependency
        # for one field. Nested "name:" keys are indented and therefore do not match.
        $match = Select-String -Path $file.FullName -Pattern '^name:\s*(\S+)' | Select-Object -First 1
        if ($match) { $names += $match.Matches[0].Groups[1].Value }
    }
    return ,@($names | Sort-Object)
}

$findings = @()

# ---------------------------------------------------------------------------------------------
# 1. The account
# ---------------------------------------------------------------------------------------------

Write-Step '1. Foundry account'

$account = Invoke-AzJson @('cognitiveservices', 'account', 'show',
    '-n', $AccountName, '-g', $ResourceGroup)

$endpointHost = $null

if (-not $account) {
    Write-Bad "$AccountName not found in $ResourceGroup"
    $findings += "The account $AccountName does not exist in $ResourceGroup. Everything below follows from that."
}
else {
    $props = Get-Prop $account 'properties'
    $state = Get-Prop $props 'provisioningState'
    $kind  = Get-Prop $account 'kind'
    $sub   = Get-Prop $props 'customSubDomainName'
    $mgmt  = Get-Prop $props 'allowProjectManagement'
    $pna   = Get-Prop $props 'publicNetworkAccess'
    $local = Get-Prop $props 'disableLocalAuth'

    Write-Note "state $state, kind $kind, location $(Get-Prop $account 'location')"
    Write-Note "customSubDomainName $(if ($sub) { $sub } else { '(none)' })"
    Write-Note "allowProjectManagement $(if ($null -ne $mgmt) { $mgmt } else { '(not set)' }), publicNetworkAccess $(if ($pna) { $pna } else { '(not set)' }), disableLocalAuth $(if ($null -ne $local) { $local } else { '(not set)' })"

    if ($state -ne 'Succeeded') {
        Write-Bad "the account is $state, not Succeeded"
        $findings += "The account $AccountName is in state '$state'. The data plane will not serve a project until the account itself is Succeeded."
    }
    if ($kind -ne 'AIServices') {
        Write-Bad "kind is '$kind'; the agents data plane needs an AIServices account"
        $findings += "The account is kind '$kind'. Agents require kind 'AIServices'; this cannot be changed in place, the account has to be recreated."
    }
    if (-not $sub) {
        Write-Bad 'no customSubDomainName, so there is no *.services.ai.azure.com host to call'
        $findings += 'The account has no customSubDomainName, so the project endpoint has no DNS name behind it.'
    }
    else {
        $endpointHost = "https://$sub.services.ai.azure.com"
        if ($sub -ne $AccountName) {
            Write-Note "note: the subdomain differs from the account name, so the endpoint host is $endpointHost"
        }
    }
    if ($mgmt -ne $true) {
        Write-Bad 'allowProjectManagement is not true, so this account cannot serve projects'
        $findings += ('The account does not have allowProjectManagement=true. infra/modules/foundry-account.bicep ' +
                      'sets it, so redeploy rather than patching the account by hand.')
    }

    $reported = Get-Prop $props 'endpoint'
    if ($reported) { Write-Note "endpoint reported by the account: $reported" }

    # properties.endpoint (singular) is the legacy Cognitive Services host and is present on every
    # account, so it says nothing about agents. properties.endpoints (plural) is the map the
    # control plane actually advertises, and the agents data plane only answers for an account
    # that lists a Foundry entry there. An account can be Succeeded and still be missing it.
    $endpoints = Get-Prop $props 'endpoints'
    if (-not $endpoints) {
        Write-Bad 'the account advertises no endpoints map at all'
        $findings += ('The account reports no properties.endpoints. That is the map the agents data plane ' +
                      'is published in, so an account without it will refuse every project. See ' +
                      '"Neither agent surface answers" in docs/azure-setup.md.')
    }
    else {
        $names = @($endpoints.PSObject.Properties | ForEach-Object { $_.Name })
        Write-Note "endpoints advertised: $($names -join ', ')"
        # 'AI Foundry API' is the documented key, and its value is the services.ai.azure.com host
        # the scripts call. Accept a couple of near neighbours in case the service renames it, but
        # anchor them so an unrelated key that merely contains 'agent' cannot pass for it.
        $foundry = @($names | Where-Object { $_ -eq 'AI Foundry API' -or $_ -match '^(AI )?(Foundry|Agent)' })
        if ($foundry.Count -gt 0) {
            Write-Good "the account advertises an agents endpoint ($($foundry -join ', '))"
        }
        else {
            Write-Bad 'no Foundry/agent entry in the endpoints map'
            $findings += ('The account advertises ' + ($names -join ', ') + " but nothing for Foundry or agents. " +
                          'The agent backend was never wired up, which is why the data plane fails while every ' +
                          'control-plane state reads Succeeded. See "Neither agent surface answers" in docs/azure-setup.md.')
        }
    }

    # The documented kill switch for the classic agents surface. It is a plain account tag, so it
    # survives redeployment and is easy to set by accident.
    $tags = Get-Prop $account 'tags'
    if ($tags) {
        $killSwitch = Get-Prop $tags 'MS-AOAI-Feature-Assistants'
        if ($killSwitch -and $killSwitch -eq 'Disabled') {
            Write-Bad 'the account tag MS-AOAI-Feature-Assistants=Disabled is set'
            $findings += ('The account carries the tag MS-AOAI-Feature-Assistants=Disabled. It blocks creating ' +
                          'and updating classic agents, threads and runs, but not reading them, so it cannot be ' +
                          'why a list call fails - it will stop provision.ps1 writing, though. Clear it with: ' +
                          'az resource tag --ids $(az cognitiveservices account show -n ' + $AccountName +
                          ' -g ' + $ResourceGroup + ' --query id -o tsv) --tags MS-AOAI-Feature-Assistants= --is-incremental')
        }
    }
}

# ---------------------------------------------------------------------------------------------
# 2. A soft-deleted account holding the name
# ---------------------------------------------------------------------------------------------

Write-Step '2. Soft-deleted accounts'

# Cognitive Services accounts are soft-deleted for 48 hours. While one exists the subdomain stays
# registered to it, so a freshly recreated account of the same name can be addressed by a URL that
# still resolves to the dead resource.
$deleted = @(Invoke-AzJson @('cognitiveservices', 'account', 'list-deleted'))
$matching = @($deleted | Where-Object { (Get-Prop $_ 'name') -eq $AccountName })

if ($matching.Count -eq 0) {
    Write-Good "no soft-deleted account named $AccountName"
}
else {
    foreach ($d in $matching) {
        Write-Bad "a soft-deleted $AccountName still exists in $(Get-Prop $d 'location')"
    }
    $findings += ("A soft-deleted account named $AccountName still holds the subdomain. Purge it, then recreate: " +
                  "az cognitiveservices account purge -n $AccountName -g $ResourceGroup -l <location>")
}

# ---------------------------------------------------------------------------------------------
# 3. The projects
# ---------------------------------------------------------------------------------------------

Write-Step '3. Projects'

$subscriptionId = az account show --query id -o tsv 2>$null
if ($LASTEXITCODE -ne 0) { $subscriptionId = $null }

$liveProjects = @()

foreach ($name in $Project) {
    if (-not $subscriptionId) { break }

    $id = "/subscriptions/$subscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.CognitiveServices/accounts/$AccountName/projects/$name"
    $p = Invoke-AzJson @('resource', 'show', '--ids', $id)

    if (-not $p) {
        Write-Bad "$name : not found"
        $findings += "Project '$name' does not exist under $AccountName."
        continue
    }

    $pstate = Get-Prop (Get-Prop $p 'properties') 'provisioningState'
    if ($pstate -eq 'Succeeded') {
        Write-Good "$name : $pstate"
        $liveProjects += $name
    }
    else {
        Write-Bad "$name : $pstate"
        $findings += "Project '$name' is in state '$pstate'."
    }

    # Agent authoring is a data action, so a subscription-level Owner assignment does not grant it.
    # Anything scoped at or above the project counts, which is why the scope is printed rather than
    # matched exactly.
    $roles = @(Invoke-AzJson @('role', 'assignment', 'list', '--scope', $id, '--include-inherited'))
    $mine = @($roles | ForEach-Object { Get-Prop $_ 'roleDefinitionName' } | Sort-Object -Unique)
    if ($mine.Count) { Write-Note "  roles at this scope: $($mine -join ', ')" }
    else { Write-Note '  no role assignments visible at this scope' }
}

# ---------------------------------------------------------------------------------------------
# 4. The agent backend (capability hosts)
# ---------------------------------------------------------------------------------------------

# A capability host is the sub-resource that tells Foundry Agent Service where to run and store
# agent data, and ARM provisions it separately from the account and the project. That is the one
# way an account and a project can both read Succeeded while the agents data plane is dead, so it
# is worth checking before concluding anything from the green states above. There is no az command
# for these, hence az rest.
Write-Step '4. Agent backend (capability hosts)'

function Get-CapabilityHosts {
    param([string] $ResourceId)

    $url = "https://management.azure.com$ResourceId/capabilityHosts?api-version=2025-06-01"
    $r = Invoke-AzJson @('rest', '--method', 'get', '--url', $url)
    if (-not $r) { return $null }
    # The comma keeps an empty result an empty array: PowerShell unrolls a bare @() on return and
    # hands back $null, which here would report a readable-but-empty list as unreadable.
    return ,@(Get-Prop $r 'value')
}

# Whether any capability host exists at all. On its own an empty list is not conclusive, because
# the docs say the service falls back to Microsoft-managed resources without one. Combined with a
# failing data plane it is the whole story, so it is recorded here and judged in section 7.
$anyCapabilityHost = $false

if (-not $subscriptionId) {
    Write-Note 'skipped: no subscription context'
}
else {
    $accountId = "/subscriptions/$subscriptionId/resourceGroups/$ResourceGroup/providers/Microsoft.CognitiveServices/accounts/$AccountName"
    $scopes = @([pscustomobject]@{ Label = "account $AccountName"; Id = $accountId })
    foreach ($name in $liveProjects) {
        $scopes += [pscustomobject]@{ Label = "project $name"; Id = "$accountId/projects/$name" }
    }

    foreach ($scope in $scopes) {
        $hosts = Get-CapabilityHosts $scope.Id

        if ($null -eq $hosts) {
            Write-Note "$($scope.Label) : could not be read"
            continue
        }

        if ($hosts.Count -eq 0) {
            # Microsoft's docs say an explicit capability host is optional and that the service
            # falls back to Microsoft-managed storage, so an empty list is not a fault by itself.
            # Section 7 decides, once it knows whether the data plane actually answers.
            Write-Note "$($scope.Label) : none"
            continue
        }

        foreach ($h in $hosts) {
            $hstate = Get-Prop (Get-Prop $h 'properties') 'provisioningState'
            if ($hstate -eq 'Succeeded') {
                $anyCapabilityHost = $true
                Write-Good "$($scope.Label) : $(Get-Prop $h 'name') $hstate"
            }
            else {
                Write-Bad "$($scope.Label) : $(Get-Prop $h 'name') $hstate"
                $findings += ("The capability host '$(Get-Prop $h 'name')' on $($scope.Label) is '$hstate', not Succeeded. " +
                              'That is the agent backend, and it provisions separately from the account and the ' +
                              'project, which is why both read Succeeded. Capability hosts cannot be updated in ' +
                              'place; see "Neither agent surface answers" in docs/azure-setup.md.')
            }
        }
    }
}

# ---------------------------------------------------------------------------------------------
# 5. Model deployments
# ---------------------------------------------------------------------------------------------

Write-Step '5. Model deployments'

$models = @(Invoke-AzJson @('cognitiveservices', 'account', 'deployment', 'list',
    '-n', $AccountName, '-g', $ResourceGroup))

if ($models.Count -eq 0) {
    Write-Bad 'none — an agent names a deployment, so every run would fail even once authoring works'
    $findings += "The account has no model deployments. The agent YAML names one under model.deployment; create it before provisioning."
}
else {
    foreach ($m in $models) {
        $mp = Get-Prop $m 'properties'
        $model = Get-Prop $mp 'model'
        Write-Note "$(Get-Prop $m 'name') : $(Get-Prop $model 'name') $(Get-Prop $model 'version'), state $(Get-Prop $mp 'provisioningState')"
    }
}

# ---------------------------------------------------------------------------------------------
# 6. The data-plane call provision.ps1 makes
# ---------------------------------------------------------------------------------------------

Write-Step '6. The call provision.ps1 makes'

$probeSucceeded = $false
$probeAttempted = $false

$token = az account get-access-token --resource 'https://ai.azure.com' --query accessToken -o tsv 2>$null
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
    Write-Bad 'could not get a token for https://ai.azure.com — run az login'
    $findings += 'No token could be obtained for https://ai.azure.com.'
    $token = $null
}
else {
    $token = $token.Trim()
    Write-Note 'token acquired for https://ai.azure.com'
}

# Reads one data-plane URL and reports what came back. Returns the response on success and $null
# on failure, printing the service's own message either way.
function Invoke-Probe {
    param([string] $Label, [string] $Url, [string] $Token)

    try {
        $r = Invoke-RestMethod -Method GET -Uri $Url -Headers @{ Authorization = ("Bearer " + $Token) }
        return $r
    }
    catch {
        # The body carries the service's own message; the exception text alone is just the status
        # line, which is what made this error opaque in the first place. Which property holds the
        # body differs between PowerShell 5.1 and 7, and under Set-StrictMode reading an absent one
        # throws, so probe for each rather than dotting through.
        $body = ''
        $ex = $_.Exception
        $respProp = $ex.PSObject.Properties['Response']
        if ($respProp -and $respProp.Value) {
            $resp = $respProp.Value
            $contentProp = $resp.PSObject.Properties['Content']
            if ($contentProp -and $contentProp.Value) {
                try { $body = $contentProp.Value.ReadAsStringAsync().Result } catch { $body = '' }
            }
        }
        if (-not $body) {
            $detail = $_.PSObject.Properties['ErrorDetails']
            if ($detail -and $detail.Value) { $body = [string] $detail.Value.Message }
        }
        # The status code is the part that separates "the service refused this request" from "the
        # service broke trying to serve it", so capture it rather than leaving only the message.
        $script:lastProbeStatus = 0
        if ($respProp -and $respProp.Value) {
            $codeProp = $respProp.Value.PSObject.Properties['StatusCode']
            if ($codeProp -and $null -ne $codeProp.Value) {
                $script:lastProbeStatus = [int] $codeProp.Value
            }
        }

        Write-Bad "  $Label : $($ex.Message)"
        if ($body) { Write-Note "    $($body -replace '\s+', ' ')" }
        $script:lastProbeBody = $body
        return $null
    }
}

$gatewayOk = $null
$reportedMissingCapHost = $false
$script:lastProbeBody = ''
$script:lastProbeStatus = 0

# Before blaming the agents API, establish whether anything at all answers on this host. This route
# shares the gateway, the DNS name and the account with the agents API but not the agent backend.
#
# What matters is not whether it returns 200 but *how* it fails. A 401, 403 or 404 is the gateway
# making a decision: it resolved the host, read the token and answered deliberately, so the front
# door is healthy and only the agent backend can be at fault. A 408 or 5xx is the gateway failing
# to serve a request it accepted, which is an account-wide or regional problem. Treating all three
# as "broken" would blame the region for what is really a missing agent backend.
if ($endpointHost) {
    $csToken = az account get-access-token --resource 'https://cognitiveservices.azure.com' --query accessToken -o tsv 2>$null
    if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($csToken)) {
        $script:lastProbeBody = ''
        $script:lastProbeStatus = 0
        $gateway = Invoke-Probe 'gateway (openai/deployments, not agents)' `
            "$endpointHost/openai/deployments?api-version=2024-10-21" $csToken.Trim()
        $gatewayStatus = $script:lastProbeStatus

        if ($gateway) {
            Write-Good '  the account answers on this host, so the gateway and DNS are fine'
            $gatewayOk = $true
        }
        elseif ($gatewayStatus -ge 400 -and $gatewayStatus -lt 500 -and $gatewayStatus -ne 408) {
            # Not every account serves this particular route, so a 404 here is unremarkable. The
            # useful part is that something answered it properly.
            Write-Good "  the gateway answered deliberately ($gatewayStatus), so the host and DNS are fine"
            $gatewayOk = $true
        }
        elseif ($gatewayStatus -eq 0) {
            # Nothing came back at all: DNS, TLS or the network, rather than the service.
            $gatewayOk = $false
            $findings += ('Nothing answered on the account host at all, so this is a name-resolution or network ' +
                          'problem rather than anything to do with agents.')
        }
        else {
            $gatewayOk = $false
            $findings += ("The account broke serving a non-agent route too ($gatewayStatus), so this is not about " +
                          'which agent API the scripts call. The account data plane or the region is at fault: ' +
                          'see "Neither agent surface answers" in docs/azure-setup.md.')
        }
    }
    else { $gatewayOk = $null }
}
else { $gatewayOk = $null }

if ($token -and $endpointHost) {
    foreach ($name in $liveProjects) {
        Write-Host "  $name" -ForegroundColor White
        $probeAttempted = $true

        # /assistants and /agents are two different resource models on one host, not an old and a
        # new spelling of one list:
        #   /assistants  classic Foundry Agent Service (spec ai/data-plane/AIAgents). Objects have
        #                asst_ ids. This is what provision.ps1 creates and the worker runs.
        #   /agents      Foundry "agents v2" (spec ai-foundry/data-plane/Foundry). Objects are
        #                name-keyed and versioned, and are created by POST /agents.
        # A classic assistant is never projected into the v2 registry, so /agents being empty is
        # the expected reading of a healthy project, not a sign that anything is missing. It is
        # probed only to show that the host serves both, and the two lists are never compared.
        $script:lastProbeBody = ''
        $classic = Invoke-Probe 'assistants (classic: the agents this repo creates)' `
            "$endpointHost/api/projects/$name/assistants?api-version=$ApiVersion" $token
        $classicBody = $script:lastProbeBody

        $classicNames = if ($classic) { Get-AgentNames $classic } else { $null }
        if ($classic) {
            if ($null -eq $classicNames) {
                Write-Warn "  assistants (classic) : 200, but the reply carries no agent list"
            }
            else {
                Write-Good "  assistants (classic) : 200, $($classicNames.Count) agent(s)$(if ($classicNames.Count) { ' : ' + ($classicNames -join ', ') })"
            }
        }

        $script:lastProbeBody = ''
        $current = Invoke-Probe 'agents (v2 registry: separate resource model)' `
            "$endpointHost/api/projects/$name/agents?api-version=$ApiVersion" $token

        $currentNames = if ($current) { Get-AgentNames $current } else { $null }
        if ($current) {
            $count = if ($null -eq $currentNames) { 0 } else { $currentNames.Count }
            Write-Note "  agents (v2 registry, separate from the above) : 200, $count agent(s)$(if ($count) { ' : ' + ($currentNames -join ', ') })"
            if (-not $count) {
                Write-Note '  empty is expected: this repo creates classic agents, which do not appear here'
            }
        }

        if ($classic -or $current) { $probeSucceeded = $true }

        # A reachable project is not the same as a provisioned one. Counting what came back against
        # the YAML files in this repository is what separates "the service is fine" from "the
        # service is fine and provision.ps1 stopped part way through", which otherwise reads as
        # success in every section above.
        if ($classic -or $current) {
            $expected = Get-ExpectedAgentNames $name
            $live = @(@($classicNames) + @($currentNames) | Where-Object { $_ } | Sort-Object -Unique)

            if ($expected.Count) {
                $missing = @($expected | Where-Object { $_ -notin $live })
                if ($missing.Count) {
                    Write-Bad "  missing $($missing.Count) of $($expected.Count) agent(s) : $($missing -join ', ')"
                    $findings += ("Project '$name' is reachable but under-provisioned: " +
                                  "$($missing -join ', ') " +
                                  "$(if ($missing.Count -eq 1) { 'is' } else { 'are' }) defined in agents/$name " +
                                  'but not deployed. Re-run agents/provision.ps1 for this project; it is ' +
                                  'idempotent, so the agents that already exist are updated rather than duplicated.')
                }
                else {
                    Write-Note "  all $($expected.Count) agent(s) defined in agents/$name are deployed"
                }
            }

        }

        # Classify on both replies together. The service alternates between 408 and 500 for the
        # same underlying fault, so keying off whichever arrived first would describe one project
        # differently from its identical twin.
        $bothBodies = @($classicBody, $script:lastProbeBody) -join ' '

        if (-not $classic -and $current) {
            $findings += ("Project '$name' is reachable, but the classic /assistants route this repo uses is " +
                          'not, while the v2 /agents route is. The classic route is deprecated with an ' +
                          'announced sunset of 2026-08-26, so this is the migration finally biting: it needs a ' +
                          'code change, not a redeployment. See "The Assistants API is deprecated" in ' +
                          'docs/azure-setup.md.')
        }
        elseif (-not $classic -and -not $current) {
            if ($bothBodies -match 'PermissionDenied|AuthorizationFailed|Forbidden') {
                $findings += "You have no agent-authoring role on '$name'. Subscription Owner does not grant one: agent APIs are data actions."
            }
            # When the gateway probe already failed, it has reported this as the account-wide fault
            # it is, so repeating it once per project would only bury it.
            elseif ($gatewayOk -ne $false -and -not $anyCapabilityHost -and -not $reportedMissingCapHost) {
                $reportedMissingCapHost = $true
                # The gateway answers, so the host is fine; the agents API fails for every project;
                # and there is no capability host anywhere. "Unable to get resource information" is
                # the gateway failing to resolve the project's agent backend, which is exactly what
                # is missing. This is the most specific explanation the script can offer.
                $findings += ("Neither agent surface answers for '$name', and there is no capability host on the " +
                              'account or on any project (section 4). The capability host is the agent backend, ' +
                              'so the service has nothing to resolve the project to, which is what "Unable to ' +
                              'get resource information" means. infra/modules/foundry-account.bicep and ' +
                              'foundry-project.bicep now create one each: redeploy, then re-provision the agents. ' +
                              'See "Neither agent surface answers" in docs/azure-setup.md.')
            }
            elseif ($gatewayOk -ne $false -and -not $anyCapabilityHost) {
                # Already reported above: it is one account-wide fault, not one per project.
            }
            elseif ($gatewayOk -ne $false) {
                # Neither surface answered, so nothing about which API the code calls is at issue.
                # The gateway resolved the host and replied, so this is the account's data plane
                # rather than the project or the request.
                $detail = if ($bothBodies -match 'Timeout|InternalServerError|Unable to get resource information') {
                    'It answered with a timeout or an internal error, which is the service failing behind the gateway, not a rejection of the request. '
                } else { '' }
                $findings += ("Neither data-plane surface answered for '$name'. $detail" +
                              'Because /agents fails too, the retired Assistants API is not the cause. ' +
                              'See "Neither agent surface answers" in docs/azure-setup.md.')
            }
        }
    }
}
elseif ($token) {
    Write-Note 'skipped: the account has no subdomain to call'
}

# ---------------------------------------------------------------------------------------------
# 7. Verdict
# ---------------------------------------------------------------------------------------------

Write-Step '7. What to do'

if ($findings.Count -eq 0) {
    if ($probeAttempted -and -not $probeSucceeded) {
        # The control plane was clean and the call still did not come back, so the script has no
        # cause to offer. Saying so is more useful than implying everything is well.
        Write-Bad 'The control plane looks healthy, but the data-plane call above did not succeed.'
        Write-Note 'Nothing in the account, the projects, the roles or the model deployments explains it.'
        Write-Note 'Re-read the error printed in section 6 and see docs/azure-setup.md, "If provision.ps1 fails".'
    }
    else {
        Write-Good 'Nothing wrong found. The account, the projects, the roles and the data plane all answered.'
        Write-Note 'If provision.ps1 still fails, re-run it: the error it reported may have been transient.'
    }
}
else {
    foreach ($f in $findings) { Write-Bad $f; Write-Host '' }
}

Write-Host ''
