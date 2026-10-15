# Foundry multi-project demo: geometry vs. identification

This repository contains a working example of **two Azure AI Foundry projects owned by two
different teams**, cooperating on one workload while being unable to reach into each other's
assets. The workload is the Enfolderer card scanner: photograph a page of collectible cards, get
back JSON listing every card.

## The two teams

| | Team A — geometry | Team B — identification |
|---|---|---|
| Foundry project | `cardgeo` | `cardid` |
| Foundry account | `<prefix>-ai` (shared) | `<prefix>-ai` (shared) |
| Resource group | `<prefix>-cardgeo` | `<prefix>-cardid` |
| Owner group | `teamAGroupObjectId` | `teamBGroupObjectId` |
| Agents | `CardBoundaryAgent` | `OrchestratorAgent`, `MtgCardIdAgent`, `PokemonCardIdAgent` |
| MCP servers | `mcp-imaging` | `mcp-cardcatalog-mtg`, `mcp-cardcatalog-pokemon` |
| Storage access | read `scans` | **none** |
| Job state (Cosmos) | **none** | **none** |

Team A's skill is *finding cards*: any game, any frame, borderless and full-art printings, cards
at oblique angles or rotated relative to each other. It returns geometry only — four corner
points per card — and knows nothing about card catalogues.

That skill is not just a prompt. Behind `CardBoundaryAgent` sits Team A's own MCP server,
`mcp-imaging`, whose `detect_cards` tool measures the card corners by edge detection. This is the
division of labour worth pointing at on screen: a chat model has no detection head and never sees
the photo at full resolution, so asked for coordinates it invents plausible ones — the tool
*measures* them, and the model supplies printed orientation and game metadata. Measured candidates
are no longer dropped by the model; identification can decline non-card or unreadable crops.
Team B gets the benefit of both
by invoking one agent, and cannot see, call or redeploy the server underneath it.

Team B's skill is *reading cards*: given a rectified crop, work out the set, collector number and
name, using a per-game agent backed by that game's catalogue MCP server.

Adding a game means adding one agent definition plus one MCP server under `agents/cardid/`.
`YugiohCardIdAgent` and `LorcanaCardIdAgent` are checked in as growth slots to show that the
reuse story does not require touching Team A at all.

Both projects live in **one Foundry account** by default, so the boundary between the teams is the
Foundry *project* boundary rather than the plain Azure RBAC boundary you get between two unrelated
resources. Each team's identity and MCP servers stay in its own resource group. Deploy with
`singleAccount=false` to give each team its own account instead; see
[Two tiers of isolation](azure-setup.md#two-tiers-of-isolation) for which tier to pick.

## End-to-end flow

1. Desktop app (`Scan Card Image…`) signs the user in with Entra ID and calls `POST /jobs`.
2. The API creates the job document in Cosmos and returns the relative URL
   `jobs/{jobId}/content` to upload to.
3. The app PUTs the image to the API with its bearer token, and the API writes it to
   `scans/{jobId}/{filename}`. The storage account has no public endpoint, so the API is the only
   thing the client can reach — which is what lets the client run anywhere.
4. `POST /jobs/{id}/submit` enqueues the job; the worker picks it up.
5. Worker → `DetectingBoundaries`: uploads the photo to the `cardgeo` project and calls Team A's
   `CardBoundaryAgent` with it as `image_file` message content. Storage is private, so an agent
   cannot be handed a URL to fetch — and the upload is deleted as soon as the run ends.
   The agent then calls its own `detect_cards` tool to measure the corners, and reviews the result
   against the picture it was sent. The worker passes the scan's blob path along for that call: it
   is a name, not a credential, and Team A's identity could already read `scans` and nothing else.
   Foundry pauses the run at that tool call and waits for approval, and the worker — Team B's
   orchestrator — approves it, having first checked the call targets the `imaging` server and not
   some other team's. A call to any other server is refused, so the boundary is enforced twice over
   by two different parties.
   The worker also runs the same shared detector locally. Its regions are authoritative: the
   agent can supply printed orientation and game metadata for unambiguous overlapping matches,
   but cannot delete regions, move measured corners, or add estimates when regions were measured.
   Missing or ambiguous metadata is logged and the candidates still reach identification.
   No measured regions triggers an explicitly logged fallback to agent estimates; failures to
   invoke or parse the agent still fail the job rather than masquerading as a successful scan.
6. Worker crops each quadrilateral itself (perspective-correct warp) and writes the crops to
   `crops/{jobId}/`. Team A never gets blob write access.
7. Worker → `Identifying`: uploads each crop to the `cardid` project and fans them out to the
   per-game agents. Team B receives one card at a time and never the whole page.
8. Worker retrieves exact-printing EUR nonfoil/foil prices after art verification, renders the
   priced photo, and writes it to private `outputs/{jobId}/priced.png`. Missing foreign-language
   prices fall back independently to English for that same printing. No image-generation model
   is involved. Pricing/rendering failures are explicit optional-output warnings, not lost scans.
   Worker → `Completed`, writing the versioned result document to Cosmos.
9. The app polls `GET /jobs/{id}` every 2s with exponential backoff and maps `cards[]` into the
   CSV shape the importer already understands.
   It also downloads the priced image from authenticated `GET /jobs/{jobId}/annotated-image`,
   saves it beside the CSV, and previews it. The route streams the job's fixed output blob;
   the client never follows a model-supplied storage URL.

## Why the boundaries are real

* **Project-scoped ownership.** Each team's group holds `Foundry Project Manager` on *its own
  project only* — see the `ownerAssignment` in `infra/modules/foundry-project.bicep`, whose scope
  is the project resource. Team B cannot edit, redeploy or read the instructions of Team A's
  boundary agent even though both projects sit in the same account and the same resource group.
* **Invoke-only cross-project access.** `infra/modules/foundry-invoke-access.bicep` grants the
  orchestrator `Foundry User` on the `cardgeo` **project** — enough to run Team A's agent, not
  enough to change it. Scoping it to the project rather than the account matters: an account-scoped
  grant would hand Team B access to every project in the account, which is the opposite of the
  claim. The same module grants the orchestrator the same role on `cardid`, so the two halves of
  the pipeline are two separate, separately revocable assignments.
* **Entra-only data plane.** The storage account has `allowSharedKeyAccess: false` and Cosmos has
  `disableLocalAuth: true`. There are no keys or connection strings to copy into a config file.
* **No secrets in the desktop app.** `aiconfig.txt` carries only an API URL, tenant id, public
  client id and scope. Sign-in is interactive (`InteractiveBrowserCredential`) or device code.
  If an old file still contains `client_secret`, the app refuses to start the scan and tells you
  to revoke the secret.
* **Least-privilege identities.** The API can write `scans`, read final `outputs`, and touch Cosmos,
  and has no access to intermediate `crops`. Only the worker can write `outputs`; Team A can read
  `scans` and nothing else.
  Neither project has any Cosmos role assignment, so neither can read job state.
* **Team B holds no storage role at all.** Its agents receive each card as an uploaded file, so
  there is nothing to grant. This is stronger than the container-scoped read it used to have:
  read on `crops` would have let Team B enumerate *every* card of *every* job, where now it sees
  only the single crop the orchestrator chose to send it, for as long as that run takes.
* **One public entry point.** Storage and Cosmos are both `publicNetworkAccess: Disabled` and
  reached only over private endpoints; every Container Apps environment sits on a subnet of this
  VNet, so that is where their outbound traffic goes. The client's entire attack surface is one
  authenticated HTTPS API.
* **Each team operates its own environment.** Team A's containers run in `<prefix>-cardgeo-env` in
  Team A's resource group, with their own log workspace. Team B cannot restart them, change their
  scale, read their environment variables or read their console output.

## Deploying

See **[azure-setup.md](azure-setup.md)** for the full walkthrough: owner groups, app
registrations, the deployment itself, the agents, and the desktop client's config file. The short
version, once `infra/main.parameters.json` is filled in:

```powershell
az deployment sub create `
  --location eastus2 `
  --template-file infra/main.bicep `
  --parameters infra/main.parameters.json
```

That creates the container registry but no images, so every container app starts on a placeholder.
Build the five images with `az acr build` and redeploy with `imageTag` set — the setup guide gives
the commands.

Then create the agents from the definitions in `agents/cardgeo/` and `agents/cardid/`, and put the
resulting agent ids into the worker's `ScanPipeline:BoundaryAgentId` and
`ScanPipeline:IdentificationAgentIds` settings (the defaults assume the agent *names* are usable as
ids).

Every service authenticates with a **user-assigned managed identity** — one per role, created
before the compute so the role assignments survive redeploys. Team A's identity lives in Team A's
resource group, which is precisely why Team B cannot grant itself anything on it.

The account topology is a parameter, not a decision baked into the templates:

```powershell
# One account per team instead of one shared account.
az deployment sub create `
  --location eastus2 `
  --template-file infra/main.bicep `
  --parameters infra/main.parameters.json `
  --parameters singleAccount=false
```

Nothing in `src/` knows how many accounts exist. The worker holds one client per project endpoint,
and those endpoints keep their shape either way.

## "Break it" scenarios

These are the point of the demo. Run a scan first so the audience sees the happy path.

### 1. Revoke the orchestrator's access to Team A's agent

```powershell
./scripts/deploy-images.ps1 -SkipBuild -GrantGeometryAccess $false
```

Use the script rather than a bare `az deployment sub create`: that would deploy `imageTag`'s empty
default and send all five container apps back to the placeholder image, which breaks the demo in a
far less interesting way.

Bicep deletes the `Foundry User` assignment on the `cardgeo` project. (RBAC changes can take a
minute or two to propagate; re-run the scan until it fails.)

Then scan an image again. Expected result:

* The job stops in **`DetectingBoundaries`** and moves to `Failed`.
* `GET /jobs/{id}` reports an error beginning
  `Foundry authorization failure (401).` followed by the failing request and the `cardgeo`
  project endpoint, so the revoked boundary is named explicitly. Foundry answers a missing project
  role with 401 and `PermissionDenied` rather than the 403 the shape of the problem suggests.
* The desktop app shows that message and offers to retry — grant the role back, click **Yes**,
  and the same job flow succeeds without restarting the app.

This is the whole point: Team B's pipeline degrades at a well-defined seam with a legible error,
rather than silently returning wrong answers or leaking through some other path. The worker
raises this from a single client instance bound to the `cardgeo` endpoint, so there is no
ambiguity about which boundary was crossed.

To restore access, run the same script with `-GrantGeometryAccess $true`.

### 2. Call Team B's MTG catalogue from Team A's project

In the Foundry portal, open the **`cardgeo`** project → `CardBoundaryAgent` → **Tools** and try to
add the `mcp-cardcatalog-mtg` server. The server lives in Team B's resource group, so it is not
among the connections Team A can select.

The stronger version of this is to hand Team A the URL anyway and call it directly:

```powershell
$token = az account get-access-token --resource "api://$prefix-mcp" --query accessToken -o tsv
$url = az containerapp show -g "$prefix-cardid" -n "$prefix-mcp-cardcatalog-mtg" `
  --query properties.configuration.ingress.fqdn -o tsv
Invoke-WebRequest -SkipHttpErrorCheck -Method Post `
  -Uri "https://$url/mcp" `
  -Headers @{ Authorization = "******"; Accept = 'application/json, text/event-stream' } `
  -ContentType 'application/json' -Body '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
```

Expected result: **403 Forbidden**, even though the URL is reachable and the token is valid. Each
server checks the caller's object id against an allow-list built by `infra/main.bicep`, and Team
A's principals are deliberately absent from Team B's catalogue servers. That list is the enforced
form of the `allowed_callers` key in `agents/cardid/mcp-cardcatalog-mtg.yaml`;
`agents/cardgeo/card-boundary-agent.yaml` records the same boundary from the other side in
`denied_connections`.

Worth showing the two failures side by side: without a token the same call is a **401**, with one
it is a **403**. The first says the caller is unknown, the second says the caller is known and
still not allowed — which is the distinction the whole demo is about.

Two useful follow-ups with the same shape:

* Have Team A's identity try to read a document from the Cosmos `jobs` container → 403, because
  `infra/modules/data-rbac.bicep` creates no Cosmos role assignment for either project identity.
* Have Team A's identity try to write to the `crops` container → 403; only the worker holds
  `Storage Blob Data Contributor` there. This is why the boundary agent returns geometry only and
  the worker does the cropping: the alternative would hand Team A blob-write rights just to save
  a hop.
* Have **Team B's** identity try to read anything in storage → 403 on every container, because it
  holds no storage role whatsoever. Then point at the same job completing successfully: a team can
  do its work without any standing access to the data it works on, because the orchestrator hands
  it one card at a time and takes it back afterwards.

### 3. Show what the project boundary does *not* isolate

Only exists in the default single-account layout, and it is the most interesting thing that layout
buys you: the two projects share one model deployment and one pool of quota.

```powershell
az cognitiveservices account deployment list `
  --name "$prefix-ai" --resource-group "$prefix-platform" -o table
```

One `gpt-4o` deployment, parented to the **account**, serving both projects. So:

* Either team can saturate the shared TPM quota and throttle the other. Run a batch of scans from
  `cardid` while someone invokes `CardBoundaryAgent` from `cardgeo` and watch the 429s cross the
  boundary that the RBAC scenarios just proved was airtight.
* A subscription Owner — or anyone with write access on the account — can change the model
  version or delete the deployment out from under both teams. Project Manager on a project does not
  grant that, which is why the account belongs to the platform team in this layout.

The point to land: a project isolates **agents, connections and authoring**. It does not isolate
**quota, model deployments, account settings or blast radius**. If the teams need those separated
too, they need separate accounts — redeploy with `singleAccount=false` and re-run this scenario to
show two independent deployments and two quota pools.

## Result contract

Frozen in `src/Enfolderer.Ai.Contracts/ScanContracts.cs` as `schemaVersion: 1`:

```json
{
  "schemaVersion": 1,
  "jobId": "…",
  "status": "Completed",
  "imageWidth": 4032,
  "imageHeight": 3024,
  "cards": [
    {
      "index": 0,
      "quad": { "points": [ {"x":10,"y":20}, {"x":110,"y":25}, {"x":112,"y":165}, {"x":8,"y":160} ] },
      "game": "mtg",
      "set": "bro",
      "collectorNumber": "167",
      "name": "Ancient Silver Dragon",
      "language": "en",
      "finish": "nonfoil",
      "confidence": 0.94,
      "agent": "cardid/MtgCardIdAgent"
    }
  ]
}
```

`agent` names the agent that produced each entry, which makes the boundary-vs-identification split
visible in the output: unidentified cards come back attributed to `cardgeo/CardBoundaryAgent` with
geometry but no name, so the audience can see exactly where a card was lost.

Contract deserialisation, the polling state machine and the card mapping are covered by
`Enfolderer.App/Tests/AiScanClientTests.cs`, which runs as part of `--selftests`.

## Scan reliability and printing evidence

A terminal Foundry run with status `failed` and error code `server_error` is retried twice,
after one and two seconds. Retries reuse the same thread and uploaded image; cleanup still runs
on failure or cancellation. Throttling has its own existing bounded retry budget. Invalid requests,
authorization failures, cancelled runs, and HTTP 5xx responses are not replayed as terminal server
errors: an HTTP failure does not prove that a run has stopped. Exhausted retries remain per-card
errors, not invented identifications.

A completed run can still return malformed JSON. The worker validates the boundary/identification
reply, logs a bounded excerpt with thread/run IDs, asks for a complete replacement, and retries
once on the same thread/images. It never repairs JSON by appending braces. A second invalid reply
fails explicitly. Text parts are combined only from the current run; older assistant messages
are not mistaken for its result. Terminal `incomplete` runs report their incomplete details rather
than waiting until timeout. Empty unresolved identifications get the same bounded re-read.

Image 04's captured failure was a completed boundary run with missing object closures and
confidence/gameHint nested in quad. It was not a token-limit truncation. Geometry on image 05
also remained sideways despite the orientation instructions. Footer reading now includes the
whole bottom quarter (including retro-frame centre/right collector numbers), with both candidate
quarter-turn footers for landscape crops. Art checks compare both quarter-turns before resizing.
There is no guessed page-specific rotation.

Art verification uses a 16-by-16 difference hash and compares bounded alignments of the crop:
75-100% of its width and height, with offsets inside the crop. This tolerates sleeve margins and
imprecise boundaries without lowering the acceptance gates: the best image's distance must still
be at most `0.30`, and its lead over the next candidate at least `0.06`. Shared-art printings remain
inconclusive. If a candidate image cannot be read, the check does not move the printing on the
strength of an incomplete field.

Each card's resolution log includes `readSet`, `readCollectorNumber`, `readName` (the original
photographed title), `readLanguage` (the footer code), and confidence. MTG language comes from
the photographed footer/title/rules, not the catalogue's English name or an English fallback
printing. Printed `JP`/`JPN` normalizes to API `ja`. Missing language/title evidence or a language
contradicting the footer or Japanese kana triggers one logged re-reading attempt on the same
images; persistent contradictions fail explicitly instead of returning a default English row.
Deploy the worker image **and** re-provision the identification prompt together for this contract
change (`.\scripts\update-demo.ps1`, without either skip switch). Additional
art logs report every candidate's distance, the closest printing, the separation, both thresholds,
and why the comparison declined. Result JSON adds optional `artDistance` and `artReason` fields;
the CSV format is unchanged. Reasons include `distance_too_large`, `separation_too_small`,
`missing_candidates`, `insufficient_candidates`, `no_references`, `no_fetchable_references`,
`no_images_readable`, and `crop_unreadable`. An inconclusive check is **not** proof of shared art.

Resolution explanations are specific to the reported outcome: `ambiguous` means the readings
did not distinguish multiple printings; `assumed` means the name was not corroborated and a
number lookup supplied the printing. Neither means that no set was read. Only `unplaced`
describes an unplaced name's catalogue default. `relocated` describes a replacement of the read
set, and `corrected` describes the name winning over a collector-number reading.

The worker logs the start of each card and persists `cardsProcessed` (including identification
failures) and `cardsIdentified` between cards. The API exposes both counters. Desktop polling
refreshes elapsed time and the counters on every response, even when the job phase has not
changed; it does not claim to stream individual model/tool operations. The final status names
the saved CSV and retains review counts. Deploy the API/worker and rebuild the desktop app to
enable this end-to-end feedback; older API responses remain compatible with zero progress.

### Priced image output

Team B's catalogue exposes `get_printing_prices` for exact set/collector-number/language queries.
The worker shares its deterministic `ScryfallPriceClient` implementation, without an additional
model call or copying monetary amounts through an agent response. Prices are fetched only after
the final art decision. The identification agents' tool allow-lists remain unchanged.

`prices` on each card carries separate nullable `nonfoil`/`foil` EUR values, source language for
each finish, `retrievedAt`, attribution, and any lookup error. Each missing foreign finish falls
back independently to English for the same printing; valid foreign values are preserved if an
English fallback request fails. A missing foil or nonfoil price is normal, not an error or zero.
No currency conversion, USD fallback, or cross-finish substitution takes place.

The deterministic PNG renderer places numbered price panels over the artwork and appends a
legend containing identities, language/fallback provenance, uncertainty, finish, and retrieval
time. The input photo is not overwritten; the rendered photo is limited to a 2400-pixel long edge
before adding the legend. The worker image includes DejaVu Sans; Windows uses Arial if DejaVu is
not installed. Labels use catalogue English names, not the original Japanese title.

The private `outputs` container grants only the worker write access and API read-only access.
The authenticated job download route derives its blob path from the stored job id, never from an
arbitrary URL. The desktop limits downloads to 64 MB, requires PNG content, saves the CSV before
attempting image downloads, and reports failed pricing/rendering/downloads without losing it.
Existing scans without the optional image fields remain compatible.

Deploy container images and the storage/RBAC template together:
`.\scripts\update-demo.ps1 -SkipAgents`. Rebuild/restart the desktop app afterwards. No new
Foundry project, prompt agent, or model deployment is required.

Offline regression tests cover both finish fallbacks, single-finish availability, missing
localized printings, duplicate-query caching, identity/language preservation, rendering,
download routing/authentication, and optional-output failures. To also write a **synthetic
test-price** preview for visual inspection (not a real valuation):

```powershell
dotnet run --project tools\Enfolderer.Ai.CardCatalog.Check -- --price-render-check test-prices.png
```

Run the offline retry, cancellation, download-failure, and image-comparison checks:

```powershell
dotnet run --project tools\Enfolderer.Ai.CardCatalog.Check
```

For the real-photo regression, including all nine cards on training page 01 and unrelated-card
negative controls:

```powershell
dotnet run --project tools\Enfolderer.Ai.CardCatalog.Check -- --photo-art
```

This tests the stored page 01 photograph with both captured live-scan boundaries and authoritative
measured crops, ensuring that all nine printings remain correct under the new geometry policy,
then fetches public Scryfall catalogue images into memory without saving them. It exercises the
worker's actual art verifier, not mocked distances. It does not call Foundry or deploy anything.
The default checks remain offline.

After deploying the worker image and rescanning, read the evidence with
`.\scripts\show-last-scan.ps1`. Its Log Analytics query is sent as a JSON file so Windows
`az.cmd` cannot interpret the KQL pipes as shell commands.

The script includes job start/completion/failure events and selects the latest attempt by its
first event, not whichever older job logged last. It displays boundary-stage failures even when
no card resolution exists. Azure CLI writes query results to a UTF-8 file, avoiding Windows
code-page loss of Japanese and accented text. Test it offline with:

```powershell
.\scripts\test-show-last-scan.ps1
```

Compare a new exported CSV with its sibling `cards.txt`, ignoring finish and row order while
preserving duplicate counts. Japanese `jp`/`jpn` annotations normalize to API `ja`; the two
Undercity Sewers annotations on page 06 are Spanish `es`, as printed:

```powershell
dotnet run --project tools\Enfolderer.Ai.Imaging.TrainingCheck -- --results training\06\scanned_cards12101110.csv
```

This reports identity, identity+language and printing+language separately and exits nonzero when
any exact printing/language or card count is wrong. The original photographed results remain
0/12 exact printings on page 05 and 4/8 on page 06; annotation normalization does not hide them.

Known limitation: rotation-aware whole-face matching alone cannot establish multilingual reading
accuracy. The following check exercises art verification with supplied identities and `ja`
language, not the live model's ability to read language:

```powershell
dotnet run --project tools\Enfolderer.Ai.CardCatalog.Check -- --photo-multilingual
```

It uses authoritative measured crops; historical boxes locate only the annotated identities.
A pass means the verifier preserved those supplied printings/languages, not that the agent read
them correctly. Use `--live-reading` below or an actual deployed scan to verify language detection.

Live sampled footer re-reading with the existing GPT-4o agent did not resolve this either and
could turn a catalogue default into a falsely "confirmed" printing. Therefore automatic retries
of merely unplaced printings were tested and rejected. Uncertainty stays visible; art acceptance
thresholds have not been relaxed. A narrower upper-face hash was also rejected because it did not
resolve these cases. Further reading/model work is required before claiming multilingual printing
accuracy is fixed.

Before measured-region retention, a live image 04 boundary check returned only 6 of the 8 annotated cards, although the
local measured detector finds all 8 (and all 47 cards across pages 01-06). Bounded JSON recovery
addresses malformed responses, not unjustified dropping of real measured regions. The approved
change now makes measured regions authoritative, retaining them even if the agent
returns an empty list. This can retain false positives, which identification must decline; it does
not solve multilingual printing accuracy. The updated adapter's live image 04 check retains 8/8.

The offline real-photo adapter regression deliberately supplies an empty agent response for each
training page and verifies that all 47 measured regions survive:

```powershell
dotnet run --project tools\Enfolderer.Ai.CardCatalog.Check -- --photo-boundaries
```

For explicitly selected live reading tests (uses Azure CLI sign-in, calls the supplied Foundry
agent, and incurs model usage without redeploying):

```powershell
dotnet run --project tools\Enfolderer.Ai.CardCatalog.Check -- --live-reading <project-endpoint> <agent-id> training\05 0,1,2
```

Indices refer to local detector order, not annotation order. Each selected result must match a
listed name/set/number/language and be placed; otherwise the check fails.

## MTG finish evidence

MTG identification uploads an additional nearest-neighbour enlargement of the full-width
bottom quarter of the rectified card (two stacked candidate footers if sideways). It is supplied in the same message as the full card,
not treated as another card. Both uploads are reused during run retries and deleted afterwards.
The enlargement does not sharpen, reconstruct, or restore details lost in the original photograph.

The MTG prompt asks for `finishMarker` (`dot`, `star`, `absent`, or `unknown`), `finishEvidence`,
and `finishConfidence` separately from identity confidence. A reliable dot between the set code
and language establishes `nonfoil`; a reliable star establishes `foil`, not `etched`. Symbol size
relative to the height and width of neighbouring letters on the same line can distinguish a
blurred round star from a small dot without visible star points. The footprint must be isolated
from adjacent letters and reflections; no absolute pixel threshold is used. Each identifier sees
only its own card, so this comparison does not use neighbouring cards on the binder page.
Rarity letters, collector suffixes, other punctuation, glare, sleeve reflections,
and the holographic authenticity stamp must not be mistaken for the finish marker.

The worker accepts a marker only at finish confidence `0.8` or above, and derives the finish
itself rather than trusting a contradictory model finish. Without other accepted finish evidence,
absent, unreadable, invalid or low-confidence readings leave finish null and evidence `unknown`.
On older frames, the full-card image is inspected for the large Wizards of the Coast shooting-star
watermark with its sweeping trail across the lower text box. Evidence `shooting_star_watermark`
with marker `absent` is accepted at the same confidence threshold and establishes `foil`, not
`etched`, without requiring visible rainbow sheen. Missing or unreadable watermarks never prove
nonfoil. Other explicitly reported visual foil/etched treatment can be retained at the same
threshold with evidence `visual`; absence of visible sheen never proves nonfoil.
These are model-reported evidence and confidence, not a calibrated pixel classifier.

The result contract preserves these fields, the worker logs them per job/card, and
`scripts/show-last-scan.ps1` displays them. The desktop exports `foil`/`etched` into the existing
modifier column. Unknown and nonfoil both use the compatible blank modifier, but unknown finishes
are named in the completion dialog for manual review.

Offline checks cover marker interpretation, uncertainty, JSON round-tripping, footer extraction,
two-image message construction and retry/cleanup, plus desktop CSV mapping. Actual marker-reading
accuracy on blurry photographs still needs a fresh deployed scan.

This change needs both updated images and re-provisioned MTG instructions: run
`.\scripts\update-demo.ps1 -NamePrefix enf-demo` without `-SkipAgents`, then rebuild/restart the
desktop app to get finish-aware CSV output and warnings.

## Running locally without Azure

Leave `ScanPlatform:CosmosEndpoint` and `ScanPlatform:StorageAccountUrl` empty. The API and worker
fall back to an in-memory job store, a local directory image store and a directory-backed queue,
and the worker uses stub agents when no Foundry endpoints are configured — enough to exercise the
desktop upload/poll loop end to end.
