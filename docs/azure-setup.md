# Cost-optimized Azure deployment

This guide applies to the `cost-optimized-azure` branch. `main` retains the separate-team,
separate-environment, private-endpoint architecture. Do not replay a `main` deployment's
parameters against this branch or deploy this template over existing resources.

This branch is **Magic: The Gathering only**. The website has no game selector, desktop
configuration defaults to `mtg`, and the API rejects other nonempty game hints with HTTP 400
before creating a job. The worker also rejects legacy non-MTG queued jobs before downloading
photos or invoking agents. Historical game identifiers remain readable in old result documents,
but do not register any agents or enable new scans.

## Architecture and costs

| Component | This branch |
|---|---|
| Container Apps | One shared Consumption environment and Log Analytics workspace for all four apps |
| Resource groups | New `<prefix>-platform`, `<prefix>-cardgeo`, `<prefix>-cardid`; component organization only, not team boundaries |
| Networking | Platform-managed ACA networking; no customer VNet, private endpoints, private DNS, NAT gateway or dedicated workload profiles |
| Storage | Standard LRS, public HTTPS transport, Entra RBAC only; shared keys and anonymous blob access disabled |
| Cosmos DB | Single region, free tier requested at creation, database-shared 400 RU/s; local/key authentication disabled |
| Foundry | One account, two logical pipeline projects, one operator owns both; worker gets invoke-only roles |
| Model | GPT-5 mini `2025-08-07`, Global Standard, capacity 300 (300,000 TPM / 300 RPM) |
| Registry | Basic ACR, admin credentials disabled, managed-identity pulls |
| API Management | Not needed or deployed; the API already enforces tenant, scope, pilot allowlist and ownership |

The original GPT-4o deployment has **zero quota** in the target subscription in Sweden Central
and the other US/European regions checked on 2026-10-19. The owner authorized GPT-5 mini instead,
subject to representative end-to-end scan validation before cutover. Only the two active agent
definitions change model; prompts, tools and JSON contracts are retained. `temperature: 0` is
removed because this reasoning model does not support that setting. This is **not** a claim of
equivalent scan accuracy or latency. Global Standard inference may run outside Sweden; the
original template also used Global Standard.

The API scales to zero with HTTP wakeup. The worker scales 0..1 using the `scan-jobs` Azure Queue
KEDA scaler authenticated as its user-assigned identity (no connection string). The scaler counts
both visible and invisible messages: a leased in-flight scan must keep the worker alive.
HTTP cold starts and queue polling add latency. MCP servers remain at one warm replica each:
Foundry tool deadlines make cold tool servers unsafe without separate cold-start validation.

ACA Consumption includes monthly subscription-wide grants (180,000 vCPU-seconds,
360,000 GiB-seconds and 2 million requests), **not unlimited free hosting**. Two warm MCP
servers can exceed those grants. Cosmos free tier discounts the first 1,000 RU/s and 25 GB, but
only one account per subscription may opt in, and opt-in must happen at account creation.
Check availability first; never silently fall back to a paid account.
ACR Basic, image builds, storage operations/capacity, Log Analytics ingestion, network egress,
Foundry tools and model tokens may be billable. Resource-group/environment consolidation alone
does not guarantee savings: the main savings are removing private networking and idle compute.
API and catalogue MCP CPU/memory sizes, TTL, scan credits, collections and authorization stay unchanged.
The owner approved raising the worker from 0.5 vCPU / 1 GiB to **1 vCPU / 2 GiB** after a
full-resolution 24-megapixel photograph exhausted the original worker heap while serializing a
Foundry request. It still scales to zero and costs more only while running. A mobile-normalized
4096-pixel version passed at the original sizing. The first larger-worker retest exposed an exit-137 termination in
the original 0.25-vCPU / 0.5-GiB imaging MCP. The owner approved **0.5 vCPU / 1 GiB for imaging
only**; the MTG catalogue MCP retains its original sizing. Imaging remains warm, so this
increases its idle cost as well as its active cost. The untouched full-resolution photo subsequently
passed with both approved increases; see the deployment record below.

See [ACA billing](https://learn.microsoft.com/azure/container-apps/billing),
[Cosmos free tier](https://learn.microsoft.com/azure/cosmos-db/free-tier) and
[managed-identity scaling](https://learn.microsoft.com/azure/container-apps/managed-identity#scale-rules).

## Public does not mean anonymous

This deployment intentionally permits public network reachability without IP allowlists.
Authorization is Entra/RBAC, not network location:

- Storage: HTTPS/TLS 1.2, `allowSharedKeyAccess: false`, `allowBlobPublicAccess: false`,
  and `publicAccess: None` on every blob container.
- Cosmos and Foundry: `disableLocalAuth: true`; only role-assigned identities can access data.
- API: valid tenant token, delegated `Scan.Submit`, explicit pilot user allowlist and ownership.
- MCP: tenant/audience JWT validation and an exact, distinct agent principal allowlist.
- Worker: no ingress; API/worker/MCP retain separate least-privilege managed identities.
- Website landing/configuration and `/healthz` are intentionally public; scan data is not.

No account keys, client secrets, SAS URLs or anonymous data access are introduced.
Do not weaken these controls to resolve a deployment failure. This branch is unsuitable for a
subscription policy requiring private-only data access.

## 1. Prerequisites and destination

Use PowerShell 7, current Azure CLI, Bicep and the `containerapp` extension. A subscription Owner
(or equivalent deployment plus role-assignment rights), app-registration/consent privileges and
Foundry model quota are required. Images are built with ACR Tasks; local Docker is not needed.
Agent provisioning requires `powershell-yaml` (`Import-Module powershell-yaml`; install it for
CurrentUser only if missing).

```powershell
az login --tenant 6a32cdb1-b813-4447-96d9-4f42e9cd37e3
az account set --subscription e3e0cdb6-dda9-477e-8d06-656c824ea765
$prefix = 'enf-cost'
$location = 'swedencentral'
$deployment = 'enfolderer-cost'
az account show --query '{tenant:tenantId,subscription:id}' -o json
az cosmosdb list --query '[].{name:name,freeTier:enableFreeTier}' -o table
foreach ($provider in 'Microsoft.App','Microsoft.CognitiveServices','Microsoft.DocumentDB',
    'Microsoft.ContainerRegistry','Microsoft.Storage','Microsoft.ManagedIdentity',
    'Microsoft.OperationalInsights') {
    az provider register --namespace $provider
    if ($LASTEXITCODE -ne 0) { throw "Registration failed: $provider" }
}
```

Wait for provider registration to complete. Verify model availability and quota before spending:

```powershell
az rest --method get --url "https://management.azure.com/subscriptions/e3e0cdb6-dda9-477e-8d06-656c824ea765/providers/Microsoft.CognitiveServices/locations/$location/usages?api-version=2025-06-01" `
    --query "value[?name.value=='OpenAI.GlobalStandard.gpt-5-mini'].{used:currentValue,limit:limit}" -o table
```

At least 300 unused capacity units are needed for a new deployment (200 additional units when
upgrading an existing allocation of 100). If unavailable, stop; do not switch model or region
without authorization. Verify the three destination resource-group names are unused.

## 2. Credential-free application registrations

```powershell
$auth = .\scripts\initialize-scan-auth.ps1 -NamePrefix $prefix
$mcpAudience = .\scripts\configure-mcp-audience.ps1 -NamePrefix $prefix
$auth | Format-List
```

The initializer creates single-tenant `<prefix>-api` and `<prefix>-desktop` registrations,
the `Scan.Submit` delegated scope, localhost public-client redirect and operator-specific consent.
It validates existing exact-name registrations rather than overwriting incompatible settings.
The MCP resource registration uses v2 tokens and has no credentials.
There are **no team security groups**. Both logical Foundry projects are administered directly by
`$auth.OwnerObjectId`. Fresh registrations have new IDs; existing tenant configs are not copied.

## 3. Deploy infrastructure

Build a private local parameters file from the checked-in example, which intentionally contains
placeholder IDs. Use a path outside source control and preserve it for recovery.

```powershell
$parameterFile = Join-Path $env:LOCALAPPDATA 'Enfolderer\cost.parameters.json'
New-Item -ItemType Directory -Force (Split-Path $parameterFile) | Out-Null
$p = Get-Content .\infra\main.parameters.json -Raw | ConvertFrom-Json
$p.parameters.namePrefix.value = $prefix
$p.parameters.location.value = $location
$p.parameters.ownerObjectId.value = $auth.OwnerObjectId
$p.parameters.apiClientId.value = $auth.ApiClientId
$p.parameters.mcpAudience.value = $mcpAudience
foreach ($key in 'scanAllowedUserObjectIds','scanDiagnosticUserObjectIds','scanAllowanceAdminUserObjectIds') {
    $p.parameters | Add-Member -MemberType NoteProperty -Name $key -Value @{ value = $auth.OwnerObjectId }
}
$p | ConvertTo-Json -Depth 20 | Set-Content $parameterFile -Encoding utf8
.\scripts\test-cost-infrastructure.ps1
az deployment sub validate --name $deployment --location $location `
    --template-file .\infra\main.bicep --parameters "@$parameterFile"
if ($LASTEXITCODE -ne 0) { throw 'Infrastructure validation failed.' }
az deployment sub create --name $deployment --location $location `
    --template-file .\infra\main.bicep --parameters "@$parameterFile"
if ($LASTEXITCODE -ne 0) { throw 'Infrastructure deployment failed.' }
```

The first deployment uses placeholder images solely to establish hostnames and identities.
It is **not a working service** until the next step succeeds. Never claim application health
from the placeholder's HTTP response. Cosmos free-tier rejection or unsupported regional
capacity must be resolved explicitly, not hidden by a paid fallback.

## 4. Provision agents and build real images

```powershell
.\scripts\update-demo.ps1 -DeploymentName $deployment -NamePrefix $prefix
```

Despite the historical script name, this creates the two functional named agents in the
two logical projects, creates secretless `AgenticIdentityToken` MCP connections, records each
agent's distinct principal, builds all four images and redeploys the complete template.
No team ownership/isolation is assumed. Preserve the two logical projects and existing agent
names because the pipeline and prompt files already use them.

For subsequent code changes:

```powershell
.\scripts\deploy-images.ps1 -DeploymentName $deployment
# Existing image rollback (does not revert prompts/model or migrate data):
.\scripts\deploy-images.ps1 -DeploymentName $deployment -SkipBuild -Tag '<previous-tag>'
```

Only roll back to another MTG-only image release; older multi-game API/worker images would
restore the removed UI and runtime behavior even though the Pokemon service has been deleted.

The script replays deployment parameters, preserves live custom-domain/certificate bindings,
user/diagnostics/admin allowlists and agent IDs, and caches parameters in ignored `.deploy-images`.
It removes retired Pokemon agent parameters when replaying an older deployment/cache.
Do not redeploy with an empty `imageTag` after bootstrap. New resource groups require new
certificates; do not copy certificate IDs from another environment.

### Removing resources from an earlier multi-game cost deployment

ARM incremental deployments do not delete resources removed from the template. For the original
`enf-cost` deployment only, explicitly remove the obsolete Pokemon resources after redeploying:

```powershell
az containerapp delete --resource-group enf-cost-cardid --name enf-cost-mcp-cardcatalog-pokemon --yes
az rest --method delete --resource https://ai.azure.com `
    --headers 'Foundry-Features=AgentEndpoints=V1Preview' `
    --url 'https://enf-cost-ai.services.ai.azure.com/api/projects/cardid/agents/PokemonCardIdAgent?api-version=2025-11-15-preview'
az rest --method delete `
    --url 'https://management.azure.com/subscriptions/e3e0cdb6-dda9-477e-8d06-656c824ea765/resourceGroups/enf-cost-platform/providers/Microsoft.CognitiveServices/accounts/enf-cost-ai/projects/cardid/connections/mcp-cardcatalog-pokemon-agent-identity?api-version=2025-04-01-preview'
az acr repository delete --name enfcostacraod57xt6zx6s4 --repository enfolderer/mcp-cardcatalog-pokemon --yes
```

These exact resources were deleted for this deployment. Do not apply these names or commands to
the existing `main` deployment. Verify four remaining apps, two named agents, and no Pokemon
project connection or registry repository. This removes one always-warm 0.25-vCPU / 0.5-GiB
replica: warm MCP CPU/memory decreases by 25% (from 1 vCPU / 2 GiB to 0.75 vCPU / 1.5 GiB).
This is not a 25% reduction in the entire Azure bill; shared registry, model, data and logging costs remain.

## 5. Mobile website and separate desktop configuration

```powershell
$outputs = az deployment sub show --name $deployment --query properties.outputs -o json | ConvertFrom-Json
$apiUrl = $outputs.apiUrl.value
$mobile = .\scripts\configure-mobile-web.ps1 -ApiClientId $auth.ApiClientId `
    -WebsiteUrl $apiUrl -AllowedUserObjectIds @($auth.OwnerObjectId) `
    -DisplayName "$prefix-mobile" -GrantAdminConsent
```

```powershell
.\scripts\deploy-images.ps1 -DeploymentName $deployment -SkipBuild `
    -MobileWebClientId $mobile.MobileWebClientId -ScanAllowedUserObjectIds $auth.OwnerObjectId
```

The SPA requires assignment and admin consent;
adding a pilot user requires app assignment as well as the API allowlist.
Guests must accept their invitation and sign in with the same email address. A Microsoft account
is not required when Entra email one-time passcode is enabled: guests without another supported
identity can receive a code by email. Check **External Identities > All identity providers >
Email one-time passcode** in the Entra admin center. The website wording covers both sign-in methods;
it does not change the tenant's authentication policy.

Create a **separate** desktop config for this deployment; do not overwrite the existing default or
point current users at the new API before scan validation. Copy the existing config format and
set its `api_base_url`, `tenant_id`, `client_id` and `scope` from `$apiUrl`, `$auth.TenantId`,
`$auth.DesktopClientId` and `$auth.Scope`. Use no client secret.

All approved users start with zero scan credits, including the owner. Grant the owner unlimited
credits before smoke scans:

```powershell
.\scripts\manage-scan-allowance.ps1 -ConfigFile '<new-config-path>' `
    -UserObjectId $auth.OwnerObjectId -Unlimited -Reason 'Cost deployment validation' -SignIn
```

For finite users use `-Credits 10 -Reason 'Initial pilot grant'`. Preserve the printed operation
GUID and reuse `-OperationId` with identical arguments after an ambiguous failure to avoid a
double grant. Admission, app consent/assignment, diagnostics permissions and credit allowance are
independent checks. Cross-tenant users need new tenant object IDs; collections/allowances and
pending jobs are not automatically migrated.

List every approved demo user and their current remaining scans, including users who have not
accepted their invitations:

```powershell
.\scripts\list-demo-users.ps1 -ConfigFile '<new-config-path>' -SignIn |
    Format-Table DisplayName, RemainingScans, InvitationStatus -AutoSize
```

Subsequent calls can omit `-SignIn`. The script defaults to `-NamePrefix enf-cost`, discovers
the live API allowlist (including the owner), and reports unlimited accounts as `Unlimited`.
It requires Azure CLI read access to the Container App and directory users, plus delegated
allowance-administrator access. `-AuthenticationRecord '<existing-record-path>'` can import an
existing desktop authentication record for the same tenant/client. Results are PowerShell
objects, so they can also be piped to `Export-Csv` or `ConvertTo-Json`. The script is read-only:
it does not assign users, grant or consume credits, or change authentication policy.

## 6. Verification and cutover gate

The `collection-add-and-browse` branch adds a mobile-only scanned collection, separate from
uploaded desktop snapshots. It needs no new Azure resources or permissions: the API and worker use
their existing Entra access to the `collections` container. Each added scan is saved once at
`{tenantId}/{objectId}/scanned/{jobId}.json`, using conditional creation to make retries safe
across API replicas. Existing `{tenantId}/{objectId}/collection.json` snapshots remain unchanged.
Include the `scanned/` receipts in any collection retention/backup policy; deleting scan images
does not remove these saved quantities. Without Azure Storage, the explicitly logged local
development fallback is in-memory and does not survive an API restart.

Deploy both the API and worker images for the automatic-add and combined-ownership changes.
The default-off **Automatically add identified cards to scanned collection** switch is captured
on each new job; the worker saves after rendering ownership, even if the browser closes.
Ownership excludes the current scan's receipt on a retry. Automatic save failures preserve
completed results and expose an explicit manual-retry message.

After deploying this feature's images, check **Add identified cards to collection** and
**View scanned collection** with two users. Verify that a scan is counted once, separate scans
increase only the matching set/collector-number/language quantity, set filtering works, and
neither additions nor snapshot replacement/deletion changes the other collection. Browse/save
must still work with no scan credits. New mobile ownership overlays add uploaded and scanned
quantities, and work with either source alone; existing overlays remain unchanged. Ownership
combines languages by set/number, while browsing retains language-specific rows. Copies present
in both sources are counted twice, so do not upload overlapping inventory if that is unwanted.
Verify the switch defaults off, auto-saving does not count the current scan in its own overlay,
retrying cannot duplicate a receipt, and a read failure in either source shows unknown ownership
rather than partial totals. Authenticated collection responses must remain `no-store`.

```powershell
.\scripts\test-cost-infrastructure.ps1
.\scripts\test-scan-access.ps1 -NamePrefix $prefix
dotnet run --project .\tools\Enfolderer.Ai.CardCatalog.Check
```

Also inspect the **live** resources: exactly one ACA managed environment/workspace, every app's
`environmentId` equal, no private endpoints/VNet/DNS zones in the new groups, Cosmos free tier
enabled at 400 shared RU/s, public transport with local/shared-key/anonymous data access disabled,
and worker queue rule using its managed identity. Check all four real images/revisions and logs.
Confirm a queue submission wakes an idle worker and completes rather than merely observing a
configured scale rule. Verify API cold-start behavior and MCP tool health.

Before cutover, use the known training photographs and recorded expectations in `training/`.
Exercise MTG rotated/footer-detail cards and multi-card pages; check:

1. Valid delegated sign-in, allowance enforcement, collection snapshot upload and scan submission.
2. Real Foundry responses and each exact-agent authenticated MCP tool invocation.
3. Valid boundary/identification JSON, expected card count and known set/collector-number matches.
4. Crops, rendered image, CSV and ownership overlays; failed/uncertain identifications stay explicit.
5. Anonymous and wrong-audience requests denied; another approved user cannot read the first user's
   jobs, original photos, results or collection.
6. Repeat representative scans to look for model-induced accuracy or latency regressions.

Do not equate a healthy container or a single successful response with scan-quality parity.
If model/tool/quality validation fails, **do not cut clients over**. Leave the existing deployment
untouched, diagnose the failure, and either correct/retest or request GPT-4o quota and redeploy
that model with its matching agent definitions. Do not silently fall back to another model or
change scan results to look successful. Model rollback requires model plus agent configuration,
not just an image tag. Old deployment and data remain the rollback path until explicitly retired.

Read-only cross-user diagnostics remain explicitly allowlisted and logged:

```powershell
.\scripts\get-scan-diagnostics.ps1 -ConfigFile '<new-config-path>' -SignIn
.\scripts\show-last-scan.ps1 -NamePrefix $prefix
```

Diagnostic permission does not grant credit-administration permission. Evidence contains private
photos; store it privately. Apply existing cleanup/retention policy; no data is copied or deleted
from the old deployment by this guide.

## 7. Deployment record and remaining acceptance tests

Deployed on 2026-10-19 into tenant `6a32cdb1-b813-4447-96d9-4f42e9cd37e3`,
subscription `e3e0cdb6-dda9-477e-8d06-656c824ea765`, Sweden Central:

- Subscription deployment: `enfolderer-cost`; current MTG-only image tag:
  `v20261022-id-recovery` (all four apps, alternate-title identification and bounded catalogue recovery).
- New resource groups: `enf-cost-platform`, `enf-cost-cardgeo`, `enf-cost-cardid`.
- Shared ACA environment: `enf-cost-platform-env`; Foundry account: `enf-cost-ai`.
- API/mobile website: <https://enf-cost-api.purplefield-875302b1.swedencentral.azurecontainerapps.io>.
- Model: GPT-5 mini `2025-08-07`, Global Standard; initially capacity 100, raised to 300 on
  2026-10-22 for the parallel-identification branch.

### Parallel-identification validation (2026-10-22)

The worker now allows nine independent card identifications with bounded, isolated transient
retries. Full-photo decode/cropping remains serialized to protect the worker's 2 GiB allocation.
The image deployment script records the release tag in the subscription deployment, so a later
redeploy does not revert to the earlier worker-only experimental image.

| Fixture | Exact printing/language matches | End-to-end time |
|---|---|---|
| `training/01/image.jpg` binder | 9/9 | 139.8 seconds |
| `training/01/loose-pair-upload.jpg` | 2/2 | 77.4 seconds |
| `training/03/image.jpg` adventure page | 7/7 | 132.9 seconds |

All three passed dated upload/crop/annotation and historical-download checks. The initial
instrumented binder baseline took 451.3 seconds and returned only 8/9 because one identification
timed out; the initial loose pair passed 2/2 in 92.6 seconds. These are representative measurements,
not a latency SLA or a controlled single-variable benchmark.

The final binder logged 183,790 input tokens (116,608 cached), 20,323 output tokens and 13,888
reasoning tokens across geometry, identification and printing-review responses, with zero
worker throttle/service retries. Token totals include follow-up tool/approval contexts, not just
one request per card. Compare completed workloads and cache state before claiming token savings.
The increased TPM allocation remains consumption-priced Global Standard, not reserved PTU.

Lower reasoning was tested but not retained: four catalogue tool failures invalidated its
quality comparison. `MtgCardIdAgent` version 3 restores the original definition and principal;
do not send a per-request `reasoning` override with a named agent (the endpoint rejects it).
Full-resolution card/footer images, complete art candidates and original reasoning are retained.
Instead, the accepted tool optimization reuses successful/404 printing searches for one minute
within a bounded catalogue cache and shares pacing between identification and price lookups.
Catalogue and worker Scryfall traffic can share an outbound IP. Each now allows two requests/second
per instance, leaving headroom below Scryfall's approximate ten/second ceiling even with two replicas
of each. Keep the aggregate limit in mind before increasing replicas.

The identification-recovery release separates the catalogue's 30-second network timeout from its
two-minute queue/cooldown/retry budget. GET retries respect `Retry-After` and cancellation; only
explicit `catalogue_transient_failure` MCP errors trigger additional bounded per-card agent retries.
Permanent authorization and unclassified tool errors are not retried or hidden.
Exact-printing catalogue `flavor_name` data supports alternate-title cards without disabling the
English wrong-card guard: the worker independently verifies an alias before accepting a confirmed
canonical/photographed title difference. The prompt treats Final Fantasy franchise markers as
distinct from the visible FCA set code. Agent definitions, principals, model SKU and capacity do
not change; deploy the worker and catalogue images together.

Inspect worker `token_usage`, `response_summary` and `timing` logs when evaluating a new setting;
missing metrics are warnings, not zero usage. Keep a candidate only after exact-printing and
language checks pass. Image rollback does not change agent versions or model capacity.

### Resource sizing

| App | vCPU / memory | Replica range |
|---|---|---|
| API | 0.5 / 1 GiB | 0..3 |
| Worker | 1 / 2 GiB | 0..1 |
| Imaging MCP | 0.5 / 1 GiB | 1..2 |
| MTG catalogue MCP | 0.25 / 0.5 GiB | 1..2 |

Live management-plane checks verified that all remaining apps use the same environment, no
`Microsoft.Network/*` resources exist in the new groups, Cosmos free tier is enabled at 400 shared
RU/s, and public data endpoints reject anonymous access. All real services passed health and
negative authorization checks. Agent identities and authenticated MCP connections were verified.
The owner completed delegated sign-in; authenticated collection snapshot upload/read/delete passed.
Temporary operator Cosmos data access used to stop an invalid synthetic test was removed.
After the final scan and the configured 300-second cooldown, a management-plane check observed
**zero worker replicas**, confirming idle scale-down rather than just a configured minimum.

The real API/queue/worker/Foundry/MCP pipeline produced these exact-printing results, including
successful annotated-image and CSV downloads:

| Training input | Detected / expected | Exact set and collector-number matches | Elapsed |
|---|---|---|---|
| `training/01/image.jpg`, original | 9 / 9 | 9 / 9 | about 4m44s |
| `training/05/image.jpg`, mobile-equivalent oriented 3072 x 4096 copy | 12 / 12 | 12 / 12 | about 4m23s |
| `training/05/image.jpg`, untouched 5712 x 4284 original with EXIF rotation | 12 / 12 | 12 / 12 | 5m29s |
| MTG-only release: `training/01/image.jpg`, original | 9 / 9 | 9 / 9 | 3m38s |

The full-resolution original-photo job was `d0dc120820944c3b8418addffed0ab6c`;
the post-removal MTG-only job was `3d8f89dd48174fdfa0ae9d93c075de2b`.
The earlier worker out-of-memory and imaging exit-137 failures were not counted as successes.
Local regression checks also passed: catalogue checks and 57/57 expected imaging-training cards.
Existing ImageSharp dependency vulnerability warnings remain outside this architecture change.

After the MTG-only rollout, live checks verified exactly four apps with the new tag, only the
boundary and MTG named agents, no Pokemon compute/connection/registry repository, and no Pokemon
worker environment setting. Authenticated API requests for Pokemon and other unsupported game
hints returned HTTP 400. Browser checks verified MTG copy, no game selector or Pokemon option,
registered scan handlers and no JavaScript runtime errors. Updated API/worker regression checks,
desktop build/self-tests and architecture checks all passed, including explicit rejection of
legacy unsupported queued jobs before downloading images or invoking agents.

**Scope updated to MTG only by the owner.** Pokemon validation is no longer an acceptance gate,
and no Pokemon service, agent, connection or images are required or retained in Azure.
Full phone sign-in/scanning and live isolation between two approved human users remain untested;
negative authorization checks are not a substitute. Repeat representative scans before claiming
model parity.

Existing deployment, desktop defaults and user data remain unchanged. A separate desktop config
was created for validation; no client cutover or old-resource teardown occurred.

`Microsoft.Quota` and `Microsoft.CognitiveServices` are registered. GPT-4o Global Standard quota
remains zero. The standard Quota REST endpoints rejected the Cognitive Services scope, so **no
GPT-4o quota request was submitted**. Use the official
[OpenAI quota increase form](https://aka.ms/oai/stuquotarequest) for Sweden Central, GPT-4o,
Global Standard, 100,000 TPM, with this subscription and tenant.
