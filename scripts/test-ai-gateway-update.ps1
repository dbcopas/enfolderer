#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Assert($condition, [string] $message) { if (-not $condition) { throw $message } }
function Reject([scriptblock] $action) {
    $failed = $false
    try { & $action } catch { $failed = $true }
    Assert $failed 'Incomplete bootstrap must stop before gateway/image deployment.'
}
$root = Join-Path $PSScriptRoot ".gateway-update-test-$([guid]::NewGuid())"
$global:GatewayUpdateTest = @{
    Bootstrap = $true; MissingCaller = $false; WrongAudience = $false
    Direct = 0; Native = 0; Deploys = 0; Parameters = $null
    Audience = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
}
function az {
    $global:LASTEXITCODE = 0
    $a = @($args)
    function Value([string] $key) { $a[[array]::IndexOf($a, $key) + 1] }
    $query = Value '--query'
    switch ($a[0..1] -join ' ') {
        'account show' {
            @{ name = 'fixture'; id = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'; user = @{ name = 'fixture-owner' } } |
                ConvertTo-Json -Compress
        }
        'cognitiveservices account' {
            ConvertTo-Json -InputObject @(@{ name = 'model'; sub = 'model' }) -Compress
        }
        'resource list' { 'model/project' }
        'containerapp show' {
            if ($query -like '*ingress.fqdn*') { "$(Value '-n').azurecontainerapps.io" }
            elseif ($query -like '*ScanPipeline__UseResponses*') {
                if ($global:GatewayUpdateTest.Bootstrap) { 'false' } else { 'true' }
            }
            elseif ($query -eq 'properties.template.containers[0].env') {
                $caller = if ($global:GatewayUpdateTest.Bootstrap -or $global:GatewayUpdateTest.MissingCaller) {
                    ''
                } else { '11111111-1111-1111-1111-111111111111,22222222-2222-2222-2222-222222222222' }
                $audience = if ($global:GatewayUpdateTest.WrongAudience) { 'foreign-audience' } else { $global:GatewayUpdateTest.Audience }
                ConvertTo-Json -InputObject @(
                    @{ name = 'McpServer__AllowedCallerObjectIds'; value = $caller },
                    @{ name = 'McpServer__Audience'; value = $audience }
                ) -Compress
            }
            else { throw "Unexpected app query: $query" }
        }
        default { throw "Unexpected Azure operation: $($a[0..1] -join ' ')" }
    }
}
try {
    $scripts = Join-Path $root 'scripts'
    $agents = Join-Path $root 'agents'
    $infra = Join-Path $root 'infra'
    foreach ($path in $scripts, $agents, $infra) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'update-demo.ps1') -Destination $scripts
    Set-Content -LiteralPath (Join-Path $infra 'main.bicep') -Value '// Fixture presence check only.'
    Set-Content -LiteralPath (Join-Path $scripts 'configure-mcp-audience.ps1') -Value @'
param($NamePrefix)
$global:GatewayUpdateTest.Audience
'@
    Set-Content -LiteralPath (Join-Path $agents 'provision-foundry.ps1') -Value @'
param($ProjectEndpoint, $Path, [string[]]$Only, $McpServerUrl, $McpAudience)
if ($McpAudience -ne $global:GatewayUpdateTest.Audience) { throw 'Wrong bootstrap audience.' }
foreach ($url in $McpServerUrl.Values) {
    if ($url -notlike 'https://alice-demo-*.azurecontainerapps.io/mcp') { throw 'Invalid initial bootstrap URL.' }
}
$global:GatewayUpdateTest.Direct++
foreach ($name in $Only) {
    $principal = switch ($name) {
        'CardBoundaryAgent' { '11111111-1111-1111-1111-111111111111' }
        'MtgCardIdAgent' { '22222222-2222-2222-2222-222222222222' }
        'PokemonCardIdAgent' { '33333333-3333-3333-3333-333333333333' }
    }
    [pscustomobject]@{ Name = $name; Id = $name; PrincipalId = $principal }
}
'@
    Set-Content -LiteralPath (Join-Path $scripts 'deploy-images.ps1') -Value @'
param($DeploymentName, $Tag, $McpAudience, $BoundaryAgentId, $MtgAgentId, $PokemonAgentId,
    $BoundaryAgentPrincipalId, $MtgAgentPrincipalId, $PokemonAgentPrincipalId)
$global:GatewayUpdateTest.Deploys++
$global:GatewayUpdateTest.Parameters = $PSBoundParameters
'@
    Set-Content -LiteralPath (Join-Path $scripts 'configure-ai-gateway-preview.ps1') -Value @'
param($NamePrefix, $SubscriptionId)
if ($SubscriptionId -ne 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb') { throw 'Wrong gateway subscription.' }
$global:GatewayUpdateTest.Native++
'@
    $update = Join-Path $scripts 'update-demo.ps1'
    & $update -NamePrefix alice-demo
    Assert ($global:GatewayUpdateTest.Direct -eq 2 -and $global:GatewayUpdateTest.Deploys -eq 1) 'First setup must create three identities before deploying real images.'
    $parameters = $global:GatewayUpdateTest.Parameters
    Assert ($parameters.BoundaryAgentId -eq 'CardBoundaryAgent' -and $parameters.MtgAgentId -eq 'MtgCardIdAgent' -and
        $parameters.PokemonAgentId -eq 'PokemonCardIdAgent' -and $parameters.McpAudience -eq $global:GatewayUpdateTest.Audience) 'Persist all named agent references and resource audience together.'
    foreach ($name in 'BoundaryAgentPrincipalId', 'MtgAgentPrincipalId', 'PokemonAgentPrincipalId') {
        Assert ([guid]::TryParse($parameters[$name], [ref]([guid]::Empty))) 'Persist exact bootstrap callers in deployment parameters.'
    }
    $global:GatewayUpdateTest.Direct = 0
    $global:GatewayUpdateTest.Deploys = 0
    Reject { & $update -NamePrefix alice-demo -SkipImages }
    Reject { & $update -NamePrefix alice-demo -SkipAgents }
    Assert ($global:GatewayUpdateTest.Deploys -eq 0 -and $global:GatewayUpdateTest.Native -eq 0 -and
        $global:GatewayUpdateTest.Direct -eq 0) 'First migration cannot skip identity/image prerequisites.'
    $global:GatewayUpdateTest.Bootstrap = $false
    & $update -NamePrefix alice-demo -SkipImages
    Assert ($global:GatewayUpdateTest.Native -eq 1 -and $global:GatewayUpdateTest.Deploys -eq 0 -and
        $global:GatewayUpdateTest.Direct -eq 0) 'Existing demo must publish only native routes, never transient direct connections.'
    $global:GatewayUpdateTest.Native = 0
    & $update -NamePrefix alice-demo
    Assert ($global:GatewayUpdateTest.Deploys -eq 1 -and $global:GatewayUpdateTest.Direct -eq 0 -and
        $global:GatewayUpdateTest.Native -eq 0) 'Normal full update uses the mandatory deployment hook without duplicate agent publishing.'
    $global:GatewayUpdateTest.Deploys = 0
    $global:GatewayUpdateTest.WrongAudience = $true
    Reject { & $update -NamePrefix alice-demo -SkipImages }
    $global:GatewayUpdateTest.WrongAudience = $false
    $global:GatewayUpdateTest.MissingCaller = $true
    Reject { & $update -NamePrefix alice-demo -SkipImages }
    Assert ($global:GatewayUpdateTest.Deploys -eq 0 -and $global:GatewayUpdateTest.Native -eq 0) 'Missing callers or changed audiences cannot silently update an incomplete demo.'
    Write-Output 'PASS gateway update bootstrap, exact deployment parameters, native-only refresh, no duplicate publishing and invalid skip/audience guards.'
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force
    Remove-Variable -Name GatewayUpdateTest -Scope Global
}
