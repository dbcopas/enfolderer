<#
.SYNOPSIS
    Provisions named Foundry agents and protected MCP project connections.
.DESCRIPTION
    Uses the existing prompt YAML as the source of record. Missing tool URLs, audience or agent
    identities are errors, never a reason to publish an agent with reduced functionality.
    Supports agent-identity backend connections or encrypted dedicated gateway keys.
    Does not change container apps or delete classic assistants.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ProjectEndpoint,
    [Parameter(Mandatory)] [string] $Path,
    [Parameter(Mandatory)] [string[]] $Only,
    [Parameter(Mandatory)] [hashtable] $McpServerUrl,
    [Parameter(Mandatory)] [guid] $McpAudience,
    [hashtable] $McpGatewayApiKeys = @{},
    [string] $ApiVersion = '2025-11-15-preview'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module powershell-yaml -ErrorAction Stop
if ($McpAudience -eq [guid]::Empty) { throw 'A registered MCP resource application client ID is required.' }
$endpoint = $ProjectEndpoint.TrimEnd('/')
$projectName = ($endpoint -split '/')[-1]
$resources = az resource list --resource-type Microsoft.CognitiveServices/accounts/projects -o json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Could not discover Foundry project resources.' }
$projects = @($resources | Where-Object {
    ($_.name -split '/')[-1] -eq $projectName
})
$project = @($projects | Where-Object {
    $accountName = ($_.name -split '/')[0]
    $account = az cognitiveservices account show -g $_.resourceGroup -n $accountName --query properties.customSubDomainName -o tsv
    if ($LASTEXITCODE -ne 0) { throw "Could not read Foundry account $accountName." }
    $endpoint -eq "https://$account.services.ai.azure.com/api/projects/$projectName"
})
if ($project.Count -ne 1) { throw "Expected one ARM project matching $endpoint; found $($project.Count)." }
$projectId = $project[0].id

function Invoke-AuthenticatedJson {
    param([string] $Uri, [string] $Resource, [string] $Method, [object] $Body)
    $token = az account get-access-token --resource $Resource --query accessToken -o tsv
    if ($LASTEXITCODE -ne 0 -or -not $token) { throw "Could not acquire a token for $Resource." }
    $headers = @{ Authorization = "Bearer $token"; 'Foundry-Features' = 'AgentEndpoints=V1Preview' }
    $args = @{ Uri = $Uri; Method = $Method; Headers = $headers; ContentType = 'application/json' }
    if ($null -ne $Body) { $args.Body = $Body | ConvertTo-Json -Depth 40 }
    Invoke-RestMethod @args
}

$definitions = @(Get-ChildItem -Path $Path -Filter '*.yaml' | Where-Object {
    -not $_.Name.StartsWith('mcp-')
} | ForEach-Object {
    $definition = Get-Content -Raw $_.FullName | ConvertFrom-Yaml
    if ($definition.name -in $Only) { [pscustomobject]@{ File = $_.FullName; Definition = $definition } }
})
foreach ($name in $Only) {
    if (@($definitions | Where-Object { $_.Definition.name -eq $name }).Count -ne 1) {
        throw "Expected exactly one definition for $name."
    }
}

foreach ($entry in $definitions) {
    $d = $entry.Definition
    if ($d.project -ne $projectName) { throw "Agent $($d.name) belongs to $($d.project), not $projectName." }
    $tools = @()
    foreach ($tool in @($d.tools)) {
        if ($tool.type -ne 'mcp') { throw "Agent $($d.name): unsupported tool type $($tool.type)." }
        if (-not $McpServerUrl.ContainsKey($tool.server)) { throw "Missing MCP URL for $($tool.server)." }
        $url = [uri]$McpServerUrl[$tool.server]
        if ($url.Scheme -ne 'https' -or $url.UserInfo) { throw "MCP URL must be HTTPS without credentials: $($tool.server)." }
        $connectionName = "$($tool.server)-agent-identity"
        $connection = @{
            properties = @{
                category = 'RemoteTool'
                authType = 'AgenticIdentityToken'
                target = $url.AbsoluteUri
                audience = "api://$McpAudience"
            }
        }
        if ($McpGatewayApiKeys.ContainsKey($tool.server)) {
            $connection.properties.authType = 'CustomKeys'
            $connection.properties.Remove('audience')
            $connection.properties.credentials = @{ keys = @{ 'api-key' = $McpGatewayApiKeys[$tool.server] } }
        }
        $null = Invoke-AuthenticatedJson -Uri "https://management.azure.com$projectId/connections/${connectionName}?api-version=2025-04-01-preview" `
            -Resource 'https://management.azure.com' -Method Put -Body $connection
        $mcp = @{
            type = 'mcp'
            server_label = $tool.server_label
            server_url = $url.AbsoluteUri
            project_connection_id = $connectionName
            require_approval = 'always'
        }
        if ($tool.ContainsKey('allowed_tools')) { $mcp.allowed_tools = @($tool.allowed_tools) }
        $tools += $mcp
    }
    $definition = @{
        kind = 'prompt'
        model = $d.model.deployment
        instructions = $d.instructions
        tools = $tools
    }
    if ($d.model.ContainsKey('temperature')) { $definition.temperature = [double]$d.model.temperature }
    if ($d.model.ContainsKey('response_format')) {
        $definition.text = @{ format = @{ type = $d.model.response_format } }
    }
    $body = @{ definition = $definition; description = $d.description.Trim() }
    # POST versions is an upsert on the new agent surface, including the first version.
    $name = [uri]::EscapeDataString($d.name)
    $version = Invoke-AuthenticatedJson -Uri "$endpoint/agents/$name/versions?api-version=$ApiVersion" `
        -Resource 'https://ai.azure.com' -Method Post -Body $body
    if (-not $version.instance_identity -or -not $version.instance_identity.principal_id) {
        throw "Agent $($d.name) has no distinct agent identity. Refusing to configure a project/worker identity fallback."
    }
    [pscustomobject]@{
        Name = $d.name
        Id = $d.name
        Version = $version.version
        PrincipalId = $version.instance_identity.principal_id
        File = $entry.File
    }
}
