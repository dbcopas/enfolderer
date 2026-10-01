<#
.SYNOPSIS
    Creates or updates the Foundry agents defined by the YAML files in this folder.

.DESCRIPTION
    The YAML files are this repository's own format: they are the source of record for each agent's
    model, instructions and tool wiring, and nothing in Azure reads them directly. This script
    translates them into calls against the Foundry Agents data plane, which is the same API the
    worker uses at run time (see src/Enfolderer.Ai.Worker/Agents/FoundryAgentClient.cs).

    Matching is by agent name: an agent that already exists is updated in place, so re-running is
    safe and is the way to push an edited prompt.

    Each project is provisioned separately and deliberately so. Team A's agent is created against
    the cardgeo endpoint and Team B's against cardid, and if you lack rights on one of them only
    that call fails — which is the security boundary the demo is about.

.PARAMETER ProjectEndpoint
    Data-plane endpoint of the target project, e.g.
    https://enf-demo-cardgeo.services.ai.azure.com/api/projects/cardgeo
    This is the geometryProjectEndpoint or identificationProjectEndpoint output of the deployment.

.PARAMETER Path
    One agent YAML file, or a folder of them. Files whose name starts with "mcp-" are skipped:
    they describe MCP servers, which are registered with the project rather than created as agents.

.PARAMETER McpServerUrl
    Maps an MCP server name from the YAML (the "server" key, e.g. mcp-imaging) to the HTTPS URL it
    is reachable at. The data plane rejects anything that is not an http(s) URL, so a server has to
    be hosted and reachable before its tool can be attached to an agent.

    Any MCP tool with no URL here is omitted, with a warning, and the agent is created without it.
    Re-run with the URL once the server is up; the agent is updated in place.

.PARAMETER ConnectedAgentId
    Maps an agent referenced by a connected_agent tool to its real agent id, keyed as
    "project/AgentName" (e.g. 'cardgeo/CardBoundaryAgent' = 'asst_abc123').

    The data plane identifies a connected agent by id, not by name. Agents in the project being
    provisioned are resolved automatically — from the ones already there, and from any created
    earlier in the same run. An agent in *another* project cannot be listed from here, which is
    the boundary the demo is about, so its id has to be passed in.

.PARAMETER Only
    Provision just these agents, by name. Without it, every agent file in the folder is
    provisioned, which includes the YugiohCardIdAgent and LorcanaCardIdAgent growth slots.

.PARAMETER ApiVersion
    Foundry data-plane API version. Must match ScanPipeline:FoundryApiVersion in the worker.

.PARAMETER WhatIf
    Show the request bodies that would be sent without calling Azure.

.EXAMPLE
    ./provision.ps1 -ProjectEndpoint $geo -Path ./cardgeo
    Creates CardBoundaryAgent in Team A's project. It takes no -McpServerUrl: its YAML declares
    no tools, because the image reaches it as message content.

.EXAMPLE
    ./provision.ps1 -ProjectEndpoint $id -Path ./cardid `
        -Only MtgCardIdAgent, OrchestratorAgent `
        -ConnectedAgentId @{ 'cardgeo/CardBoundaryAgent' = 'asst_5YS2jhx13zVgso1f5yE6Dm9c' }
    Creates Team B's MTG agent and its orchestrator, pointing the orchestrator's cross-project
    connection at the boundary agent id printed by the cardgeo run.

.NOTES
    Requires the powershell-yaml module:  Install-Module powershell-yaml -Scope CurrentUser
    Requires an az login whose account can author agents in the target project.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)] [string] $ProjectEndpoint,
    [Parameter(Mandatory = $true)] [string] $Path,
    [string[]] $Only,
    [hashtable] $McpServerUrl = @{},
    [hashtable] $ConnectedAgentId = @{},
    [string] $ApiVersion = 'v1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Get-Module -ListAvailable -Name powershell-yaml)) {
    throw "The powershell-yaml module is required. Install it with: Install-Module powershell-yaml -Scope CurrentUser"
}
Import-Module powershell-yaml -ErrorAction Stop

$endpoint = $ProjectEndpoint.TrimEnd('/')
# Last path segment of the endpoint, matching the "project" key in the YAML. Used to tell a
# connected agent in this project from one that lives across the boundary.
$projectName = ($endpoint -split '/')[-1]

# The Foundry data plane takes an Entra token for this resource, the same one the worker requests.
function Get-FoundryToken {
    $token = az account get-access-token --resource 'https://ai.azure.com' --query accessToken -o tsv
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
        throw 'Could not obtain a token for https://ai.azure.com. Run az login first.'
    }
    return $token
}

# Translates one parsed YAML document into the data-plane agent payload. Only the fields the
# service understands are sent: keys such as denied_connections and allowed_callers document the
# boundaries that infra/main.bicep enforces, and have no data-plane equivalent.
function ConvertTo-AgentPayload {
    param(
        [hashtable] $Definition,
        [hashtable] $McpUrls,
        [hashtable] $AgentIds,
        [string] $ProjectName
    )

    foreach ($required in 'name', 'model', 'instructions') {
        if (-not $Definition.ContainsKey($required)) {
            throw "Agent definition is missing the required '$required' key."
        }
    }

    $model = $Definition.model
    if (-not $model.ContainsKey('deployment')) {
        throw "Agent '$($Definition.name)' has no model.deployment."
    }

    $payload = [ordered]@{
        name         = $Definition.name
        model        = $model.deployment
        instructions = $Definition.instructions
    }

    if ($Definition.ContainsKey('description')) {
        # Folded scalars keep a trailing newline; harmless, but it shows up in the portal.
        $payload.description = $Definition.description.Trim()
    }
    if ($model.ContainsKey('temperature')) {
        $payload.temperature = [double] $model.temperature
    }
    if ($model.ContainsKey('response_format')) {
        $payload.response_format = @{ type = $model.response_format }
    }

    # Tool wiring differs per tool type and, for connected agents and MCP servers, depends on
    # connections that must already exist in the project.
    #
    # An update is a merge: a key left out of the payload keeps whatever the live agent already has.
    # So `tools` must be sent whenever the definition has a tools key at all, including when that
    # key is an empty list — otherwise an agent that used to declare an MCP server keeps it forever,
    # and every run fails with tool_server_error long after the YAML stopped asking for the tool.
    if ($Definition.ContainsKey('tools')) {
        $tools = @()
        $dropped = 0
        foreach ($tool in @($Definition.tools | Where-Object { $_ })) {
            switch ($tool.type) {
                'mcp' {
                    # The YAML names the server; only the caller knows where it ended up running.
                    # The data plane insists on an http(s) URL, so a server that is not yet hosted
                    # cannot be attached at all: leave the tool off and re-run later.
                    if (-not $McpUrls.ContainsKey($tool.server)) {
                        $dropped++
                        Write-Warning "Agent '$($Definition.name)': no URL given for MCP server '$($tool.server)', so that tool is being left off. Re-run with -McpServerUrl @{ '$($tool.server)' = 'https://...' } once it is hosted."
                    }
                    else {
                        $serverUrl = [string] $McpUrls[$tool.server]
                        if ($serverUrl -notmatch '^https?://') {
                            throw "MCP server '$($tool.server)' must be an http(s) URL, but got '$serverUrl'."
                        }
                        $mcp = [ordered]@{
                            type         = 'mcp'
                            server_label = $tool.server_label
                            server_url   = $serverUrl
                        }
                        # An empty YAML sequence parses to $null, and @($null) is a one-element
                        # array holding $null, which the data plane rejects. Send the key only when
                        # there is something to put in it.
                        if ($tool.ContainsKey('allowed_tools')) {
                            $allowed = @($tool.allowed_tools | Where-Object { $_ })
                            if ($allowed.Count -gt 0) { $mcp.allowed_tools = $allowed }
                        }
                        $tools += $mcp
                    }
                }
                'connected_agent' {
                    # The data plane wants the target's real agent id, not its name, so the target
                    # must already exist. Agents in this project are resolved from the listing
                    # taken at start-up; agents in another project cannot be listed from here and
                    # have to be supplied with -ConnectedAgentId.
                    $key = "$($tool.project)/$($tool.agent)"
                    $connectedId = if ($AgentIds.ContainsKey($key)) {
                        [string] $AgentIds[$key]
                    }
                    elseif ($tool.project -eq $ProjectName -and $AgentIds.ContainsKey($tool.agent)) {
                        [string] $AgentIds[$tool.agent]
                    }
                    else {
                        $null
                    }

                    # Left off with a warning rather than failing, exactly like an MCP server with
                    # no URL: provisioning one game only is a legitimate thing to do, and the
                    # orchestrator is updated in place when the other agents arrive.
                    if (-not $connectedId) {
                        $dropped++
                        $hint = if ($tool.project -eq $ProjectName) {
                            "Provision '$($tool.agent)' in this project and re-run."
                        }
                        else {
                            "It lives in project '$($tool.project)', so pass its id and re-run: -ConnectedAgentId @{ '$key' = 'asst_...' }"
                        }
                        Write-Warning "Agent '$($Definition.name)': no id known for connected agent '$($tool.agent)', so that tool is being left off. $hint"
                        continue
                    }

                    $connected = [ordered]@{
                        id   = $connectedId
                        name = $tool.name
                    }
                    # The service requires a description: it is what the calling model reads to
                    # decide when to invoke this agent.
                    $connected.description = if ($tool.ContainsKey('description')) {
                        [string] $tool.description
                    }
                    else {
                        "Delegates to $($tool.agent) in project $($tool.project)."
                    }

                    $tools += [ordered]@{
                        type            = 'connected_agent'
                        connected_agent = $connected
                    }
                }
                default { throw "Agent '$($Definition.name)' uses unsupported tool type '$($tool.type)'." }
            }
        }

        # The tool list is sent whole, so a tool left off above is removed from the live agent
        # rather than merely not added. Say so: the warnings alone read like "nothing changed".
        if ($dropped -gt 0) {
            Write-Warning "Agent '$($Definition.name)': the tool list is replaced wholesale, so the $dropped tool(s) left off above will be removed from the agent if it already has them. Re-run once they can be resolved."
        }

        $payload.tools = $tools
    }

    return $payload
}

function Invoke-Foundry {
    param(
        [string] $Method,
        [string] $RelativeUrl,
        [string] $Token,
        [object] $Body
    )

    $headers = @{ Authorization = ("Bearer " + $Token) }
    $url = "$endpoint$RelativeUrl"

    if ($null -ne $Body) {
        $json = $Body | ConvertTo-Json -Depth 10
        return Invoke-RestMethod -Method $Method -Uri $url -Headers $headers `
            -ContentType 'application/json' -Body $json
    }
    return Invoke-RestMethod -Method $Method -Uri $url -Headers $headers
}

$files = if (Test-Path -Path $Path -PathType Container) {
    Get-ChildItem -Path $Path -Filter '*.yaml' | Where-Object { $_.Name -notlike 'mcp-*' } | Sort-Object Name
}
else {
    @(Get-Item -Path $Path)
}

if (-not $files) {
    throw "No agent definitions found at $Path."
}

# The orchestrator references the other agents as connected agents, so it has to go last.
$files = @($files | Where-Object { $_.Name -notlike '*orchestrator*' }) +
         @($files | Where-Object { $_.Name -like '*orchestrator*' })

$token = if ($PSCmdlet.ShouldProcess($endpoint, 'Acquire Foundry token')) { Get-FoundryToken } else { $null }

$existing = @{}
if ($token) {
    # The first call is the one that proves the endpoint, the token and the project all line up.
    # Its failures are about the deployment rather than about anything in the YAML, so they are
    # translated here instead of surfacing as an Invoke-RestMethod stack trace from Invoke-Foundry.
    try {
        $list = Invoke-Foundry -Method 'GET' -RelativeUrl "/assistants?api-version=$ApiVersion" -Token $token
    }
    catch {
        $body = ''
        $detail = $_.PSObject.Properties['ErrorDetails']
        if ($detail -and $detail.Value) { $body = [string] $detail.Value.Message }

        Write-Host ''
        Write-Host "Could not read the agents in $endpoint" -ForegroundColor Red
        if ($body) { Write-Host "  $($body -replace '\s+', ' ')" -ForegroundColor DarkGray }
        Write-Host ''

        if ($body -match 'Unable to get resource information') {
            Write-Host 'That message means the service could not resolve the project behind this' -ForegroundColor DarkYellow
            Write-Host 'endpoint. It is about the deployment, not about the agent files.' -ForegroundColor DarkYellow
        }
        Write-Host 'Find out which part is missing:' -ForegroundColor DarkYellow
        Write-Host ''
        Write-Host "  ./scripts/diagnose-foundry.ps1 -Prefix <your prefix>"
        Write-Host ''
        Write-Host 'It checks the account, its subdomain, any soft-deleted account holding the name,'
        Write-Host 'the projects, your roles and the model deployments, then repeats this same call.'
        Write-Host ''
        throw "Could not list agents in $endpoint."
    }
    foreach ($agent in $list.data) {
        if ($agent.name) { $existing[$agent.name] = $agent.id }
    }
}

# Ids the payload builder may resolve a connected agent against: every agent already in this
# project, plus anything the caller supplied for other projects.
$knownAgentIds = @{}
foreach ($entry in $existing.GetEnumerator()) { $knownAgentIds[$entry.Key] = $entry.Value }
foreach ($entry in $ConnectedAgentId.GetEnumerator()) { $knownAgentIds[$entry.Key] = $entry.Value }

$results = foreach ($file in $files) {
    $definition = ConvertFrom-Yaml -Yaml (Get-Content -Path $file.FullName -Raw)

    # Filtered before the payload is built, so a skipped agent does not warn about MCP servers
    # or fail to resolve a connected agent it was never going to be provisioned with.
    $name = [string] $definition.name
    if ($Only -and $name -notin $Only) { continue }

    $payload = ConvertTo-AgentPayload -Definition $definition -McpUrls $McpServerUrl `
        -AgentIds $knownAgentIds -ProjectName $projectName

    $agentId = $existing[$name]
    $action = if ($agentId) { "Update agent '$name' ($agentId)" } else { "Create agent '$name'" }

    if (-not $PSCmdlet.ShouldProcess($endpoint, $action)) {
        $payload | ConvertTo-Json -Depth 10 | Write-Host
        continue
    }

    $response = if ($agentId) {
        Invoke-Foundry -Method 'POST' -RelativeUrl "/assistants/$agentId`?api-version=$ApiVersion" -Token $token -Body $payload
    }
    else {
        Invoke-Foundry -Method 'POST' -RelativeUrl "/assistants?api-version=$ApiVersion" -Token $token -Body $payload
    }

    # Newly created agents become resolvable targets for later files in this run, which is what
    # lets the orchestrator connect to agents provisioned moments earlier.
    if ($response.id) { $knownAgentIds[$name] = $response.id }

    [pscustomobject]@{
        Name = $name
        Id   = $response.id
        File = $file.Name
    }
}

$results | Format-Table -AutoSize
