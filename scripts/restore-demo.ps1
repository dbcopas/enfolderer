<#
.SYNOPSIS
    Restores the signed-in owner's hibernated demo and captured dedicated Search.
.DESCRIPTION
    Recreates infrastructure without rebuilding images or restoring deleted photos.
    Reports DNS, certificate, client and Foundry reconnection work; health is not proof of
    complete agent connectivity. Never claims demo readiness before those steps are verified.
    When captured, restores dedicated Search and its exact model access, then rebuilds the
    public Oracle corpus with configure-scan-knowledge.ps1 -OracleBulk. Requires Search data
    permissions, public Scryfall access, and the retained synthesis model deployment.
    Recreates captured dedicated preview gateways with fresh identities and credentials,
    reconnects the real model/tools and verifies live inference, tools and token enforcement.
.EXAMPLE
    .\scripts\restore-demo.ps1 -WhatIf
.EXAMPLE
    .\scripts\restore-demo.ps1 -ManifestPath C:\private\demo.json
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param([string] $ManifestPath)
#Requires -Version 7.0
. (Join-Path $PSScriptRoot 'demo-lifecycle.ps1')
. (Join-Path $PSScriptRoot 'ai-gateway-preview.ps1')
$subscriptionHint = $null
if ($ManifestPath) {
    $hint = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json -Depth 100
    if (-not [guid]::TryParse($hint.subscriptionId, [ref]([guid]::Empty))) { throw 'Invalid manifest subscription hint.' }
    $subscriptionHint = [string]$hint.subscriptionId
    $hint = $null
}
$identity = Get-DemoIdentity -SubscriptionId $subscriptionHint
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
$search = Get-DemoProperty $manifest 'search'
$previewGateway = Get-DemoProperty $manifest 'previewGateway'
if ($previewGateway) { Assert-DemoPreviewRecovery $previewGateway $subscription $prefix }
if ($search) { Assert-DemoSearchManifest $search $subscription $prefix }
if (-not $PSCmdlet.ShouldProcess("$subscription / $prefix", 'Restore saved deployment (custom domains require new certificate binding)')) { return }
$work = Join-Path $PSScriptRoot ".demo-restore-$([guid]::NewGuid())"
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
    if ($search) {
        Restore-DemoSearch $search $subscription $work
        Write-Output 'Dedicated Search restored and public Oracle corpus rebuilt; synthesis model account/deployment retained.'
    }
    elseif ((Get-DemoProperty $manifest.parameters 'scanKnowledgeEndpoint')) {
        Write-Warning 'Legacy manifest has no Search recovery snapshot; configured Search was not deleted/restored by this workflow. Verify it manually.'
    }
    if ($previewGateway) {
        & (Join-Path $PSScriptRoot 'configure-ai-gateway-preview.ps1') -NamePrefix $prefix -SubscriptionId $subscription -Confirm:$false
        & (Join-Path $PSScriptRoot 'test-ai-gateway.ps1') -NamePrefix $prefix -SubscriptionId $subscription
        & (Join-Path $PSScriptRoot 'test-ai-gateway-model.ps1') -NamePrefix $prefix -SubscriptionId $subscription
        Write-Output 'Dedicated preview gateways recreated, fresh runtime keys stored, agent connections refreshed and live tool/model checks passed.'
    }
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
    if ($previewGateway) {
        Write-Output '2. Dedicated preview connections, model inference, authentication denials and native token limit were verified automatically.'
    }
    else {
        Write-Output "2. Reconnect existing Foundry agents: .\scripts\update-demo.ps1 -DeploymentName $($manifest.deploymentName) -NamePrefix $prefix -SkipImages"
    }
    foreach ($domain in $manifest.customDomains) {
        Write-Output "3. DNS $($domain.name): CNAME -> $fqdn; TXT asuid.$($domain.name) -> $($api.properties.customDomainVerificationId). Create a NEW managed certificate and bind it; the old certificate ID cannot be reused."
    }
    if (-not @($manifest.customDomains).Count) {
        Write-Output '3. No custom domains were saved. If using the generated API URL, update Entra SPA redirect URIs and desktop api_base_url to the new URL.'
    }
    Write-Output '4. Check the separate Scan API backend and external bookmarks against the new API URL. Review dedicated gateway monitoring without enabling payload capture. Preserve tenant, audience, allowlists and per-agent permissions.'
    Write-Output "5. Run .\scripts\test-scan-access.ps1 -NamePrefix $prefix, sign in through the website, and submit a sample scan. Verify identification, overlay and collection access."
    Write-Output 'Deleted scans, crops, generated overlays and queued work are NOT restored. Collection snapshots and credit balances were retained.'
}
catch {
    $manifest.status = 'RestoreFailed'
    Save-DemoManifest $manifest $ManifestPath
    throw
}
finally {
    foreach ($file in 'template.json', 'parameters.json', 'search.json', 'search-model.json') {
        $path = Join-Path $work $file
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    Remove-Item -LiteralPath $work
}
