Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-DemoAz {
    param([string[]] $Arguments)
    $text = & az @Arguments --only-show-errors -o json
    if ($LASTEXITCODE -ne 0) { throw "Azure command failed: az $($Arguments -join ' ')" }
    if ($text) { return ($text -join "`n") | ConvertFrom-Json -Depth 100 }
}

function Get-DemoIdentity {
    $account = Invoke-DemoAz @('account', 'show')
    if ($account.user.type -ne 'user') { throw 'Sign in as a human deployment owner with az login.' }
    $user = Invoke-DemoAz @('ad', 'signed-in-user', 'show')
    [pscustomobject]@{ Tenant = $account.tenantId; User = $user.id }
}

function Test-DemoOwner {
    param($Identity, [string] $Subscription, [string] $Prefix)
    foreach ($suffix in 'platform', 'cardgeo', 'cardid') {
        $scope = "/subscriptions/$Subscription/resourceGroups/$Prefix-$suffix"
        $grants = @(Invoke-DemoAz @('role', 'assignment', 'list', '--subscription', $Subscription,
            '--assignee', $Identity.User, '--include-groups', '--include-inherited', '--scope', $scope))
        if (-not @($grants | Where-Object {
            ($_.roleDefinitionId -split '/')[-1] -eq '8e3af657-a8ff-443c-a75c-2fe8c4bcb635'
        }).Count) { return $false }
    }
    return $true
}

function Assert-DemoOwner {
    param($Identity, [string] $Subscription, [string] $Prefix)
    if (-not (Test-DemoOwner $Identity $Subscription $Prefix)) {
        throw "Current user must have effective Owner on all three $Prefix deployment groups."
    }
}

function Find-DemoDeployment {
    param($Identity, [string] $SubscriptionId, [string] $DeploymentName)
    $subscriptions = @(Invoke-DemoAz @('account', 'list') | Where-Object {
        $_.state -eq 'Enabled' -and $_.tenantId -eq $Identity.Tenant -and
        (-not $SubscriptionId -or $_.id -eq $SubscriptionId)
    })
    $found = @(
        foreach ($subscription in $subscriptions) {
            $deployments = @(Invoke-DemoAz @('deployment', 'sub', 'list', '--subscription', $subscription.id))
            foreach ($deployment in $deployments) {
                if ($DeploymentName -and $deployment.name -ne $DeploymentName) { continue }
                $parameters = $deployment.properties.parameters
                if (-not $parameters -or -not $parameters.PSObject.Properties['namePrefix'] -or
                    -not $parameters.PSObject.Properties['apiClientId'] -or
                    -not $parameters.PSObject.Properties['imageTag']) { continue }
                $prefix = [string]$parameters.namePrefix.value
                if ($prefix -notmatch '^[a-z0-9][a-z0-9-]{1,10}[a-z0-9]$') { continue }
                if ($deployment.properties.provisioningState -ne 'Succeeded') { continue }
                [pscustomobject]@{
                    Subscription = $subscription.id; Prefix = $prefix; Deployment = $deployment
                }
            }
        }
    )
    # Repeated successful deployments of one environment are one candidate, not separate demos.
    $candidates = @($found | Group-Object { "$($_.Subscription)/$($_.Prefix)" } | ForEach-Object {
        $_.Group | Sort-Object { $_.Deployment.properties.timestamp } -Descending | Select-Object -First 1
    } | Where-Object { Test-DemoOwner $Identity $_.Subscription $_.Prefix })
    if ($candidates.Count -ne 1) {
        $choices = ($candidates | ForEach-Object {
            "$($_.Subscription) / $($_.Deployment.name) / $($_.Prefix)"
        }) -join "`n"
        throw "Expected one discoverable deployment; found $($candidates.Count). Specify -SubscriptionId and -DeploymentName.`n$choices"
    }
    Assert-DemoOwner $Identity $candidates[0].Subscription $candidates[0].Prefix
    $candidates[0]
}

function Get-DemoApps {
    param([string] $Subscription, [string] $Prefix)
    foreach ($entry in @(
        @('platform', 'api'), @('platform', 'worker'), @('cardgeo', 'mcp-imaging'),
        @('cardid', 'mcp-cardcatalog-mtg'), @('cardid', 'mcp-cardcatalog-pokemon')
    )) {
        Invoke-DemoAz @('containerapp', 'show', '--subscription', $Subscription,
            '-g', "$Prefix-$($entry[0])", '-n', "$Prefix-$($entry[1])")
    }
}

function Get-DemoStateDirectory {
    param($Identity)
    Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Enfolderer\DemoLifecycle\$($Identity.Tenant)\$($Identity.User)"
}

function Save-DemoManifest {
    param($Manifest, [string] $Path)
    $temporary = "$Path.tmp"
    $Manifest | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Assert-DemoManifest {
    param($Manifest, $Identity)
    if ($Manifest.schemaVersion -ne 1 -or $Manifest.ownerObjectId -ne $Identity.User -or
        $Manifest.tenantId -ne $Identity.Tenant) { throw 'Manifest belongs to another user/tenant or has an unsupported format.' }
    if ($Manifest.prefix -notmatch '^[a-z0-9][a-z0-9-]{1,10}[a-z0-9]$' -or
        -not [guid]::TryParse($Manifest.subscriptionId, [ref]([guid]::Empty))) {
        throw 'Invalid manifest deployment scope.'
    }
    Assert-DemoOwner $Identity $Manifest.subscriptionId $Manifest.prefix
}

function Wait-DemoHealth {
    param([string] $Url, [int] $Attempts = 30)
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $health = Invoke-RestMethod "$($Url.TrimEnd('/'))/healthz" -TimeoutSec 15
            if ($health.status -eq 'ok') { return }
            $failure = 'health response did not report ok'
        }
        catch { $failure = $_.Exception.Message }
        if ($attempt -lt $Attempts) { Start-Sleep -Seconds 10 }
    }
    throw "Health check failed for $Url : $failure"
}
