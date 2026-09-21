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
- The **.NET 8 SDK**, for steps 5 and 6. A machine with only the runtime installed still has a
  working `dotnet` command, so the missing SDK shows up as `dotnet publish` reporting
  `The application 'publish' does not exist`. Check with `dotnet --list-sdks`, which must list an
  `8.x` entry.
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

This creates three resource groups (`<prefix>-platform`, `<prefix>-cardgeo`, `<prefix>-cardid`),
the four managed identities, the storage account with the `scans` and `crops` containers and the
`scan-jobs` queue, the Cosmos account with the `jobs` container, one Foundry account holding both
the `cardgeo` and `cardid` projects and a shared `gpt-4o` deployment, the API and worker App
Services, and all the role assignments.

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

Re-running the deployment is safe. ARM templates are declarative, so a second `az deployment sub
create` reconciles whatever already exists rather than duplicating it; there is no need to delete
the resource groups after a partial failure.

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
./agents/provision.ps1 -ProjectEndpoint $id -Path ./agents/cardid `
  -Only MtgCardIdAgent, PokemonCardIdAgent, OrchestratorAgent
```

Drop `PokemonCardIdAgent` from `-Only` to provision MTG alone. The worker maps game to agent by
name, so a card the boundary agent reports as Pokemon simply comes back unidentified rather than
failing the job — and adding the game later is one more name in that list.

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
az webapp config appsettings set `
  --resource-group $platformRg --name "$prefix-worker" --settings `
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

### Publish them

```powershell
foreach ($server in 'Imaging', 'CardCatalog.Mtg', 'CardCatalog.Pokemon') {
  $site = switch ($server) {
    'Imaging'             { 'mcp-imaging' }
    'CardCatalog.Mtg'     { 'mcp-cardcatalog-mtg' }
    'CardCatalog.Pokemon' { 'mcp-cardcatalog-pokemon' }
  }
  $rg = if ($server -eq 'Imaging') { "$prefix-cardgeo" } else { "$prefix-cardid" }

  dotnet publish "src/Enfolderer.Ai.Mcp.$server" -c Release -o "$stage/$site"
  if ($LASTEXITCODE -ne 0) { throw "publish failed for $server" }

  Compress-Archive -Path "$stage/$site/*" -DestinationPath "$stage/$site.zip" -Force
  az webapp deploy --resource-group $rg --name "$prefix-$site" --src-path "$stage/$site.zip" --type zip
}
```

The same `/*` caveat as step 6 applies. `$stage` is defined there; if you are doing step 5 first,
run its first two lines to create it.

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
Invoke-RestMethod "https://$prefix-mcp-imaging.azurewebsites.net/healthz"
```

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
  -Uri "https://$prefix-mcp-cardcatalog-mtg.azurewebsites.net/mcp" `
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
az webapp config appsettings set --resource-group "$prefix-cardid" `
  --name "$prefix-mcp-cardcatalog-pokemon" --settings POKEMONTCG_API_KEY="<your key>"
```

## 6. Deploy the API and worker

```powershell
$stage = Join-Path $env:TEMP "enfolderer-deploy"
New-Item -ItemType Directory -Force -Path $stage | Out-Null

dotnet publish src/Enfolderer.Ai.Api    -c Release -o "$stage/api"
if ($LASTEXITCODE -ne 0) { throw "publish failed for the API" }
dotnet publish src/Enfolderer.Ai.Worker -c Release -o "$stage/worker"
if ($LASTEXITCODE -ne 0) { throw "publish failed for the worker" }

Compress-Archive -Path "$stage/api/*"    -DestinationPath "$stage/api.zip"    -Force
Compress-Archive -Path "$stage/worker/*" -DestinationPath "$stage/worker.zip" -Force

az webapp deploy --resource-group $platformRg --name "$prefix-api" `
  --src-path "$stage/api.zip" --type zip
az webapp deploy --resource-group $platformRg --name "$prefix-worker" `
  --src-path "$stage/worker.zip" --type zip
```

The `$LASTEXITCODE` checks stop a failed publish from cascading. Without them PowerShell carries
on, `Compress-Archive` complains that the output folder does not exist, and `az webapp deploy`
complains about a missing zip — three errors describing one failure, with the real cause scrolled
off the top.

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

## Costs and teardown

The Basic App Service plan, the provisioned-throughput Cosmos container, and the `gpt-4o`
deployment all bill while they exist. Job documents self-expire after seven days via the container
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
