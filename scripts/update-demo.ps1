<#
.SYNOPSIS
    Puts the current source and the current agent prompts live, in one run, with no arguments.

.DESCRIPTION
    This is the script to run after pulling a change to this repository. A change can land in two
    quite different places, and until this session nothing did both:

      * C# changes ship inside container images, so they need a build and a redeploy.
      * Agent instructions live in agents/*.yaml, which nothing in Azure reads. They only reach
        Foundry when agents/provision-foundry.ps1 sends them, and an agent whose prompt was edited but
        never re-provisioned goes on behaving exactly as it did before, with no sign anywhere that
        the file on disk and the agent in Azure have diverged.

    Forgetting the second is the usual reason a fix appears not to work. So this script always does
    both, in the order they depend on each other, and discovers every name and URL it needs rather
    than asking you for them:

      1. The deployment, and from it the name prefix every resource is named after.
      2. The Foundry account and project endpoints, read from the resource groups rather than the
         deployment outputs, so a deployment that failed once still gives usable answers.
      3. Each team's MCP server URLs, read from the container apps themselves. These cannot be
         composed by hand: every team has its own Container Apps environment and so its own random
         default domain, and a URL built from the wrong one fails at agent run time with a 424.
      4. Verifies the MCP resource registration and its v2 token audience.
         Configures the dedicated AI Gateway preview: model synthesis and three isolated tool gateways.
      5. Provisions the three live named agents with distinct agent identities and encrypted
         gateway-key connections. Each tool gateway uses its own backend managed identity.
      6. Builds and deploys images with named agent references and exact per-agent caller IDs.
         SkipImages is allowed only after the first Responses migration.

    Re-running is the normal way to use it. Agents receive a new version under the same name and
    identity; images get a fresh tag each run, because ARM diffs the template and re-pushing a tag
    that is already deployed produces no new revision and no change.

    It does not scan anything. When it finishes, scan a page from the desktop app, then run
    ./scripts/show-last-scan.ps1 to read back what the pipeline decided about each card.

    No session-only PowerShell variables need to survive a reboot. Discovery reads Azure resources;
    deploy-images.ps1 replays recorded deployment parameters, with a local .deploy-images cache
    for recovery after a failed deployment. Keep that cache. Azure CLI authentication may need
    refreshing with az login, and the active subscription must be the one holding the demo.

.PARAMETER DeploymentName
    Subscription-scope deployment to read the name prefix from. Defaults to the name used in
    docs/azure-setup.md.

.PARAMETER NamePrefix
    Skip deployment lookup and use this prefix. Only needed if the deployment was removed; the
    prefix is the part before "-platform" in your resource group names.

.PARAMETER SkipImages
    Only re-provision the agents. Use this when the change was to a YAML prompt alone, which is by
    far the most common case. Gateway/connection refresh still takes several minutes.

.PARAMETER SkipAgents
    Skip the separate agent stage. Image deployment still refreshes the mandatory gateway
    configuration and agent connections/prompts; it cannot leave stale gateway credentials.

.EXAMPLE
    ./scripts/update-demo.ps1

    Everything: build, deploy, re-provision both projects, point the apps at the new agents.

.EXAMPLE
    ./scripts/update-demo.ps1 -SkipImages

    Push edited agent prompts/connections and persist their caller IDs, without rebuilding images.
#>

[CmdletBinding()]
param(
    [string] $DeploymentName = 'enfolderer-scan',
    [string] $NamePrefix,
    [switch] $SkipImages,
    [switch] $SkipAgents,
    [string] $MobileWebClientId,
    [string] $ScanAllowedUserObjectIds
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step { param([string] $T) Write-Host ''; Write-Host $T -ForegroundColor Cyan }
function Write-Note { param([string] $T) Write-Host "  $T" -ForegroundColor DarkGray }
function Write-Good { param([string] $T) Write-Host "  $T" -ForegroundColor Green }
function Write-Bad  { param([string] $T) Write-Host "  $T" -ForegroundColor Red }

function Invoke-Az {
    <#
        .SYNOPSIS
        Run az and return stdout, or $null if it failed.

        .DESCRIPTION
        Discovery asks a lot of questions whose answer may legitimately be "that does not exist" -
        a resource group for a topology you did not deploy, an app that has not been created yet.
        Those must not stop the script, so failure is a value here rather than an exception. Real
        failures are reported by the caller, which knows what the answer was for.
    #>
    param([Parameter(Mandatory)] [string[]] $Arguments)

    # Not $Args: that is an automatic variable, and binding a parameter over it fails in ways that
    # look like the call never happened.
    $out = & az @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    if ($null -eq $out) { return $null }
    $text = ($out -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return $text
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $repoRoot 'infra/main.bicep'))) {
    throw "infra/main.bicep not found under $repoRoot. Run this script from within the repository."
}

Push-Location $repoRoot
try {
    # ---------------------------------------------------------------- 1. who are we
    Write-Step '1. Azure account'

    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw @"
The Azure CLI (az) is not installed, and everything here goes through it.

Install it, then run this script again:
    winget install --exact --id Microsoft.AzureCLI
"@
    }

    $account = Invoke-Az @('account', 'show', '-o', 'json')
    if (-not $account) {
        throw @"
Not signed in to Azure.

    az login

If you have more than one subscription, pick the one holding the demo:
    az account list --query "[].{name:name, id:id}" -o table
    az account set --subscription <id>
"@
    }
    $account = $account | ConvertFrom-Json
    Write-Note "subscription $($account.name) ($($account.id))"
    Write-Note "signed in as $($account.user.name)"

    # ---------------------------------------------------------------- 2. name prefix
    Write-Step '2. Find the deployment'

    if (-not $NamePrefix) {
        $deployment = Invoke-Az @('deployment', 'sub', 'show', '--name', $DeploymentName, '-o', 'json')
        if ($deployment) {
            $deployment = $deployment | ConvertFrom-Json
            $recorded = $deployment.properties.PSObject.Properties['parameters']
            if ($recorded -and $recorded.Value -and $recorded.Value.PSObject.Properties['namePrefix']) {
                $NamePrefix = $recorded.Value.namePrefix.value
                Write-Note "deployment '$DeploymentName' records prefix '$NamePrefix'"
            }
        }
    }

    if (-not $NamePrefix) {
        # A failed deployment replaces the successful one and is stored with no parameters at all,
        # so the resource groups outlast the record of how they were made. Their names still carry
        # the prefix, which is all that is needed from here on.
        Write-Note "deployment '$DeploymentName' gave no prefix; looking for '*-platform' resource groups"
        $groups = Invoke-Az @('group', 'list', '--query', "[?ends_with(name,'-platform')].name", '-o', 'tsv')
        $candidates = @()
        if ($groups) { $candidates = @($groups -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }

        if ($candidates.Count -eq 1) {
            $NamePrefix = $candidates[0] -replace '-platform$', ''
            Write-Note "found resource group $($candidates[0]), so the prefix is '$NamePrefix'"
        }
        elseif ($candidates.Count -gt 1) {
            throw @"
More than one deployment of this demo is in the subscription:
$($candidates | ForEach-Object { "    $_" } | Out-String)
Say which one to update, using the part of the name before '-platform':

    ./scripts/update-demo.ps1 -NamePrefix <prefix>
"@
        }
        else {
            throw @"
Nothing of this demo was found in subscription '$($account.name)'.

There is no deployment named '$DeploymentName' and no resource group ending in '-platform', so the
infrastructure has not been created yet. That is step 3 of docs/azure-setup.md. If it is deployed
under a different deployment name, list them with:

    az deployment sub list --query "[].{name:name, state:properties.provisioningState}" -o table

then re-run naming it:

    ./scripts/update-demo.ps1 -DeploymentName <name>
"@
        }
    }

    $platformRg = "$NamePrefix-platform"
    $geoRg      = "$NamePrefix-cardgeo"
    $idRg       = "$NamePrefix-cardid"
    Write-Good "prefix '$NamePrefix'"

    # ---------------------------------------------------------------- 3. project endpoints
    Write-Step '3. Find the Foundry projects'

    function Find-Project {
        <#
            .SYNOPSIS
            Locate a Foundry project by name and return its data-plane endpoint.

            .DESCRIPTION
            The template has two topologies - one shared account with two projects, or an account
            per team - and the demo is usually run on the first and explained in terms of the
            second. Rather than ask which, search the team's own resource group first and then the
            platform one, which covers both without needing to know.

            The endpoint is built from the account's custom subdomain, not from the account name.
            They are usually the same string and are allowed not to be, and the data plane only
            answers on the subdomain.
        #>
        param([Parameter(Mandatory)] [string] $Project, [Parameter(Mandatory)] [string[]] $SearchGroups)

        foreach ($rg in $SearchGroups) {
            $accounts = Invoke-Az @('cognitiveservices', 'account', 'list', '-g', $rg,
                '--query', "[?kind=='AIServices'].{name:name, sub:properties.customSubDomainName}", '-o', 'json')
            if (-not $accounts) { continue }

            foreach ($a in ($accounts | ConvertFrom-Json)) {
                $found = Invoke-Az @('resource', 'list',
                    '--resource-type', 'Microsoft.CognitiveServices/accounts/projects',
                    '--query', "[?name=='$($a.name)/$Project'].name", '-o', 'tsv')
                if (-not $found) { continue }

                $sub = if ($a.sub) { $a.sub } else { $a.name }
                return [pscustomobject] @{
                    Account        = $a.name
                    ResourceGroup  = $rg
                    Endpoint       = "https://$sub.services.ai.azure.com/api/projects/$Project"
                }
            }
        }
        return $null
    }

    $geo = Find-Project -Project 'cardgeo' -SearchGroups @($geoRg, $platformRg)
    $idn = Find-Project -Project 'cardid'  -SearchGroups @($idRg, $platformRg)

    foreach ($p in @(@{ N = 'cardgeo'; V = $geo }, @{ N = 'cardid'; V = $idn })) {
        if (-not $p.V) {
            throw @"
No Foundry project named '$($p.N)' was found in $platformRg, $geoRg or $idRg.

The projects are created by the deployment in step 3 of docs/azure-setup.md. See what exists:

    az resource list --resource-type Microsoft.CognitiveServices/accounts/projects -o table
"@
        }
        Write-Note "$($p.N): $($p.V.Endpoint)"
    }

    # ---------------------------------------------------------------- 4. MCP server URLs
    Write-Step '4. Find the MCP servers'

    function Get-McpUrl {
        <#
            .SYNOPSIS
            The https URL an MCP container app answers on, or $null if it is not deployed.

            .DESCRIPTION
            Always read, never composed. Each team has its own Container Apps environment, and an
            environment's default domain carries a random suffix chosen when it was created - so
            the same server name under a different team is a completely different hostname. A URL
            assembled from the wrong team's domain resolves to nothing, and Foundry reports that as
            a 424 at run time rather than at provisioning time, long after the mistake was made.
        #>
        param([Parameter(Mandatory)] [string] $Name, [Parameter(Mandatory)] [string] $ResourceGroup)

        $fqdn = Invoke-Az @('containerapp', 'show', '-g', $ResourceGroup, '-n', "$NamePrefix-$Name",
            '--query', 'properties.configuration.ingress.fqdn', '-o', 'tsv')
        if (-not $fqdn) { return $null }
        return "https://$fqdn/mcp"
    }

    $geoMcp = @{}
    $idMcp  = @{}
    $bootstrapCallers = @()
    $currentAudiences = @()
    foreach ($s in @(
        @{ Name = 'mcp-imaging';            Rg = $geoRg; Map = $geoMcp },
        @{ Name = 'mcp-cardcatalog-mtg';     Rg = $idRg;  Map = $idMcp },
        @{ Name = 'mcp-cardcatalog-pokemon'; Rg = $idRg;  Map = $idMcp }
    )) {
        $url = Get-McpUrl -Name $s.Name -ResourceGroup $s.Rg
        if ($url) {
            $s.Map[$s.Name] = $url
            Write-Note "$($s.Name) -> $url"
            $settings = Invoke-Az @('containerapp', 'show', '-g', $s.Rg, '-n', "$NamePrefix-$($s.Name)",
                '--query', 'properties.template.containers[0].env', '-o', 'json')
            if (-not $settings) { throw "Cannot read MCP bootstrap configuration for $($s.Name)." }
            $settings = @($settings | ConvertFrom-Json)
            $caller = @($settings | Where-Object name -eq 'McpServer__AllowedCallerObjectIds')
            $bootstrapCallers += $(if ($caller.Count -eq 1) { ($caller[0].value -split ',')[0] } else { '' })
            $currentAudiences += @($settings | Where-Object name -eq 'McpServer__Audience' | ForEach-Object value)
        }
        else {
            throw "$($s.Name) not found in $($s.Rg). Refusing to provision an agent without its required tools."
        }
    }

    # ---------------------------------------------------------------- 5. images
    $tag = "v$((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmm'))"

    $responses = Invoke-Az @('containerapp', 'show', '-g', $platformRg, '-n', "$NamePrefix-worker",
        '--query', "properties.template.containers[0].env[?name=='ScanPipeline__UseResponses'].value | [0]", '-o', 'tsv')
    $bootstrap = $responses -ne 'true' -or @($bootstrapCallers | Where-Object {
        -not [guid]::TryParse($_, [ref]([guid]::Empty)) -or $_ -eq [guid]::Empty.ToString()
    }).Count -gt 0
    if ($bootstrap -and ($SkipImages -or $SkipAgents)) {
        throw 'The first agent/gateway migration must provision identities and deploy images together. Omit -SkipImages and -SkipAgents.'
    }
    # ---------------------------------------------------------------- 6. agents
    if ($SkipAgents) {
        Write-Step '6. Agents (skipped)'
        Write-Note '-SkipAgents skips this stage; image deployment still refreshes mandatory gateway configuration and agent connections/prompts'
        if (-not $SkipImages) {
            $webPass = @{}
            if ($MobileWebClientId) { $webPass.MobileWebClientId = $MobileWebClientId }
            if ($ScanAllowedUserObjectIds) { $webPass.ScanAllowedUserObjectIds = $ScanAllowedUserObjectIds }
            & (Join-Path $PSScriptRoot 'deploy-images.ps1') -DeploymentName $DeploymentName -Tag $tag @webPass
        }
        Write-Host ''
        Write-Good 'Done.'
        return
    }

    Write-Step '6. Configure dedicated AI Gateway preview and named agents'
    $audience = & (Join-Path $PSScriptRoot 'configure-mcp-audience.ps1') -NamePrefix $NamePrefix
    $pass = @{ McpAudience = $audience }
    if ($bootstrap) {
        Write-Note 'Bootstrapping distinct agent identities before the first real service images; deployment then replaces these connections with native gateway routes.'
        $provision = Join-Path $repoRoot 'agents\provision-foundry.ps1'
        $agents = @(& $provision -ProjectEndpoint $geo.Endpoint -Path (Join-Path $repoRoot 'agents\cardgeo') `
            -Only CardBoundaryAgent -McpServerUrl $geoMcp -McpAudience $audience)
        $agents += @(& $provision -ProjectEndpoint $idn.Endpoint -Path (Join-Path $repoRoot 'agents\cardid') `
            -Only MtgCardIdAgent,PokemonCardIdAgent -McpServerUrl $idMcp -McpAudience $audience)
        foreach ($entry in @(
            @{ Name = 'CardBoundaryAgent'; Parameter = 'Boundary' },
            @{ Name = 'MtgCardIdAgent'; Parameter = 'Mtg' },
            @{ Name = 'PokemonCardIdAgent'; Parameter = 'Pokemon' }
        )) {
            $agent = @($agents | Where-Object Name -eq $entry.Name)
            if ($agent.Count -ne 1) { throw "Missing bootstrap identity for $($entry.Name)." }
            if (-not [guid]::TryParse($agent[0].PrincipalId, [ref]([guid]::Empty)) -or
                $agent[0].PrincipalId -eq [guid]::Empty.ToString()) { throw "Invalid bootstrap identity for $($entry.Name)." }
            $pass["$($entry.Parameter)AgentId"] = $agent[0].Id
            $pass["$($entry.Parameter)AgentPrincipalId"] = $agent[0].PrincipalId
        }
        if (@($agents.PrincipalId | Select-Object -Unique).Count -ne 3) { throw 'Bootstrap requires three distinct agent identities.' }
    }
    if ($SkipImages) {
        if ($currentAudiences.Count -ne 3 -or @($currentAudiences | Where-Object { $_ -ne $audience }).Count) {
            throw 'MCP audience changed; redeploy images without -SkipImages to update all backend token validators together.'
        }
        & (Join-Path $PSScriptRoot 'configure-ai-gateway-preview.ps1') -NamePrefix $NamePrefix -SubscriptionId $account.id
    }
    else {
        if ($MobileWebClientId) { $pass.MobileWebClientId = $MobileWebClientId }
        if ($ScanAllowedUserObjectIds) { $pass.ScanAllowedUserObjectIds = $ScanAllowedUserObjectIds }
        & (Join-Path $PSScriptRoot 'deploy-images.ps1') -DeploymentName $DeploymentName -Tag $tag @pass
    }

    Write-Host ''
    Write-Good 'Done. The images and the agent prompts in Azure now match this working copy.'
    Write-Host ''
    Write-Host 'Next: scan a page from the desktop app, then read back what it decided:' -ForegroundColor DarkYellow
    Write-Host '    ./scripts/show-last-scan.ps1'
}
finally {
    Pop-Location
}
