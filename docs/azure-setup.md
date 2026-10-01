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
VPN, no private resolver and no jump host. Step 8 optionally puts API Management in front of it,
which moves the public hostname but not the shape of this picture.

Everything behind the API is private:

```text
desktop client ──HTTPS──▶  <prefix>-api  ─┐
                                          │  every Container Apps
                           <prefix>-worker┤  environment sits on a
                                          │  subnet of the demo VNet
                  cardgeo mcp-imaging  ───┘
                                          │
                                          ▼
                             private endpoints (blob, queue, Cosmos)
                                          │
                                          ▼
                            storage account + Cosmos account
                             publicNetworkAccess: Disabled
```

Two consequences are worth knowing before you read the rest of this guide:

- **The client uploads the image to the API, not to a blob.** There is no URL that would let a
  client write to the account, and there never will be. `POST /jobs` returns the job id; the client
  PUTs the image to `jobs/{jobId}/content` with its bearer token, and the API — which holds the
  write permission on the `scans` container — relays the bytes.
- **Storage and Cosmos are private unconditionally.** There is no parameter to turn either back on.
  In a policy-governed subscription `publicNetworkAccess: Enabled` is reverted anyway, and the demo
  is more honest without it. Both accounts therefore depend on their private endpoints: an account
  with public access disabled and no endpoint is unreachable from everywhere, including from your
  own compute.
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

If that deployment fails, **do not simply run it again.** Go to
[If the deployment fails](#if-the-deployment-fails) below and run the diagnostic first: several of
the failures here cannot be repaired by redeploying, because the fix involves deleting something and
`az deployment sub create` never deletes anything.

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

**Run this first.** It changes nothing, and it tells you which of the known causes you have hit
instead of leaving you to infer it from an ARM error that is frequently empty:

```powershell
./scripts/diagnose-containerapps.ps1 -Prefix $prefix
```

Re-running the deployment is usually safe. ARM templates are declarative, so a second `az
deployment sub create` reconciles whatever already exists rather than duplicating it; there is no
need to delete the resource groups after a partial failure.

**But re-running is not a repair.** `az deployment sub create` only creates and updates. It cannot
delete anything, so any failure whose fix is "delete this and start again" will survive every retry
and fail in exactly the same way. A failed Container Apps environment is precisely that failure, and
it is the one you are most likely to hit — see the first entry below. When the diagnostic tells you
an environment is unusable, the recovery is:

```powershell
./scripts/migrate-to-containerapps.ps1 -Prefix $prefix -Confirm:$false
```

That script is the deployment path for anything other than a clean first run: it deletes what has to
be deleted, in the order it has to happen, and then runs the same deployment as above. The name says
"migrate" because that is what it was written for, but every failure mode in this section is encoded
in it as a preflight check.

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
      --query "[].{Name:name, State:properties.provisioningState, Ip:properties.staticIp, Group:properties.infrastructureResourceGroup}" -o table
  }
  ```

  Delete any that are not `Succeeded`, **and any that say `Succeeded` but have an empty `Ip`** —
  that is the repaired-but-broken state described above, and it is the one that looks fine. Wait for
  both the environment and its `ME_` resource group to actually disappear — `az containerapp env
  delete` returns before the deletion finishes,
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
- **`ManagedEnvironmentNoAvailableCapacityInRegion: Managed Cluster '<name>' provision failed`** —
  nothing is wrong with your template, your subscription or your permissions. Every Container Apps
  environment is backed by a managed cluster, and Azure could not allocate one in that region at
  that moment. Capacity is shared across all customers in a region and fluctuates hour to hour, so
  this is transient: wait a few hours and re-run.

  Re-run the **script**, not the deployment on its own:

  ```powershell
  ./scripts/migrate-to-containerapps.ps1 -Prefix $prefix -Confirm:$false
  ```

  The reason is the same one as everywhere else in this section: the failed attempt leaves an
  environment behind in a `Failed` state, ARM will not replace it, and only step 3b deletes it. Run
  `az deployment sub create` again and it will fail on the leftover rather than on capacity, which
  looks like the same problem but is not.

  You can see whether a region is likely to work before committing twenty minutes to finding out —
  a region that will not offer you the Consumption workload profile has no capacity to give you:

  ```powershell
  az containerapp env workload-profile list-supported --location swedencentral -o table
  ```

  <a id="the-region-is-out-of-capacity"></a>
  **Moving to a different region is not a one-parameter change.** A Container Apps environment must
  sit in the same region as the subnet it is injected into, and that subnet belongs to a VNet that
  also carries the private endpoints for storage. So moving the environments means moving the VNet,
  the storage account, Cosmos, the Foundry account and the registry as well — in effect, deploying
  the whole thing again from scratch. If you decide to do that, deploy alongside the existing one
  rather than trying to convert it:

  ```powershell
  $prefix   = 'enf-demo2'          # must be globally unique: storage and Foundry names derive from it
  $location = 'westeurope'
  ```

  Rewrite `infra/main.parameters.json` with the block in [step 3](#3-deploy-the-infrastructure),
  then deploy under a **new** deployment name, because Azure pins a subscription-scope deployment's
  location to its name and would reject the new region otherwise:

  ```powershell
  az deployment sub create `
    --name enfolderer-scan-westeurope `
    --location $location `
    --template-file infra/main.bicep `
    --parameters infra/main.parameters.json
  ```

  That gives you three new resource groups and leaves the old ones untouched, so you can tear down
  whichever one you do not keep with `az group delete`. The app registrations from
  [step 2](#2-register-the-api-and-the-desktop-client) and the owner groups from
  [step 1](#1-create-the-owner-groups) are subscription-independent and are reused as they are; the
  agents have to be created again in the new projects, because agents live inside a Foundry project.
- **`ContainerAppOperationError: Failed to provision revision for container app '<name>'. Error
  details: .`** — note the empty details, and note that it usually hits *every* app at once. ARM has
  nothing useful to say, and neither does `az deployment operation group list`: it returns the same
  string with `details: null`. The real reason is spread across the environment, the subnet, the app
  and the revision. Read all four at once:

  ```powershell
  ./scripts/diagnose-containerapps.ps1 -Prefix enf-demo
  ```

  That changes nothing, and it ends with a verdict naming which of the causes below applies. The
  individual commands, if you would rather run them yourself:

  ```powershell
  az containerapp revision list -g $platformRg -n "$prefix-api" `
    --query "[].{Name:name, Active:properties.active, Running:properties.runningState, Health:properties.healthState, Error:properties.provisioningError}" -o table
  az containerapp logs show -g $platformRg -n "$prefix-api" --type system --tail 100
  ```

  Two causes account for almost all of it:

  - **The environment is one of the broken-but-`Succeeded` ones** from the entry above. Nothing can
    start in an environment with no ingress IP, which is why all five apps fail together and why the
    message is empty. Check `properties.staticIp` on all three environments before looking at
    anything else. The fix is to delete and recreate them, not to redeploy the apps — and note that
    **`az deployment sub create` will not do this for you.** It is the whole reason
    `scripts/migrate-to-containerapps.ps1` exists; running the raw deployment against a broken
    environment fails the same way every time, however many times you retry it.
  - **Nothing is listening on the ingress target port.** Container Apps injects default TCP startup,
    liveness and readiness probes bound to `targetPort` whenever ingress is enabled, and the startup
    probe has to pass before a revision counts as provisioned. The first deployment runs every app on
    `mcr.microsoft.com/k8se/quickstart:latest`, which listens on **80**, while our own images listen
    on 8080 (`EXPOSE 8080` in `src/Dockerfile`), so the templates declare `targetPort: 80` while
    `imageTag` is empty and 8080 once it is set. If you hand-edit an app's image, move its port too.
    This one usually reports `Error details: Operation expired.` rather than an empty string, and it
    cannot explain the worker, which has no ingress and therefore no probes.

  If neither fits, suspect **egress**. A VNet-injected environment pulls both its own platform
  images and yours out through your subnet, so a tenant policy that attaches an NSG or a route table
  to the `*-apps` subnets — neither of which this template creates — will stop every revision from
  starting. One command discriminates it:

  ```powershell
  az network vnet subnet show -g $platformRg --vnet-name "$prefix-vnet" -n platform-apps `
    --query "{nsg:networkSecurityGroup.id, routeTable:routeTable.id}" -o json
  ```

  Anything non-null there was added by policy, and whatever it does it must still allow outbound 443
  to the `MicrosoftContainerRegistry`, `AzureFrontDoor.FirstParty` and `AzureActiveDirectory` service
  tags (the last is needed because every app here uses a managed identity), and TCP+UDP 53 to
  `168.63.129.16`. Behind a firewall the equivalent FQDN allow-list is `mcr.microsoft.com`,
  `*.data.mcr.microsoft.com`, `packages.aks.azure.com`, `acs-mirror.azureedge.net`,
  `login.microsoftonline.com` and `*.identity.azure.net`. `scripts/migrate-to-containerapps.ps1`
  warns in its step 1 if it finds either attached.

  Whichever cause it turns out to be, **the ARM message is truncated, not empty at the source.** The
  full inner error is in the per-operation list, and the per-revision detail is in
  `runningStateDetails`:

  ```powershell
  az deployment operation group list -g $platformRg --name hosting `
    --query "[?properties.provisioningState=='Failed'].{name:properties.targetResource.resourceName, msg:properties.statusMessage}" -o json
  az containerapp revision show -g $platformRg -n "$prefix-api" --revision $revision `
    --query "{health:properties.healthState, running:properties.runningState, details:properties.runningStateDetails}" -o json
  ```

  An **empty** revision list means the app never got as far as creating a revision, which narrows it
  to an image pull or template validation rather than a crash or a probe. In that state
  `az containerapp logs show --type system` fails with `KeyError: 'eventStreamEndpoint'`, because
  there is no revision to stream from; use `az monitor activity-log list -g $platformRg --query
  "[?status.value=='Failed']"` instead.
- **`MaxNumberOfRegionalEnvironmentsInSubExceeded`.** The deployment creates three environments,
  one per team, and some regions cap a subscription at very few. Delete unused environments in the
  region, request an increase, or deploy somewhere else.
- **`NoRegisteredProviderFound ... for type 'accounts/projects'`**, listing supported API versions.
  The Bicep is pinned to an API version your tenant's resource provider does not offer. Take the
  newest stable version (no `-preview` suffix) from the list in the error and update the
  `Microsoft.CognitiveServices/...@<version>` lines in `infra/modules/foundry-account.bicep`,
  `infra/modules/foundry-project.bicep` and `infra/modules/foundry-invoke-access.bicep`. Foundry moves quickly, so this pinning is
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
./agents/provision.ps1 -ProjectEndpoint $id -Path ./agents/cardid `
  -Only MtgCardIdAgent, PokemonCardIdAgent, OrchestratorAgent -McpServerUrl @{
    'mcp-cardcatalog-mtg'     = $mcp['mcp-cardcatalog-mtg']
    'mcp-cardcatalog-pokemon' = $mcp['mcp-cardcatalog-pokemon']
  }
```

`cardgeo` takes no `-McpServerUrl`: `card-boundary-agent.yaml` declares `tools: []`, because the
image reaches that agent as message content and the imaging server's only tool, `crop_quad`, is
work the worker does itself. The server is still deployed and still running as Team A's identity;
it is simply not attached to the agent.

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

### If `provision.ps1` fails

The script's first call is a `GET` for the agents already in the project. It proves three things at
once — that the endpoint resolves, that your token is accepted, and that the service can find the
project — so a failure there is about the deployment, never about the agent YAML.

Run the diagnostic before changing anything:

```powershell
./scripts/diagnose-foundry.ps1 -Prefix $prefix
```

It reads the account, its subdomain and settings, any soft-deleted account holding the same name,
each project, your role assignments and the model deployments, then repeats the same data-plane call
and prints what came back. It changes nothing.

- **`InternalServerError: Unable to get resource information.`, or `Timeout: The operation was
  timeout.`** — what matters is not the message but **which surfaces fail**, and the diagnostic
  prints both. The two messages are interchangeable: the same account can answer one call with a
  500 and the next identical call with a 408.

  If `/assistants` fails but `/agents` answers, read
  [The Assistants API is deprecated](#the-assistants-api-is-deprecated) below: the deployment is fine
  and the code is calling a surface that no longer exists.

  If **both** fail, see [Neither agent surface answers](#neither-agent-surface-answers). The
  retirement is not the cause, and neither is anything in the agent YAML.

  One account-level cause is worth knowing before you read further, because everything looks
  correct when you hit it: a **soft-deleted account of the same name**. Cognitive Services accounts
  are soft-deleted for 48 hours and keep their subdomain registered the whole time, so the DNS name
  resolves to the dead account rather than the new one. List and purge:

  ```powershell
  az cognitiveservices account list-deleted -o table
  az cognitiveservices account purge -n "$prefix-ai" -g "$prefix-platform" -l $location
  ```

  Purging is immediate and final, so be certain the deleted account is not one you still want. After
  purging, redeploy [step 3](#3-deploy-the-infrastructure) so the subdomain binds to the new account.

  If nothing is flagged and both surfaces fail, check **Azure Service Health** for your region. This
  exact message has been reported before as a regional AI Services incident with no customer-side
  cause.
- **`PermissionDenied`, or a 401/403** — you have no agent-authoring role on the project. Agent APIs
  are **data actions**, so subscription Owner grants nothing here; you need Foundry Project Manager
  on that specific project, which [step 1](#1-create-the-owner-groups) grants through the team's
  group. Group membership is carried in the token, so a token issued before you were added will not
  have it. Force a fresh one:

  ```powershell
  az account get-access-token --resource https://ai.azure.com --query expiresOn -o tsv
  az logout
  az login
  ```

- **A tool or connected agent is rejected** — that is the agent content rather than the project, and
  it is covered by [Attaching the MCP tools](#attaching-the-mcp-tools) above.

### The Assistants API is deprecated

**This affects this repository directly and is not something you can fix by redeploying.**

Azure AI Foundry has two agent data planes living on the same project endpoint. They are **two
different resource models, not an old and a new spelling of one thing**:

| | Path | Objects | Status |
| --- | --- | --- | --- |
| Classic ("Assistants") | `/assistants`, `/threads`, `/runs` | `asst_…` ids | deprecated, announced sunset **2026-08-26**, still served |
| Foundry agents v2 | `/agents`, `/conversations`, `/responses` | name-keyed, versioned | current GA |

`agents/provision.ps1`, `scripts/cleanup.ps1` and the worker's `FoundryAgentClient` were all written
against the classic surface. It worked when the demo was first built, and it is on borrowed time
now.

**An agent created through `/assistants` is never projected into `/agents`.** The v2 registry is a
separate store, populated only by `POST /agents`. So `GET /agents` returning an empty `data` array
is the *expected* reading of a perfectly healthy project that was provisioned by this repository —
it is not evidence that anything is missing, and it is never a reason to delete or re-create an
agent that `/assistants` lists. `diagnose-foundry.ps1` reports the two independently and does not
compare them.

Because they are separate stores, the sunset is a **migration, not a route rename**: the successor
to `/assistants` is `POST /agents` for definitions, and `/conversations` plus `/responses` for
execution. Nothing redirects.

What the gateway returns for a sunset path is **not documented**, and it is not a clean 404. Do not
read a 500 on `/assistants` as proof of the sunset on its own — a sick account returns the same
500. Enforcement is only demonstrated when `/assistants` fails **and `/agents` succeeds**.

> Microsoft's docs describe the classic surface as deprecated with an announced sunset date, and the
> route is still present, un-removed, in the current `azure-rest-api-specs` TypeSpec for
> api-versions `2025-05-01`, `v1` and `2025-05-15-preview`. Treat "retired" as *announced but not
> yet enforced*, and let your own `200` be the evidence.

Confirm it in one call. Both surfaces take the same token and the same project endpoint, so the only
variable is the path:

```powershell
$token = az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv
$headers = @{ Authorization = 'Bearer ' + $token }
$project = 'https://enf-demo-ai.services.ai.azure.com/api/projects/cardgeo'

# the classic surface: this is where this repository's agents live
try { Invoke-RestMethod "$project/assistants?api-version=v1" -Headers $headers } catch { $_.ErrorDetails.Message }

# the v2 registry: expected to be empty unless you created agents with POST /agents
try { Invoke-RestMethod "$project/agents?api-version=v1" -Headers $headers } catch { $_.ErrorDetails.Message }
```

`./scripts/diagnose-foundry.ps1 -Prefix $prefix` does the same thing for both projects, alongside all
the control-plane checks, and tells you which of the two cases you are in.

If `/agents` answers and `/assistants` does not, the deployment is healthy and the **code** needs
migrating: agents become versioned resources created with a `definition` object, threads become
conversations, and runs become responses. That is a change to `provision.ps1`, `cleanup.ps1` and
`FoundryAgentClient`, not to any Bicep.

If **neither** answers, this is not the retirement — go back to
[If `provision.ps1` fails](#if-provisionps1-fails) and work through the account-level causes.

### Neither agent surface answers

Both `/assistants` and `/agents` fail, with some mixture of:

```text
500 { "error": { "code": "InternalServerError", "message": "Unable to get resource information." } }
408 { "error": { "code": "Timeout",             "message": "The operation was timeout." } }
```

The two alternate for the same URL between runs a minute apart, and that alternation is the most
useful signal in the whole failure. A client-side mistake — wrong path, wrong api-version, wrong
token audience, missing role — is **deterministic**, and it names itself: a wrong audience gives
401, a missing role gives 403, an unknown path gives 404. Timeouts and internal errors that come
and go are the service failing *behind* the gateway, after it has already accepted your request as
well-formed and authorised.

**In this repository the usual cause is a missing capability host** — the agent backend, which ARM
provisions as a separate sub-resource, so the account and the project both report `Succeeded` with
nothing behind them. Step 2 is therefore the one to check first. The rest of the list exists to
rule out the alternatives, roughly in order of how cheaply each can be dismissed.

A word on reading status codes while you work down it, because it is easy to get backwards: a
**4xx is good news**. It means the gateway resolved the host, read your token and made a
deliberate decision, so the front door is healthy and the fault is further in. Only a **408 or a
5xx** means the service broke trying to serve you.

**1. Does the account advertise an agents endpoint at all?** `properties.endpoint` (singular) is the
legacy `*.cognitiveservices.azure.com` host and is present on every Cognitive Services account, so
seeing only that proves nothing. The map that matters is `properties.endpoints` (plural):

```powershell
az cognitiveservices account show -n "$prefix-ai" -g "$prefix-platform" --query properties.endpoints -o json
```

The key to look for is literally **`AI Foundry API`**, and its value is the
`https://<subdomain>.services.ai.azure.com/api/projects/<project>` host the scripts call. Seeing
`properties.endpoint` report only `cognitiveservices.azure.com` is **normal and not a symptom** —
the two are genuinely different values, and Microsoft's own templates emit them as separate outputs.
What *would* be a symptom is `AI Foundry API` missing from the plural map. The diagnostic checks
this for you and says so in section 1.

**2. Is the agent backend provisioned?** This is the one mechanism by which every control-plane
state reads `Succeeded` while the data plane is dead. A **capability host** is a sub-resource that
tells Foundry Agent Service where to run and store agent data, and ARM provisions it *separately*
from the account and the project, so neither of their `provisioningState` values reflects it. There
is no `az` command for capability hosts, so this goes through `az rest`:

```powershell
$subId = az account show --query id -o tsv
$accountId = "/subscriptions/$subId/resourceGroups/$prefix-platform/providers/Microsoft.CognitiveServices/accounts/$prefix-ai"

az rest --method get --url "https://management.azure.com$accountId/capabilityHosts?api-version=2025-06-01"

foreach ($p in 'cardgeo', 'cardid') {
    az rest --method get --url "https://management.azure.com$accountId/projects/$p/capabilityHosts?api-version=2025-06-01"
}
```

Read the result together with step 3 below, because neither is conclusive alone:

| Capability hosts | Agents data plane | Reading |
| --- | --- | --- |
| present, `Succeeded` | fails | the backend exists; keep going down this list |
| present, `Failed`/`Creating` | fails | **this is the fault** — the backend never finished provisioning |
| none | fails, gateway answers | **this is the fault** — there is no backend to resolve the project to |
| none | works | fine; the service is using Microsoft-managed defaults |

That last row is why an empty list is not damning on its own: Microsoft's docs say an explicit
capability host is optional and the service falls back to Microsoft-managed resources without one.
But "Unable to get resource information" is precisely the gateway failing to resolve a project's
backing agent resources, so **no capability host plus a failing data plane is the whole story**.

`infra/modules/foundry-account.bicep` and `infra/modules/foundry-project.bicep` now create one each
— an account-level host that enables Agent Service, and a project-level host per team so each
team's agents and conversations stay in its own project. Neither declares any storage or
vector-store connections, which is what selects the Microsoft-managed resources behind them.

The two are not quite the same shape, which is worth knowing if you compare them against a sample
you find elsewhere: the **account** host takes `capabilityHostKind: 'Agents'`, and the **project**
host has no such property at all. Its schema is only the four connection lists, so its `properties`
is deliberately empty. Some Microsoft samples pass `capabilityHostKind` on the project host anyway;
the ARM spec's `ProjectCapabilityHost` does not define it.

If you deployed before these were added, redeploy and then re-provision:

```powershell
# $location and $prefix are the ones you set in step 3; re-set them if this is a new shell.
az deployment sub what-if `
  --location $location `
  --template-file infra/main.bicep `
  --parameters infra/main.parameters.json
```

The what-if output should show the two capability hosts as the only creates. If it wants to change
anything else, read it before continuing — that would mean the deployed infrastructure has drifted
from the templates for some other reason.

```powershell
./scripts/deploy-images.ps1 -SkipBuild
```

That redeploys the template without rebuilding anything. Use it rather than a bare
`az deployment sub create --parameters infra/main.parameters.json`, which would omit `imageTag` and
send every container app back to the placeholder image — see
[Record the agent ids](#record-the-agent-ids). If you have not built any images yet, there is
nothing to preserve and the plain deployment above is fine.

Then confirm the capability hosts exist before doing anything else — this is the check that tells
you the redeploy actually fixed the thing you were chasing:

```powershell
./scripts/diagnose-foundry.ps1 -Prefix $prefix
```

Section 4 should now list `<account>-caphost` and `<project>-caphost` as `Succeeded`, and section 6
should get a `200` instead of a 500. Only once section 6 answers is it worth running
`agents/provision.ps1` again — see
[Re-provisioning the agents after a redeploy](#re-provisioning-the-agents-after-a-redeploy).

Capability hosts **cannot be updated in place**. If one is stuck in `Failed`, delete it and let the
next deployment recreate it — note this destroys the agents in that project, which for us is
harmless because they are re-provisioned from YAML:

```powershell
az rest --method delete --url "https://management.azure.com$accountId/projects/cardgeo/capabilityHosts/cardgeo-caphost?api-version=2025-06-01"
```

The diagnostic reports all of this in section 4.

**3. Does anything at all answer on that host?** The model-inference route shares the gateway, the
DNS name and the account with the agents API, but not the agent backend. It needs a token for a
different audience:

```powershell
$csToken = az account get-access-token --resource https://cognitiveservices.azure.com --query accessToken -o tsv
$headers = @{ Authorization = 'Bearer ' + $csToken }
Invoke-RestMethod "https://$prefix-ai.services.ai.azure.com/openai/deployments?api-version=2024-10-21" -Headers $headers
```

Both a 200 and a clean **404** narrow the fault to the agent service — not every account serves
this particular route, and what matters is that something answered deliberately rather than broke.
A **408 or 5xx here** is the account or the region, and no change to which agent URL the scripts
call will help. The diagnostic runs this as the first probe in section 6 and draws that distinction
for you.

For a second opinion that shares none of our code, the CLI has its own preview data-plane command
that reaches the same surface through Microsoft's client stack:

```powershell
az cognitiveservices agent list -a "$prefix-ai" -g "$prefix-platform" --project-name cardgeo
```

If that fails the same way, the fault is definitively not in anything this repository does.

**4. Is the classic-agents kill switch set?** This will not explain a failing *list* — the tag blocks
creating and updating agents, threads and runs, and reads are explicitly unaffected — but it will
stop `provision.ps1` writing once the reads recover, so it is worth clearing now if it is set. It is
a plain account tag, so it survives redeployment:

```powershell
az cognitiveservices account show -n "$prefix-ai" -g "$prefix-platform" --query tags -o json
```

If that shows `"MS-AOAI-Feature-Assistants": "Disabled"`, clear it:

```powershell
$accountId = az cognitiveservices account show -n "$prefix-ai" -g "$prefix-platform" --query id -o tsv
az resource tag --ids $accountId --tags "MS-AOAI-Feature-Assistants=" --is-incremental
```

**5. Is the region unwell?** Reach for this only once steps 1-4 are clean, and be sceptical of it:
a regional fault should take the whole host down with it, so if step 3 got a deliberate answer from
the gateway, the region is probably fine and something specific to the agent backend is not.

That said, there is a real shared dependency worth knowing about. Sweden Central is the default
here, and if you also hit `ManagedEnvironmentNoAvailableCapacityInRegion` on the Container Apps
deployment, that is not a coincidence. Foundry Agent Service runs agents on Azure
Container Apps managed environments: the capability host has an `acaEnvironmentConnections` property
and an `enablePublicHostingEnvironment` flag, and network-injected Foundry accounts require a subnet
delegated to `Microsoft.App/environments`, which is the Container Apps managed-environment resource
type. `ManagedEnvironmentNoAvailableCapacityInRegion` is a `Microsoft.App` capacity error. A region
that cannot give you a managed environment plausibly cannot give the agent service one either, and
the gateway would report that as exactly what you are seeing: a failure to resolve the project's
backing resources.

To be clear about what is established and what is not: the architectural dependency is documented,
the causal link to your errors is inference.

The portal is the readable way to check: **portal.azure.com** → search **Service Health** →
**Service issues** in the left-hand menu, then set the **Region** filter to your region and the
**Service** filter to **Azure AI services** and **Azure OpenAI**. Widen **Time range** to the last
week, because an issue that has just been resolved drops off the default view.

The same list from the CLI, in two steps so the URL stays readable — note the backtick in
`` `$filter ``, which stops PowerShell expanding it as a variable:

```powershell
$subId = az account show --query id -o tsv
$since = (Get-Date).AddDays(-3).ToString('yyyy-MM-dd')
$url = "https://management.azure.com/subscriptions/$subId/providers/Microsoft.ResourceHealth/events" +
       "?api-version=2022-10-01&`$filter=properties/impactStartTime ge '$since'"

az rest --method get --url $url --query "value[].properties.{title:title,status:status,type:eventType}" -o table
```

**An empty result does not exonerate the region.** In previously reported Sweden Central Agent
Service outages — agents vanishing, 500s on create, 408s on read — Azure Status and Service Health
showed nothing at the time, and the incidents were confirmed only afterwards. So an empty list is
much weaker evidence than a populated one. If everything else on this page checks out, waiting an
hour and re-running the diagnostic is a legitimate next step rather than a cop-out.

If you need the demo working now rather than eventually, the mitigation Microsoft has given for
these incidents is to **fail the Foundry account over to another region**, typically West Europe.
That is not a small change here — see
[The region is out of capacity](#the-region-is-out-of-capacity) for why `location` cannot be moved
on its own.

**6. Only if the account itself is the outlier, recreate it.** If step 3 fails while the rest of the
subscription is healthy and Service Health is clear, the account is in a state the control plane
will not report. Deleting and redeploying it is safe here because the account holds no data we care
about — the agents are re-provisioned from YAML, and scans live in Storage and Cosmos, which are
separate resources. **Purging is what makes the subdomain reusable**; without it the new account
cannot take the same name for 48 hours:

```powershell
$location = az cognitiveservices account show -n "$prefix-ai" -g "$prefix-platform" --query location -o tsv
az cognitiveservices account delete -n "$prefix-ai" -g "$prefix-platform"
az cognitiveservices account purge -n "$prefix-ai" -g "$prefix-platform" -l $location
```

Then redeploy [step 3](#3-deploy-the-infrastructure) and re-provision the agents as described in
[Re-provisioning the agents after a redeploy](#re-provisioning-the-agents-after-a-redeploy).

If you would rather not wait on the region, deploying the whole demo elsewhere is a larger change
than it looks — see [The region is out of capacity](#the-region-is-out-of-capacity) for why the
`location` parameter cannot be changed on its own.

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

The worker passes these straight to the agent data plane as `assistant_id`, and that plane keys
agents by an `asst_…` value rather than by the name in the YAML. The template defaults to the
names, which fails every run, so record the real ids now.

Put them in `infra/main.parameters.json`, alongside the values already there:

```json
"boundaryAgentId": { "value": "asst_..." },
"mtgAgentId":      { "value": "asst_..." },
"pokemonAgentId":  { "value": "asst_..." }
```

Then apply them:

```powershell
./scripts/deploy-images.ps1 -SkipBuild `
  -BoundaryAgentId asst_... -MtgAgentId asst_... -PokemonAgentId asst_...
```

`-SkipBuild` means no image is rebuilt: the script keeps the tag the apps are already running and
changes only the agent ids.

**Do not do this with a bare `az deployment sub create`.** Two different settings revert if you do:

- `az containerapp update --set-env-vars` works until the next deployment, because every deployment
  rewrites the container app's environment from the template.
- A deployment that does not pass `imageTag` deploys its empty default, and an empty `imageTag`
  means *no images exist yet* — so all five apps go back to the
  `mcr.microsoft.com/k8se/quickstart` placeholder, and every route starts returning 404. The
  placeholder also listens on port 80 rather than 8080, so ingress moves with it.

`deploy-images.ps1` avoids both by replaying the previous deployment's parameters, `imageTag`
included, and overriding only what you asked it to change.

Check what the worker is actually using at any time:

```powershell
az containerapp show -g $platformRg -n "$prefix-worker" `
  --query "properties.template.containers[0].env[?starts_with(name,'ScanPipeline__')].{name:name,value:value}" -o table
```

If that prints `CardBoundaryAgent` rather than an `asst_…` value, the ids have been reverted — or
were never applied.

<a id="re-provisioning-the-agents"></a>
### Re-provisioning the agents after a redeploy

Anything that changes an MCP server's URL — redeploying the environments, or recreating them after
a failure — leaves the agents pointing at hostnames that no longer resolve. The fix is to run the
same `provision.ps1` commands again. It is an update, not a recreate: the script matches existing
agents **by name** and patches them in place, so the `asst_…` ids you recorded above stay valid and
nothing downstream needs changing.

Read the current URLs out of the deployment, then re-run both projects:

```powershell
$mcp = @{}
foreach ($o in 'geometryMcpServerUrls', 'identificationMcpServerUrls') {
  (az deployment sub show --name enfolderer-scan --query "properties.outputs.$o.value" -o json |
     ConvertFrom-Json) | ForEach-Object { $mcp[$_.name] = $_.url }
}
$mcp   # sanity check: five entries, each an https://...azurecontainerapps.io URL

./agents/provision.ps1 -ProjectEndpoint $geo -Path ./agents/cardgeo -McpServerUrl $mcp

./agents/provision.ps1 -ProjectEndpoint $id -Path ./agents/cardid -McpServerUrl $mcp `
  -ConnectedAgentId @{ 'cardgeo/CardBoundaryAgent' = $boundaryId }
```

`$geo`, `$id` and `$boundaryId` come from [Resuming in a new shell](#resuming-in-a-new-shell) and
from the `cardgeo` run above. Team A's project first, for the same reason as the first time: Team B's
orchestrator needs the boundary agent's id and cannot look it up itself.

Re-provisioning is safe to repeat. If you are unsure whether it is needed, run it — an agent that is
already correct is patched with identical content.

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

One script does the build and the redeploy that follows it, and then checks the result. Run it from
the repository root:

```powershell
./scripts/deploy-images.ps1
```

It picks a fresh tag from the current UTC time, builds all five images, redeploys the template with
that tag, and finishes by printing the image each app is running and calling the API's `/healthz`.
Expect it to take ten to fifteen minutes, nearly all of it in the builds.

Use it every time you change code, not just the first time. It is the answer to "how do I deploy
what I just wrote".

Two things it does that are easy to get wrong by hand. It always picks a **new** tag, because ARM
and `az containerapp update` both diff the template — re-pushing the same tag produces no new
revision and your change silently does not go live. And it **replays the parameters of the previous
deployment** rather than reading `infra/main.parameters.json`, which is checked in holding
placeholder object ids; deploying from that file when you supplied the real ids on the command line
would revoke the role assignments the demo is about. Pass `-UseParametersFile` if your copy of that
file does hold your real values and you would rather use it.

To roll back, point the apps at a tag already in the registry without rebuilding:

```powershell
./scripts/deploy-images.ps1 -SkipBuild -Tag v1
```

The rest of this section is what the script does, if you would rather run it yourself or need to
adapt it.

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

`deploy-images.ps1` above already did this. Read on only if you ran the build by hand.

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
foreach ($entry in $mcp.GetEnumerator()) {
  $url = $entry.Value -replace '/mcp$', '/healthz'
  try {
    "{0,-26} {1}" -f $entry.Key, (Invoke-RestMethod $url -TimeoutSec 20).status
  }
  catch {
    "{0,-26} FAILED: {1}" -f $entry.Key, $_.Exception.Message
  }
}
```

All three should report `ok`. The `try` has to be a statement in its own right: PowerShell has no
try *expression*, so folding it into the output string as `"..." + (try { ... })` fails to parse
with `The term 'try' is not recognized as a name of a cmdlet`.

**A 404 from every server means they are running the placeholder image**, not that `/healthz` is
missing — `McpServerHost` maps it unconditionally, so a deployed server always answers it. The
usual cause is a deployment that did not pass `imageTag`; see
[Record the agent ids](#record-the-agent-ids). Confirm by looking at what each app is running:

```powershell
$prefix = 'enf-demo'   # whatever you used for namePrefix
foreach ($rg in "$prefix-platform", "$prefix-cardgeo", "$prefix-cardid") {
  az containerapp list -g $rg `
    --query "[].{Name:name, Image:properties.template.containers[0].image}" -o table
}
```

`mcr.microsoft.com/k8se/quickstart` in that column is the placeholder. Put the real images back
with:

```powershell
./scripts/deploy-images.ps1
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

Then add `"mcpAudience": { "value": "api://<prefix>-mcp" }` (the same identifier URI you just set) to
`infra/main.parameters.json` and re-run the deployment from step 3.

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

Anything still showing `mcr.microsoft.com/k8se/quickstart` is on the placeholder. Either it never
got a deployment with `imageTag` set, or a later deployment omitted `imageTag` and reverted it —
both are fixed by `./scripts/deploy-images.ps1`.

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

The **worker** is a separate identity with a different set, and a job that fails in
`DetectingBoundaries` with `AuthorizationPermissionMismatch` is usually this one rather than the
API's: the first thing a job does is read back the photograph the API just wrote. Its three
assignments are **Storage Blob Data Reader** on `scans`, **Storage Blob Data Contributor** on
`crops` and **Storage Queue Data Contributor** on the account — reader on `scans`, so the worker
can never overwrite the original, and contributor only on the crops it produces:

```powershell
$workerPrincipal = az identity show -g $platformRg -n "$prefix-worker-id" --query principalId -o tsv
az role assignment list --assignee $workerPrincipal --all `
  --query "[].{Role:roleDefinitionName, Scope:scope}" -o table
```

`infra/modules/data-rbac.bicep` creates all three. If one is missing, redeploy rather than granting
it by hand, so the template stays the single description of who can reach what:

```powershell
./scripts/deploy-images.ps1 -SkipBuild
```

Granting by hand does more than duplicate the template: it **blocks** it. See "If the deployment
fails with RoleAssignmentExists" below.

**Read the role names in that table, not just the scopes.** A row reading plain **Reader** on the
`scans` container is not Storage Blob Data Reader and grants no blob access at all: Reader is a
control-plane role, with `*/read` under `actions` and nothing under `dataActions`. It lets the
identity see that the container exists, so the listing looks correct while every read fails with
`AuthorizationPermissionMismatch`. Azure's built-in roles pair off into control-plane and data-plane
versions with similar names — Reader and Storage Blob Data Reader, Contributor and Storage Blob Data
Contributor — and only the data ones reach the bytes.

Deployments before this was corrected granted the control-plane Reader to the worker and to Team A.
Redeploying adds the right assignment beside the old one rather than replacing it, because an
assignment's name is derived from the role definition id. Clear the leftovers:

```powershell
./scripts/cleanup.ps1 -Prefix $prefix -WhatIf     # lists what would go
./scripts/cleanup.ps1 -Prefix $prefix -Confirm:$false
```

The worker names the refused principal in its own log, the same way the API does, so compare the
`oid` there with `$workerPrincipal` above:

```powershell
az containerapp logs show -g $platformRg -n "$prefix-worker" --tail 50
```

A newly granted blob role takes a few minutes to propagate, and a replica that started before it
was honoured keeps the refusal cached. Restart it rather than waiting:

```powershell
$revision = az containerapp revision list -g $platformRg -n "$prefix-worker" `
  --query "[?properties.active].name | [0]" -o tsv
az containerapp revision restart -g $platformRg -n "$prefix-worker" --revision $revision
```

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

### If Cosmos returns 403

A refusal from Cosmos rather than Storage looks like this:

```text
Azure Cosmos DB returned 403/0 for the identity oid=776dde8b-… appid=… tid=…
Either that principal lacks the Cosmos DB Built-in Data Contributor role on the
account, or the account was unreachable over the network.
```

Cosmos reports both faults with the same status **and the same substatus**, so the 502 cannot tell
you which it was. Two things narrow it down: the **oid**, because a common cause is the app
presenting a *different* identity than the one the role was granted to; and the API's own log,
which carries Cosmos's original message and does distinguish them.

Cosmos has **two separate role systems**, and this is the one people do not expect. The familiar
`az role assignment` commands manage *control-plane* roles — who may rename the account, change its
throughput, read its keys. Reading and writing documents is governed by a second, parallel set of
assignments stored on the account itself, listed with `az cosmosdb sql role assignment`. Subscription
Owner grants you nothing in that second system, and `az role assignment list` will never show it, so
an identity can look fully privileged and still be refused.

**The portal cannot show you these either.** The account's *Access control (IAM)* blade lists
control-plane assignments only, so a correctly configured identity appears nowhere in it. Do not
read that absence as a missing role — the CLI below is the only view of the data plane.

`infra/modules/data-rbac.bicep` creates the two assignments the pipeline needs, so a complete
deployment has them. Check what is actually there:

```powershell
$platformRg   = "$prefix-platform"
# Derived from the prefix, so read it back rather than composing it.
$cosmosName   = az cosmosdb list -g $platformRg --query "[0].name" -o tsv
$apiPrincipal    = az identity show -g $platformRg -n "$prefix-api-id"    --query principalId -o tsv
$workerPrincipal = az identity show -g $platformRg -n "$prefix-worker-id" --query principalId -o tsv

az cosmosdb sql role assignment list -g $platformRg -a $cosmosName `
  --query "[].{Principal:principalId, Role:roleDefinitionId, Scope:scope}" -o table

"api:    $apiPrincipal"
"worker: $workerPrincipal"
```

Both principals must appear in that table. Note it lists `principalId` — the identity's **object
id** — not its client id, so compare against the two values printed underneath.

If a principal is missing, grant it. `00000000-0000-0000-0000-000000000002` is the built-in **Cosmos
DB Built-in Data Contributor** definition; it is the same id in every Cosmos account:

```powershell
$cosmosId = az cosmosdb show -g $platformRg -n $cosmosName --query id -o tsv

foreach ($p in $apiPrincipal, $workerPrincipal) {
  az cosmosdb sql role assignment create -g $platformRg -a $cosmosName `
    --role-definition-id "$cosmosId/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002" `
    --principal-id $p --scope $cosmosId
}
```

Re-running this for a principal that already has the role creates a **second, identical
assignment** rather than failing — so the listing will show the same principal twice. Duplicates
are harmless (permissions are a union), but they are a sign you did not need to run it. Remove one
with `az cosmosdb sql role assignment delete -g $platformRg -a $cosmosName --role-assignment-id <name>`
if the clutter bothers you.

A genuinely new assignment takes a minute or two to be honoured, so wait before retrying.

### The principal is listed and it is still refused

This is the case to expect, because `infra/modules/data-rbac.bicep` already creates both
assignments: a complete deployment has them, so finding them present proves only that the template
worked. Granting them again changes nothing.

Work through these in order.

**1. Is there a private endpoint for Cosmos?** This is the one that does not look like a
permissions problem but reports itself as one. A Cosmos account with **public network access
disabled and no private endpoint** is unreachable from everywhere, and the refusal it returns is a
`403` — the same status and substatus as a missing role, with no hint that the cause is the network
rather than RBAC. Check both halves:

```powershell
az cosmosdb show -g $platformRg -n $cosmosName --query publicNetworkAccess -o tsv

az network private-endpoint list -g $platformRg `
  --query "[].{Name:name, Target:privateLinkServiceConnections[0].privateLinkServiceId}" -o table
```

If the first prints `Disabled` and no endpoint in the second targets the Cosmos account, that is the
fault. `infra/modules/network.bicep` creates it — along with the `privatelink.documents.azure.com`
zone and its VNet link, all three of which are needed — so redeploy:

```powershell
./scripts/deploy-images.ps1 -SkipBuild
```

A deployment that predates that endpoint will not have one, and a subscription policy that disables
public access on new accounts produces exactly this state. Confirm the fix from inside a replica,
where a `10.x` address means DNS reached the private zone:

```powershell
az containerapp exec -g $platformRg -n "$prefix-api" `
  --command "getent hosts $cosmosName.documents.azure.com"
```

**2. Does the oid in the error match the API identity?** This is why the error names it.

```powershell
az identity show -g $platformRg -n "$prefix-api-id" --query principalId -o tsv
```

If it differs, the app is presenting a different identity than the one that was granted. Check that
`ScanPlatform__ManagedIdentityClientId` holds the API identity's **client id** — a different GUID
from the object id above, and the usual mix-up:

```powershell
az identity show -g $platformRg -n "$prefix-api-id" --query clientId -o tsv

$env = az containerapp show -g $platformRg -n "$prefix-api" `
  --query "properties.template.containers[0].env" -o json | ConvertFrom-Json
($env | Where-Object name -eq 'ScanPlatform__ManagedIdentityClientId').value
```

**3. Restart the revision.** A replica that started before the assignment existed caches both its
token and the refusal, and will keep returning 403 long after the grant is in place. Restarting is
the only way to clear it:

```powershell
$revision = az containerapp revision list -g $platformRg -n "$prefix-api" `
  --query "[?properties.active].name | [0]" -o tsv
az containerapp revision restart -g $platformRg -n "$prefix-api" --revision $revision
```

**4. Read the API's own log.** The exception is logged in full, and Cosmos's own message says which
of the two it was — a network block names the client IP and the public internet, an RBAC refusal
names the principal. Neither distinction survives into the 502:

```powershell
az containerapp logs show -g $platformRg -n "$prefix-api" --tail 100
```

**5. Confirm it is the account you think.** The endpoint the API was configured with must be the
account you granted on:

```powershell
($env | Where-Object name -eq 'ScanPlatform__CosmosEndpoint').value
"expected: https://$cosmosName.documents.azure.com:443/"
```

### If Foundry returns 401 PermissionDenied

A job that fails in `DetectingBoundaries` or `Identifying` with

```text
Foundry authorization failure (401). Foundry request POST /files against
https://<account>.services.ai.azure.com/api/projects/cardgeo failed with 401:
{"error":{"code":"PermissionDenied","message":"Principal does not have access to API/Operation."}}
```

is the worker being refused by a Foundry **project**. Note the status: Foundry answers a missing
project role with `401`, not the `403` that storage and Cosmos use for the same situation, and the
message names neither the principal nor the role. The endpoint in the message is the useful part —
it names which project refused, and therefore which boundary was crossed.

The worker needs **Foundry User** on both projects: on `cardid` to run Team B's identification
agents, and on `cardgeo` to run Team A's boundary agent. Both come from
`infra/modules/foundry-invoke-access.bicep`. List what it actually holds:

```powershell
$workerPrincipal = az identity show -g $platformRg -n "$prefix-worker-id" --query principalId -o tsv
az role assignment list --assignee $workerPrincipal --all `
  --query "[?contains(scope, 'CognitiveServices')].{Role:roleDefinitionName, Scope:scope}" -o table
```

Each scope must end in `/projects/cardgeo` or `/projects/cardid`. A grant on the **account** —
a scope ending at `…/accounts/<name>` — would work, and is wrong: it reaches every project in the
account, which is the boundary this demo exists to show. If either row is missing, redeploy rather
than granting by hand:

```powershell
./scripts/deploy-images.ps1 -SkipBuild
```

Only the `cardgeo` row is missing deliberately, when the deployment was last run with
`-GrantGeometryAccess $false`. That is demo scenario 1 in `docs/foundry-demo.md`; restore it with
`./scripts/deploy-images.ps1 -SkipBuild -GrantGeometryAccess $true`.

If both rows are present and the call is still refused, the agent id is the next suspect rather
than the role — see "Record the agent ids" in step 4. A 401 means the project refused the
principal; a run that starts and then fails names the agent instead.

### If a run fails with tool_server_error

```text
Agent 'asst_…' run ended with status 'failed': {
  "code": "tool_server_error",
  "message": "MCP Connector error. Http status: 424, error details: Error retrieving tool
   list from MCP server: 'imaging'. Http status code: 424 (Failed Dependency)"
}
```

This failure is the opposite of the ones above: the worker is fully authorised — the upload, the
thread and the run all returned `200` — and the *agent* then failed because Foundry could not reach
a tool server attached to it. The `424` is Foundry reporting that its own outbound call to the MCP
server did not produce a tool list.

Read the server label in the message first; it tells you whether that tool should be there at all.
`CardBoundaryAgent` declares `tools: []`, so a label such as `imaging` on it means the live agent
is **stale** — provisioned from an earlier definition that did attach the imaging server. Check what
the agent actually has:

```powershell
$geo   = az deployment sub show --name enfolderer-scan --query properties.outputs.geometryProjectEndpoint.value -o tsv
$token = az account get-access-token --resource 'https://ai.azure.com' --query accessToken -o tsv
$agent = Invoke-RestMethod -Uri "$geo/assistants/asst_5YS2jhx13zVgso1f5yE6Dm9c?api-version=v1" `
  -Headers @{ Authorization = 'Bearer ' + $token }
$agent.tools | ConvertTo-Json -Depth 5
```

Anything other than `[]` is left over. Re-provision to clear it — agents are matched by name, so
the id is kept and the worker needs no change:

```powershell
./agents/provision.ps1 -ProjectEndpoint $geo -Path ./agents/cardgeo
```

Note no `-McpServerUrl`: that is what makes the tool list empty. Re-run the `Invoke-RestMethod`
above to confirm `tools` is now `[]`, then scan again.

If the label *is* one the YAML asks for — `catalogue` on an identification agent — the server
itself is unreachable. Check it answers at all, from your own machine:

```powershell
$mcpUrl = az containerapp show -g "$prefix-cardid" -n "$prefix-mcp-cardcatalog-mtg" `
  --query properties.configuration.ingress.fqdn -o tsv
Invoke-RestMethod -Uri "https://$mcpUrl/healthz"
```

A `404` there means the app is still on the placeholder image: run `./scripts/deploy-images.ps1`.
Anything else is the container failing to start — `./scripts/diagnose-containerapps.ps1 -Prefix
$prefix` reports which.

> **Why a stale tool survives a re-provision in an older checkout.** The data plane treats an agent
> update as a merge, so a key left out of the payload keeps its current value. `provision.ps1` used
> to omit `tools` whenever a definition produced none, which meant an agent that had stopped
> declaring a tool kept it forever and no amount of re-provisioning removed it. It now always sends
> the list when the YAML has a `tools` key, and warns when a tool it could not resolve is about to
> be removed.

### If the deployment fails with RoleAssignmentExists

```text
{"code":"ResourceDeploymentFailure","target":"…/deployments/data-rbac", …
 {"code":"RoleAssignmentExists","message":"The role assignment already exists.
  The ID of the existing role assignment is 04d6663e9d994c1db26146747a9a64c2."}}
```

This is what granting one of the template's roles by hand costs you. Azure identifies a role
assignment by **principal + role + scope**, but *names* it with a GUID chosen by whoever created
it. The template derives that name deterministically with `guid()`; a grant made in the portal or
with `az role assignment create` gets a random one. So ARM asks Azure to create an assignment that
already exists under a different name, and Azure refuses rather than adopting it — which fails the
whole deployment, including the parts that have nothing to do with RBAC.

Nothing is lost by deleting the hand-made assignment: the template grants the same role at the same
scope, under its own name, as soon as the collision is gone.

The message ends with the assignment's **name** — the last segment of its resource id — so look it
up first. Match on the id rather than on a `name` property, which not every version of `az` returns.
Filtering happens in PowerShell rather than in `--query`, because `az` on Windows is a `.cmd`
wrapper that mangles a JMESPath expression containing `?`:

```powershell
$name = "04d6663e9d994c1db26146747a9a64c2"   # from the message
$all = az role assignment list --all -o json | ConvertFrom-Json
$doomed = @($all | Where-Object { ($_.id -split '/')[-1] -eq $name })

# Confirm it is one of the template's before deleting it.
$doomed | Select-Object roleDefinitionName, principalId, scope | Format-List

az role assignment delete --ids $doomed.id --yes
```

If `$doomed` is empty, delete by principal, role and scope instead — that identifies the assignment
the way Azure does, and never depends on the name. List what the worker holds:

```powershell
$worker = az identity show -g enf-demo-platform -n enf-demo-worker-id --query principalId -o tsv
az role assignment list --all --assignee $worker -o json | ConvertFrom-Json |
  Select-Object roleDefinitionName, scope | Format-List
```

Every row whose role and scope the template also grants — see the table in
[If Azure returns 403](#if-azure-returns-403) — is a collision. Remove each one:

```powershell
az role assignment delete --assignee $worker `
  --role "Storage Blob Data Reader" `
  --scope "/subscriptions/<sub>/resourceGroups/enf-demo-platform/providers/Microsoft.Storage/storageAccounts/<account>/blobServices/default/containers/scans"
```

Then re-run the deployment:

```powershell
./scripts/deploy-images.ps1 -SkipBuild
```

If it fails again naming a different id, repeat — each hand-made grant collides separately. The
script recognises this error and prints these steps for you.

### If the deployment records no parameters

```text
deploy-images.ps1: The property 'Properties' cannot be found on this object.
```

A failed deployment **replaces** the successful one of the same name, and ARM stores a failure with
`"parameters": null` and no outputs. So one bad run — a `RoleAssignmentExists` collision, say —
destroys the record `deploy-images.ps1` replays, and the script can no longer find the prefix, the
registry or your object ids.

The script now keeps its own copy under `.deploy-images/` (gitignored) every time it reads a good
deployment, and falls back to it. That copy only exists from its first successful run onwards, so if
you hit this before then, recover once from the parameters file. Collect the real values:

```powershell
$prefix = 'enf-demo'
az containerapp show -g "$prefix-platform" -n "$prefix-api" -o json |
  ConvertFrom-Json |
  ForEach-Object { $_.properties.template.containers[0].env } |
  Where-Object name -eq 'AzureAd__ClientId' |
  Select-Object -ExpandProperty value           # apiClientId

az ad group show --group "Enfolderer Team A (Geometry)"       --query id -o tsv   # teamAGroupObjectId
az ad group show --group "Enfolderer Team B (Identification)" --query id -o tsv   # teamBGroupObjectId
```

Put them, the prefix and the region into `infra/main.parameters.json` — it ships with placeholder
object ids, which is why the script normally avoids it. `location` must be the region the deployment
was created in; a subscription deployment cannot change it. Also set the agent ids, since the file's
defaults are agent *names*, which the data plane will not accept:

```powershell
$p = Get-Content -Raw infra/main.parameters.json | ConvertFrom-Json
$p.parameters.namePrefix.value         = 'enf-demo'
$p.parameters.location.value           = 'swedencentral'
$p.parameters.apiClientId.value        = $apiClientId
$p.parameters.teamAGroupObjectId.value = $teamA
$p.parameters.teamBGroupObjectId.value = $teamB
$p.parameters.boundaryAgentId.value    = '<asst_… for the boundary agent>'
$p.parameters.mtgAgentId.value         = '<asst_… for the MTG agent>'
$p | ConvertTo-Json -Depth 10 | Set-Content infra/main.parameters.json -Encoding utf8
```

Then deploy once from it, naming the tag already in the registry:

```powershell
az acr repository show-tags --name "<registry>" --repository enfolderer/api -o table
./scripts/deploy-images.ps1 -SkipBuild -Tag "<tag>" -UseParametersFile
```

`-UseParametersFile` deliberately ignores both the failed deployment and the local cache, so it is
the one switch that always works here. It refuses to run while any of the three ids is still an
all-zero GUID: deploying those would point every role assignment at a principal that does not exist,
and the deployment would report success while nothing could sign in.

That run records the parameters in ARM again and saves the local copy, so every later run can go
back to replaying them. Revert your edits afterwards; those object ids should not be committed:

```powershell
git checkout infra/main.parameters.json
```

## 7. Point the desktop app at the deployment

Create `aiconfig.txt` beside `Enfolderer.App.exe` (the app writes a template on the first scan if
the file is missing):

```powershell
$tenantId = az account show --query tenantId -o tsv
# The FQDN has a generated suffix, so read it back rather than composing it.
$apiUrl   = az deployment sub show --name enfolderer-scan `
  --query properties.outputs.apiUrl.value -o tsv

# The two app ids were set in step 2. If this is a new shell they are empty, so
# read them back from Entra by display name rather than assuming they survived.
$apiAppId    = az ad app list --display-name "Enfolderer Scan API" --query "[0].appId" -o tsv
$clientAppId = az ad app list --display-name "Enfolderer Desktop"  --query "[0].appId" -o tsv

foreach ($v in 'apiUrl','apiAppId','clientAppId') {
  if (-not (Get-Variable $v -ValueOnly)) { throw "$v is empty - see the note below this block." }
}

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

If the guard throws, the named variable is empty and the file would have been written with a blank
value — which the desktop app rejects with *Missing or empty 'client_id' in aiconfig.txt*. An empty
`$apiAppId` or `$clientAppId` means the lookup found no app with that display name: either step 2
has not been run, or the app was registered under a different name. List what is there with

```powershell
az ad app list --query "[].{name:displayName, appId:appId}" -o table
```

and either adjust the `--display-name` strings above to match, or go back to step 2 and create the
registrations. An empty `$apiUrl` means the deployment name is not `enfolderer-scan`; list yours
with `az deployment sub list --query "[].name" -o table`.

A trailing `/` on `api_base_url` is optional — the app normalises it either way.

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

## 8. Put API Management in front — *optional*

The API works without this. APIM is here for architectural completeness: it is the gateway tier a
real deployment would have, and it gives the demo somewhere to point at when someone asks where
rate limiting, quotas or a developer portal would live.

Be clear about what it is and is not. APIM sits **in front of** the API container app; it does not
replace it. The four endpoints hold a state machine and relay up to 64 MB of image bytes into a
storage account with no public endpoint, which is application code, not gateway policy.

Use the **Consumption** tier. It bills per call — roughly $3.50 per million with the first million
each month free — so for a demo it is effectively free, and it provisions in a few minutes rather
than the 30 to 45 a dedicated tier takes. It cannot join a VNet, which does not matter here: the
container app's ingress is already public, so APIM reaches it the same way the desktop client does.

> A tier that *could* reach private storage directly — Standard v2, around $700 a month — is only
> needed if you try to make APIM replace the API rather than front it. Don't.

Create the instance, using the same region as everything else so the gateway is not calling across
regions:

```powershell
$prefix   = 'enfolderer'
$rg       = "$prefix-platform"
$location = az group show -n $rg --query location -o tsv

az apim create `
  --name "$prefix-apim" `
  --resource-group $rg `
  --publisher-name 'Enfolderer Demo' `
  --publisher-email 'you@example.com' `
  --sku-name Consumption `
  --location $location
```

Then import the API, pointing it at the container app's ingress:

```powershell
$apiFqdn = az containerapp show -g $rg -n "$prefix-api" `
  --query properties.configuration.ingress.fqdn -o tsv

az apim api create `
  --resource-group $rg --service-name "$prefix-apim" `
  --api-id scan --path scan --display-name 'Scan API' `
  --service-url "https://$apiFqdn" --protocols https
```

This prints `api_id is not a known attribute of class ... and will be ignored`. **That is a
cosmetic CLI bug, not a failure** ([azure-cli#27978](https://github.com/Azure/azure-cli/issues/27978)):
the CLI passes `api_id` to an SDK model that has no such field, the SDK warns, and the API is
created anyway — `--api-id` is still required, so there is nothing to change. Confirm rather than
trust it:

```powershell
az apim api show --resource-group $rg --service-name "$prefix-apim" `
  --api-id scan --query "{name:displayName, path:path, backend:serviceUrl}" -o table
```

**An API with no operations returns 404 for everything.** `az apim api create` registers the API
and its backend but defines no routes, and APIM only forwards a request that matches an operation —
it is not a transparent proxy. This API has no OpenAPI document to import from, so the operations
have to be declared. Three wildcard operations, one per verb the client uses, cover all four routes:

```powershell
$ops = @(
  @{ id = 'wildcard-get';  method = 'GET';  name = 'Any GET'  }
  @{ id = 'wildcard-post'; method = 'POST'; name = 'Any POST' }
  @{ id = 'wildcard-put';  method = 'PUT';  name = 'Any PUT'  }
)
foreach ($op in $ops) {
  az apim api operation create `
    --resource-group $rg --service-name "$prefix-apim" --api-id scan `
    --operation-id $op.id --display-name $op.name `
    --method $op.method --url-template '/*'
}
```

A single operation cannot cover every verb: APIM has no wildcard *method*, only a wildcard path, so
one operation per verb is the minimum. `/*` matches the whole remaining path including slashes, so
`jobs/{id}/content` is covered without declaring it.

By default APIM requires a subscription key, which the desktop app does not send. The app already
presents an Entra token that the API itself validates, so turn the key off rather than adding a
second credential:

```powershell
az apim api update `
  --resource-group $rg --service-name "$prefix-apim" `
  --api-id scan --subscription-required false
```

Smoke-test the gateway before changing the client. `/healthz` is the only route that does not
require a token, which makes it a clean test of routing alone:

```powershell
$gateway = az apim show -g $rg -n "$prefix-apim" --query gatewayUrl -o tsv
Invoke-RestMethod -Uri "$gateway/scan/healthz"
```

`status : ok` means the gateway, the wildcard operations and the backend URL are all correct.

Read a 404 by its body, because two different things return one:

| Response | Who answered | Cause |
| --- | --- | --- |
| JSON, `{"statusCode": 404, "message": "Resource not found"}` | APIM | No operation matched — add the wildcard operations above |
| Plain text, `404 page not found` | the backend | APIM forwarded correctly; the container app has no such route |

The second one is the more confusing, because it means the gateway is working. Check what the app
is actually running before looking anywhere else:

```powershell
az containerapp show -g $rg -n "$prefix-api" `
  --query "properties.template.containers[0].image" -o tsv
```

If that prints `mcr.microsoft.com/k8se/quickstart:latest`, the app never left the placeholder from
step 3 and is not this project's code at all. That image is a Go sample whose 404 body is exactly
`404 page not found`. Go back and run step 5.

Note also that the backend has no `/scan` prefix of its own: APIM strips it before forwarding, so
testing the container app directly means `https://<app-fqdn>/healthz`, not `/scan/healthz`.

Repoint the desktop app at the gateway. The `--path scan` above means the gateway prefixes every
route, so the base URL ends in `/scan`:

```powershell
# Set api_base_url in aiconfig.txt to this value:
"$gateway/scan/"
```

The trailing slash matters. The client resolves `jobs` against this base address, so without it
`.../scan` would resolve to `.../jobs` and drop the prefix.

Three things to check once it is in place. APIM's default forwarding preserves the `Authorization`
header, so the API still sees the caller's token — if every call starts returning 401, that is the
first thing to confirm. The default HTTP timeout is well under the time a full scan takes; the
desktop app polls `GET /jobs/{id}` rather than holding a connection open, so this does not bite,
but it would if you ever made the submit call synchronous.

The third is the one that would actually stop a scan. **Do not add a policy that reads the request
body on the upload route.** Consumption tier buffers at 2 MB once a policy inspects the body, and
`PUT /jobs/{id}/content` carries up to 64 MB. With no body-reading policy the gateway streams the
request through and the size is not a problem, which is why the import above adds no policies at
all. `validate-content`, `set-body` and anything calling `context.Request.Body` would each break
the upload while leaving the other three routes working — a confusing failure worth avoiding.

To remove it again, which costs nothing to do:

```powershell
az apim delete --name "$prefix-apim" --resource-group $rg --yes
```

Then set `api_base_url` in `aiconfig.txt` back to the container app URL from step 7.

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
`CardBoundaryAgent` no longer has any tools — it used to call a tool that handed back a blob URL,
which no longer exists — and because every MCP URL changed when the servers moved off
`azurewebsites.net`. And
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
Delegator` grants (nothing signs a blob URL any more), any account-wide `Storage Blob Data Contributor`
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
GB ingested, which at demo volume is pennies. If you added APIM in step 8, the Consumption tier
bills per call and a demo will not approach the free million, so it adds nothing measurable.

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
