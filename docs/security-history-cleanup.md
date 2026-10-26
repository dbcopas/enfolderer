# Published-data cleanup status and recovery plan

## Current status

The bounded security review found no confirmed embedded API key, password, private key or bearer
token in the scanned tracked text and available history. This is not proof that none exists
anywhere, nor a guarantee about live service access controls.

The current photo in `training/05/image.jpg` has been sanitized without re-encoding its pixels.
The approved branch histories now use that sanitized version too. Both `mainDb.db` and `mtgstudio.collection`
remain unchanged at the user's request and contain collection/ownership data. Their removal or
sanitization is **not approved**.

Live service hardening was completed first using genuine Foundry agent identities. On 2026-10-16,
the user then approved replacing the original photo blob on exactly two published branches and
force-updating those branches with exact SHA leases. That rewrite is now published.

## Completed GPS-history cleanup

- Created and verified a local mirror backup restricted to the current Windows account.
- Replaced only the original `training/05/image.jpg` blob with its losslessly sanitized version.
  `git-filter-repo` was unavailable, so Git's built-in index filter ran in a separate mirror.
- Preserved all commit authors, committers, timestamps, messages and parent topology across both
  branches. Rewritten commit IDs change; their old cryptographic signatures cannot remain valid.
- Verified all 205 rewritten commits: no other historical file content changed. The `ai_agents`
  tip tree was identical before and after rewriting, including both collection databases.
- Release build and offline security/Responses/training checks passed, retaining 57/57 cards.
- Atomically published only `ai_agents` and `copilot/ensure-ai-service-functionality` using exact
  expected-old-SHA leases. Main, all tags, releases and other branch refs were not changed.
- Rewritten tips at publication were `63306279dd6051cf1bc05b73656f64ca86048a5b` and
  `a17be10ecc5682f2fcff9e2dd1ee44514d74b722`, respectively. The second branch is PR #3's source.
- A fresh remote clone passed integrity checks and contained only the sanitized photo across
  all branches and tags. PR #3's head updated, but GitHub's read-only `refs/pull/3/merge` still
  pointed to the old merge commit `bdfba24ea4c0c637079d28dc98dbfca497e8a4bc`, which contains
  the original photo. This remaining platform-owned reference needs GitHub cleanup.

Collaborators should re-clone or carefully transplant unpublished work onto the rewritten history;
do not merge an old clone's branch back. The restricted backup and commit/ref maps contain the
original data and must not be published. GitHub-owned PR refs and cached objects are outside the
branch force-push scope: follow up with GitHub Support for sensitive-data cleanup as appropriate.
No release assets were deleted, and no main merge was performed.

## Procedure for any additional history cleanup

1. Pause merges and pushes. Confirm repository visibility, forks, open pull requests, all branch/tag
   refs containing the original photo, and whether downloadable release assets contain it.
2. Approve the exact scope: replace historical versions of the affected photo with sanitized
   versions, preserving each version's pixels and orientation. If removal is preferred, approve
   removing that path from history instead. Do not remove unrelated files or collection databases.
3. Make an access-restricted backup of the repository and record all remote ref SHAs for recovery.
   The backup contains the sensitive originals and must not be published.
4. In a separate mirror clone, use `git-filter-repo` to rewrite only the approved blobs/paths.
   Do not run the rewrite in the development checkout.
5. Scan rewritten reachable objects for GPS metadata and credentials, and run the training,
   application build and security regression checks on the cleaned branch tip.
6. Review the before/after ref map and get explicit approval for the specific branches/tags
   to force-update. Coordinate any temporary protection changes with the repository owner.
7. Update only those approved refs. Remove affected downloadable assets only if separately
   approved. Contact GitHub Support about cached/PR refs where applicable; coordinate fork owners.
8. Require collaborators to re-clone or carefully migrate their work without merging the old
   history back. Verify the remote refs and repeat the scan.

Rewriting Git cannot recall copies already downloaded, cached, forked or cloned. For location data,
assume the original information may already have been copied. If credentials are ever discovered,
revoke/rotate them first; history cleanup alone does not make an exposed credential safe.

## Collection data

No changes are planned without a separate decision. An eventual sanitization proposal must first
inventory the exact quantity/holding tables and relationships, preserve the catalogue and schema
needed for builds, and preview the affected rows/counts without publishing private values.
Back up the user's working inventory outside the repository before changing distributed copies.
Already-published database versions and release packages would require their own approved cleanup.

## Deployment follow-up

This section records the original 2026-10-16 identity migration. The current integrated
dedicated AI Gateway preview uses encrypted gateway-key project connections and a separate
managed identity per tool backend. Original agent callers remain for controlled compatibility;
worker/project identities remain excluded. It does not forward the original bearer identity.
See the [current gateway trust boundaries and lifecycle](../README.md#integrated-dedicated-ai-gateway-preview);
the dated evidence below is not a claim about the newer gateway hop.

Read-only inspection on 2026-10-16 confirmed public ingress and an empty `McpServer__Audience`
on all three demo MCP services. That was an active exposure, not just a template default.

The selected migration is the new Foundry agent object model with stable agent Responses endpoints.
The geometry, Magic and Pokemon agents receive distinct Entra Agent IDs and blueprints.
`AgenticIdentityToken` project connections acquire short-lived tool tokens inside Foundry; the
worker does not mint or forward the tool tokens. Each MCP server's allow-list contains only its
matching agent principal. Project/worker managed identities are not substitute callers.

Completed staging evidence on 2026-10-16:

- The tenant provisioned a distinct agent identity and blueprint, and its stable endpoint executed.
- A credential-free, single-tenant MCP resource registration issues v2 access tokens.
- A separate authenticated MCP host rejected anonymous initialize and a wrong-audience token
  with 401. Foundry's call from a valid but unlisted geometry agent failed with downstream 403.
- An allowed MTG agent used the real Responses client, inline image/footer input and MCP approval
  continuation to identify `mh3/233` as Ugin's Labyrinth with a confirmed printing.
- Background response polling and deletion succeeded against Azure, not only mock HTTP.
- Release build, local API/MCP ownership/auth tests, new Responses tests and all 57 training-photo
  boundary checks passed. Existing dependency advisories remain; this is not a clean vulnerability bill.

Rollout/verification:

Production rollout completed on 2026-10-16 with all five images at `v20261016-1246`.
All three production MCP servers passed anonymous/wrong-audience 401 checks, with exact
per-agent principal allow-lists and agent-identity connections. A genuine unlisted geometry agent
was refused catalogue access with downstream 403. Production geometry retained photo 07's ten
physical cards. The obsolete worker `Foundry User` grants were removed from both projects.

The user then confirmed a fresh desktop scan, CSV and marked-up image all worked as expected.
Worker logs corroborate job `1ac41ea6db0e436d85ccbb0e1cbf9d11`: eight detected, eight identified,
priced image stored, completed in 133,834 ms of worker processing. No response-cleanup or
authorization-failure events appeared in the checked worker log window. This verifies the
Consumer-only worker path end-to-end; it does not establish every printing as certain.

Temporary staging app, agent, connection and image have been removed. At rollout completion,
history cleanup had not yet started. Two-real-user API isolation is still not live-tested; the local HTTP integration checks
cover it, and the successful desktop scan verifies owner access.

A fresh-shell `update-demo.ps1 -SkipImages` replay also completed successfully: it rediscovered
the deployed configuration, retained all three agent principals and the MCP audience, reconciled
worker access, and confirmed healthy services without rebuilding images. No shell-only values
need to be saved for this update path; Azure sign-in is still required.

1. `update-demo.ps1` provisions named agents/connections before building and deploying all five
   services. Audience and distinct caller IDs become recorded deployment parameters, so normal
   subsequent updates preserve the boundary. Missing identities/URLs abort rather than dropping tools.
2. The worker uses `Foundry Agent Consumer` (`endpoints/interact/action`), not the authoring-capable
   `Foundry User` role. `sync-foundry-worker-access.ps1` removes obsolete exact worker/project
   grants after requiring the replacement Consumer assignments. Incremental ARM alone does not
   delete omitted assignments.
3. Run `scripts/test-scan-access.ps1` against the production apps to verify health, anonymous and
   wrong-audience rejection, exact principal allow-lists and agent-identity connections.
4. Test two actual API users: the owner can create/upload/submit/poll/download, while the second
   user and another tenant cannot. Ownerless legacy jobs remain denied; start new scans rather
   than granting ownership based on knowledge of a job ID.
5. Repeat the full scan and boundary-isolation demo, verify private image access, and inspect
   authorization logs. Rollback must not restore anonymous public MCP access; disable ingress
   if authentication cannot be made to work.

The local two-user checks use test-only identities and in-memory stores. They are not evidence of
a live two-human-user ownership test. Azure CLI cannot acquire a token for this custom scan API
with its existing consent; use the already-configured desktop sign-in rather than weakening API
authentication or granting extra CLI access just for testing.

The identity architecture aligns with Agent 365, but Agent 365 onboarding, telemetry and Microsoft
365 publication are separate work. Creating an Agent ID does not imply that those steps occurred.
