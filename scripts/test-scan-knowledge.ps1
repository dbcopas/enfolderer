#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$calls = [Collections.Generic.List[object]]::new()
$failIndex = $false
$bulkCount = 10001
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function az {
    $global:LASTEXITCODE = 0
    @{ accessToken = 'offline-test-token'; expires_on = [DateTimeOffset]::UtcNow.AddHours(1).ToUnixTimeSeconds() } | ConvertTo-Json
}
function Invoke-RestMethod {
    param($Uri, $Method, $Headers, $ContentType, $Body)
    if ($Uri -eq 'https://api.scryfall.com/bulk-data/oracle_cards') {
        return [pscustomobject]@{ type = 'oracle_cards'; jsonl_download_uri = 'https://data.scryfall.io/fixture.jsonl.gz'; updated_at = '2026-10-20T00:00:00Z' }
    }
    if ($Uri -like 'https://api.scryfall.com/cards/named*') {
        return [pscustomobject]@{
            id = '11111111-1111-1111-1111-111111111111'; name = 'Fixture'
            set = 'tst'; collector_number = '1'; scryfall_uri = 'https://scryfall.com/card/tst/1/fixture'
            card_faces = @([pscustomobject]@{ name = 'Front'; oracle_text = 'Front fixture fact' },
                [pscustomobject]@{ name = 'Back'; oracle_text = 'Back fixture fact' })
        }
    }
    $payload = $Body | ConvertFrom-Json
    $calls.Add(@{ Uri = $Uri; Method = $Method; Payload = $payload })
    if ($Uri -like '*/docs/search?*') {
        return [pscustomobject]@{ value = @([pscustomobject]@{id='ffffffff-ffff-ffff-ffff-ffffffffffff'}) }
    }
    if ($Uri -like '*/docs/index?*') {
        return [pscustomobject]@{ value = @($payload.value | ForEach-Object { [pscustomobject]@{ status = -not $failIndex } }) }
    }
    return [pscustomobject]@{}
}
function Invoke-WebRequest {
    param($Uri, $Headers, $OutFile)
    $file = [IO.File]::Create($OutFile)
    $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Compress)
    $writer = [IO.StreamWriter]::new($gzip)
    try {
        for ($i = 1; $i -le $bulkCount; $i++) {
            $id = '00000000-0000-0000-0000-' + $i.ToString('000000000000')
            $writer.WriteLine((@{
                id = $id; oracle_id = $id; name = "Card $i"; set = 'tst'; collector_number = "$i"
                oracle_text = 'Public fixture'; scryfall_uri = "https://scryfall.com/card/tst/$i/fixture"
            } | ConvertTo-Json -Compress))
        }
    }
    finally { $writer.Dispose() }
}
$parameters = @{
    SearchEndpoint = 'https://fixture.search.windows.net'
    ModelEndpoint = 'https://fixture.openai.azure.com'
    ModelDeployment = 'supported-model'; ModelName = 'supported-model'; CardNames = @('Fixture', 'Fixture')
}
$script = Join-Path $PSScriptRoot 'configure-scan-knowledge.ps1'
& $script @parameters -WhatIf
Assert ($calls.Count -eq 0) 'WhatIf must not invoke the Search data plane.'
$null = & $script @parameters -Confirm:$false
Assert ($calls.Count -eq 4) 'Setup must create one index, import one deduplicated card, then source/base.'
$import = @($calls | Where-Object Uri -like '*/docs/index?*')[0].Payload.value[0]
Assert ($import.id -eq '11111111-1111-1111-1111-111111111111') 'Source keys must be stable Scryfall IDs.'
Assert ($import.content -like '*Front fixture fact*' -and $import.content -like '*Back fixture fact*') 'Both faces must be indexed.'
Assert ($import.url -eq 'https://scryfall.com/card/tst/1/fixture' -and $import.retrievedAt) 'Imported facts require source and retrieval date.'
$base = @($calls | Where-Object Uri -like '*/knowledgebases/*')[0].Payload
Assert ($base.outputMode -eq 'answerSynthesis' -and $base.models[0].azureOpenAIParameters.deploymentId -eq 'supported-model') 'Setup must wire real IQ synthesis to the selected model.'
$calls.Clear()
$failIndex = $true
$rejected = $false
try { $null = & $script @parameters -Confirm:$false }
catch { $rejected = $_.Exception.Message -like '*Indexing failed*' }
Assert ($rejected -and $calls.Count -eq 2) 'Per-document indexing failures must stop setup before knowledge-base success.'
$parameters.SearchEndpoint = 'https://evil.example'
$calls.Clear()
$rejected = $false
try { $null = & $script @parameters -Confirm:$false }
catch { $rejected = $_.Exception.Message -like '*SearchEndpoint*' }
Assert ($rejected -and $calls.Count -eq 0) 'Unsafe endpoints must fail before token acquisition or mutation.'
$parameters.SearchEndpoint = 'https://fixture.search.windows.net'
$parameters.Remove('CardNames')
$failIndex = $false
$calls.Clear()
$null = & $script @parameters -OracleBulk -Confirm:$false
$batches = @($calls | Where-Object Uri -like '*/docs/index?*')
Assert ($batches.Count -eq 102) '10001 cards require 101 import batches and one stale-document cleanup batch.'
Assert ($batches[0].Payload.value.Count -eq 100 -and $batches[0].Payload.value[0].names[0] -eq 'card 1') 'Bulk uses bounded batches and filterable normalized identity names.'
Assert ($batches[-1].Payload.value[0].'@search.action' -eq 'delete') 'Stale documents are deleted only after successful bulk import.'
$calls.Clear()
$bulkCount = 3
$rejected = $false
try { $null = & $script @parameters -OracleBulk -Confirm:$false }
catch { $rejected = $_.Exception.Message -like '*unexpectedly small*' }
Assert ($rejected -and -not @($calls | Where-Object Uri -like '*/docs/search?*').Count) 'Truncated bulk export cannot trigger stale-document deletion or claim completion.'
Write-Output 'All knowledge setup checks passed: seed and compressed bulk imports, bounded batches, source provenance, stable keys, coverage names, stale cleanup, truncated export, partial failure, WhatIf and endpoint validation.'
