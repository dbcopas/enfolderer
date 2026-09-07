# Setting up the Azure resources

Step-by-step deployment of the scan pipeline. The Bicep under `infra/` does most of the work; the
manual steps are the two Entra app registrations, the Foundry agents, and the desktop client's
config file, none of which can be created from an ARM template.

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

```bash
az login
az account set --subscription "<subscription-id>"
```

## 1. Create the owner groups

The two Foundry projects are owned by different Entra groups. This is what stops Team B editing
Team A's agent, so use two real groups even in a demo tenant.

```bash
az ad group create --display-name "Enfolderer Team A (Geometry)"       --mail-nickname enfolderer-team-a
az ad group create --display-name "Enfolderer Team B (Identification)" --mail-nickname enfolderer-team-b

az ad group show --group "Enfolderer Team A (Geometry)"       --query id -o tsv
az ad group show --group "Enfolderer Team B (Identification)" --query id -o tsv
```

Keep both object ids. Add yourself to whichever group you want to demo from — and deliberately
*not* to the other, so the 403s in the walkthrough are genuine.

## 2. Register the API and the desktop client

Two registrations: the API exposes a scope, and the desktop app is a **public client** with no
secret.

```bash
# The API.
apiAppId=$(az ad app create --display-name "Enfolderer Scan API" --query appId -o tsv)
az ad app update --id "$apiAppId" --identifier-uris "api://$apiAppId"
```

Add the scope. In the portal: **App registrations → Enfolderer Scan API → Expose an API → Add a
scope**, named `Scan.Submit`, admin *and* user consentable. Or with the CLI:

```bash
scopeId=$(uuidgen)
az ad app update --id "$apiAppId" --set api.oauth2PermissionScopes="[{
  \"id\": \"$scopeId\",
  \"value\": \"Scan.Submit\",
  \"type\": \"User\",
  \"isEnabled\": true,
  \"adminConsentDisplayName\": \"Submit card scans\",
  \"adminConsentDescription\": \"Allows the signed-in user to submit card scan jobs.\",
  \"userConsentDisplayName\": \"Submit card scans\",
  \"userConsentDescription\": \"Allows you to submit card scan jobs.\"
}]"
```

Then the desktop client:

```bash
clientAppId=$(az ad app create \
  --display-name "Enfolderer Desktop" \
  --is-fallback-public-client true \
  --public-client-redirect-uris "http://localhost" \
  --required-resource-accesses "[{
    \"resourceAppId\": \"$apiAppId\",
    \"resourceAccess\": [{\"id\": \"$scopeId\", \"type\": \"Scope\"}]
  }]" \
  --query appId -o tsv)

az ad app permission admin-consent --id "$clientAppId"   # or let users consent at first sign-in
echo "API app id:    $apiAppId"
echo "Client app id: $clientAppId"
```

Do **not** create a client secret for either registration. The desktop app rejects a config file
containing `client_secret`, and the services authenticate with managed identities.

## 3. Deploy the infrastructure

Fill in `infra/main.parameters.json`:

```json
{
  "namePrefix":         { "value": "enf-demo" },
  "location":           { "value": "eastus2" },
  "teamAGroupObjectId": { "value": "<Team A group object id>" },
  "teamBGroupObjectId": { "value": "<Team B group object id>" },
  "apiClientId":        { "value": "<API app id from step 2>" },
  "grantIdentificationAccessToGeometry": { "value": true }
}
```

`namePrefix` is 3–12 characters and seeds every resource name, so keep it short and unique — the
storage account and the two Foundry accounts need globally unique names.

Preview, then deploy:

```bash
az deployment sub what-if \
  --location eastus2 \
  --template-file infra/main.bicep \
  --parameters infra/main.parameters.json

az deployment sub create \
  --name enfolderer-scan \
  --location eastus2 \
  --template-file infra/main.bicep \
  --parameters infra/main.parameters.json
```

This creates three resource groups (`<prefix>-platform`, `<prefix>-cardgeo`, `<prefix>-cardid`),
the four managed identities, the storage account with the `scans` and `crops` containers and the
`scan-jobs` queue, the Cosmos account with the `jobs` container, two Foundry accounts each with one
project and a `gpt-4o` deployment, the API and worker App Services, and all the role assignments.

Collect the outputs:

```bash
az deployment sub show --name enfolderer-scan --query properties.outputs -o json
```

You need `apiUrl`, `geometryProjectEndpoint`, and `identificationProjectEndpoint` for the steps
below. Role assignments can take a couple of minutes to propagate; if the first scan fails with a
403, wait and retry before assuming a misconfiguration.

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

```bash
az webapp config appsettings set \
  --resource-group enf-demo-platform --name enf-demo-worker --settings \
  ScanPipeline__BoundaryAgentId="<boundary agent id>" \
  ScanPipeline__IdentificationAgentIds__mtg="<mtg agent id>" \
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

```bash
dotnet publish src/Enfolderer.Ai.Api    -c Release -o /tmp/api
dotnet publish src/Enfolderer.Ai.Worker -c Release -o /tmp/worker

(cd /tmp/api    && zip -r ../api.zip    .)
(cd /tmp/worker && zip -r ../worker.zip .)

az webapp deploy --resource-group enf-demo-platform --name enf-demo-api    --src-path /tmp/api.zip    --type zip
az webapp deploy --resource-group enf-demo-platform --name enf-demo-worker --src-path /tmp/worker.zip --type zip
```

The Bicep already set every app setting, including
`ScanPlatform__ManagedIdentityClientId` — the client id of that service's user-assigned identity.
That setting is not optional: a host can carry several user-assigned identities, and without it the
credential cannot tell which one to present. If you attach another identity later, keep the setting
pointing at the one that holds the role assignments.

Check the API is up:

```bash
curl "https://enf-demo-api.azurewebsites.net/healthz"
```

A warning in the API log that `AzureAd:TenantId` is not configured means the API is running
unauthenticated — acceptable locally, not in a deployment. Confirm the setting survived.

## 7. Point the desktop app at the deployment

Create `aiconfig.txt` beside `Enfolderer.App.exe` (the app writes a template on the first scan if
the file is missing):

```ini
api_base_url=https://enf-demo-api.azurewebsites.net
tenant_id=<your tenant id>
client_id=<desktop client app id from step 2>
scope=api://<API app id from step 2>/Scan.Submit
#game_hint=mtg
#use_device_code=true
```

Then **Tools → Scan Card Image…**, pick a photo, and sign in when prompted. Uncomment
`use_device_code` if the machine has no usable browser.

## Verifying the boundaries

Once a scan succeeds end to end, confirm the demo assets are real:

```bash
# The worker's identity can reach Cosmos.
az cosmosdb sql role assignment list \
  --account-name enf-demo-cosmos --resource-group enf-demo-platform -o table

# Neither team identity appears in that list. Confirm Team A's roles are storage-only:
geoPrincipal=$(az identity show -g enf-demo-cardgeo -n enf-demo-cardgeo-id --query principalId -o tsv)
az role assignment list --assignee "$geoPrincipal" --all -o table
```

Team A should show exactly one assignment: **Storage Blob Data Reader** on the `scans` container.
No Cosmos, no `crops`, nothing in `<prefix>-cardid`.

Then run the two "break it" scenarios in [foundry-demo.md](foundry-demo.md).

## Costs and teardown

The Basic App Service plan, the provisioned-throughput Cosmos container, and the `gpt-4o`
deployments all bill while they exist. Job documents self-expire after seven days via the container
TTL, but the resources do not. Tear down when you are done:

```bash
az group delete --name enf-demo-platform --yes --no-wait
az group delete --name enf-demo-cardgeo  --yes --no-wait
az group delete --name enf-demo-cardid   --yes --no-wait

az ad app delete --id "$apiAppId"
az ad app delete --id "$clientAppId"
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
