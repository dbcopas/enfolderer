<#
.SYNOPSIS
    Verifies real preview model inference, authentication and native token-limit enforcement.
.DESCRIPTION
    Creates a temporary model/alias over the existing deployment with a one-token limit,
    proves HTTP 429, and removes both assets in finally. No production policy is changed.
#>
#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $SubscriptionId,
    [Parameter(Mandatory)] [string] $NamePrefix
)
. (Join-Path $PSScriptRoot 'demo-lifecycle.ps1')
. (Join-Path $PSScriptRoot 'ai-gateway-preview.ps1')
$identity = Get-DemoIdentity $SubscriptionId
Assert-DemoOwner $identity $SubscriptionId $NamePrefix
$id = Get-PreviewGatewayId $SubscriptionId $NamePrefix
$gateway = Invoke-PreviewArm $SubscriptionId "$id`?api-version=$script:GatewayPreviewVersion"
Assert-PreviewGateway $gateway $SubscriptionId $NamePrefix $identity.User
$url = "https://$($gateway.properties.frontend.defaultHostname)/default/models/openai/v1/chat/completions"
$key = Get-PreviewRuntimeKey $SubscriptionId $id 'scan-questions'
$body = @{ model = 'gpt-4o'; max_tokens = 20; messages = @(@{ role = 'user'; content = 'Reply with gateway-ready.' }) }
$response = Invoke-WebRequest $url -Method Post -ContentType application/json `
    -Headers @{ 'api-key' = $key } -Body ($body | ConvertTo-Json -Depth 10) -SkipHttpErrorCheck
if ($response.StatusCode -ne 200) { throw "Preview model request failed: HTTP $($response.StatusCode)." }
$answer = $response.Content | ConvertFrom-Json
if (-not $answer.usage.total_tokens -or -not $answer.choices[0].message.content) { throw 'Missing model answer/token usage.' }
Write-Output "PASS real dedicated preview model completion; reported tokens: $($answer.usage.total_tokens)."
$denied = Invoke-WebRequest $url -Method Post -ContentType application/json `
    -Headers @{ 'api-key' = 'invalid' } -Body ($body | ConvertTo-Json -Depth 10) -SkipHttpErrorCheck
if ($denied.StatusCode -notin @(401, 403)) { throw 'Invalid runtime key was not rejected.' }
foreach ($entry in @(
    @{ Suffix = '-geo'; Server = 'mcp-imaging' },
    @{ Suffix = '-mtg'; Server = 'mcp-cardcatalog-mtg' },
    @{ Suffix = '-pkm'; Server = 'mcp-cardcatalog-pokemon' }
)) {
    $toolGateway = Get-OptionalPreviewGateway $SubscriptionId (Get-PreviewGatewayId $SubscriptionId $NamePrefix $entry.Suffix)
    if (-not $toolGateway) { throw 'Missing isolated tool gateway.' }
    Assert-PreviewGateway $toolGateway $SubscriptionId $NamePrefix $identity.User
    $toolUrl = "https://$($toolGateway.properties.frontend.defaultHostname)/default/toolservers/$($entry.Server)/mcp"
    $crossDomain = Invoke-WebRequest $toolUrl -Method Post -ContentType application/json `
        -Headers @{ 'api-key' = $key } -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' -SkipHttpErrorCheck
    if ($crossDomain.StatusCode -notin @(401, 403)) { throw "Model credential was not isolated from $($entry.Server)." }
}
Write-Output 'PASS model credential denied by all three isolated tool gateways.'
$modelId = "$id/workspaces/default/modelProviders/foundry/models"
$existing = Invoke-PreviewArm $SubscriptionId "$modelId/gpt-4o`?api-version=$script:GatewayPreviewVersion"
$name = "probe-$([guid]::NewGuid().ToString('N').Substring(0,12))"
$createdModel = $false
$createdAlias = $false
try {
    $null = Set-PreviewAsset $SubscriptionId "$modelId/$name" @{
        displayName = $name; supportedEndpoints = @('/chat/completions')
        deployment = $existing.properties.deployment
        policies = @(@{ type = 'tokenLimit'; count = 1; period = 'minute'; counterKey = @('identity'); scope = 'resource' })
    }
    $createdModel = $true
    $null = Set-PreviewAsset $SubscriptionId "$id/workspaces/default/aliases/$name" @{
        aliasName = $name; backendModels = @(@{ resourceId = "$modelId/$name" })
    }
    $createdAlias = $true
    $body.model = $name
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(2)
    do {
        $limited = Invoke-WebRequest $url -Method Post -ContentType application/json `
            -Headers @{ 'api-key' = $key } -Body ($body | ConvertTo-Json -Depth 10) -SkipHttpErrorCheck
        if ($limited.StatusCode -ne 404) { break }
        if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'Temporary preview alias did not propagate to the runtime.' }
        Start-Sleep -Seconds 5
    } while ($true)
    for ($attempt = 0; $limited.StatusCode -eq 200 -and $attempt -lt 5; $attempt++) {
        Start-Sleep -Seconds 2
        $limited = Invoke-WebRequest $url -Method Post -ContentType application/json `
            -Headers @{ 'api-key' = $key } -Body ($body | ConvertTo-Json -Depth 10) -SkipHttpErrorCheck
    }
    if ($limited.StatusCode -ne 429) { throw "Native token-limit probe expected HTTP 429, got $($limited.StatusCode)." }
    Write-Output 'PASS native dedicated preview token policy returned HTTP 429; invalid runtime key rejected.'
}
finally {
    if ($createdAlias) { $null = Invoke-PreviewArm $SubscriptionId "$id/workspaces/default/aliases/$name`?api-version=$script:GatewayPreviewVersion" DELETE }
    if ($createdModel) { $null = Invoke-PreviewArm $SubscriptionId "$modelId/$name`?api-version=$script:GatewayPreviewVersion" DELETE }
    $key = $null
}
