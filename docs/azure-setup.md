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

## One public entry point

The only thing the outside world can reach is the **API**. The desktop client calls it over HTTPS
with an Entra token and never talks to any other Azure resource — so it runs from anywhere, with no
VPN, no private resolver and no jump host.

Everything behind the API is private:

```text
desktop client ──HTTPS──▶  <prefix>-api  ─┐
                                          │  every Container Apps
                           <prefix>-worker┤  environment sits on a
                                          │  subnet of the demo VNet
                  cardgeo mcp-imaging  ───┘
                                          │
                                          ▼
                             private endpoints (blob, queue)
                                          │
                                          ▼
                                   storage account
                             publicNetworkAccess: Disabled
```

Two consequences are worth knowing before you read the rest of this guide:

- **The client uploads the image to the API, not to a blob.** Earlier versions handed out a
  write-only blob SAS. A SAS is no use against an account with no public endpoint, so `POST /jobs`
  now returns a relative URL, `jobs/{jobId}/content`, that the client PUTs to with its bearer token.
  The API holds the write permission on the `scans` container and relays the bytes.
- **Storage is private unconditionally.** There is no parameter to turn it back on. In a
  policy-governed subscription `publicNetworkAccess: Enabled` is reverted anyway, and the demo is
  more honest without it.
- **The agents are sent image bytes, not image URLs.** Foundry runs the agents on Microsoft's own
  service, outside your VNet, so it cannot fetch a private blob however the URL is signed. The
  worker uploads the photo to Team A's project and each crop to Team B's project through the
  Foundry Files API, references it as `image_file`, and deletes it when the run ends. A run that
  dies before that leaves the upload behind; `scripts/cleanup.ps1 -FoundryFiles` sweeps them.

The VNet, its subnets, the private endpoints and the private DNS zones are all created by
`infra/modules/network.bicep` as part of the main deployment; there is no separate step. Each team
gets its own `/27`, because a Container Apps environment takes the whole subnet: the delegation to
`Microsoft.App/environments` is exclusive and the platform reserves addresses from it for the
revisions it runs.

## What runs where

Everything is a **container app**; there is no App Service anywhere in the deployment. Five images
are built from one `src/Dockerfile`, pushed to one Azure Container Registry, and run across three
Container Apps environments:

| Environment | Resource group | Apps | Ingress |
|---|---|---|---|
| `<prefix>-platform-env` | `<prefix>-platform` | `<prefix>-api`, `<prefix>-worker` | API public; worker **none** |
| `<prefix>-cardgeo-env` | `<prefix>-cardgeo` | `<prefix>-mcp-imaging` | public |
| `<prefix>-cardid-env` | `<prefix>-cardid` | `<prefix>-mcp-cardcatalog-mtg`, `-pokemon` | public |

One environment per team rather than one shared environment, because the environment is the unit a
team owns and operates — revisions, scale, secrets and console logs all belong to it. A shared
environment would put both teams' containers in one resource under one set of permissions, and the
resource-group boundary the rest of the demo rests on would stop being a boundary. Each
environment gets its own Log Analytics workspace for the same reason.

The MCP servers need public ingress because Foundry calls tool servers from its own service, not
from your VNet. That is precisely why they authorise callers by object id themselves: reachable by
anyone is not the same as callable by anyone.

The worker has **no ingress at all** — not public, and not internal either. It takes its work from
the queue, so nothing needs to reach it.

## Why user-assigned

The security boundary between Team A and Team B is expressed entirely as role assignments on
identities. A system-assigned identity is created and destroyed with its host, so deleting and
recreating a container app silently drops every role assignment granted to it, and the demo breaks
in a way that looks like a Foundry problem. User-assigned identities are separate resources
created before anything that consumes them, so:

- role assignments are made once and survive redeploys and recreated hosts;
- Team A's identity lives in Team A's resource group and is a resource Team B cannot touch;
- you can grant access before the compute exists, avoiding a deployment-order dependency.

Four identities are created, one per role:

| Identity | Resource group | Used by | Gets |
|---|---|---|---|
| `<prefix>-api-id` | `<prefix>-platform` | Job API | Cosmos read/write, write `scans`, queue data |
| `<prefix>-worker-id` | `<prefix>-platform` | Worker | Cosmos read/write, read `scans`, write `crops`, queue data |
| `<prefix>-cardgeo-id` | `<prefix>-cardgeo` | Team A's Foundry project | read `scans` only |
| `<prefix>-cardid-id` | `<prefix>-cardid` | Team B's Foundry project | invoke Team A's agent; no storage role at all |

Neither team identity has any Cosmos role assignment, so neither can read job state.

## Prerequisites

- An Azure subscription where you can create resource groups and **role assignments** (Owner or
  User Access Administrator — Contributor is not enough, because the deployment grants RBAC).
- Azure CLI 2.60 or later with the Bicep tooling: `az bicep install`.
- The `containerapp` CLI extension: `az extension add --name containerapp --upgrade`.
- **The subscription registered for `Microsoft.Network/AllowBringYourOwnPublicIpAddress`.** Each
  Container Apps environment here sits on your own VNet, and such an environment creates a public
  IP and a load balancer *in your subscription*, in a managed resource group prefixed `ME_`. That
  counts as bringing your own public IP, and it is gated:

  ```powershell
  az feature show --namespace Microsoft.Network --name AllowBringYourOwnPublicIpAddress -o table
  ```

  If that does not say `Registered`, do it now rather than at deployment time — see
  [step 3](#3-deploy-the-infrastructure), where the consequences of skipping it are spelled out.
- **No Docker, and no .NET SDK.** Images are built by ACR Tasks in Azure, from source uploaded by
  `az acr build`, so your machine needs neither. If you want to build locally as well you will
  need both, but nothing in this guide does.
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

### Resuming in a new shell

Variables die with the shell, but nothing you create is lost — every id below is stored in Entra or
Azure and can be read back. If you close the terminal, reboot, or come back the next day, re-run
the block above and then this one to rebuild the whole session. Each line is a lookup, so it is
safe to run at any point and as often as you like:

```powershell
az login   # only if `az account show` fails

$teamAGroupId = az ad group show --group "Enfolderer Team A (Geometry)"       --query id -o tsv
$teamBGroupId = az ad group show --group "Enfolderer Team B (Identification)" --query id -o tsv

$apiAppId    = az ad app list --display-name "Enfolderer Scan API" --query "[0].appId" -o tsv
$clientAppId = az ad app list --display-name "Enfolderer Desktop"  --query "[0].appId" -o tsv
$scopeId     = az ad app show --id $apiAppId `
  --query "api.oauth2PermissionScopes[?value=='Scan.Submit'].id" -o tsv

$tenantId = az account show --query tenantId -o tsv

[pscustomobject]@{
  teamA = $teamAGroupId; teamB = $teamBGroupId
  api   = $apiAppId;     client = $clientAppId
  scope = $scopeId;      tenant = $tenantId
} | Format-List
```

Anything still blank simply has not been created yet — go to the step that creates it. A blank
`$clientAppId` before step 2d is expected, for instance. A value you *did* create coming back blank
almost always means a display-name mismatch: the lookups match on the exact strings above, so if
you named something differently, adjust the `--display-name` to suit.

Once step 3 has deployed, add the deployment outputs. These are the project endpoints and MCP URLs
that steps 4 onwards pass to `provision.ps1`, and they are read straight back out of the deployment
record, so there is nothing to write down:

```powershell
$outputs = az deployment sub show --name enfolderer-scan --query properties.outputs -o json |
           ConvertFrom-Json

$geo    = $outputs.geometryProjectEndpoint.value
$id     = $outputs.identificationProjectEndpoint.value
$apiUrl = $outputs.apiUrl.value

$mcp = @{}
foreach ($o in 'geometryMcpServerUrls','identificationMcpServerUrls') {
  (az deployment sub show --name enfolderer-scan --query "properties.outputs.$o.value" -o json |
     ConvertFrom-Json) | ForEach-Object { $mcp[$_.name] = $_.url }
}

[pscustomobject]@{ geo = $geo; id = $id; api = $apiUrl } | Format-List
$mcp
```

`$geo` and `$id` should end in `/api/projects/cardgeo` and `/api/projects/cardid`, and each MCP URL
in `/mcp`.

An unset PowerShell variable is an empty string rather than an error, so forgetting this block does
not fail here — it fails later, at the point of use, as
`Cannot bind argument to parameter 'ProjectEndpoint' because it is an empty string`. If you see
that from `provision.ps1`, you have skipped this block rather than found a bug in the script.

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

Keep both object ids. Add yourself to **both** groups for now — step 4 creates agents in both
projects, and each project only admits its own owning group:

```powershell
$me = az ad signed-in-user show --query id -o tsv
az ad group member add --group "Enfolderer Team A (Geometry)"       --member-id $me
az ad group member add --group "Enfolderer Team B (Identification)" --member-id $me
```

Being a subscription Owner does not help here. Foundry's agent APIs are *data* actions, so they
come only from a Foundry role assignment; without one you get

```text
The principal `you@example.com` lacks the required data action
`Microsoft.CognitiveServices/accounts/AIServices/agents/read`
```

Group membership is carried in the token, so after joining a group run `az logout` and `az login`
again, or the cached token still reflects the old membership.

Once everything is provisioned, drop yourself from one group before demoing, so the 403s in the
walkthrough are genuine:

```powershell
az ad group member remove --group "Enfolderer Team B (Identification)" --member-id $me
```

The backtick (`` ` ``) is PowerShell's line continuation. It must be the **last** character on the
line: a trailing space after it is a syntax error, and an easy one to introduce when copying.

## 2. Register the API and the desktop client

Two registrations: the API exposes a scope, and the desktop app is a **public client** with no
secret. **2b can be done in the portal or from PowerShell** — pick one, not both. Whichever you
pick, carry on at **2c**, which sets the two variables the rest of the guide needs. The last
sub-step, 2e, is optional and most people skip it.

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
consent in 2e fails with `Request_BadRequest` and the misleading text "the application needs access
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
registration (this is the same lookup as
[Resuming in a new shell](#resuming-in-a-new-shell), narrowed to the two ids this step needs):

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

"API app id:    $apiAppId"
"Client app id: $clientAppId"
```

`-AsArray` needs PowerShell 7. On 5.1 a single-element array collapses to a bare object and `az`
rejects the file, so write `"[" + ($access | ConvertTo-Json -Depth 5) + "]"` instead.

Do **not** create a client secret for either registration. The desktop app rejects a config file
containing `client_secret`, and the services authenticate with managed identities.

### 2e. Consent to the scope — *optional, and usually skippable*

`Scan.Submit` is a **user-consentable** scope, so each person can consent for themselves the first
time they sign in: the browser shows a one-off "Enfolderer Desktop wants to sign you in and read
your profile / Submit card scans" prompt, they click **Accept**, and that is the end of it. For a
demo on your own account, **do nothing here and go to step 3.**

Granting consent tenant-wide up front, so nobody sees that prompt, requires the **Privileged Role
Administrator** or **Global Administrator** role — being subscription Owner is not enough, because
this is an Entra directory role rather than an Azure resource one:

```powershell
az ad app permission admin-consent --id $clientAppId
```

If that returns `Authorization_RequestDenied` / "This operation can only be performed by an
administrator", you simply do not hold one of those roles. It is not a misconfiguration and nothing
needs fixing: carry on to step 3 and accept the prompt at first sign-in. Ask an administrator to
run the command only if you are rolling this out to other people and want to suppress the prompt
for them.

Check which directory roles the account you are signed in as actually holds:

```powershell
az rest --method get `
  --url "https://graph.microsoft.com/v1.0/me/transitiveMemberOf/microsoft.graph.directoryRole" `
  --query "value[].displayName" -o tsv
```

If that list is empty or lacks *Global Administrator* / *Privileged Role Administrator*, there is
**no command that grants you one** — self-elevation into a directory role is exactly what the role
system exists to prevent. Two things that do work:

- **Sign in as a different account that holds the role.** `az login --allow-no-subscriptions`
  (admin accounts often have no subscription), run the consent command, then `az login` back.
- **Activate the role through PIM**, if your account is an *eligible* rather than permanent member.
  Portal: **Entra ID → Privileged Identity Management → My roles → Directory roles → Activate**.
  Eligibility still has to have been granted to you beforehand; PIM only makes dormant eligibility
  live.

Note that `POST /providers/Microsoft.Authorization/elevateAccess` — the "elevate access" command
you may find while searching — does **not** help here. It grants an existing Global Administrator
*User Access Administrator* over Azure resources, which is the opposite direction to what this
step needs, and it requires Global Administrator to begin with.

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
    singleAccount      = @{ value = $true }
    grantIdentificationAccessToGeometry = @{ value = $true }
  }
}
$params | ConvertTo-Json -Depth 5 | Set-Content -Path infra/main.parameters.json -Encoding utf8
```

`namePrefix` is 3–12 characters and seeds every resource name, so keep it short and unique — the
storage account and the Foundry account need globally unique names.

`singleAccount` picks the isolation tier; see [Two tiers of isolation](#two-tiers-of-isolation)
below for what you are choosing between. Leave it `$true` unless you have read that section and
decided otherwise.

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

`--location` says where the deployment *record* is stored, not where the resources go — that comes
from the `location` parameter in the file above. But a subscription-scope deployment pins its
location to its name on the first run, so every later deployment called `enfolderer-scan` must pass
the same one or Azure refuses it:

```
InvalidDeploymentLocation: Invalid deployment location 'westeurope'.
The deployment 'ENFOLDERER-SCAN' already exists in location 'swedencentral'.
```

That is a rejection, not a partial deployment: nothing was changed. If you see it, you are either
in a new shell where `$location` was never set or set differently, or you edited the parameters
file by hand without updating the variable. Re-run the variable block at the top of this guide with
the location the deployment already uses, and deploy again.

This creates three resource groups (`<prefix>-platform`, `<prefix>-cardgeo`, `<prefix>-cardid`),
the four managed identities, the storage account with the `scans` and `crops` containers and the
`scan-jobs` queue, the Cosmos account with the `jobs` container, one Foundry account holding both
the `cardgeo` and `cardid` projects and a shared `gpt-4o` deployment, the container registry, the
three Container Apps environments and the five container apps, and all the role assignments.

On the first run the registry has no images, so every container app starts on a placeholder — see
[step 5](#5-host-the-mcp-servers).

Confirm the project-scoped role assignments landed, because this is the boundary the whole demo
rests on and ARM will tell you plainly if it did not:

```powershell
$geoProjectId = az resource show `
  --ids "/subscriptions/$((az account show --query id -o tsv))/resourceGroups/$prefix-platform/providers/Microsoft.CognitiveServices/accounts/$prefix-ai/projects/cardgeo" `
  --query id -o tsv
az role assignment list --scope $geoProjectId -o table
```

You should see `Foundry Project Manager` for Team A's group and `Foundry User` for Team B's
identity, both with a scope ending in `/projects/cardgeo`. If the deployment failed on those
assignments instead, your tenant does not accept project-scoped RBAC; redeploy with
`singleAccount=false` to fall back to one account per team, which scopes the same roles at the
account.

Collect the outputs — this is the same block as
[Resuming in a new shell](#resuming-in-a-new-shell), and every later step reads these variables:

```powershell
$outputs = az deployment sub show --name enfolderer-scan --query properties.outputs -o json |
           ConvertFrom-Json
$geo    = $outputs.geometryProjectEndpoint.value
$id     = $outputs.identificationProjectEndpoint.value
$apiUrl = $outputs.apiUrl.value
[pscustomobject]@{ geo = $geo; id = $id; api = $apiUrl } | Format-List
```

You need those three for the steps below. Role assignments can take a couple of minutes to
propagate; if the first scan fails with a 403, wait and retry before assuming a misconfiguration.

### If the deployment fails

Re-running the deployment is usually safe. ARM templates are declarative, so a second `az
deployment sub create` reconciles whatever already exists rather than duplicating it; there is no
need to delete the resource groups after a partial failure. **The one exception is a failed
Container Apps environment** — see the first entry below, which is also the failure you are most
likely to hit.

- **`SubscriptionNotRegisteredForFeature ... Microsoft.Network/AllowBringYourOwnPublicIpAddress`**,
  reported by `ConfigureAllocatedClusterHandler` after a long wait, for every environment at once.
  Nothing is wrong with the template. A Container Apps environment on a custom VNet creates a
  public IP and a load balancer in your subscription, and your subscription is not registered for
  the feature that permits it. The gate is not enforced when ARM accepts the deployment, which is
  why it costs you twenty minutes before saying so.

  ```powershell
  az feature register --namespace Microsoft.Network --name AllowBringYourOwnPublicIpAddress
  az feature show --namespace Microsoft.Network --name AllowBringYourOwnPublicIpAddress -o table
  # once it reads Registered:
  az provider register --namespace Microsoft.Network --wait
  ```

  Registration is self-service and usually takes seconds. The provider re-registration is **not**
  optional — the feature has no effect until `Microsoft.Network` is refreshed.

  Then **delete the failed environments before redeploying.** This is the exception to "re-running
  is safe": an environment that failed this way can come back from a retry reporting `Succeeded`
  while its `staticIp` is null and its `ME_` group contains no public IP or load balancer, after
  which every app in it fails to start for reasons that look nothing like the real cause. A failed
  environment also keeps a service association link on its subnet, so the subnet cannot be reused
  until it is gone.

  ```powershell
  foreach ($rg in $platformRg, $geometryRg, $identificationRg) {
    az containerapp env list -g $rg `
      --query "[].{Name:name, State:properties.provisioningState, Group:properties.infrastructureResourceGroup}" -o table
  }
  ```

  Delete any that are not `Succeeded`, and wait for both the environment and its `ME_` resource
  group to actually disappear — `az containerapp env delete` returns before the deletion finishes,
  and creating over the top then fails with `ManagedEnvironmentScheduledForDelete`.
  `scripts/migrate-to-containerapps.ps1` does all of this, including the polling, in its step 3b,
  and refuses to deploy at all while the feature is unregistered.

  If registration succeeds but the deployment is then refused with `RequestDisallowedByPolicy`, a
  tenant policy is blocking public IP creation outright. That needs a policy exemption, and you can
  prove it in isolation for the price of one IP:

  ```powershell
  az network public-ip create -g $platformRg -n byoip-probe --sku Standard --allocation-method Static
  az network public-ip delete -g $platformRg -n byoip-probe
  ```

  There is no workaround inside the template. An environment with no custom VNet cannot reach a
  private endpoint — Container Apps on the default network can only talk to internet-accessible
  endpoints — and this storage account has no public endpoint. The older Consumption-only
  environment type does not help either: it creates public IPs in your subscription too, in an
  `MC_` group.
- **`MaxNumberOfRegionalEnvironmentsInSubExceeded`.** The deployment creates three environments,
  one per team, and some regions cap a subscription at very few. Delete unused environments in the
  region, request an increase, or deploy somewhere else.
- **`NoRegisteredProviderFound ... for type 'accounts/projects'`**, listing supported API versions.
  The Bicep is pinned to an API version your tenant's resource provider does not offer. Take the
  newest stable version (no `-preview` suffix) from the list in the error and update the
  `Microsoft.CognitiveServices/...@<version>` lines in `infra/modules/foundry-account.bicep`,
  `infra/modules/foundry-project.bicep` and `infra/modules/cross-project-access.bicep`. Foundry moves quickly, so this pinning is
  the part of the template most likely to age.
- **A `Warning BCP081: ... does not have types available`** at compile time is worth heeding rather
  than ignoring: it usually means that API version does not exist for that resource type, and the
  deployment will fail later with the error above. A clean `bicep build` should emit nothing at
  all.
- **The resource provider is not registered at all.** Run
  `az provider register --namespace Microsoft.CognitiveServices --wait`.
- **No quota for `gpt-4o` in your region.** Change `location`, or edit the `modelDeployments`
  default in `infra/modules/foundry-account.bicep` to a model you do have quota for. Under the
  default single-account layout both teams draw on this one deployment, so size the capacity for
  the pair of them.
- **The role assignment on a project is rejected** — an `InvalidResourceType` or scope-validation
  error naming `.../projects/cardgeo`. Deploy with `singleAccount=false`. You lose the Foundry
  project boundary as the demo's subject and fall back to plain Azure RBAC between two accounts.

## 4. Create the agents

The YAML files in `agents/cardgeo/` and `agents/cardid/` are **this repository's own format**, not
something Azure reads. They are the source of record for each agent's model, prompt and tool
wiring; creating the agents means transferring that content into the project, either with the
script below or by hand in the portal.

Note which project each file belongs to. `agents/cardgeo/` is Team A's and `agents/cardid/` is
Team B's, and keeping them apart is the whole point of the demo — creating everything in one
project would work but would destroy the boundary you are demonstrating.

This step uses `$geo`, `$id` and `$mcp` from the deployment outputs. If you are in a fresh shell,
run the [deployment outputs block](#resuming-in-a-new-shell) now — an unset variable is an empty
string, so skipping it fails at the first `provision.ps1` call rather than here.

### Option A: the provisioning script

`agents/provision.ps1` reads the YAML and calls the Foundry Agents data plane — the same API the
worker uses at run time. It needs the `powershell-yaml` module once:

```powershell
Install-Module powershell-yaml -Scope CurrentUser
```

Preview what would be sent with `-WhatIf`, then drop it to apply:

```powershell
./agents/provision.ps1 -ProjectEndpoint $geo -Path ./agents/cardgeo -WhatIf

./agents/provision.ps1 -ProjectEndpoint $geo -Path ./agents/cardgeo
```

Team A first: the orchestrator in Team B connects to `CardBoundaryAgent`, and the data plane
identifies a connected agent by **id**, not by name. Take the `asst_...` id the run above printed
and pass it across the boundary — Team B cannot list Team A's agents to find it, which is the point:

```powershell
$boundaryId = "asst_..."   # from the cardgeo run

./agents/provision.ps1 -ProjectEndpoint $id -Path ./agents/cardid `
  -Only MtgCardIdAgent, PokemonCardIdAgent, OrchestratorAgent `
  -ConnectedAgentId @{ 'cardgeo/CardBoundaryAgent' = $boundaryId }
```

Agents in the *same* project are resolved automatically, including ones created earlier in the
same run, so only the cross-project reference needs `-ConnectedAgentId`. A connected agent whose
id cannot be resolved is left off with a warning rather than failing the run, so re-running later
attaches it — which also means `-WhatIf` warns about same-project agents it has not really
created.

Drop `PokemonCardIdAgent` from `-Only` to provision MTG alone. The worker maps game to agent by
name, so a card the boundary agent reports as Pokemon simply comes back unidentified rather than
failing the job — and adding the game later is one more name in that list. The orchestrator warns
that it is leaving the Pokemon tool off, which is expected; re-run with the agent in `-Only` to
attach it.

If a run fails with `PermissionDenied` and `lacks the required data action
...agents/read`, you are not in that project's owning group — see
[step 1](#1-create-the-owner-groups). Each project admits only its own team, so it is normal for
one of these two commands to succeed and the other to fail.

### Attaching the MCP tools

The YAML names each MCP server (`mcp-imaging`, `mcp-cardcatalog-mtg`, `mcp-cardcatalog-pokemon`)
but not where it runs, and the data plane only accepts an **`https://` URL** for a tool server. So
an MCP server has to be hosted and reachable before its tool can be attached.

Run the commands above first and the agents are created with their prompts and models, and a
warning per MCP tool that was left off. Do [step 5](#5-host-the-mcp-servers), then re-run with the
URLs to attach the tools — agents are matched by name, so this updates them in place:

```powershell
./agents/provision.ps1 -ProjectEndpoint $geo -Path ./agents/cardgeo -McpServerUrl @{
  'mcp-imaging' = $mcp['mcp-imaging']
}
./agents/provision.ps1 -ProjectEndpoint $id -Path ./agents/cardid `
  -Only MtgCardIdAgent, PokemonCardIdAgent, OrchestratorAgent -McpServerUrl @{
    'mcp-cardcatalog-mtg'     = $mcp['mcp-cardcatalog-mtg']
    'mcp-cardcatalog-pokemon' = $mcp['mcp-cardcatalog-pokemon']
  }
```

Pass only the URLs for servers that project is allowed to use. Handing `cardgeo` a catalogue URL
is exactly what [demo scenario 2](foundry-demo.md#2-call-team-bs-mtg-catalogue-from-team-as-project)
is about, and it should fail on RBAC rather than on configuration.

It prints a name-to-id table — keep it for the worker settings below. Agents are matched by name,
so re-running updates them in place; that is how you push an edited prompt. `OrchestratorAgent` is
always provisioned last because it references the others. Files beginning `mcp-` are skipped: they
describe MCP servers, which are step 5.

Without `-Only` every file in the folder is provisioned, including the `YugiohCardIdAgent` and
`LorcanaCardIdAgent` growth slots. Add them when you want to show a new game arriving without
Team A being involved.

If the script fails on the tool wiring, create that one agent in the portal instead. Connected
agents and MCP tools depend on connections existing in the project first, and the portal creates
those for you as part of the same dialogue.

### Option B: the portal

**Foundry portal → your project → Agents → New agent**, then copy from the YAML:

| Portal field | YAML key |
|---|---|
| Agent name | `name` |
| Deployment | `model.deployment` |
| Instructions | `instructions` (the whole block, verbatim) |
| Description | `description` |
| Temperature | `model.temperature` |
| Response format | `model.response_format` — set *JSON object* when present |

The remaining keys are not portal fields. `denied_connections` and `allowed_callers` document
boundaries that `infra/main.bicep` enforces through RBAC; `owner` and `project` record which team
owns the file.

Create them in this order:

1. In **`cardgeo`** (Team A): `CardBoundaryAgent`. Its instructions are the prompt the worker
   sends, so keep the two in sync if you edit either.
2. In **`cardid`** (Team B): `MtgCardIdAgent` and `PokemonCardIdAgent`, each with its catalogue MCP
   server from step 5 added under **Tools**.
3. Still in `cardid`: `OrchestratorAgent`, then add a **connected agent** pointing at `cardgeo`'s
   `CardBoundaryAgent`. This cross-project connection is what demo scenario 1 revokes.

### Record the agent ids

The worker defaults to using the agent *names* as ids. If the ids differ — the data plane usually
returns `asst_…` values — set them explicitly:

```powershell
az containerapp update `
  --resource-group $platformRg --name "$prefix-worker" --set-env-vars `
  ScanPipeline__BoundaryAgentId="<boundary agent id>" `
  ScanPipeline__IdentificationAgentIds__mtg="<mtg agent id>" `
  ScanPipeline__IdentificationAgentIds__pokemon="<pokemon agent id>"
```

## 5. Host the MCP servers

The three servers under `src/Enfolderer.Ai.Mcp.*` are ASP.NET applications that expose MCP over
streamable HTTP at `/mcp`. They have to be, because the Foundry agent data plane will only accept
an `https://` URL for a tool server — a stdio process has no address it can be given.

`infra/main.bicep` deploys them for you, each in its owning team's resource group and running as
that team's identity: `mcp-imaging` in `cardgeo`, `mcp-cardcatalog-mtg` and
`mcp-cardcatalog-pokemon` in `cardid`. Nothing extra to create; they came with step 3.

### Build and push the images

This is the same step for the MCP servers as for the API and the worker, so it is done once, here,
for all five. Step 6 assumes it has been run.

Every service is built from the single `src/Dockerfile`; `PROJECT` selects which one. The build
happens in Azure — `az acr build` uploads the source and ACR Tasks compiles it — so you need
neither Docker nor the .NET SDK locally.

```powershell
$acr = az deployment sub show --name enfolderer-scan `
  --query properties.outputs.registryName.value -o tsv
$tag = 'v1'

$images = @{
  'api'                     = 'Enfolderer.Ai.Api'
  'worker'                  = 'Enfolderer.Ai.Worker'
  'mcp-imaging'             = 'Enfolderer.Ai.Mcp.Imaging'
  'mcp-cardcatalog-mtg'     = 'Enfolderer.Ai.Mcp.CardCatalog.Mtg'
  'mcp-cardcatalog-pokemon' = 'Enfolderer.Ai.Mcp.CardCatalog.Pokemon'
}

foreach ($image in $images.Keys) {
  az acr build --registry $acr --image "enfolderer/$image`:$tag" `
    --build-arg "PROJECT=$($images[$image])" --file src/Dockerfile .
  if ($LASTEXITCODE -ne 0) { throw "image build failed for $image" }
}
```

Run it from the repository root: the trailing `.` is the build context, and every service project
has `ProjectReference`s reaching back up into `src/`. `.dockerignore` keeps the upload down to
`src/` — without it the desktop app, its SQLite database and `images.zip` would go up too.

The backtick in ``"enfolderer/$image`:$tag"`` escapes the colon. PowerShell would otherwise read
`$image:` as a scoped variable and expand the whole thing to nothing.

### Point the apps at the images

The first deployment in step 3 had no images to run, so every app started on a Microsoft sample
container. Redeploy with the tag to switch them over:

```powershell
az deployment sub create --name enfolderer-scan --location $location `
  --template-file infra/main.bicep --parameters infra/main.parameters.json `
  --parameters imageTag=$tag
```

This second deployment is not optional and cannot be replaced by `az containerapp update`: as well
as the image, it adds the registry configuration that tells each app to pull as its own managed
identity. Once it has run, later image changes *are* just an `update` — see step 6.

### Read the URLs

The deployment tells you them, so there is nothing to look up. This builds the `$mcp` hashtable in
the shape `provision.ps1 -McpServerUrl` expects, keyed by the same server names the YAML uses:

```powershell
$mcp = @{}
foreach ($o in 'geometryMcpServerUrls','identificationMcpServerUrls') {
  (az deployment sub show --name enfolderer-scan --query "properties.outputs.$o.value" -o json |
     ConvertFrom-Json) | ForEach-Object { $mcp[$_.name] = $_.url }
}
$mcp
```

Check each one is alive before wiring it to an agent. `/healthz` is deliberately left open so you
can do this without a token:

```powershell
Invoke-RestMethod "$($mcp['mcp-imaging'] -replace '/mcp$', '/healthz')"
```

Container Apps FQDNs contain a generated suffix, so there is no hostname you can write out by
hand — take it from `$mcp`, which you have just built.

Then go back to [attaching the MCP tools](#attaching-the-mcp-tools) in step 4 and re-run
`provision.ps1` with those URLs.

### Locking them down

A reachable MCP endpoint that anyone can call would undercut the whole demo, so each server
authenticates its callers and then checks them against an allow-list of principal object ids — the
enforced form of the `allowed_callers` key in the server's YAML. Team A's principals are not on
Team B's catalogue servers, so
[demo scenario 2](foundry-demo.md#2-call-team-bs-mtg-catalogue-from-team-as-project) fails even if
someone hands Team A the URL.

This is off until you give the deployment an audience, because the audience has to be an
identifier URI that exists in your tenant. Create a registration for it and redeploy:

```powershell
$mcpAppId = az ad app create --display-name "Enfolderer MCP" --query appId -o tsv
az ad app update --id $mcpAppId --identifier-uris "api://$prefix-mcp"
az ad sp create --id $mcpAppId
```

Then add `"mcpAudience": { "value": "api://enf-demo-mcp" }` to `infra/main.parameters.json` and
re-run the deployment from step 3.

Until you do, the servers start with a warning in their log saying they are running
unauthenticated. Treat that warning as a blocker before demoing the security boundaries, not after:

```text
McpServer:TenantId or McpServer:Audience is not configured; mcp-cardcatalog-mtg is running
unauthenticated. This is only acceptable for local development.
```

Verify the lock-down took by calling `/mcp` without a token — it should be `401`, while `/healthz`
stays `200`:

```powershell
$response = Invoke-WebRequest -SkipHttpErrorCheck -Method Post `
  -Uri $mcp['mcp-cardcatalog-mtg'] `
  -Headers @{ Accept = 'application/json, text/event-stream' } `
  -ContentType 'application/json' -Body '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}'
$response.StatusCode
```

One caveat worth knowing before you rely on this: whether Foundry presents its project managed
identity when calling a tool server depends on how the connection is configured, and if it cannot,
these servers will refuse it. If the agents start reporting 401 from their tools, that is what has
happened — configure the connection to send the identity, or leave `mcpAudience` empty and keep the
endpoints off the demo's security story rather than pretending they are protected.

The Pokémon catalogue works anonymously but is rate-limited; if you have a pokemontcg.io key, set
it on that site. Scryfall needs no key.

```powershell
az containerapp update --resource-group "$prefix-cardid" `
  --name "$prefix-mcp-cardcatalog-pokemon" --set-env-vars POKEMONTCG_API_KEY="<your key>"
```

`--set-env-vars` adds to what is there rather than replacing it, so this will not quietly drop the
`McpServer__*` settings the Bicep put in. Each `az containerapp update` creates a new revision and
the old one is drained, which takes a few seconds.

## 6. Check the API and worker

Their images were built and deployed in [step 5](#5-host-the-mcp-servers) along with the MCP
servers'. There is nothing further to push; this step is about confirming it worked.

The Bicep already set every environment variable, including
`ScanPlatform__ManagedIdentityClientId` — the client id of that service's user-assigned identity.
That setting is not optional: an app can carry several user-assigned identities, and without it the
credential cannot tell which one to present. If you attach another identity later, keep the setting
pointing at the one that holds the role assignments.

```powershell
$apiUrl = az deployment sub show --name enfolderer-scan `
  --query properties.outputs.apiUrl.value -o tsv
Invoke-RestMethod "$apiUrl/healthz"
```

There is no equivalent call for the worker: it has no ingress, public or internal, because it takes
its work from the queue and nothing needs to reach it. Check it by looking at its log instead —
see below. It still serves `/healthz` inside its own container, which `az containerapp exec` can
reach if you ever want it.

Confirm both apps are actually running your images rather than the placeholder from the first
deployment:

```powershell
az containerapp list -g $platformRg `
  --query "[].{Name:name, Image:properties.template.containers[0].image, Replicas:properties.runningStatus}" -o table
```

Anything still showing `mcr.microsoft.com/k8se/quickstart` never got the second deployment with
`imageTag` set.

A warning in the API log that `AzureAd:TenantId` is not configured means the API is running
unauthenticated — acceptable locally, not in a deployment. Confirm the setting survived.

### Redeploy after every code change

Granting a role and redeploying the Bicep does not update the running code, and pulling the repo
does not either — each app runs whatever image tag it was last pointed at. If a stack trace still
names the line numbers of a version you have since changed, that is the whole explanation.

Build a new tag and point the app at it:

```powershell
$tag = 'v2'
az acr build --registry $acr --image "enfolderer/worker`:$tag" `
  --build-arg PROJECT=Enfolderer.Ai.Worker --file src/Dockerfile .

az containerapp update -g $platformRg -n "$prefix-worker" `
  --image "$acr.azurecr.io/enfolderer/worker:$tag"
```

Use a new tag each time rather than overwriting `v1`. `az containerapp update` decides whether to
create a revision by comparing the template it is given with the current one, so re-pushing the
same tag changes nothing it can see and the old image keeps running — the exact failure this
section exists to warn about, in a new costume.

`az containerapp update` returns once the new revision is *created*, not once it is serving. Give
it a few seconds and re-check `/healthz` rather than testing immediately.

### Reading the logs

```powershell
az containerapp logs show -g $platformRg -n "$prefix-worker" --follow --tail 50
```

Unlike `az webapp log tail`, this does not replay the last few lines of every historical log file
before it starts, so what you see is genuinely current. For history, query the environment's
workspace — each environment has its own, so Team A's logs are in `<prefix>-cardgeo-env-logs` and
are not readable from Team B's.

### If Azure returns 403

`POST /jobs` reports a refusal as a 502 naming the principal Azure actually refused, for example:

```text
Azure Storage returned 403 AuthorizationFailure for the identity
oid=6d85290d-… appid=… tid=…
```

That object id is the thing to check, because the common cause is not a missing role but the app
presenting a *different* identity than the one the roles were granted to. Compare it:

```powershell
$apiPrincipal = az identity show -g $platformRg -n "$prefix-api-id" --query principalId -o tsv
$apiPrincipal   # must equal the oid in the error

# Which identities is the app actually carrying, and which was it told to present?
az containerapp show -g $platformRg -n "$prefix-api" --query identity -o json
$env = az containerapp show -g $platformRg -n "$prefix-api" `
  --query "properties.template.containers[0].env" -o json | ConvertFrom-Json
($env | Where-Object name -eq 'ScanPlatform__ManagedIdentityClientId').value
```

The filtering is done in PowerShell rather than with `--query`, because `az` on Windows is a `.cmd`
wrapper and `cmd` mangles a JMESPath expression containing `?`. A double-quoted projection such as
`"[].{Role:roleDefinitionName}"` survives, which is why the role listing below can use `--query`.

The last value must be the identity's **client id**, not its principal id — they are different
GUIDs, and the wrong one leaves the credential unable to find the identity at all.

If the object ids do match, it really is the role assignment. Storage data-plane grants take a few
minutes to propagate, so a role granted seconds earlier is expected to fail:

```powershell
az role assignment list --assignee $apiPrincipal --all `
  --query "[].{ObjectId:principalId, Role:roleDefinitionName, Scope:scope}" -o table
```

The explicit `--query` matters. The default table output has a *Principal* column holding
`principalName`, which for a managed identity is its **client id** — so the listing appears to
contradict the `--assignee` you passed, and looks as though the roles belong to some other
principal. They do not; it is the same identity under its other GUID. `principalId` is the object
id and is what compares against the `oid` in the error.

Expect **Storage Blob Data Contributor** scoped to the `scans` container, and **Storage Queue Data
Contributor** on the account. The API needs write on `scans` because it relays the client's upload
into the container itself.

If the object ids match *and* those roles are present, read the error code again. Storage
distinguishes the two kinds of refusal, and only one of them is about permissions:

| Error code | Message ends | Means |
| --- | --- | --- |
| `AuthorizationPermissionMismatch` | "…using this permission." | The identity is missing a role. |
| `AuthorizationFailure` | "…to perform this operation." | The request was blocked before RBAC was consulted — a network rule. |

`AuthorizationFailure` with every role in place means the request reached the storage account over
the **public** endpoint, which is closed. That points at the private path, not at RBAC. Check that
the environment is joined to the VNet and that the app resolves the account privately:

```powershell
az containerapp env show -g $platformRg -n "$prefix-platform-env" `
  --query "properties.vnetConfiguration" -o json

# Then resolve the account from inside a replica — not from your laptop, which has no line of
# sight to the private zone. A 10.x address is correct; a public one means DNS never reached it.
az containerapp exec -g $platformRg -n "$prefix-api" `
  --command "getent hosts <storageaccount>.blob.core.windows.net"
```

`infrastructureSubnetId` must be populated. Unlike App Service there is no separate "route all"
switch to forget: an environment on a custom VNet sends all its outbound traffic through the
subnet.

Resolving to a public address instead means the private DNS zone link is missing. Confirm the zone
exists and is linked to the VNet:

```powershell
az network private-dns zone list -g $platformRg -o table
az network private-dns link vnet list -g $platformRg -z "privatelink.blob.core.windows.net" -o table
```

Do **not** try to fix this by re-enabling public access. The account is private on purpose, and in a
policy-governed subscription the setting will be reverted under you anyway.

## 7. Point the desktop app at the deployment

Create `aiconfig.txt` beside `Enfolderer.App.exe` (the app writes a template on the first scan if
the file is missing):

```powershell
$tenantId = az account show --query tenantId -o tsv
# The FQDN has a generated suffix, so read it back rather than composing it.
$apiUrl   = az deployment sub show --name enfolderer-scan `
  --query properties.outputs.apiUrl.value -o tsv

@"
api_base_url=$apiUrl
tenant_id=$tenantId
client_id=$clientAppId
scope=api://$apiAppId/Scan.Submit
#game_hint=mtg
#use_device_code=true
"@ | Set-Content -Path .\aiconfig.txt -Encoding utf8
```

The `@"` … `"@` here-string expands variables; `@'` … `'@` would not, and would leave the literal
`$prefix` in the file.

Then **Tools → Scan Card Image…**, pick a photo, and sign in when prompted.

Set `use_device_code=true` if the interactive browser sign-in cannot complete on that machine. The
usual reason is not a missing browser but a passkey: signing in with a phone needs a Bluetooth link
between the phone and the PC, which hangs at *Connecting to device* on a virtual machine, over
Remote Desktop, or with Bluetooth unavailable. Device code flow avoids the pairing altogether — the
whole sign-in happens on the phone. The app opens the sign-in page, copies the code to the
clipboard, shows both so they can be copied again, and closes the prompt once sign-in finishes.

### Signing in once

Sign-in is remembered across runs. The tokens live in the Azure SDK's persistent cache, and
`aiauth.json` — written beside `aiconfig.txt` — records *which* account in that cache to reuse. It
holds no token and no secret, only the username, home account id, tenant and client id. Delete it to
force a fresh sign-in; changing `tenant_id` or `client_id` in `aiconfig.txt` invalidates it
automatically, as does the cached token expiring or being revoked, each of which simply prompts
again.

On Windows the token cache is encrypted with DPAPI under the signed-in user's profile. Where
encryption is unavailable the SDK refuses to write the cache rather than storing tokens in the
clear, and the app leaves it that way: it prompts every scan instead. For a demo whose subject is
security boundaries, prompting is the better failure. If you need the cache on such a machine, that
is a deliberate change to `TokenCachePersistenceOptions.UnsafeAllowUnencryptedStorage` in
`BinderScanService`, not a setting.

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

Then run the "break it" scenarios in [foundry-demo.md](foundry-demo.md).

## Two tiers of isolation

Foundry gives you two places to draw a line between teams, and choosing the wrong one is the most
common mistake this demo exists to correct. `singleAccount` decides which one you get.

| | Project | Account |
|---|---|---|
| Agents, threads, files | isolated | isolated |
| Connections and tool servers | isolated | isolated |
| Authoring rights (`Foundry Project Manager`) | isolated | isolated |
| Model deployments | **shared** | isolated |
| Token quota and throttling | **shared** | isolated |
| Local-auth, networking, account settings | **shared** | isolated |
| Blast radius of a delete | **shared** | isolated |

**Projects isolate the work. Accounts isolate the platform underneath it.**

`singleAccount=true` (the default) is one account holding both projects. The boundary between the
teams is then genuinely Foundry's: the role assignments are scoped to the project resource, and
Team B cannot touch Team A's agents despite sharing an account and a resource group. This is the
layout to demo, because it shows the project boundary doing real work — and because scenario 3 in
[foundry-demo.md](foundry-demo.md) can then show its limits honestly.

`singleAccount=false` is one account per team, in each team's own resource group. Stronger
isolation, weaker demonstration: two separate accounts would be isolated from each other whatever
they contained, so it shows Azure RBAC rather than anything specific to Foundry. Reach for it when
the risk warrants it — teams that must not share quota, must not be able to delete each other's
model deployments, or that need different networking or data-residency settings.

The rule of thumb: separate projects for teams that trust the same platform team, separate accounts
for teams that do not. Either way nothing in `src/` changes; the worker holds one client per
project endpoint and those endpoints keep their shape.

## Migrating a deployment that predates Container Apps

If you deployed this before the move to Container Apps, **you cannot simply redeploy**. Two things
are in the way, and both fail in ways that describe the symptom rather than the cause:

- ARM incremental deployments never delete a resource merely removed from a template, so the old
  App Service sites and plans survive every redeploy, keep billing, and keep holding their VNet
  integration subnets.
- The subnets changed size, name and delegation — a Container Apps environment needs a `/27`
  delegated to `Microsoft.App/environments` where App Service needed a `/28` delegated to
  `Microsoft.Web/serverFarms` — and the new ranges overlap the old ones. A subnet cannot be resized
  or re-delegated while anything sits in it.

So the old compute and the old VNet have to go *before* the deployment, not as part of it.
`scripts/migrate-to-containerapps.ps1` does that in the right order. Preview first:

```powershell
./scripts/migrate-to-containerapps.ps1 -Prefix $prefix -WhatIf
./scripts/migrate-to-containerapps.ps1 -Prefix $prefix -Confirm:$false
```

It surveys what actually exists before touching anything, so re-running it is safe and a step that
is already done says so. Everything outside the compute tier is left alone and survives: the
resource groups, the four managed identities and their role assignments, storage, Cosmos, the
Foundry account, both projects, your agents and their `asst_` ids, the private DNS zones, and both
app registrations.

It stops short of three things that need a value only you have, and prints them with the values
filled in: re-provisioning the agents, telling the worker their ids, and repointing `aiconfig.txt`
at the API's new hostname.

Two of those are easy to skip and shouldn't be. **The agents must be re-provisioned**, because
`CardBoundaryAgent` no longer has any tools — it used to call `get_image_sas`, which no longer
exists — and because every MCP URL changed when the servers moved off `azurewebsites.net`. And
**the desktop app's `api_base_url` must change**, for the same reason.

If `az acr build` is refused by policy in your tenant — the same class of block that closed the
storage account's public endpoint — use `-SkipImageBuild`. The script then stops after the first
deployment rather than pointing the apps at image tags that do not exist. Push the five images
another way, then finish the migration with `-ImagesAlreadyPushed`, which skips the build and
carries straight on to the redeploy:

```powershell
./scripts/migrate-to-containerapps.ps1 -Prefix $prefix -SkipImageBuild -Confirm:$false
# push enfolderer/{api,worker,mcp-imaging,mcp-cardcatalog-mtg,mcp-cardcatalog-pokemon}:v1
./scripts/migrate-to-containerapps.ps1 -Prefix $prefix -ImagesAlreadyPushed -Confirm:$false
```

## Removing what the templates no longer create

ARM deployments are incremental. Removing a role assignment from a Bicep file does **not** remove
it from the subscription — the next deployment simply stops asserting it, and the grant stays
where it is, invisible in the templates and live in the tenant. Anything you created by hand while
debugging is in the same position.

`scripts/cleanup.ps1` deletes the ones this project has stopped using: the `Storage Blob
Delegator` grants (nothing mints a SAS any more), any account-wide `Storage Blob Data Contributor`
added by hand while chasing a 403, and Team B's old read on `crops`. Run it with `-WhatIf` first —
it prints what it would delete and touches nothing:

```powershell
./scripts/cleanup.ps1 -Prefix $prefix -WhatIf
./scripts/cleanup.ps1 -Prefix $prefix -Confirm:$false
```

It deletes by assignment id rather than by `--assignee`/`--role`/`--scope`, because that trio
matches on the scope string exactly and silently finds nothing when the case differs — a grant
written at `/resourcegroups/...` will not be matched by a filter spelling it `/resourceGroups/...`.

The other kind of debris is Foundry file uploads. Each run uploads an image and deletes it
afterwards, but a worker that crashes mid-run leaves one behind. Sweep uploads older than six
hours, across both projects:

```powershell
./scripts/cleanup.ps1 -Prefix $prefix -FoundryFiles `
  -GeometryProjectEndpoint $geo -IdentificationProjectEndpoint $id -WhatIf
```

Six hours is the `-FileAgeHours` default and exists so a sweep cannot delete an upload belonging to
a run that is still going. Leave yourself that margin unless you know nothing is running.

## Costs and teardown

The provisioned-throughput Cosmos container and the `gpt-4o` deployment bill while they exist. The
container apps bill per vCPU-second and GiB-second: five apps at one replica each, mostly idle, is
roughly the same order as the App Service plans this replaced, and the three Container Apps
environments themselves are free. The registry is Basic and the Log Analytics workspaces bill per
GB ingested, which at demo volume is pennies.

If a demo is weeks away, set the apps to scale to zero rather than tearing the whole thing down —
the identities, role assignments and agents all survive:

```powershell
foreach ($app in "$prefix-api", "$prefix-worker") {
  az containerapp update -g $platformRg -n $app --min-replicas 0
}
```

Put them back to `--min-replicas 1` before demoing. A cold start is a few seconds of nothing
happening at exactly the moment someone is watching, and the MCP servers should stay at 1
regardless: Foundry times a tool call out long before a cold replica answers.

Job documents self-expire after seven days via the container TTL, but the resources do not. Tear
down when you are done:

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
