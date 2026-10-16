<#
.SYNOPSIS
    Removes obsolete classic authoring grants and reconciles the geometry-access demo switch.
.DESCRIPTION
    Run after Bicep creates the Consumer grants. Incremental ARM does not delete omitted roles.
    Only exact worker/project Foundry User assignments and an explicitly disabled geometry
    Consumer grant are removed; team authoring and unrelated grants are untouched.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $NamePrefix = 'enf-demo',
    [bool] $GrantGeometryAccess = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$worker = az identity show -g "$NamePrefix-platform" -n "$NamePrefix-worker-id" --query principalId -o tsv
if ($LASTEXITCODE -ne 0 -or -not $worker) { throw 'Could not read the worker identity.' }
$projects = @(az resource list --resource-type Microsoft.CognitiveServices/accounts/projects -o json | ConvertFrom-Json |
    Where-Object { $_.resourceGroup -in @("$NamePrefix-platform", "$NamePrefix-cardgeo", "$NamePrefix-cardid") })
if ($LASTEXITCODE -ne 0) { throw 'Could not read Foundry projects.' }
$roles = @(az role assignment list --assignee $worker --all -o json | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect worker assignments.' }
$consumer = 'eed3b665-ab3a-47b6-8f48-c9382fb1dad6'
$classic = '53ca6127-db72-4b80-b1b0-d745d6d5456d'

foreach ($name in 'cardgeo', 'cardid') {
    $matches = @($projects | Where-Object { ($_.name -split '/')[-1] -eq $name })
    if ($matches.Count -ne 1) { throw "Expected one $name project in this demo." }
    $scope = $matches[0].id
    $projectRoles = @($roles | Where-Object { $_.scope -eq $scope })
    $keepConsumer = $name -eq 'cardid' -or $GrantGeometryAccess
    if ($keepConsumer -and -not ($projectRoles | Where-Object { $_.roleDefinitionId.EndsWith("/$consumer") })) {
        throw "Consumer grant missing on $scope. Deploy Bicep successfully before removing legacy access."
    }
    foreach ($role in $projectRoles) {
        if ($role.roleDefinitionId.EndsWith("/$classic") -or (
            -not $keepConsumer -and $role.roleDefinitionId.EndsWith("/$consumer"))) {
            if ($PSCmdlet.ShouldProcess($role.id, 'Remove obsolete worker access')) {
                az role assignment delete --ids $role.id
                if ($LASTEXITCODE -ne 0) { throw "Could not remove exact assignment $($role.id)." }
                Write-Host "Removed $($role.roleDefinitionName) from worker on $name."
            }
        }
    }
}
