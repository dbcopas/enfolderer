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

.EXAMPLE
    ./scripts/deploy-images.ps1

    Build everything under a fresh tag and switch the apps onto it.

.EXAMPLE
    ./scripts/deploy-images.ps1 -SkipBuild -Tag v1

    Roll the apps back to images already in the registry.

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
    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step { param([string] $T) Write-Host ''; Write-Host $T -ForegroundColor Cyan }
function Write-Note { param([string] $T) Write-Host "  $T" -ForegroundColor DarkGray }
function Write-Good { param([string] $T) Write-Host "  $T" -ForegroundColor Green }
function Write-Bad  { param([string] $T) Write-Host "  $T" -ForegroundColor Red }

# The single Dockerfile publishes whichever project PROJECT names, so one entry per service is the
# whole build matrix. The key is the image repository, the value the project directory.
$images = [ordered] @{
    'api'                     = 'Enfolderer.Ai.Api'
    'worker'                  = 'Enfolderer.Ai.Worker'
    'mcp-imaging'             = 'Enfolderer.Ai.Mcp.Imaging'
    'mcp-cardcatalog-mtg'     = 'Enfolderer.Ai.Mcp.CardCatalog.Mtg'
    'mcp-cardcatalog-pokemon' = 'Enfolderer.Ai.Mcp.CardCatalog.Pokemon'
}

if (-not $Tag) { $Tag = 'v{0}' -f (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmm') }

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

    $location = $deployment.location
    $parameters = $deployment.properties.parameters
    if (-not $parameters.PSObject.Properties['namePrefix']) {
        throw "Deployment '$DeploymentName' records no namePrefix parameter. Is it this template?"
    }
    $prefix = $parameters.namePrefix.value
    Write-Note "deployment '$DeploymentName' in $location, prefix '$prefix'"

    # az omits null properties, so outputs is absent rather than empty on a failed deployment.
    $outputs = $deployment.properties.PSObject.Properties['outputs']
    if (-not $outputs -or -not $outputs.Value.PSObject.Properties['registryName']) {
        throw @"
Deployment '$DeploymentName' has no registryName output, so it did not complete. Re-run step 3 of
docs/azure-setup.md before building images, and check why it failed with:

    az deployment sub show --name $DeploymentName --query properties.provisioningState -o tsv
"@
    }
    $registry = $outputs.Value.registryName.value
    Write-Note "registry $registry"
    Write-Note "tag      $Tag"

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
    }
    else {
        # Replay the deployed parameters with imageTag overridden. ARM records each one as
        # { "type": ..., "value": ... }; a parameters file wants only the value, so rebuild them.
        $replay = @{}
        foreach ($p in $parameters.PSObject.Properties) {
            $replay[$p.Name] = @{ value = $p.Value.value }
        }
        $replay['imageTag'] = @{ value = $Tag }

        $paramFile = Join-Path ([System.IO.Path]::GetTempPath()) "enfolderer-params-$([guid]::NewGuid()).json"
        @{
            '$schema'      = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
            contentVersion = '1.0.0.0'
            parameters     = $replay
        } | ConvertTo-Json -Depth 10 | Set-Content -Path $paramFile -Encoding utf8

        Write-Note "parameters replayed from the deployed values, with imageTag=$Tag"
        $deployArgs += @('--parameters', "@$paramFile")
    }

    try {
        az @deployArgs -o none
        if ($LASTEXITCODE -ne 0) { throw "Deployment failed. See the error above." }
    }
    finally {
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

    $stillPlaceholder = 0
    foreach ($app in $apps) {
        $running = az containerapp show -g $app.rg -n $app.name `
            --query "properties.template.containers[0].image" -o tsv 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $running) {
            Write-Bad "$($app.name): not found in $($app.rg)"
            continue
        }
        if ($running -like '*k8se/quickstart*') {
            Write-Bad "$($app.name): still on the placeholder ($running)"
            $stillPlaceholder++
        }
        else {
            Write-Good "$($app.name): $running"
        }
    }

    # The API's /healthz is anonymous, so this needs no token and is a clean end-to-end check of
    # image, ingress port and revision health at once.
    $apiFqdn = az containerapp show -g "$prefix-platform" -n "$prefix-api" `
        --query "properties.configuration.ingress.fqdn" -o tsv 2>$null
    if ($apiFqdn) {
        Write-Step "4. Call the API"
        Write-Note "https://$apiFqdn/healthz"
        try {
            $health = Invoke-RestMethod -Uri "https://$apiFqdn/healthz" -TimeoutSec 30
            Write-Good "healthz: $($health | ConvertTo-Json -Compress)"
        }
        catch {
            Write-Bad "healthz did not answer: $($_.Exception.Message)"
            Write-Note "A revision can take a minute to come up. Wait and retry:"
            Write-Note "  Invoke-RestMethod https://$apiFqdn/healthz"
            Write-Note "If it keeps failing, the revision did not start. Diagnose it with:"
            Write-Note "  ./scripts/diagnose-containerapps.ps1 -Prefix $prefix"
        }
    }

    Write-Host ''
    if ($stillPlaceholder -gt 0) {
        Write-Bad "$stillPlaceholder app(s) are still on the placeholder image."
        Write-Note "Run ./scripts/diagnose-containerapps.ps1 -Prefix $prefix to find out why."
    }
    else {
        Write-Good "All five apps are running tag '$Tag'."
        Write-Note "Deploy a later code change with: ./scripts/deploy-images.ps1"
    }
}
finally {
    Pop-Location
}
