<#
.SYNOPSIS
    Restores the signed-in owner's hibernated demo from its saved deployment template.
.DESCRIPTION
    Recreates infrastructure without rebuilding images or restoring deleted photos.
    Reports DNS, certificate, client and Foundry reconnection work; health is not proof of
    complete agent connectivity. Never claims demo readiness before those steps are verified.
.EXAMPLE
    .\scripts\restore-demo.ps1 -WhatIf
.EXAMPLE
    .\scripts\restore-demo.ps1 -ManifestPath C:\private\demo.json
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param([string] $ManifestPath)
#Requires -Version 7.0
. (Join-Path $PSScriptRoot 'demo-lifecycle.ps1')
$identity = Get-DemoIdentity
if (-not $ManifestPath) {
    $directory = Get-DemoStateDirectory $identity
    $files = @(Get-ChildItem -LiteralPath $directory -Filter '*.json' -ErrorAction Stop | Where-Object {
        $saved = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json -Depth 100
        $saved.status -ne 'InfrastructureRestored'
    })
    if ($files.Count -ne 1) { throw "Expected one recovery manifest; found $($files.Count). Specify -ManifestPath." }
    $ManifestPath = $files[0].FullName
}
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -Depth 100
Assert-DemoManifest $manifest $identity
if ($manifest.status -notin @('Prepared', 'Stopped', 'StopFailed', 'RestoreFailed', 'InfrastructureRestored')) {
    throw "Unsupported recovery state: $($manifest.status)"
}
$subscription = $manifest.subscriptionId
$prefix = $manifest.prefix
if (-not $PSCmdlet.ShouldProcess("$subscription / $prefix", 'Restore saved deployment (custom domains require new certificate binding)')) { return }
$work = Join-Path ([IO.Path]::GetTempPath()) "enfolderer-restore-$([guid]::NewGuid())"
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $templatePath = Join-Path $work 'template.json'
    $parametersPath = Join-Path $work 'parameters.json'
    $manifest.template | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $templatePath -Encoding utf8
    @{ parameters = $manifest.parameters } | ConvertTo-Json -Depth 100 |
        Set-Content -LiteralPath $parametersPath -Encoding utf8
    $null = Invoke-DemoAz @('deployment', 'sub', 'create', '--subscription', $subscription,
        '-n', $manifest.deploymentName, '-l', $manifest.location, '--template-file', $templatePath,
        '--parameters', "@$parametersPath")
    $apps = @(Get-DemoApps $subscription $prefix)
    foreach ($app in $apps) {
        if ($app.properties.configuration.PSObject.Properties['ingress'] -and
            $app.properties.configuration.ingress -and $app.properties.configuration.ingress.fqdn) {
            Wait-DemoHealth "https://$($app.properties.configuration.ingress.fqdn)"
        }
    }
    $manifest.status = 'InfrastructureRestored'
    Save-DemoManifest $manifest $ManifestPath
    $api = @($apps | Where-Object name -eq "$prefix-api")[0]
    $fqdn = $api.properties.configuration.ingress.fqdn
    Write-Output "Infrastructure restored for $prefix; endpoint health checks passed. Not yet verified demo-ready."
    Write-Output "API: https://$fqdn"
    Write-Output 'Required follow-up (do not skip):'
    Write-Output "1. Select subscription $subscription with az account set --subscription $subscription."
    Write-Output "2. Reconnect existing Foundry agents to the NEW MCP URLs: .\scripts\update-demo.ps1 -DeploymentName $($manifest.deploymentName) -NamePrefix $prefix -SkipImages"
    foreach ($domain in $manifest.customDomains) {
        Write-Output "3. DNS $($domain.name): CNAME -> $fqdn; TXT asuid.$($domain.name) -> $($api.properties.customDomainVerificationId). Create a NEW managed certificate and bind it; the old certificate ID cannot be reused."
    }
    if (-not @($manifest.customDomains).Count) {
        Write-Output '3. No custom domains were saved. If using the generated API URL, update Entra SPA redirect URIs and desktop api_base_url to the new URL.'
    }
    Write-Output '4. Check APIM backend URLs and any external bookmarks/configuration against the new API URL. Preserve existing tenant, audience, allowlists and per-agent permissions.'
    Write-Output "5. Run .\scripts\test-scan-access.ps1 -NamePrefix $prefix, sign in through the website, and submit a sample scan. Verify identification, overlay and collection access."
    Write-Output 'Deleted scans, crops, generated overlays and queued work are NOT restored. Collection snapshots and credit balances were retained.'
}
catch {
    $manifest.status = 'RestoreFailed'
    Save-DemoManifest $manifest $ManifestPath
    throw
}
finally {
    foreach ($file in 'template.json', 'parameters.json') {
        $path = Join-Path $work $file
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    Remove-Item -LiteralPath $work
}
