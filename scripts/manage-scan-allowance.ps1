<#
.SYNOPSIS
    Inspect or adjust an approved pilot user's one-time scan allowance.
.DESCRIPTION
    Requires an explicitly configured allowance administrator, not merely a diagnostics operator.
    Grant ten with -Credits 10 -Reason 'Initial pilot grant'. Top-ups and refunds are positive
    credits; negative adjustments cannot make the balance negative. -Unlimited makes the account
    unlimited; a later credit adjustment without this switch returns it to finite mode.
    Always retain and reuse OperationId after an ambiguous failure so a retry cannot double-grant.
    The same operation ID must not be reused with different parameters.
.EXAMPLE
    .\scripts\manage-scan-allowance.ps1 -UserObjectId '<guest-oid>' -Credits 10 -Reason 'Initial grant'
.EXAMPLE
    .\scripts\manage-scan-allowance.ps1 -UserObjectId '<owner-oid>' -Unlimited -Reason 'Owner account' -SignIn
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [guid] $UserObjectId,
    [int] $Credits = 0,
    [switch] $Unlimited,
    [string] $Reason,
    [guid] $OperationId = [guid]::NewGuid(),
    [string] $ConfigFile = (Join-Path $PSScriptRoot '..\Enfolderer.App\aiconfig.defaults.txt'),
    [string] $OutputDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Enfolderer\ScanAllowances'),
    [switch] $SignIn
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($UserObjectId -eq [guid]::Empty) { throw 'A nonempty tenant user object ID is required.' }
$adjust = $PSBoundParameters.ContainsKey('Credits') -or $Unlimited.IsPresent
if ($adjust -and [string]::IsNullOrWhiteSpace($Reason)) { throw 'A reason is required for every adjustment.' }
$command = @{ objectId = $UserObjectId.ToString('D') }
if ($adjust) {
    $command.operationId = $OperationId.ToString('D')
    $command.delta = $Credits
    $command.unlimited = $Unlimited.IsPresent
    $command.reason = $Reason
    Write-Host "Allowance operation ID: $OperationId (reuse this ID for a retry)"
}
$temp = [IO.Path]::GetTempFileName()
try {
    $command | ConvertTo-Json | Set-Content -LiteralPath $temp -Encoding utf8
    $record = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Enfolderer\DiagnosticsAuth\aiauth.json'
    $mode = if ($SignIn) { 'sign-in' } else { 'silent' }
    $tool = Join-Path $PSScriptRoot '..\tools\Enfolderer.Ai.ScanDiagnostics'
    & dotnet run --project $tool -- $ConfigFile $OutputDirectory allowance 24 $record none $mode $temp
    if ($LASTEXITCODE -ne 0) { throw 'Allowance operation failed. Inspect the error; reuse OperationId if retrying an adjustment.' }
}
finally { Remove-Item -LiteralPath $temp }
