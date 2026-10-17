<#
.SYNOPSIS
    Restores the pinned MIT-licensed Microsoft authentication browser dependency.
.DESCRIPTION
    The checked-in assets allow offline builds and avoid a runtime CDN dependency.
    Integrity is verified before replacing the local library. No Node.js build is required.
#>
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$directory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Enfolderer.Ai.Api\wwwroot\lib'
$expected = 'TlDXht2cVvKBRAwDJrpYYtb+vUcYNxMgTELqFTRcnu0SP//KlFc7eE8so0+lJ03E'
$url = 'https://cdn.jsdelivr.net/npm/@azure/msal-browser@4.25.0/lib/msal-browser.min.js'
$response = Invoke-WebRequest -Uri $url
$bytes = if ($response.Content -is [byte[]]) { $response.Content } else { [Text.Encoding]::UTF8.GetBytes($response.Content) }
$actual = [Convert]::ToBase64String([Security.Cryptography.SHA384]::HashData($bytes))
if ($actual -ne $expected) { throw 'MSAL package integrity check failed. No local asset was changed.' }
$license = Invoke-WebRequest -Uri 'https://cdn.jsdelivr.net/npm/@azure/msal-browser@4.25.0/LICENSE'
if ($license.Content -notmatch 'MIT License' -or $license.Content -notmatch 'Microsoft') {
    throw 'Expected Microsoft MIT license was not returned. No local asset was changed.'
}
$null = New-Item -ItemType Directory -Path $directory -Force
[IO.File]::WriteAllBytes((Join-Path $directory 'msal-browser.min.js'), $bytes)
$license.Content | Set-Content -LiteralPath (Join-Path $directory 'MSAL-LICENSE.txt') -Encoding utf8
Write-Host "Restored MSAL Browser 4.25.0 and its MIT license ($($bytes.Length) bytes)."
