# Setting up the Azure resources

Step-by-step deployment of the scan pipeline. The Bicep under `infra/` does most of the work; the
manual steps are the two Entra app registrations, the Foundry agents, and the desktop client's
config file, none of which can be created from an ARM template.

Every command below is **PowerShell**. Use PowerShell 7 (`pwsh`) if you have it: Windows
PowerShell 5.1 works, but it mangles arguments containing quotes when it hands them to `az.cmd`,
which matters in [step 2](#2-register-the-api-and-the-desktop-client). The JSON arguments there are
passed as files (`"@file.json"`) rather than inline strings specifically to sidestep that.

Everything uses **user-assigned managed identities**. Nothing in this deployment has a connection
string, a storage key, or a client secret: the storage account is created with
`allowSharedKeyAccess: false` and Cosmos with `disableLocalAuth: true`, so key-based access is not
merely discouraged, it is impossible.

## Why user-assigned

The security boundary between Team A and Team B is expressed entirely as role assignments on
identities. A system-assigned identity is created and destroyed with its host, so deleting and
recreating an App Service silently drops every role assignment granted to it, and the demo breaks
in a way that looks like a Foundry problem. User-assigned identities are separate resources
created before anything that consumes them, so:

- role assignments are made once and survive redeploys and recreated hosts;
- Team A's identity lives in Team A's resource group and is a resource Team B cannot touch;
- you can grant access before the compute exists, avoiding a deployment-order dependency.

Four identities are created, one per role:

| Identity | Resource group | Used by | Gets |
|---|---|---|---|
| `<prefix>-api-id` | `<prefix>-platform` | Job API | Cosmos read/write, blob SAS delegation, queue data |
| `<prefix>-worker-id` | `<prefix>-platform` | Worker | Cosmos read/write, read `scans`, write `crops`, queue data |
| `<prefix>-cardgeo-id` | `<prefix>-cardgeo` | Team A's Foundry project | read `scans` only |
| `<prefix>-cardid-id` | `<prefix>-cardid` | Team B's Foundry project | read `crops` only, invoke Team A's agent |

Neither team identity has any Cosmos role assignment, so neither can read job state.

## Prerequisites

- An Azure subscription where you can create resource groups and **role assignments** (Owner or
  User Access Administrator — Contributor is not enough, because the deployment grants RBAC).
- Azure CLI 2.60 or later with the Bicep tooling: `az bicep install`.
- Permission to create Entra app registrations and security groups, or someone who can do it.
- Quota for a vision-capable model (`gpt-4o`) in your chosen region.

```powershell
az login
az account set --subscription "<subscription-id>"
```

Set the values the rest of this guide reuses. Keep the session open, or re-run this block in a new
one — every later step refers to these variables:

```powershell
$prefix   = "enf-demo"
$location = "eastus2"
$platformRg = "$prefix-platform"
$geometryRg = "$prefix-cardgeo"
$identificationRg = "$prefix-cardid"
```

`az ... -o tsv` returns a string in PowerShell, but with a trailing newline when the command emits
more than one line. Every capture below queries a single scalar, so `$var = az ...` is safe; if you
adapt one to return several values you will get an array and should add `| Select-Object -First 1`.

## 1. Create the owner groups

The two Foundry projects are owned by different Entra groups. This is what stops Team B editing
Team A's agent, so use two real groups even in a demo tenant.

```powershell
az ad group create --display-name "Enfolderer Team A (Geometry)" `
                   --mail-nickname enfolderer-team-a
az ad group create --display-name "Enfolderer Team B (Identification)" `
                   --mail-nickname enfolderer-team-b

$teamAGroupId = az ad group show --group "Enfolderer Team A (Geometry)"       --query id -o tsv
$teamBGroupId = az ad group show --group "Enfolderer Team B (Identification)" --query id -o tsv
$teamAGroupId, $teamBGroupId
```

Keep both object ids. Add yourself to whichever group you want to demo from — and deliberately
*not* to the other, so the 403s in the walkthrough are genuine.

The backtick (`` ` ``) is PowerShell's line continuation. It must be the **last** character on the
line: a trailing space after it is a syntax error, and an easy one to introduce when copying.

## 2. Register the API and the desktop client

Two registrations: the API exposes a scope, and the desktop app is a **public client** with no
secret. There are three sub-steps, and **2b can be done in the portal or from PowerShell** — pick
one, not both. Whichever you pick, carry on at **2c**, which sets the two variables the rest of the
guide needs.

### 2a. Create the API registration

```powershell
$apiAppId = az ad app create --display-name "Enfolderer Scan API" --query appId -o tsv
az ad app update --id $apiAppId --identifier-uris "api://$apiAppId"
az ad sp create --id $apiAppId
```

Skip the first two lines if you already created the app in the portal, but **still run
`az ad sp create`** (it is idempotent, so running it twice is harmless).

That third line is easy to overlook and the failure it causes is confusing. An app *registration*
is only a definition; the **service principal** is the object that represents it inside your
tenant, and the portal creates one silently while `az ad app create` does not. Without it, granting
consent in 2d fails with `Request_BadRequest` and the misleading text "the application needs access
to service(s) (…) that your organization has not subscribed to" — where the GUID in the parentheses
is this API's own app id.

### 2b. Add the `Scan.Submit` scope — *portal or PowerShell, not both*

**In the portal:** **App registrations → Enfolderer Scan API → Expose an API → Add a scope**. If
prompted for an Application ID URI accept the default `api://<appId>`. Name the scope
`Scan.Submit`, set **Who can consent** to *Admins and users*, fill in the four display/description
boxes with anything sensible, and click **Add scope**. That is the whole of 2b — **skip the
PowerShell block below and go to 2c.**

**Or from PowerShell** — this does exactly the same thing as the portal steps above, so only run it
if you did *not* use the portal. It writes the JSON to a file rather than passing it inline, so no
quoting has to survive the trip through `az`:

```powershell
$scopeId     = [guid]::NewGuid().Guid
$apiObjectId = az ad app show --id $apiAppId --query id -o tsv

$body = @{
  api = @{
    oauth2PermissionScopes = @(
      @{
        id                      = $scopeId
        value                   = "Scan.Submit"
        type                    = "User"
        isEnabled               = $true
        adminConsentDisplayName = "Submit card scans"
        adminConsentDescription = "Allows the signed-in user to submit card scan jobs."
        userConsentDisplayName  = "Submit card scans"
        userConsentDescription  = "Allows you to submit card scan jobs."
      }
    )
  }
}
$bodyFile = Join-Path $env:TEMP "scan-scope.json"
$body | ConvertTo-Json -Depth 6 | Set-Content -Path $bodyFile -Encoding utf8

az rest --method PATCH `
  --url "https://graph.microsoft.com/v1.0/applications/$apiObjectId" `
  --headers "Content-Type=application/json" `
  --body "@$bodyFile"
```

This calls Graph directly instead of `az ad app update --set`, because `--set` takes its value as
one `key=value` token and the JSON inside it has to survive both PowerShell and `az.cmd`. `az rest
--body` documents the `@file` form, so nothing is quoted at all. Note the object id in the URL:
Graph wants the application's `id`, not its `appId`, and passing the wrong one gives a confusing
404.

### 2c. Collect the API app id and scope id

**Everyone does this**, whichever route you took through 2b. If you used the portal, or you have
opened a new shell since 2a, `$apiAppId` and `$scopeId` are not set — read them back from the
registration:

```powershell
$apiAppId = az ad app list --display-name "Enfolderer Scan API" --query "[0].appId" -o tsv
$scopeId  = az ad app show --id $apiAppId `
  --query "api.oauth2PermissionScopes[?value=='Scan.Submit'].id" -o tsv

"API app id: $apiAppId"
"Scope id:   $scopeId"
```

Both must be non-empty GUIDs before you continue. An empty `$scopeId` means the scope was not saved
under the name `Scan.Submit` — check the spelling and capitalisation in the portal, since the
filter is case-sensitive.

If you ran the PowerShell version of 2b in this same shell, both variables are already set and this
block simply confirms them.

### 2d. Create the desktop client registration

This part has no portal instructions above it; run it regardless of how you did 2b.

```powershell
$access = @(
  @{
    resourceAppId  = $apiAppId
    resourceAccess = @(@{ id = $scopeId; type = "Scope" })
  }
)
$accessFile = Join-Path $env:TEMP "scan-access.json"
$access | ConvertTo-Json -Depth 5 -AsArray | Set-Content -Path $accessFile -Encoding utf8

$clientAppId = az ad app create `
  --display-name "Enfolderer Desktop" `
  --is-fallback-public-client true `
  --public-client-redirect-uris "http://localhost" `
  --required-resource-accesses "@$accessFile" `
  --query appId -o tsv

az ad sp create --id $clientAppId

az ad app permission admin-consent --id $clientAppId   # or let users consent at first sign-in
"API app id:    $apiAppId"
"Client app id: $clientAppId"
```

Both service principals must exist before the consent call: the client's to hold the grant, and
the API's from 2a to be the target of it. Granting admin consent needs Privileged Role
Administrator or Global Administrator; if you have neither, skip that line and let each user
consent at first sign-in.

`-AsArray` needs PowerShell 7. On 5.1 a single-element array collapses to a bare object and `az`
rejects the file, so write `"[" + ($access | ConvertTo-Json -Depth 5) + "]"` instead.

Do **not** create a client secret for either registration. The desktop app rejects a config file
containing `client_secret`, and the services authenticate with managed identities.

## 3. Deploy the infrastructure

Fill in `infra/main.parameters.json`. You can do it by hand, or from the variables already in the
session:

```powershell
$params = [ordered]@{
  '$schema'      = "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#"
  contentVersion = "1.0.0.0"
  parameters     = [ordered]@{
    namePrefix         = @{ value = $prefix }
    location           = @{ value = $location }
    teamAGroupObjectId = @{ value = $teamAGroupId }
    teamBGroupObjectId = @{ value = $teamBGroupId }
    apiClientId        = @{ value = $apiAppId }
    grantIdentificationAccessToGeometry = @{ value = $true }
  }
}
$params | ConvertTo-Json -Depth 5 | Set-Content -Path infra/main.parameters.json -Encoding utf8
```

`namePrefix` is 3–12 characters and seeds every resource name, so keep it short and unique — the
storage account and the two Foundry accounts need globally unique names.

Preview, then deploy:

```powershell
az deployment sub what-if `
  --location $location `
  --template-file infra/main.bicep `
  --parameters infra/main.parameters.json

az deployment sub create `
  --name enfolderer-scan `
  --location $location `
  --template-file infra/main.bicep `
  --parameters infra/main.parameters.json
```

This creates three resource groups (`<prefix>-platform`, `<prefix>-cardgeo`, `<prefix>-cardid`),
the four managed identities, the storage account with the `scans` and `crops` containers and the
`scan-jobs` queue, the Cosmos account with the `jobs` container, two Foundry accounts each with one
project and a `gpt-4o` deployment, the API and worker App Services, and all the role assignments.

Collect the outputs:

```powershell
$outputs = az deployment sub show --name enfolderer-scan --query properties.outputs -o json |
           ConvertFrom-Json
$outputs.apiUrl.value
$outputs.geometryProjectEndpoint.value
$outputs.identificationProjectEndpoint.value
```

You need those three for the steps below. Role assignments can take a couple of minutes to
propagate; if the first scan fails with a 403, wait and retry before assuming a misconfiguration.

## 4. Create the agents

The agent definitions are in `agents/cardgeo/` and `agents/cardid/`. Create them in the Foundry
portal (or with the Foundry SDK), in this order:

1. In the **`cardgeo`** project, create `CardBoundaryAgent` from
   `agents/cardgeo/card-boundary-agent.yaml`: a `gpt-4o` deployment, temperature 0, JSON object
   response format, and the instructions verbatim. The prompt is the one the worker sends, so keep
   the two in sync if you edit it.
2. In the **`cardid`** project, create `MtgCardIdAgent` and `PokemonCardIdAgent` from their YAML
   files, each bound to its catalogue MCP server.
3. Still in `cardid`, create `OrchestratorAgent` and add a **connected agent** pointing at
   `cardgeo`'s `CardBoundaryAgent`. This connection is what step 5's revoke breaks.

`YugiohCardIdAgent` and `LorcanaCardIdAgent` are checked in but not deployed; create them the same
way when you want to show how a new game is added without touching Team A.

Note the agent ids. If they differ from the agent names, update the worker's settings:

```powershell
az webapp config appsettings set `
  --resource-group $platformRg --name "$prefix-worker" --settings `
  ScanPipeline__BoundaryAgentId="<boundary agent id>" `
  ScanPipeline__IdentificationAgentIds__mtg="<mtg agent id>" `
  ScanPipeline__IdentificationAgentIds__pokemon="<pokemon agent id>"
```

## 5. Host the MCP servers

The three MCP servers under `src/Enfolderer.Ai.Mcp.*` are stdio processes. Run them wherever your
Foundry project can reach them, and register each with the project that owns it:
`mcp-imaging` in `cardgeo`, `mcp-cardcatalog-mtg` and `mcp-cardcatalog-pokemon` in `cardid`.

Do not register a catalogue server in `cardgeo`. Being unable to is
[demo scenario 2](foundry-demo.md#2-call-team-bs-mtg-catalogue-from-team-as-project).

The Pokémon catalogue works anonymously but is rate-limited; if you have a pokemontcg.io key, set
`POKEMONTCG_API_KEY` in that server's environment. Scryfall needs no key.

## 6. Deploy the API and worker

```powershell
$stage = Join-Path $env:TEMP "enfolderer-deploy"
New-Item -ItemType Directory -Force -Path $stage | Out-Null

dotnet publish src/Enfolderer.Ai.Api    -c Release -o "$stage/api"
dotnet publish src/Enfolderer.Ai.Worker -c Release -o "$stage/worker"

Compress-Archive -Path "$stage/api/*"    -DestinationPath "$stage/api.zip"    -Force
Compress-Archive -Path "$stage/worker/*" -DestinationPath "$stage/worker.zip" -Force

az webapp deploy --resource-group $platformRg --name "$prefix-api" `
  --src-path "$stage/api.zip" --type zip
az webapp deploy --resource-group $platformRg --name "$prefix-worker" `
  --src-path "$stage/worker.zip" --type zip
```

Note the `/*` in the `Compress-Archive` paths. Without it the archive contains a top-level `api`
folder, App Service finds no `.dll` at the root, and the site starts and then 500s — a failure that
looks like a code problem rather than a packaging one.

The Bicep already set every app setting, including
`ScanPlatform__ManagedIdentityClientId` — the client id of that service's user-assigned identity.
That setting is not optional: a host can carry several user-assigned identities, and without it the
credential cannot tell which one to present. If you attach another identity later, keep the setting
pointing at the one that holds the role assignments.

Check the API is up:

```powershell
Invoke-RestMethod "https://$prefix-api.azurewebsites.net/healthz"
```

A warning in the API log that `AzureAd:TenantId` is not configured means the API is running
unauthenticated — acceptable locally, not in a deployment. Confirm the setting survived.

## 7. Point the desktop app at the deployment

Create `aiconfig.txt` beside `Enfolderer.App.exe` (the app writes a template on the first scan if
the file is missing):

```powershell
$tenantId = az account show --query tenantId -o tsv

@"
api_base_url=https://$prefix-api.azurewebsites.net
tenant_id=$tenantId
client_id=$clientAppId
scope=api://$apiAppId/Scan.Submit
#game_hint=mtg
#use_device_code=true
"@ | Set-Content -Path .\aiconfig.txt -Encoding utf8
```

The `@"` … `"@` here-string expands variables; `@'` … `'@` would not, and would leave the literal
`$prefix` in the file.

Then **Tools → Scan Card Image…**, pick a photo, and sign in when prompted. Uncomment
`use_device_code` if the machine has no usable browser.

## Verifying the boundaries

Once a scan succeeds end to end, confirm the demo assets are real:

```powershell
# The worker's identity can reach Cosmos.
az cosmosdb sql role assignment list `
  --account-name "$prefix-cosmos" --resource-group $platformRg -o table

# Neither team identity appears in that list. Confirm Team A's roles are storage-only:
$geoPrincipal = az identity show -g $geometryRg -n "$prefix-cardgeo-id" --query principalId -o tsv
az role assignment list --assignee $geoPrincipal --all -o table
```

Team A should show exactly one assignment: **Storage Blob Data Reader** on the `scans` container.
No Cosmos, no `crops`, nothing in `<prefix>-cardid`.

Then run the two "break it" scenarios in [foundry-demo.md](foundry-demo.md).

## Costs and teardown

The Basic App Service plan, the provisioned-throughput Cosmos container, and the `gpt-4o`
deployments all bill while they exist. Job documents self-expire after seven days via the container
TTL, but the resources do not. Tear down when you are done:

```powershell
az group delete --name $platformRg       --yes --no-wait
az group delete --name $geometryRg       --yes --no-wait
az group delete --name $identificationRg --yes --no-wait

az ad app delete --id $apiAppId
az ad app delete --id $clientAppId
```

Deleting the resource groups also deletes the managed identities and every role assignment scoped
to them. If you plan to redeploy, keep `<prefix>-platform` and delete only the compute — the
identities and their RBAC will still be there.

## Running without Azure

Leave `ScanPlatform:StorageAccountUrl` and `ScanPlatform:CosmosEndpoint` empty and the API and
worker fall back to an in-memory job store, a local directory image store, and a directory-backed
queue; with no Foundry endpoints configured the worker uses stub agents. That exercises the whole
upload/poll loop with no subscription and no cost, which is the fastest way to check the desktop
app before deploying anything.
