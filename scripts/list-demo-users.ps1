<#
.SYNOPSIS
    Lists approved demo users and their remaining scan allowances.
.DESCRIPTION
    Read-only. Discovers users from the live API allowlist, resolves their names through Microsoft
    Graph, and reads allowances through the existing delegated allowance-administration client.
    Requires Azure CLI sign-in with Container App and directory-user read access, .NET, and a scan
    allowance administrator. No user IDs, credits or credentials are hardcoded.
    Returns objects suitable for Format-Table, Export-Csv or ConvertTo-Json. Unlimited accounts
    show 'Unlimited' rather than the backing numeric balance. Unfunded accounts show zero with
    AllowanceGranted false. Temporary allowance files are removed; tokens stay in the OS cache.
.PARAMETER ConfigFile
    Desktop configuration for this deployment, not the old tenant's shipped defaults.
.PARAMETER AuthenticationRecord
    Optional existing authentication record for this tenant/client, such as the desktop record.
.PARAMETER SignIn
    Sign in interactively once. Subsequent users reuse that session.
.EXAMPLE
    .\scripts\list-demo-users.ps1 -ConfigFile C:\private\aiconfig.txt -SignIn |
        Format-Table DisplayName, RemainingScans, InvitationStatus -AutoSize
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ConfigFile,
    [ValidatePattern('^[a-zA-Z0-9-]{3,12}$')] [string] $NamePrefix = 'enf-cost',
    [string] $AuthenticationRecord,
    [switch] $SignIn
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ConfigFile = (Resolve-Path -LiteralPath $ConfigFile).Path
$config = @{}
foreach ($line in Get-Content -LiteralPath $ConfigFile) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
    $pair = $line.Split('=', 2)
    if ($pair.Count -ne 2) { throw 'Configuration entries must use key=value.' }
    $config[$pair[0].Trim()] = $pair[1].Trim()
}
foreach ($key in 'api_base_url', 'tenant_id', 'client_id', 'scope') {
    if (-not $config.ContainsKey($key) -or [string]::IsNullOrWhiteSpace($config[$key])) {
        throw "Missing configuration entry: $key"
    }
}
$tenant = ([guid]$config.tenant_id).ToString('D')
$clientId = ([guid]$config.client_id).ToString('D')
$api = [uri]$config.api_base_url
if (-not $api.IsAbsoluteUri -or $api.Scheme -ne 'https' -or $api.AbsolutePath -ne '/' -or $api.Query -or $api.Fragment -or $api.UserInfo) {
    throw 'api_base_url must be an HTTPS origin without a path, query or credentials.'
}
$account = & az account show -o json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Azure CLI sign-in is required.' }
if ($account.tenantId -ne $tenant) { throw 'Azure CLI and the configuration must target the same tenant.' }
$app = & az containerapp show --subscription $account.id -g "$NamePrefix-platform" -n "$NamePrefix-api" -o json | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw 'Could not read the demo API; verify the subscription, name prefix and read access.' }
$settings = @{}
foreach ($setting in $app.properties.template.containers[0].env) {
    if ($setting.PSObject.Properties['value']) { $settings[$setting.name] = $setting.value }
}
if ($settings['AzureAd__TenantId'] -ne $tenant -or $config.scope -ne "api://$($settings['AzureAd__ClientId'])/Scan.Submit") {
    throw 'Configuration tenant/scope does not match the live demo API.'
}
$ingress = $app.properties.configuration.ingress
$hosts = @($ingress.fqdn)
if ($ingress.PSObject.Properties['customDomains']) { $hosts += @($ingress.customDomains | ForEach-Object { $_.name }) }
if ($api.Host -notin $hosts) { throw 'Configuration URL does not match a live demo API hostname.' }
$ids = @(([string]$settings['MobileWeb__AllowedUserObjectIds']).Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
    ForEach-Object {
        $id = [guid]$_.Trim()
        if ($id -eq [guid]::Empty) { throw 'The demo allowlist contains an empty user GUID.' }
        $id.ToString('D')
    } | Select-Object -Unique)
if ($ids.Count -eq 0) { throw 'The live demo API has no explicit approved-user allowlist.' }
$graphToken = & az account get-access-token --tenant $tenant --resource https://graph.microsoft.com --query accessToken -o tsv
if ($LASTEXITCODE -ne 0 -or -not $graphToken) { throw 'Could not acquire a tenant-specific Microsoft Graph token.' }
$headers = @{ Authorization = "Bearer $graphToken" }
$stateDirectory = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Enfolderer\DiagnosticsAuth'
$record = Join-Path $stateDirectory "$tenant-$clientId.json"
$importRecord = if ($AuthenticationRecord) { (Resolve-Path -LiteralPath $AuthenticationRecord).Path } else { 'none' }
$mode = if ($SignIn) { 'sign-in' } else { 'silent' }
$tool = Join-Path $PSScriptRoot '..\tools\Enfolderer.Ai.ScanDiagnostics'
$work = Join-Path ([IO.Path]::GetTempPath()) ("enfolderer-users-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $rows = foreach ($id in $ids) {
        $user = Invoke-RestMethod -Headers $headers -Uri "https://graph.microsoft.com/v1.0/users/${id}?`$select=id,displayName,userType,externalUserState"
        if ($user.id -ne $id) { throw "Microsoft Graph returned a different user for $id." }
        $directory = Join-Path $work $id
        New-Item -ItemType Directory -Path $directory | Out-Null
        $command = Join-Path $directory 'read-command.json'
        @{ objectId = $id } | ConvertTo-Json | Set-Content -LiteralPath $command -Encoding utf8
        $output = @(& dotnet run --project $tool -- $ConfigFile $directory allowance 24 $record $importRecord $mode $command)
        if ($LASTEXITCODE -ne 0) {
            throw "Allowance read failed for $id. Verify allowance-administrator access and use -SignIn if needed. $($output -join ' ')"
        }
        $mode = 'silent'
        $allowance = Get-Content -Raw -LiteralPath (Join-Path $directory 'allowance.json') | ConvertFrom-Json
        if ($allowance.ownerKey -ne "${tenant}:$id" -or
            ($allowance.remaining -isnot [long] -and $allowance.remaining -isnot [int]) -or
            $allowance.remaining -lt 0 -or $allowance.unlimited -isnot [bool] -or $allowance.granted -isnot [bool]) {
            throw "Invalid allowance response for $id."
        }
        [pscustomobject]@{
            DisplayName = $user.displayName
            ObjectId = $id
            RemainingScans = if ($allowance.unlimited) { 'Unlimited' } else { $allowance.remaining }
            InvitationStatus = if ($user.userType -eq 'Guest') { $user.externalUserState } else { 'Not applicable' }
            AllowanceGranted = $allowance.granted
            Unlimited = $allowance.unlimited
        }
    }
    $rows | Sort-Object DisplayName, ObjectId
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
