<#
.SYNOPSIS
    Creates or verifies the demo's single-tenant, credential-free MCP resource registration.
.DESCRIPTION
    Returns the application client ID. Tokens are issued for api://<client-id> with v2 JWTs;
    MCP servers validate the client ID audience and their explicit agent-principal allow-list.
    No credentials, redirect URIs, delegated scopes or broad API permissions are created.
#>
[CmdletBinding()]
param([string] $NamePrefix = 'enf-demo')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$apps = @(az ad app list --display-name "$NamePrefix-mcp" -o json | ConvertFrom-Json |
    Where-Object { $_.displayName -eq "$NamePrefix-mcp" })
if ($LASTEXITCODE -ne 0) { throw 'Could not discover the MCP resource registration.' }
if ($apps.Count -gt 1) { throw "Multiple registrations named $NamePrefix-mcp; resolve the ambiguity before deployment." }
if ($apps.Count -eq 0) {
    $app = az ad app create --display-name "$NamePrefix-mcp" --sign-in-audience AzureADMyOrg -o json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the MCP resource registration.' }
    $token = az account get-access-token --resource-type ms-graph --query accessToken -o tsv
    if ($LASTEXITCODE -ne 0 -or -not $token) { throw 'Could not acquire a Graph token.' }
    $body = @{ identifierUris = @("api://$($app.appId)"); api = @{ requestedAccessTokenVersion = 2 } }
    $null = Invoke-RestMethod -Method Patch -Uri "https://graph.microsoft.com/v1.0/applications/$($app.id)" `
        -Headers @{ Authorization = "Bearer $token" } -ContentType application/json -Body ($body | ConvertTo-Json -Depth 5)
}
else {
    $app = $apps[0]
    if ($app.signInAudience -ne 'AzureADMyOrg' -or $app.api.requestedAccessTokenVersion -ne 2 `
        -or "api://$($app.appId)" -notin $app.identifierUris) {
        throw 'Existing MCP registration must be single-tenant, issue v2 tokens and expose api://<client-id>. Review it rather than overwriting its configuration.'
    }
}
$principals = @(az ad sp list --filter "appId eq '$($app.appId)'" -o json | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the MCP resource service principal.' }
if ($principals.Count -eq 0) {
    $null = az ad sp create --id $app.appId -o none
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the MCP resource service principal.' }
}
return [string]$app.appId
