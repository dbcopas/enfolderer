<#
.SYNOPSIS
    Creates credential-free, single-tenant API and desktop registrations for a new deployment.
.DESCRIPTION
    Existing exact-name registrations are verified rather than overwritten. Exposes Scan.Submit,
    configures localhost public-client sign-in and grants delegated consent to the operator only.
    Mobile browser registration is configured separately once the API hostname exists.
#>
[CmdletBinding()]
param([string] $NamePrefix = 'enf-cost')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-Az {
    param([string[]] $Arguments)
    $result = & az @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed: $($Arguments[0..1] -join ' ')." }
    if ($result) { ($result -join "`n") | ConvertFrom-Json }
}

function Send-Graph {
    param([string] $Method, [string] $Path, [object] $Body)
    $temp = [IO.Path]::GetTempFileName()
    try {
        $Body | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temp -Encoding utf8
        Read-Az @('rest', '--method', $Method, '--url', "https://graph.microsoft.com/v1.0/$Path",
            '--headers', 'Content-Type=application/json', '--body', "@$temp", '-o', 'json')
    }
    finally { Remove-Item -LiteralPath $temp }
}

function Get-App {
    param([string] $Name)
    $apps = @(Read-Az @('ad', 'app', 'list', '--display-name', $Name, '-o', 'json') |
        Where-Object displayName -eq $Name)
    if ($apps.Count -gt 1) { throw "Multiple registrations named $Name." }
    if ($apps.Count -eq 1) {
        if ($apps[0].signInAudience -ne 'AzureADMyOrg' -or $apps[0].passwordCredentials.Count -ne 0 `
            -or $apps[0].keyCredentials.Count -ne 0) {
            throw "$Name must be single-tenant with no application credentials."
        }
        return $apps[0]
    }
    Read-Az @('ad', 'app', 'create', '--display-name', $Name, '--sign-in-audience', 'AzureADMyOrg', '-o', 'json')
}

function Get-ServicePrincipal {
    param([string] $AppId)
    $principals = @(Read-Az @('ad', 'sp', 'list', '--filter', "appId eq '$AppId'", '-o', 'json'))
    if ($principals.Count -gt 1) { throw "Multiple service principals for $AppId." }
    if ($principals.Count -eq 1) { return $principals[0] }
    Read-Az @('ad', 'sp', 'create', '--id', $AppId, '-o', 'json')
}

$me = Read-Az @('ad', 'signed-in-user', 'show', '-o', 'json')
$account = Read-Az @('account', 'show', '-o', 'json')
$api = Get-App "$NamePrefix-api"
$scopes = @($api.api.oauth2PermissionScopes | Where-Object value -eq 'Scan.Submit')
if ($scopes.Count -gt 1) { throw 'Multiple Scan.Submit scopes.' }
if ($scopes.Count -eq 0) {
    $scope = @{
        id = [guid]::NewGuid().ToString()
        value = 'Scan.Submit'
        type = 'User'
        isEnabled = $true
        adminConsentDisplayName = 'Submit card scans'
        adminConsentDescription = 'Allows approved users to submit and manage their own scans.'
        userConsentDisplayName = 'Submit card scans'
        userConsentDescription = 'Submit and manage your own scans.'
    }
    $null = Send-Graph PATCH "applications/$($api.id)" @{
        identifierUris = @("api://$($api.appId)")
        api = @{ oauth2PermissionScopes = @($api.api.oauth2PermissionScopes) + @($scope) }
    }
    $scopeId = $scope.id
} else {
    if (-not $scopes[0].isEnabled -or "api://$($api.appId)" -notin $api.identifierUris) {
        throw 'Existing API must expose enabled Scan.Submit at api://<client-id>.'
    }
    $scopeId = $scopes[0].id
}
$apiSp = Get-ServicePrincipal $api.appId
$desktop = Get-App "$NamePrefix-desktop"
$access = @(@{
    resourceAppId = $api.appId
    resourceAccess = @(@{ id = $scopeId; type = 'Scope' })
})
if ($desktop.requiredResourceAccess.Count -eq 0 -and $desktop.publicClient.redirectUris.Count -eq 0) {
    $null = Send-Graph PATCH "applications/$($desktop.id)" @{
        isFallbackPublicClient = $true
        publicClient = @{ redirectUris = @('http://localhost') }
        requiredResourceAccess = $access
    }
} elseif (-not $desktop.isFallbackPublicClient -or 'http://localhost' -notin $desktop.publicClient.redirectUris `
    -or -not @($desktop.requiredResourceAccess | Where-Object {
        $_.resourceAppId -eq $api.appId -and $_.resourceAccess.id -contains $scopeId
    })) {
    throw 'Existing desktop registration does not match the API scope and localhost public-client configuration.'
}
$desktopSp = Get-ServicePrincipal $desktop.appId
$grants = Read-Az @('rest', '--method', 'GET', '--url',
    "https://graph.microsoft.com/v1.0/servicePrincipals/$($desktopSp.id)/oauth2PermissionGrants", '-o', 'json')
if (-not @($grants.value | Where-Object {
    $_.resourceId -eq $apiSp.id -and $_.consentType -eq 'Principal' -and $_.principalId -eq $me.id `
        -and ($_.scope -split ' ') -contains 'Scan.Submit'
})) {
    $null = Send-Graph POST 'oauth2PermissionGrants' @{
        clientId = $desktopSp.id
        consentType = 'Principal'
        principalId = $me.id
        resourceId = $apiSp.id
        scope = 'Scan.Submit'
    }
}
[pscustomobject]@{
    TenantId = $account.tenantId
    OwnerObjectId = $me.id
    ApiClientId = $api.appId
    DesktopClientId = $desktop.appId
    Scope = "api://$($api.appId)/Scan.Submit"
}
