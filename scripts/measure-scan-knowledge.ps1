<#
.SYNOPSIS
    Reports actual Search configuration, index/storage statistics and time-bounded usage.
.DESCRIPTION
    Read-only by default. StartTime and EndTime are mandatory UTC-compatible timestamps.
    Azure Monitor metrics are usage, not invoices or estimates. IncludeActualCost queries
    Cost Management for this Search resource only (billing data can lag).
    Optional BudgetAmount AND BudgetContact create/update a resource-filtered monthly
    alert at 100 percent of the explicitly supplied amount, in the billing currency.
    A budget is NOT a spending cap and never stops Search or model usage.
    Search, model token charges, Foundry and retained infrastructure can bill separately.
.EXAMPLE
    .\scripts\measure-scan-knowledge.ps1 -StartTime 2026-10-01T00:00:00Z -EndTime 2026-10-20T00:00:00Z
.EXAMPLE
    .\scripts\measure-scan-knowledge.ps1 -StartTime 2026-10-01T00:00:00Z -EndTime 2026-10-20T00:00:00Z -BudgetAmount 50 -BudgetContact owner@example.org -WhatIf
#>
#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)] [DateTimeOffset] $StartTime,
    [Parameter(Mandatory)] [DateTimeOffset] $EndTime,
    [string] $SubscriptionId,
    [string] $DeploymentName,
    [switch] $IncludeActualCost,
    [Nullable[decimal]] $BudgetAmount,
    [string[]] $BudgetContact,
    [ValidatePattern('^[a-zA-Z0-9_-]{1,63}$')] [string] $BudgetName = 'scan-knowledge'
)
. (Join-Path $PSScriptRoot 'demo-lifecycle.ps1')
if ($EndTime -le $StartTime -or $EndTime -gt [DateTimeOffset]::UtcNow) { throw 'Supply an explicit past time range with EndTime after StartTime.' }
if (($null -ne $BudgetAmount) -ne [bool]$BudgetContact) { throw 'BudgetAmount and BudgetContact must be supplied together.' }
if ($null -ne $BudgetAmount -and $BudgetAmount -le 0) { throw 'BudgetAmount must be greater than zero, in the billing currency.' }
foreach ($contact in $BudgetContact) {
    if ($contact -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') { throw 'Each BudgetContact must be an explicit email address.' }
}
$identity = Get-DemoIdentity -SubscriptionId $SubscriptionId
$target = Find-DemoDeployment $identity $SubscriptionId $DeploymentName
$subscription = $target.Subscription
$parameter = Get-DemoProperty $target.Deployment.properties.parameters 'scanKnowledgeEndpoint'
if (-not $parameter -or -not $parameter.value) { throw 'Selected deployment has no scanKnowledgeEndpoint.' }
$uri = [uri]$parameter.value
if ($uri.Scheme -ne 'https' -or $uri.Host -notmatch '^([a-z0-9-]+)\.search\.windows\.net$' -or
    $uri.AbsolutePath -ne '/' -or -not $uri.IsDefaultPort -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) {
    throw 'Configured Search endpoint must be an Azure Search HTTPS origin.'
}
$services = @(Invoke-DemoAz @('resource', 'list', '--subscription', $subscription,
    '--resource-type', 'Microsoft.Search/searchServices') | Where-Object name -eq $uri.Host.Split('.')[0])
if ($services.Count -ne 1) { throw 'Configured Search service cannot be uniquely resolved.' }
$service = Invoke-DemoAz @('resource', 'show', '--subscription', $subscription,
    '--ids', $services[0].id, '--api-version', '2025-05-01')
$stats = Invoke-DemoSearchRead $subscription $uri.AbsoluteUri 'servicestats'
$indexes = @( (Invoke-DemoSearchRead $subscription $uri.AbsoluteUri 'indexes').value )
$indexStats = @(
    foreach ($index in $indexes) {
        [pscustomobject]@{ name = $index.name; statistics = Invoke-DemoSearchRead $subscription $uri.AbsoluteUri "indexes/$($index.name)/stats" }
    }
)
$start = $StartTime.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')
$end = $EndTime.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ')
$definitions = @(Invoke-DemoAz @('monitor', 'metrics', 'list-definitions', '--subscription', $subscription, '--resource', $service.id))
$metrics = @(
    foreach ($definition in $definitions) {
        $aggregations = @($definition.supportedAggregationTypes | Where-Object { $_ -in @('Total', 'Average', 'Maximum', 'Minimum', 'Count') })
        if (-not $aggregations.Count) { continue }
        $arguments = @('monitor', 'metrics', 'list', '--subscription', $subscription, '--resource', $service.id,
            '--metric', $definition.name.value, '--start-time', $start, '--end-time', $end,
            '--interval', 'PT1H', '--aggregation') + $aggregations
        Invoke-DemoAz $arguments
    }
)
$cost = $null
if ($IncludeActualCost) {
    $body = @{
        type = 'ActualCost'; timeframe = 'Custom'; timePeriod = @{ from = $start; to = $end }
        dataset = @{ granularity = 'Daily'; aggregation = @{ totalCost = @{ name = 'PreTaxCost'; function = 'Sum' } }
            filter = @{ dimensions = @{ name = 'ResourceId'; operator = 'In'; values = @($service.id) } } }
    } | ConvertTo-Json -Depth 20 -Compress
    $cost = Invoke-DemoAz @('rest', '--method', 'post', '--subscription', $subscription, '--url',
        "https://management.azure.com/subscriptions/$subscription/providers/Microsoft.CostManagement/query?api-version=2023-03-01", '--body', $body)
}
$budget = $null
if ($null -ne $BudgetAmount) {
    $scope = ($service.id -split '/providers/')[0]
    $budgetId = "$scope/providers/Microsoft.Consumption/budgets/$BudgetName"
    if ($PSCmdlet.ShouldProcess($budgetId, "Set monthly Search-only budget alert to $BudgetAmount billing-currency units (NOT a spending cap)")) {
        $month = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-01T00:00:00Z')
        $body = @{ properties = @{
            category = 'Cost'; amount = $BudgetAmount; timeGrain = 'Monthly'
            timePeriod = @{ startDate = $month; endDate = [DateTimeOffset]::UtcNow.AddYears(1).ToString('yyyy-MM-01T00:00:00Z') }
            filter = @{ dimensions = @{ name = 'ResourceId'; operator = 'In'; values = @($service.id) } }
            notifications = @{ Actual100Percent = @{ enabled = $true; operator = 'GreaterThanOrEqualTo'
                threshold = 100; thresholdType = 'Actual'; contactEmails = @($BudgetContact) } }
        } } | ConvertTo-Json -Depth 20 -Compress
        $budget = Invoke-DemoAz @('rest', '--method', 'put', '--subscription', $subscription,
            '--url', "https://management.azure.com$budgetId`?api-version=2024-08-01", '--body', $body)
    }
}
[pscustomobject]@{
    resourceId = $service.id; startUtc = $start; endUtc = $end
    configuration = $service; serviceStatistics = $stats; indexStatistics = $indexStats
    metricDefinitions = $definitions; metrics = $metrics; actualCost = $cost; budget = $budget
    caveat = 'Metrics are not billed costs; empty metrics do not prove zero use. Billing data can lag. Budget alerts are not spending caps. Model/Foundry and retained resources bill separately.'
}
