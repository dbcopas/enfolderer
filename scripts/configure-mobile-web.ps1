<#
.SYNOPSIS
    Creates or updates the private mobile SPA registration without a client secret.
.DESCRIPTION
    Uses the existing scan API's delegated Scan.Submit scope. Configures exact HTTPS redirect URIs,
    PKCE SPA login and enterprise-app assignment requirements. Explicitly supplied pilot users are
    assigned to the web app. The same allowlist must be passed to deploy-images.ps1.
    Requires Graph permissions to manage applications and app assignments. Assignment-required
    apps need tenant-wide admin consent; -GrantAdminConsent requests it with appropriate privileges.
    Without that switch, registration can be prepared before an administrator approves consent.
    It does not invite users; invite/redeem tenant guests before supplying their tenant object IDs.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ApiClientId,
    [Parameter(Mandatory)] [uri] $WebsiteUrl,
    [Parameter(Mandatory)] [string[]] $AllowedUserObjectIds,
    [string] $DisplayName = 'Enfolderer Mobile',
    [switch] $GrantAdminConsent
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($WebsiteUrl.Scheme -ne 'https' -or $WebsiteUrl.AbsolutePath -ne '/' -or $WebsiteUrl.Query -or $WebsiteUrl.Fragment) {
    throw 'WebsiteUrl must be an exact HTTPS origin, with no path, query or fragment.'
}
$users = @($AllowedUserObjectIds | ForEach-Object { ([guid]$_).ToString('D') } | Select-Object -Unique)
if ($users.Count -eq 0) { throw 'At least one approved pilot user is required.' }

function Read-Az {
    param([string[]] $Arguments)
    $result = & az @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed: $($Arguments[0..1] -join ' '). Check Graph permissions and sign-in." }
    if ($result) { ($result -join "`n") | ConvertFrom-Json }
}
function Send-Graph {
    param([string] $Method, [string] $Url, $Body)
    $temp = [IO.Path]::GetTempFileName()
    try {
        $Body | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temp -Encoding utf8
        Read-Az @('rest', '--method', $Method, '--url', $Url, '--headers', 'Content-Type=application/json', '--body', "@$temp", '-o', 'json')
    }
    finally { Remove-Item -LiteralPath $temp }
}
$api = Read-Az @('ad', 'app', 'show', '--id', $ApiClientId, '-o', 'json')
$scope = @($api.api.oauth2PermissionScopes | Where-Object { $_.value -eq 'Scan.Submit' -and $_.isEnabled })
if ($scope.Count -ne 1) { throw 'API must expose one enabled delegated Scan.Submit permission.' }
$apiSp = Read-Az @('ad', 'sp', 'show', '--id', $ApiClientId, '-o', 'json')
$apps = @(Read-Az @('ad', 'app', 'list', '--display-name', $DisplayName, '-o', 'json') |
    Where-Object displayName -eq $DisplayName)
if ($apps.Count -gt 1) { throw 'Multiple matching mobile registrations; resolve the duplicate before continuing.' }
$web = if ($apps.Count -eq 1) { $apps[0] } else {
    Read-Az @('ad', 'app', 'create', '--display-name', $DisplayName, '--sign-in-audience', 'AzureADMyOrg', '-o', 'json')
}
$null = Send-Graph PATCH "https://graph.microsoft.com/v1.0/applications/$($web.id)" @{
    signInAudience = 'AzureADMyOrg'
    spa = @{ redirectUris = @($WebsiteUrl.AbsoluteUri) }
    requiredResourceAccess = @(@{ resourceAppId = $ApiClientId; resourceAccess = @(@{ id = $scope[0].id; type = 'Scope' }) })
}
$servicePrincipals = @(Read-Az @('ad', 'sp', 'list', '--filter', "appId eq '$($web.appId)'", '-o', 'json'))
$sp = if ($servicePrincipals.Count -eq 0) {
    Read-Az @('ad', 'sp', 'create', '--id', $web.appId, '-o', 'json')
} else { $servicePrincipals[0] }
$null = Send-Graph PATCH "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)" @{ appRoleAssignmentRequired = $true }
$assignments = Read-Az @('rest', '--method', 'GET', '--url', "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/appRoleAssignedTo", '-o', 'json')
foreach ($user in $users) {
    $null = Read-Az @('ad', 'user', 'show', '--id', $user, '-o', 'json')
    if (-not @($assignments.value | Where-Object principalId -eq $user)) {
        $null = Send-Graph POST "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/appRoleAssignedTo" @{
            principalId = $user; resourceId = $sp.id; appRoleId = '00000000-0000-0000-0000-000000000000'
        }
    }
}
if ($GrantAdminConsent) {
    $grants = Read-Az @('rest', '--method', 'GET', '--url', "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/oauth2PermissionGrants", '-o', 'json')
    $grant = @($grants.value | Where-Object { $_.resourceId -eq $apiSp.id -and $_.consentType -eq 'AllPrincipals' })
    if ($grant.Count -eq 0) {
        $null = Send-Graph POST 'https://graph.microsoft.com/v1.0/oauth2PermissionGrants' @{
            clientId = $sp.id; consentType = 'AllPrincipals'; resourceId = $apiSp.id; scope = 'Scan.Submit'
        }
    } elseif (($grant[0].scope -split ' ') -notcontains 'Scan.Submit') {
        $null = Send-Graph PATCH "https://graph.microsoft.com/v1.0/oauth2PermissionGrants/$($grant[0].id)" @{
            scope = ($grant[0].scope + ' Scan.Submit').Trim()
        }
    }
}
$consentGrants = Read-Az @('rest', '--method', 'GET', '--url', "https://graph.microsoft.com/v1.0/servicePrincipals/$($sp.id)/oauth2PermissionGrants", '-o', 'json')
$hasAdminConsent = @($consentGrants.value | Where-Object {
    $_.resourceId -eq $apiSp.id -and $_.consentType -eq 'AllPrincipals' -and
    ($_.scope -split ' ') -contains 'Scan.Submit'
}).Count -gt 0
if (-not $hasAdminConsent) {
    if ($GrantAdminConsent) { throw 'Tenant-wide Scan.Submit consent was not found after the grant request.' }
    Write-Warning 'Registration prepared, but sign-in is blocked until an Entra administrator grants tenant-wide Scan.Submit consent. Assignment-required apps do not support user consent.'
} else {
    Write-Host 'Tenant-wide Scan.Submit admin consent verified.'
}
Write-Host 'Private mobile registration configured. Assignment remains required. No client secret was created.'
[pscustomobject]@{ MobileWebClientId = $web.appId; ScanAllowedUserObjectIds = $users -join ','; WebsiteUrl = $WebsiteUrl.AbsoluteUri }
