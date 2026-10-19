Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Join-Path ([IO.Path]::GetTempPath()) ("enfolderer-users-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$tenant = '11111111-1111-1111-1111-111111111111'
$owner = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
$guest = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
$unfunded = 'cccccccc-cccc-cccc-cccc-cccccccccccc'
$global:DemoUsersTest = [pscustomobject]@{
    Tenant = $tenant; Ids = "$owner,$guest,$unfunded,$guest"; Modes = @(); Directories = @()
    FailAllowance = $false; FailGraph = $false; InvalidBalance = $false; GraphTenant = ''; Calls = 0
}
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function az {
    $global:LASTEXITCODE = 0
    $state = $global:DemoUsersTest
    if ($args[0] -eq 'account' -and $args[1] -eq 'show') {
        return (@{ id = 'test-subscription'; tenantId = $state.Tenant } | ConvertTo-Json)
    }
    if ($args[0] -eq 'account' -and $args[1] -eq 'get-access-token') {
        $state.GraphTenant = $args[[array]::IndexOf($args, '--tenant') + 1]
        return 'mock-graph-token'
    }
    if ($args[0] -eq 'containerapp') {
        return (@{ properties = @{
            template = @{ containers = @(@{ env = @(
                @{ name = 'AzureAd__TenantId'; value = '11111111-1111-1111-1111-111111111111' },
                @{ name = 'AzureAd__ClientId'; value = 'dddddddd-dddd-dddd-dddd-dddddddddddd' },
                @{ name = 'MobileWeb__AllowedUserObjectIds'; value = $state.Ids }
            ) }) }
            configuration = @{ ingress = @{ fqdn = 'test.example'; customDomains = @(@{ name = 'scan.example' }) } }
        } } | ConvertTo-Json -Depth 12)
    }
    throw "Unexpected Azure CLI call: $args"
}
function Invoke-RestMethod {
    param($Headers, $Uri)
    Assert ($Headers.Authorization -eq 'Bearer mock-graph-token') 'Graph must use the explicit tenant token.'
    if ($global:DemoUsersTest.FailGraph) { throw 'Graph lookup failed.' }
    $id = ([uri]$Uri).AbsolutePath.Split('/')[-1]
    [pscustomobject]@{
        id = $id
        displayName = if ($id.StartsWith('a')) { 'Owner' } elseif ($id.StartsWith('b')) { "Guest $([char]0x00E9)" } else { 'Unfunded' }
        userType = if ($id.StartsWith('a')) { 'Member' } else { 'Guest' }
        externalUserState = if ($id.StartsWith('c')) { 'PendingAcceptance' } else { 'Accepted' }
    }
}
function dotnet {
    $global:LASTEXITCODE = 0
    $state = $global:DemoUsersTest
    $directory = $args[[array]::IndexOf($args, 'allowance') - 1]
    $command = Get-Content -Raw -LiteralPath $args[-1] | ConvertFrom-Json
    Assert (@($command.PSObject.Properties).Count -eq 1) 'Reads must never send a credit adjustment or operation ID.'
    $state.Calls++
    $state.Modes += $args[-2]
    $state.Directories += Split-Path -Parent $directory
    if ($state.FailAllowance) { $global:LASTEXITCODE = 1; return 'Mock allowance failure.' }
    $id = $command.objectId
    @{ ownerKey = "11111111-1111-1111-1111-111111111111:$id"
        granted = -not $id.StartsWith('c')
        remaining = if ($state.InvalidBalance) { $null } elseif ($id.StartsWith('b')) { 12 } else { 0 }
        unlimited = $id.StartsWith('a') } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $directory 'allowance.json') -Encoding utf8
}
try {
    $config = Join-Path $root 'config.txt'
    @(
        'api_base_url=https://scan.example'
        "tenant_id=$tenant"
        'client_id=eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee'
        'scope=api://dddddddd-dddd-dddd-dddd-dddddddddddd/Scan.Submit'
    ) | Set-Content -LiteralPath $config
    $script = Join-Path $PSScriptRoot 'list-demo-users.ps1'
    $rows = @(& $script -ConfigFile $config -NamePrefix test -SignIn)
    Assert ($rows.Count -eq 3 -and $global:DemoUsersTest.Calls -eq 3) 'Enumerate every approved user once, including the owner.'
    Assert (($rows | Where-Object ObjectId -eq $owner).RemainingScans -eq 'Unlimited') 'Unlimited must not appear as zero scans.'
    Assert (($rows | Where-Object ObjectId -eq $guest).RemainingScans -eq 12) 'Finite credits must remain numeric and exact.'
    Assert (($rows | Where-Object ObjectId -eq $guest).DisplayName.Contains([char]0x00E9)) 'Names must preserve Unicode.'
    Assert (-not ($rows | Where-Object ObjectId -eq $unfunded).AllowanceGranted) 'Unfunded accounts must be explicit.'
    Assert (($rows | Where-Object ObjectId -eq $unfunded).InvitationStatus -eq 'PendingAcceptance') 'Pending invitees must be included.'
    Assert (($global:DemoUsersTest.Modes -join ',') -eq 'sign-in,silent,silent') 'Only the first user should trigger interactive sign-in.'
    Assert ($global:DemoUsersTest.GraphTenant -eq $tenant) 'Graph must target the configured tenant.'
    foreach ($directory in $global:DemoUsersTest.Directories) {
        Assert (-not (Test-Path -LiteralPath $directory)) 'Temporary allowance files must be removed.'
    }
    $global:DemoUsersTest.FailAllowance = $true
    $failed = $false
    try { & $script -ConfigFile $config -NamePrefix test }
    catch { $failed = $_.Exception.Message.Contains('Allowance read failed') }
    Assert $failed 'Failed reads must not become zero balances.'
    $global:DemoUsersTest.FailAllowance = $false
    $global:DemoUsersTest.InvalidBalance = $true
    $failed = $false
    try { & $script -ConfigFile $config -NamePrefix test }
    catch { $failed = $_.Exception.Message.Contains('Invalid allowance response') }
    Assert $failed 'Malformed balances must not be reported as successful reads.'
    $global:DemoUsersTest.InvalidBalance = $false
    $global:DemoUsersTest.FailGraph = $true
    $failed = $false
    try { & $script -ConfigFile $config -NamePrefix test }
    catch { $failed = $_.Exception.Message.Contains('Graph lookup failed') }
    Assert $failed 'Directory lookup errors must remain explicit.'
    $global:DemoUsersTest.FailGraph = $false
    $global:DemoUsersTest.Tenant = $guest
    $failed = $false
    try { & $script -ConfigFile $config -NamePrefix test }
    catch { $failed = $_.Exception.Message.Contains('same tenant') }
    Assert $failed 'Wrong-tenant Azure CLI context must be rejected.'
    $global:DemoUsersTest.Tenant = $tenant
    $global:DemoUsersTest.Ids = 'not-a-guid'
    $failed = $false
    try { & $script -ConfigFile $config -NamePrefix test }
    catch { $failed = $true }
    Assert $failed 'Invalid allowlist IDs must be rejected before querying users.'
    $global:DemoUsersTest.Ids = ''
    $failed = $false
    try { & $script -ConfigFile $config -NamePrefix test }
    catch { $failed = $_.Exception.Message.Contains('no explicit approved-user allowlist') }
    Assert $failed 'Missing allowlists must not produce misleading empty reports.'
    Write-Output 'All demo-user reporting checks passed.'
}
finally {
    foreach ($directory in ($global:DemoUsersTest.Directories | Select-Object -Unique)) {
        Assert (-not (Test-Path -LiteralPath $directory)) 'Failure paths must remove temporary allowance files.'
    }
    Remove-Variable DemoUsersTest -Scope Global
    Remove-Item -LiteralPath $root -Recurse -Force
}
