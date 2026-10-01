<#
.SYNOPSIS
    Builds the five service images and switches the container apps onto them.

.DESCRIPTION
    The first `az deployment sub create` runs before any image exists, so every container app
    starts on Microsoft's sample container, mcr.microsoft.com/k8se/quickstart. That image answers
    every request with the plain-text body

        404 page not found

    which is the usual sign that this script has never been run. Nothing in the pipeline works
    until it has: the API serves no routes, and the worker processes no jobs.

    It does three things, in order:

      1. Builds all five images with `az acr build`. The build happens in Azure — ACR Tasks
         compiles the source this script uploads — so you need neither Docker nor the .NET SDK.
      2. Redeploys infra/main.bicep with the new tag. This is not the same as
         `az containerapp update --image`: as well as the image it sets each app's ingress target
         port (80 on the placeholder, 8080 on ours) and the registry configuration that lets the
         app pull as its own managed identity. An `update` alone leaves an app that cannot pull,
         or one whose ingress points at a port nothing is listening on.
      3. Reports the image and revision state of all five apps, and calls the API's /healthz.

    The redeploy reuses the parameters of the previous deployment rather than reading
    infra/main.parameters.json, because that file is checked in with placeholder object ids. If
    you supplied the real ones on the command line, or edited the file and later reverted it,
    deploying from the file would revoke the role assignments the demo depends on. Everything is
    taken from what is already deployed; only imageTag changes. Pass -UseParametersFile if you
    want the file used instead.

    Safe to re-run. Each run defaults to a new tag, because `az containerapp update` and ARM both
    diff the template: re-pushing the same tag produces no new revision and your change does not
    go live.

.PARAMETER DeploymentName
    Name of the subscription-scope deployment to read parameters from and redeploy. Must match the
    one used in step 3 of docs/azure-setup.md.

.PARAMETER Tag
    Image tag to build and deploy. Defaults to a UTC timestamp, e.g. v20260930-1412, which is
    always new.

.PARAMETER UseParametersFile
    Deploy from infra/main.parameters.json instead of the previous deployment's parameters. Only
    correct if that file holds your real values.

.PARAMETER SkipBuild
    Skip the image build and only redeploy. Use with -Tag to point the apps at a tag that already
    exists in the registry, for example to roll back.

.PARAMETER BoundaryAgentId
    Agent id of Team A's boundary agent, e.g. asst_…. Omit to keep whatever the last deployment
    used.

.PARAMETER MtgAgentId
    Agent id of Team B's Magic: The Gathering identification agent.

.PARAMETER PokemonAgentId
    Agent id of Team B's Pokemon identification agent.

.PARAMETER GrantGeometryAccess
    Whether the orchestrator may invoke Team A's boundary agent. Pass $false to run demo scenario
    1 and $true to restore it. Omit to keep whatever the last deployment used. Use this rather than
    a bare `az deployment sub create`, which would deploy imageTag's empty default and send every
    app back to the placeholder image.

.EXAMPLE
    ./scripts/deploy-images.ps1

    Build everything under a fresh tag and switch the apps onto it.

.EXAMPLE
    ./scripts/deploy-images.ps1 -SkipBuild -Tag v1

    Roll the apps back to images already in the registry.

.EXAMPLE
    ./scripts/deploy-images.ps1 -SkipBuild -BoundaryAgentId asst_abc -MtgAgentId asst_def

    Change only the agent ids, leaving the running images alone. Use this rather than a bare
    `az deployment sub create`: that would deploy imageTag's empty default and send every app back
    to the placeholder.

.EXAMPLE
    ./scripts/deploy-images.ps1 -SkipBuild -GrantGeometryAccess $false

    Revoke the orchestrator's access to Team A's project, which is demo scenario 1.

.NOTES
    Run from the repository root: the build context is '.', and every service project has
    ProjectReferences reaching up into src/.

    Requires an az login with rights to deploy the template — the same ones step 3 needed, since
    this re-runs it.
#>
[CmdletBinding()]
param(
    [string] $DeploymentName = 'enfolderer-scan',
    [string] $Tag,
    [switch] $UseParametersFile,
    [switch] $SkipBuild,
    [string] $BoundaryAgentId,
    [string] $MtgAgentId,
    [string] $PokemonAgentId,
    [bool] $GrantGeometryAccess
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step { param([string] $T) Write-Host ''; Write-Host $T -ForegroundColor Cyan }
function Write-Note { param([string] $T) Write-Host "  $T" -ForegroundColor DarkGray }
function Write-Good { param([string] $T) Write-Host "  $T" -ForegroundColor Green }
function Write-Bad  { param([string] $T) Write-Host "  $T" -ForegroundColor Red }

# RoleAssignmentExists means someone granted one of the template's roles by hand. Azure identifies
# a role assignment by principal + role + scope, but names it by a GUID the creator chooses: the
# template derives that name with guid(), while a portal or `az role assignment create` grant gets a
# random one. ARM therefore asks for an assignment that already exists under a different name, and
# Azure refuses rather than adopting it. Deleting the hand-made one lets the template own it again.
function Show-RoleAssignmentCollisionHelp {
    Write-Host ''
    Write-Host 'A role this template grants was already granted by hand.' -ForegroundColor Red
    Write-Host ''
    Write-Host 'Azure matches a role assignment on principal + role + scope, but names it with a'
    Write-Host 'GUID chosen by whoever created it. The template derives its name from the three;'
    Write-Host 'a grant made in the portal or with `az role assignment create` gets a random one.'
    Write-Host 'Azure will not adopt the existing assignment under a new name, so the deployment'
    Write-Host 'fails until the hand-made one is removed. Nothing is lost by removing it: the'
    Write-Host 'template grants the same role at the same scope.'
    Write-Host ''
    Write-Host 'The error above ends with the id of the assignment in the way, e.g.' -ForegroundColor DarkYellow
    Write-Host '  "The ID of the existing role assignment is 04d6663e9d994c1db26146747a9a64c2."'
    Write-Host ''
    Write-Host 'That is the assignment name, the last segment of its resource id. Find it, check'
    Write-Host 'that the role and scope are the ones you expect, then delete it:' -ForegroundColor DarkYellow
    Write-Host '  $name = "<the id from the message>"'
    Write-Host '  $all = az role assignment list --all -o json | ConvertFrom-Json'
    Write-Host '  $doomed = @($all | Where-Object { ($_.id -split "/")[-1] -eq $name })'
    Write-Host '  $doomed | Select-Object roleDefinitionName, principalId, scope | Format-List'
    Write-Host '  az role assignment delete --ids $doomed.id --yes   # --ids takes several'
    Write-Host ''
    Write-Host 'If that matches nothing, the assignment is one az will not list by id — delete it'
    Write-Host 'by principal, role and scope instead. List what the worker holds:' -ForegroundColor DarkYellow
    Write-Host '  $worker = az identity show -g <prefix>-platform -n <prefix>-worker-id --query principalId -o tsv'
    Write-Host '  az role assignment list --all --assignee $worker -o json | ConvertFrom-Json |'
    Write-Host '    Select-Object roleDefinitionName, scope | Format-List'
    Write-Host 'then remove the one this template also grants, e.g.:' -ForegroundColor DarkYellow
    Write-Host '  az role assignment delete --assignee $worker --role "Storage Blob Data Reader" --scope <scope>'
    Write-Host ''
}

# The single Dockerfile publishes whichever project PROJECT names, so one entry per service is the
# whole build matrix. The key is the image repository, the value the project directory.
$images = [ordered] @{
    'api'                     = 'Enfolderer.Ai.Api'
    'worker'                  = 'Enfolderer.Ai.Worker'
    'mcp-imaging'             = 'Enfolderer.Ai.Mcp.Imaging'
    'mcp-cardcatalog-mtg'     = 'Enfolderer.Ai.Mcp.CardCatalog.Mtg'
    'mcp-cardcatalog-pokemon' = 'Enfolderer.Ai.Mcp.CardCatalog.Pokemon'
}

# A build needs a tag no image already uses, because both ARM and `az containerapp update` decide
# whether to create a revision by diffing the template: re-pushing a tag it is already running
# changes nothing it can see. -SkipBuild is the opposite case — it deploys an existing image — so
# there the tag is resolved from the deployment further down instead.
if (-not $Tag -and -not $SkipBuild) {
    $Tag = 'v{0}' -f (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmm')
}

# Resolve the repository root from this script's location, so the build context is right however
# the script was invoked.
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $repoRoot 'infra/main.bicep'))) {
    throw "infra/main.bicep not found under $repoRoot. Run this script from within the repository."
}
Push-Location $repoRoot
try {
    Write-Step "0. Read the existing deployment"

    # A subscription-scope deployment keeps the parameters it was given, so the whole session can
    # be rebuilt from it. It also pins its own location, and a redeploy must pass the same one.
    $deployment = az deployment sub show --name $DeploymentName -o json 2>$null | ConvertFrom-Json
    if (-not $deployment) {
        throw @"
No subscription deployment named '$DeploymentName' was found.

That deployment is step 3 of docs/azure-setup.md and has to succeed before there is anything to put
images into. If yours has a different name, pass it:

    ./scripts/deploy-images.ps1 -DeploymentName <name>

List the ones you have with:

    az deployment sub list --query "[].{name:name, state:properties.provisioningState}" -o table
"@
    }

    # A failed deployment replaces the successful one of the same name, and ARM records a failure
    # with "parameters": null and no outputs at all. The deployment is therefore not a durable
    # source of truth: one bad run would leave this script with nothing to replay and no way back.
    # So keep a copy next to the repository, refreshed whenever ARM's own copy is readable.
    $cache = Join-Path $repoRoot ".deploy-images/$DeploymentName.json"
    $location = $deployment.location
    $recorded = $deployment.properties.PSObject.Properties['parameters']
    $parameters = if ($recorded) { $recorded.Value } else { $null }

    $outputs = $deployment.properties.PSObject.Properties['outputs']
    $registry = $null
    if ($outputs -and $outputs.Value -and $outputs.Value.PSObject.Properties['registryName']) {
        $registry = $outputs.Value.registryName.value
    }

    if ($parameters -and $parameters.PSObject.Properties['namePrefix'] -and $registry) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $cache) | Out-Null
        [ordered] @{
            deployment   = $DeploymentName
            savedUtc     = (Get-Date).ToUniversalTime().ToString('o')
            location     = $location
            registryName = $registry
            parameters   = $parameters
        } | ConvertTo-Json -Depth 20 | Set-Content -Path $cache -Encoding utf8
    }
    elseif (Test-Path $cache) {
        $saved = Get-Content -Raw -Path $cache | ConvertFrom-Json
        $location = $saved.location
        $parameters = $saved.parameters
        $registry = $saved.registryName
        Write-Bad "deployment '$DeploymentName' is in a failed state and records no parameters"
        Write-Note "using the copy this script saved at $($saved.savedUtc) in .deploy-images/"
    }
    else {
        throw @"
Deployment '$DeploymentName' records no parameters, so there is nothing to replay.

That is what ARM leaves behind when a deployment fails: the failed run replaces the successful one
under the same name, and a failed run is stored with no parameters and no outputs. This script
normally keeps its own copy in .deploy-images/, but there is none yet — it only started saving one
after your last successful run.

Recover by deploying once from the parameters file. Fill in infra/main.parameters.json with your
real values:

    namePrefix           the prefix you deployed with, e.g. enf-demo
    location             the region, e.g. swedencentral
    apiClientId          az containerapp show -g <prefix>-platform -n <prefix>-api ``
                           --query "properties.template.containers[0].env[?name=='AzureAd__ClientId'].value" -o tsv
    teamAGroupObjectId   az ad group show --group "Enfolderer Team A (Geometry)" --query id -o tsv
    teamBGroupObjectId   az ad group show --group "Enfolderer Team B (Identification)" --query id -o tsv

then run this script once with -UseParametersFile, naming the tag already in the registry:

    ./scripts/deploy-images.ps1 -SkipBuild -Tag <tag> -UseParametersFile

That run succeeds, ARM records the parameters again, and every later run can go back to replaying
them. Do not commit the filled-in parameters file.
"@
    }

    $prefix = $parameters.namePrefix.value
    Write-Note "deployment '$DeploymentName' in $location, prefix '$prefix'"
    if (-not $registry) {
        $registry = az acr list -g "$prefix-platform" --query "[0].name" -o tsv
        if ($LASTEXITCODE -ne 0 -or -not $registry) {
            throw "No container registry found in $prefix-platform. Re-run step 3 of docs/azure-setup.md."
        }
        $registry = $registry.Trim()
        Write-Note "registry $registry (found in $prefix-platform; the deployment records no outputs)"
    }
    else {
        Write-Note "registry $registry"
    }

    # The whole reason this script replays parameters: imageTag defaults to empty, and an empty
    # imageTag means "no images exist yet, run the placeholder". So any deployment that forgets to
    # pass it silently sends all five apps back to mcr.microsoft.com/k8se/quickstart, whose only
    # visible symptom is a 404 on every route. Recover the tag the deployment is really using.
    $deployedTag = ''
    if ($parameters.PSObject.Properties['imageTag']) {
        $deployedTag = [string] $parameters.imageTag.value
    }

    if (-not $Tag) {
        if (-not $deployedTag) {
            throw @"
-SkipBuild was given, but deployment '$DeploymentName' records no image tag, so there is no
existing image to point the apps at. Every app is on the placeholder right now.

Build and deploy images first:

    ./scripts/deploy-images.ps1

or name a tag that already exists in ${registry}:

    ./scripts/deploy-images.ps1 -SkipBuild -Tag <tag>

List what the registry holds with:

    az acr repository show-tags --name $registry --repository enfolderer/api -o table
"@
        }
        $Tag = $deployedTag
        Write-Note "tag      $Tag (from the deployment; -SkipBuild, so nothing is rebuilt)"
    }
    else {
        Write-Note "tag      $Tag"
    }

    if ($deployedTag -and $deployedTag -ne $Tag -and $SkipBuild) {
        Write-Note "the apps are currently on '$deployedTag' and will be moved to '$Tag'"
    }

    if (-not $SkipBuild) {
        Write-Step "1. Build the images"
        Write-Note "Five builds in Azure, a few minutes each. Nothing is built on this machine."

        $n = 0
        foreach ($image in $images.Keys) {
            $n++
            Write-Host ""
            Write-Host "  [$n/$($images.Count)] $image" -ForegroundColor White

            # The colon needs escaping: PowerShell would otherwise read "$image:" as a scoped
            # variable reference and expand the whole string to nothing.
            az acr build --registry $registry --image "enfolderer/$image`:$Tag" `
                --build-arg "PROJECT=$($images[$image])" --file src/Dockerfile .
            if ($LASTEXITCODE -ne 0) {
                throw "Build failed for $image. Nothing has been deployed; fix it and re-run."
            }
        }
        Write-Good "all $($images.Count) images built"
    }
    else {
        Write-Step "1. Build the images — skipped"
        Write-Note "Using tag '$Tag', which must already exist in $registry."
    }

    Write-Step "2. Point the apps at the images"

    # Agent ids are deployment parameters, so they are set here rather than with
    # `az containerapp update --set-env-vars`, which the next deployment would overwrite.
    $overrides = [ordered] @{}
    if ($BoundaryAgentId) { $overrides['boundaryAgentId'] = $BoundaryAgentId }
    if ($MtgAgentId)      { $overrides['mtgAgentId']      = $MtgAgentId }
    if ($PokemonAgentId)  { $overrides['pokemonAgentId']  = $PokemonAgentId }
    if ($PSBoundParameters.ContainsKey('GrantGeometryAccess')) {
        $overrides['grantIdentificationAccessToGeometry'] = $GrantGeometryAccess
    }
    foreach ($o in $overrides.GetEnumerator()) {
        Write-Note "$($o.Key) = $($o.Value)"
    }

    $paramFile = $null
    $deployArgs = @(
        'deployment', 'sub', 'create',
        '--name', $DeploymentName,
        '--location', $location,
        '--template-file', 'infra/main.bicep'
    )

    if ($UseParametersFile) {
        Write-Note "parameters from infra/main.parameters.json, as requested"
        $deployArgs += @('--parameters', 'infra/main.parameters.json', '--parameters', "imageTag=$Tag")
        foreach ($o in $overrides.GetEnumerator()) {
            # A [bool] interpolates as True/False, which the CLI does not accept for a Bicep bool.
            $value = if ($o.Value -is [bool]) { $o.Value.ToString().ToLowerInvariant() } else { $o.Value }
            $deployArgs += @('--parameters', "$($o.Key)=$value")
        }
    }
    else {
        # Replay the deployed parameters with imageTag overridden. ARM records each one as
        # { "type": ..., "value": ... }; a parameters file wants only the value, so rebuild them.
        $replay = @{}
        foreach ($p in $parameters.PSObject.Properties) {
            $replay[$p.Name] = @{ value = $p.Value.value }
        }
        $replay['imageTag'] = @{ value = $Tag }
        foreach ($o in $overrides.GetEnumerator()) {
            $replay[$o.Key] = @{ value = $o.Value }
        }

        $paramFile = Join-Path ([System.IO.Path]::GetTempPath()) "enfolderer-params-$([guid]::NewGuid()).json"
        @{
            '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
            contentVersion = '1.0.0.0'
            parameters     = $replay
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $paramFile -Encoding utf8

        Write-Note "parameters replayed from the deployed values, with imageTag=$Tag"
        $deployArgs += @('--parameters', "@$paramFile")
    }

    # az writes the failure JSON to stderr, and redirecting it into the success stream is not safe
    # under $ErrorActionPreference = 'Stop' — Windows PowerShell turns native stderr into
    # ErrorRecords and would throw before the text could be read. A temporary file keeps it out of
    # the pipeline, so the error can be both shown and inspected.
    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        az @deployArgs -o none 2>$errFile
        $deployExit = $LASTEXITCODE

        $deployError = ''
        if (Test-Path $errFile) { $deployError = [string] (Get-Content $errFile -Raw) }
        if ($deployError) { Write-Host $deployError }

        if ($deployExit -ne 0) {
            # -like, not -match: this is a literal substring test, not a pattern.
            if ($deployError -like '*RoleAssignmentExists*') {
                Show-RoleAssignmentCollisionHelp
            }
            throw "Deployment failed. See the error above."
        }
    }
    finally {
        Remove-Item $errFile -Force -ErrorAction SilentlyContinue
        if ($paramFile -and (Test-Path $paramFile)) {
            Remove-Item $paramFile -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Good "deployment succeeded"

    Write-Step "3. Check what is running"

    $apps = @(
        @{ name = "$prefix-api";                     rg = "$prefix-platform" }
        @{ name = "$prefix-worker";                  rg = "$prefix-platform" }
        @{ name = "$prefix-mcp-imaging";             rg = "$prefix-cardgeo" }
        @{ name = "$prefix-mcp-cardcatalog-mtg";     rg = "$prefix-cardid" }
        @{ name = "$prefix-mcp-cardcatalog-pokemon"; rg = "$prefix-cardid" }
    )

    # One read per app, returning both the image and the ingress hostname. Two separate `az
    # containerapp show` calls would be twice the chances of a transient CLI failure, and when one
    # of the pair failed the report contradicted itself: an app listed as missing in this section
    # and answering /healthz in the next.
    #
    # az exits non-zero and writes to stderr for a genuinely absent app, but also for a throttled
    # or dropped request, and the two are indistinguishable from the exit code. Retrying tells them
    # apart: an app that does not exist fails every time.
    $stillPlaceholder = 0
    $missing = 0
    $unhealthy = 0
    foreach ($app in $apps) {
        $info = $null
        foreach ($attempt in 1..3) {
            $json = az containerapp show -g $app.rg -n $app.name --query `
                "{image:properties.template.containers[0].image, fqdn:properties.configuration.ingress.fqdn}" `
                -o json 2>$null
            if ($LASTEXITCODE -eq 0 -and $json) {
                $info = $json | ConvertFrom-Json
                break
            }
            if ($attempt -lt 3) { Start-Sleep -Seconds 3 }
        }

        if (-not $info -or -not $info.image) {
            Write-Bad "$($app.name): could not be read from $($app.rg) after 3 attempts"
            $missing++
            continue
        }

        if ($info.image -like '*k8se/quickstart*') {
            Write-Bad "$($app.name): still on the placeholder ($($info.image))"
            $stillPlaceholder++
            continue
        }

        # The tag is the part worth seeing; the registry host is the same for all five.
        $shortImage = ($info.image -split '/')[-1]

        # Every app with ingress serves an anonymous /healthz — the API from its own Program.cs,
        # the MCP servers from McpServerHost — so this checks image, ingress port and revision
        # health together. The worker has none: it takes its work from the queue.
        if (-not $info.fqdn) {
            if ($app.name -eq "$prefix-worker") {
                Write-Good "$($app.name): $shortImage (no ingress, as designed)"
            }
            else {
                Write-Bad "$($app.name): $shortImage but no ingress"
                $unhealthy++
            }
            continue
        }

        try {
            $health = Invoke-RestMethod -Uri "https://$($info.fqdn)/healthz" -TimeoutSec 30
            Write-Good "$($app.name): $shortImage — healthz $($health.status)"
        }
        catch {
            Write-Bad "$($app.name): $shortImage — healthz failed: $($_.Exception.Message)"
            $unhealthy++
        }
    }

    if ($unhealthy -gt 0) {
        Write-Host ''
        Write-Note "A revision can take a minute to come up, so wait and re-run before digging in."
        Write-Note "If it persists, the revision did not start. Diagnose it with:"
        Write-Note "  ./scripts/diagnose-containerapps.ps1 -Prefix $prefix"
    }

    Write-Host ''
    if ($stillPlaceholder -gt 0) {
        Write-Bad "$stillPlaceholder app(s) are still on the placeholder image."
        Write-Note "Run ./scripts/diagnose-containerapps.ps1 -Prefix $prefix to find out why."
    }
    elseif ($missing -gt 0) {
        Write-Bad "$missing app(s) could not be read. The deployment may be incomplete."
        Write-Note "Run ./scripts/diagnose-containerapps.ps1 -Prefix $prefix to find out why."
    }
    elseif ($unhealthy -gt 0) {
        Write-Bad "All apps are on tag '$Tag', but $unhealthy did not answer /healthz."
    }
    else {
        Write-Good "All five apps are running tag '$Tag' and answering /healthz."
        Write-Note "Deploy a later code change with: ./scripts/deploy-images.ps1"
    }
}
finally {
    Pop-Location
}
