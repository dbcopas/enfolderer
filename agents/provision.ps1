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

.PARAMETER Only
    Provision just these agents, by name. Without it, every agent file in the folder is
    provisioned, which includes the YugiohCardIdAgent and LorcanaCardIdAgent growth slots.

.PARAMETER ApiVersion
    Foundry data-plane API version. Must match ScanPipeline:FoundryApiVersion in the worker.

.PARAMETER WhatIf
    Show the request bodies that would be sent without calling Azure.

.EXAMPLE
    ./provision.ps1 -ProjectEndpoint $geo -Path ./cardgeo
    Creates CardBoundaryAgent in Team A's project.

.NOTES
    Requires the powershell-yaml module:  Install-Module powershell-yaml -Scope CurrentUser
    Requires an az login whose account can author agents in the target project.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)] [string] $ProjectEndpoint,
    [Parameter(Mandatory = $true)] [string] $Path,
    [string[]] $Only,
    [string] $ApiVersion = 'v1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Get-Module -ListAvailable -Name powershell-yaml)) {
    throw "The powershell-yaml module is required. Install it with: Install-Module powershell-yaml -Scope CurrentUser"
}
Import-Module powershell-yaml -ErrorAction Stop

$endpoint = $ProjectEndpoint.TrimEnd('/')

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
    param([hashtable] $Definition)

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
    # connections that must already exist in the project. Emitted only when present.
    if ($Definition.ContainsKey('tools') -and $Definition.tools) {
        $tools = @()
        foreach ($tool in $Definition.tools) {
            switch ($tool.type) {
                'mcp' {
                    $mcp = [ordered]@{
                        type         = 'mcp'
                        server_label = $tool.server_label
                        server_url   = "mcp://$($tool.server)"
                    }
                    if ($tool.ContainsKey('allowed_tools')) {
                        $mcp.allowed_tools = @($tool.allowed_tools)
                    }
                    $tools += $mcp
                }
                'connected_agent' {
                    # Resolved by name at provisioning time; the target agent must exist first,
                    # which is why the orchestrator is provisioned last.
                    $tools += [ordered]@{
                        type            = 'connected_agent'
                        connected_agent = [ordered]@{
                            name    = $tool.name
                            project = $tool.project
                            agent   = $tool.agent
                        }
                    }
                }
                default { throw "Agent '$($Definition.name)' uses unsupported tool type '$($tool.type)'." }
            }
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
    $list = Invoke-Foundry -Method 'GET' -RelativeUrl "/assistants?api-version=$ApiVersion" -Token $token
    foreach ($agent in $list.data) {
        if ($agent.name) { $existing[$agent.name] = $agent.id }
    }
}

$results = foreach ($file in $files) {
    $definition = ConvertFrom-Yaml -Yaml (Get-Content -Path $file.FullName -Raw)
    $payload = ConvertTo-AgentPayload -Definition $definition

    $name = $payload.name
    if ($Only -and $name -notin $Only) { continue }
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

    [pscustomobject]@{
        Name = $name
        Id   = $response.id
        File = $file.Name
    }
}

$results | Format-Table -AutoSize
