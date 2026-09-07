# Foundry multi-project demo: geometry vs. identification

This repository contains a working example of **two Azure AI Foundry projects owned by two
different teams**, cooperating on one workload while being unable to reach into each other's
assets. The workload is the Enfolderer card scanner: photograph a page of collectible cards, get
back JSON listing every card.

## The two teams

| | Team A — geometry | Team B — identification |
|---|---|---|
| Foundry project | `cardgeo` | `cardid` |
| Resource group | `<prefix>-cardgeo` | `<prefix>-cardid` |
| Owner group | `teamAGroupObjectId` | `teamBGroupObjectId` |
| Agents | `CardBoundaryAgent` | `OrchestratorAgent`, `MtgCardIdAgent`, `PokemonCardIdAgent` |
| MCP servers | `mcp-imaging` | `mcp-cardcatalog-mtg`, `mcp-cardcatalog-pokemon` |
| Data access | read `scans` | read `crops` |
| Job state (Cosmos) | **none** | **none** |

Team A's skill is *finding cards*: any game, any frame, borderless and full-art printings, cards
at oblique angles or rotated relative to each other. It returns geometry only — four corner
points per card — and knows nothing about card catalogues.

Team B's skill is *reading cards*: given a rectified crop, work out the set, collector number and
name, using a per-game agent backed by that game's catalogue MCP server.

Adding a game means adding one agent definition plus one MCP server under `agents/cardid/`.
`YugiohCardIdAgent` and `LorcanaCardIdAgent` are checked in as growth slots to show that the
reuse story does not require touching Team A at all.

## End-to-end flow

1. Desktop app (`Scan Card Image…`) signs the user in with Entra ID and calls `POST /jobs`.
2. The API creates the job document in Cosmos and returns a **write-only, single-blob,
   short-lived user-delegation SAS** for `scans/{jobId}/{filename}`.
3. The app uploads the image directly to Blob Storage — the API never sees the bytes.
4. `POST /jobs/{id}/submit` enqueues the job; the worker picks it up.
5. Worker → `DetectingBoundaries`: calls Team A's `CardBoundaryAgent` through the `cardgeo`
   project endpoint with a read-only SAS for the scan.
6. Worker crops each quadrilateral itself (perspective-correct warp) and writes the crops to
   `crops/{jobId}/`. Team A never gets blob write access.
7. Worker → `Identifying`: fans the crops out to the per-game agents in the `cardid` project.
8. Worker → `Completed`, writing the versioned result document to Cosmos.
9. The app polls `GET /jobs/{id}` every 2s with exponential backoff and maps `cards[]` into the
   CSV shape the importer already understands.

## Why the boundaries are real

* **Separate resource groups with separate owner groups.** Team B has `Azure AI Project Manager`
  only on its own account, so it cannot edit, redeploy or read the instructions of Team A's
  boundary agent.
* **Invoke-only cross-project access.** `infra/modules/cross-project-access.bicep` grants the
  `cardid` project identity `Azure AI User` on the `cardgeo` account — enough to run the agent,
  not enough to change it. It is deployed *into Team A's resource group*, because only Team A can
  grant access to Team A's assets.
* **Entra-only data plane.** The storage account has `allowSharedKeyAccess: false` and Cosmos has
  `disableLocalAuth: true`. There are no keys or connection strings to copy into a config file.
* **No secrets in the desktop app.** `aiconfig.txt` carries only an API URL, tenant id, public
  client id and scope. Sign-in is interactive (`InteractiveBrowserCredential`) or device code.
  If an old file still contains `client_secret`, the app refuses to start the scan and tells you
  to revoke the secret.
* **Least-privilege identities.** The API can mint SAS tokens and touch Cosmos but has no blob
  data role. Team A can read `scans` and nothing else. Team B can read `crops` and nothing else.
  Neither project has any Cosmos role assignment, so neither can read job state.

## Deploying

```bash
az deployment sub create \
  --location eastus2 \
  --template-file infra/main.bicep \
  --parameters infra/main.parameters.json
```

Fill in `teamAGroupObjectId`, `teamBGroupObjectId` and `apiClientId` first. Then create the agents
from the definitions in `agents/cardgeo/` and `agents/cardid/`, and put the resulting agent ids
into the worker's `ScanPipeline:BoundaryAgentId` and `ScanPipeline:IdentificationAgentIds`
settings (the defaults assume the agent *names* are usable as ids).

## "Break it" scenarios

These are the point of the demo. Run a scan first so the audience sees the happy path.

### 1. Revoke Team B's access to Team A's agent

```bash
az deployment sub create \
  --location eastus2 \
  --template-file infra/main.bicep \
  --parameters infra/main.parameters.json \
  --parameters grantIdentificationAccessToGeometry=false
```

Bicep deletes the `Azure AI User` assignment on the `cardgeo` account. (RBAC changes can take a
minute or two to propagate; re-run the scan until it fails.)

Then scan an image again. Expected result:

* The job stops in **`DetectingBoundaries`** and moves to `Failed`.
* `GET /jobs/{id}` reports an error beginning
  `Foundry authorization failure (403).` followed by the failing request and the `cardgeo`
  project endpoint, so the revoked boundary is named explicitly.
* The desktop app shows that message and offers to retry — grant the role back, click **Yes**,
  and the same job flow succeeds without restarting the app.

This is the whole point: Team B's pipeline degrades at a well-defined seam with a legible error,
rather than silently returning wrong answers or leaking through some other path. The worker
raises this from a single client instance bound to the `cardgeo` endpoint, so there is no
ambiguity about which boundary was crossed.

To restore access, redeploy with `grantIdentificationAccessToGeometry=true`.

### 2. Call Team B's MTG catalogue from Team A's project

In the Foundry portal, open the **`cardgeo`** project → `CardBoundaryAgent` → **Tools** and try to
add the `mcp-cardcatalog-mtg` server (or, from a shell using Team A's identity, call its
`lookup_by_set_and_number` tool).

Expected result: **403 Forbidden**. The MCP server lives in the `cardid` project, and Team A's
identity has no role assignment there — the server is not even listed among the connections Team
A can select. `agents/cardgeo/card-boundary-agent.yaml` records this in `denied_connections`, and
`agents/cardid/mcp-cardcatalog-mtg.yaml` records the inverse in `allowed_callers`.

Two useful follow-ups with the same shape:

* Have Team A's identity try to read a document from the Cosmos `jobs` container → 403, because
  `infra/modules/data-rbac.bicep` creates no Cosmos role assignment for either project identity.
* Have Team A's identity try to write to the `crops` container → 403; only the worker holds
  `Storage Blob Data Contributor` there. This is why the boundary agent returns geometry only and
  the worker does the cropping: the alternative would hand Team A blob-write rights just to save
  a hop.

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

## Running locally without Azure

Leave `ScanPlatform:CosmosEndpoint` and `ScanPlatform:StorageAccountUrl` empty. The API and worker
fall back to an in-memory job store, a local directory image store and a directory-backed queue,
and the worker uses stub agents when no Foundry endpoints are configured — enough to exercise the
desktop upload/poll loop end to end.
