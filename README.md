# Enfolderer

Enfolderer is a Windows desktop app for arranging a Magic: The Gathering collection into virtual binders. Give it a small text file describing the cards and their order; it resolves card data and images from Scryfall, then displays paginated binder spreads that match your physical filing system.

![Enfolderer screenshot](https://github.com/user-attachments/assets/0a6ef719-fce1-4905-a152-0cc9e9dd308c)

## The core workflow

1. Create a binder definition (`.txt`) for a set or collection.
2. Open it in Enfolderer.
3. Browse the resulting binders, update quantities by clicking cards, and use the display to organize your physical collection.

The app preserves your requested ordering while keeping physical two-sided cards together and aligning pairs within binder rows. It automatically adds binders as the list grows.

## Getting started

Enfolderer requires Windows and the .NET 8 Desktop Runtime. Published builds include `mainDb.db` and `mtgstudio.collection`; keep both files beside `Enfolderer.App.exe`.

Run the app, then choose **Open** from the toolbar and select a binder definition file. The repository includes examples such as `binder_alt_arts.txt`, `binder_promos.txt`, and `binder_secret_lair.txt`.

If MTG Studio maintains your collection database somewhere else, place a symbolic link named `mtgstudio.collection` beside the executable instead of copying it:

```powershell
New-Item -ItemType SymbolicLink -Path .\mtgstudio.collection -Target "C:\Path\To\Your\mtgstudio.collection"
```

## Binder definition files

A binder file is a plain-text list organized into set sections. Enfolderer obtains card names, layouts, and images from Scryfall unless you provide an explicit entry.

```text
# Strixhaven Mystical Archive
=STA
1-10
11-20||50-55
296-298&&361-363

# A named entry that does not call Scryfall
Dragon Token;TOKEN;1

# Five empty card-back slots
5;backface
```

### Common syntax

| Syntax | Meaning |
| --- | --- |
| `=SET` | Begin a set section; applies until the next set section. |
| `123` | Add one collector number. |
| `001-010` | Add an inclusive range; matching zero padding is retained. |
| `1-5\|\|30-34` | Interleave sequences. |
| `296-340&&361-405` | Pair two ranges for composite display numbers. |
| `J1-5`, `RA 1-8`, `2-5J-b` | Prefixes and suffixes in collector numbers. |
| `★1-3` | Display star-suffixed collector numbers. |
| `123;Custom Name` | Use a custom display name while retaining Scryfall metadata. |
| `Name;SET;Number` | Add an explicit card entry without an API lookup. |
| `N+lang` | Add a normal card plus a language variant. |
| `N;backface` | Add `N` fixed card-back placeholder slots. |
| `# comment` | Add a comment. |

An optional first non-comment line beginning with `**` configures the view:

```text
** 3x3, pages=36, Firebrick, 2E8B57
```

It accepts `4x3`, `3x3`, or `2x2`; `pages=<positive number>`; and color names or six-digit hex colors for successive binder covers. Remaining covers receive generated colors.

## Using the binder

- Choose a 4×3, 3×3, or 2×2 layout and set pages per binder from **View**.
- Move through pages and binders with the navigation controls, or jump directly to a binder and page.
- Search cards by name with **Ctrl+F**.
- Click a card to cycle its quantity between 0, 1, and 2. Quantities come from `mtgstudio.collection` for MTG Studio-backed cards and `mainDb.db` for custom/imported cards.
- Enfolderer caches metadata and images in `%LocalAppData%\Enfolderer\cache`, allowing subsequent loads of the same file to avoid repeated metadata requests.

For empty back slots, Enfolderer first looks for a local card-back image beside the collection file or executable, then in `%USERPROFILE%\Pictures\Enfolderer` and the executable's `images` directory. Supported names include `Magic_card_back.jpg`, `card_back.jpg`, and `back.jpg`; an embedded fallback is used otherwise.

## Supporting tools

**File** contains only **Exit**. **View** contains layouts and binder navigation settings.
**Scan** contains **Scan Single Image...** and **Scan Multiple Images in Folder...**; the latter
scans supported images directly in the selected folder, not its subfolders.
**Tools** always offers the **Land Viewer...** and **Token Viewer...**.
Enable **Advanced** in the main menu bar to reveal the import, export/report and collection
maintenance groups under **Tools**. Advanced is off each time the app starts.
**Help > GitHub Project...** opens this repository in your default browser.

The advanced utilities include:

- **Update Card Database from CSV...** maps MTG Studio or compatible CSV data to `mainDb.db`, with a review step before applying updates.
- **Import Scryfall Set into Card Database...** imports a set by code. Hold **Shift** while invoking it to replace existing rows for that set.
- **Import Sets Missing from Card Database** imports binder-file set codes not yet found in the local database.
- The **Export and Reports** group creates inventory and Moxfield-oriented exports, compares collection/wants CSVs, exports wants for each `binder*.txt` in a folder, and creates deck pull/missing-card reports.
- The land and token viewers open CSV-based, searchable 3×3 binder views and let you mark entries as owned.

The advanced **Collection Maintenance** actions modify the local `mtgstudio.collection`; use them only when you understand the intended database operation. **Restore Collection from Backup (.bak)...** restores its adjacent `.bak` file. **Set All Quantities to Their Card IDs (Diagnostic)...** is a diagnostic operation, not a normal inventory update.

## Scanning cards with the Azure AI pipeline

This branch supports **Magic: The Gathering only**. The website has no game selector, desktop
configuration defaults to `mtg`, and other game hints are rejected by the API before work is created.
Only imaging and MTG catalogue MCP services are built or deployed; no Pokemon hosting is required.

The API requires Entra authentication and records each job's creator tenant/object identity.
Only that identity can upload, submit, poll or download its job. Jobs created before ownership
was recorded are inaccessible through these routes; create a new scan rather than claiming an
old job by its ID. The API refuses to start without its tenant/client configuration, and public
MCP servers refuse missing tenant/audience or caller allow-lists, including local runs.
The worker uses named Foundry agent Responses endpoints. Each agent has its own Entra Agent ID;
Foundry obtains short-lived MCP tokens through `AgenticIdentityToken` project connections.
Geometry and Magic servers accept only their corresponding agent principal, not worker
or project managed identities. Run `.\scripts\update-demo.ps1` for the first migration; it provisions
identities/connections before deploying images and persists the audience and caller IDs.
See [setup and migration](docs/azure-setup.md#4-provision-agents-and-build-real-images).

**Scan Single Image...** turns a photograph into binder entries: the app uploads a single image to a
job API and polls it while a multi-agent Azure AI Foundry pipeline finds each card's outline and
identifies it. That API is the only endpoint the app talks to, so it runs from anywhere. **Scan Multiple Images in Folder...** runs the same job API once per image in
a selected folder. Both write `SET;NUMBER;MODIFIER;LANGUAGE;NAME` CSV rows; the modifier column is
always blank for import compatibility. Scanning does not determine whether a card is foiled.
Scans automatically save `scanned_cards_yyyyMMdd_HHmmss.csv`, using local date and time, beside
the selected image (or inside the selected folder for a batch). There is no output save dialog.
If that filename already exists, a numeric suffix preserves the earlier CSV. The completion
dialog shows the saved path; priced images are saved alongside it.
During processing, the status bar reports elapsed polling time, processed/detected cards, and
successful identifications (processed includes unreadable cards). The final status retains the
saved filename and counts of unidentified cards, missing numbers, printings needing review,
and art-based printing changes. Per-card progress requires the updated worker
and API as well as the desktop app.
The progress bar counts completed images rather than showing 100% when a scan starts. Hover over
the status text to read the full message if it is truncated.

### Mobile website

The API container also serves a responsive website at its HTTPS root. There is no sixth image,
extra always-on web container, Node build, public blob URL or client secret. The browser uses
Microsoft MSAL with authorization-code/PKCE, a dedicated single-tenant SPA registration, and
the existing delegated `Scan.Submit` API permission. The pinned MSAL library and its MIT license
are served locally; `scripts/restore-web-assets.ps1` restores the integrity-checked dependency.

The private pilot uses an explicit tenant user/guest object-ID allowlist. It applies to both web
and desktop API calls. An external user first accepts a tenant guest invitation and must be
assigned to the web enterprise app and included in that allowlist. Assignment-required applications
need one-time tenant-wide administrator consent; ordinary approved users need no administrator role.
Guests sign in with the invited email address, using their existing identity or an emailed
one-time passcode when enabled in Entra. A Microsoft account is not required for email-passcode guests.
See [mobile setup](docs/azure-setup.md#5-mobile-website-and-separate-desktop-configuration).

The beta welcome uses plain-language invitation guidance rather than identity-provider or
directory terminology. **How to use the scanner & understand your results** is collapsed by
default and explains photo quality, scan credits, uncertain results, prices, collection checking,
automatic saving and downloads. Tracking an existing collection requires an MTG Studio database,
exported as a mobile collection snapshot from the Enfolderer desktop client and uploaded to the
website; users must not upload the raw database. The instructions distinguish that workflow
from saving newly scanned cards, which needs no MTG Studio database. The default-off auto-save switch is labelled
**Automatically save identified cards**; its behaviour is unchanged.

**Request access** opens a public form for a name, invitation email and optional note. It works
before sign-in, including when sign-in is unavailable. The request button and form are hidden
once a signed-in account is detected, including a restored session; sign out to submit a request
for more scans. The admin inbox remains available to signed-in administrators.
Submissions are saved privately, not sent by email. Signed-in scan-allowance administrators see
**Access requests (admin)** in the mobile workspace. Open it (or use **Refresh requests**) to see
pending requests and remove them after review. Emails are self-reported and unverified; the form
does not create users, send invitations, approve access or grant credits.

`POST /access-requests` accepts bounded JSON (4096 bytes; name 100, email 254, note 500 characters).
A global-per-API-process limiter permits five submissions per minute, without an unbounded
per-IP cache or trusting forwarded headers. This is basic beta abuse protection, not a CAPTCHA
or a cross-replica quota; monitor public usage before opening the beta widely. Pending requests
are deduplicated by normalized email with conditional blob creation, so retries cannot replace
someone else's pending details or reveal whether an address is already recorded.

Requests use `_access-requests/<SHA256-of-normalized-email>.json` in the existing private
`collections` container. No new Azure service, permission, anonymous blob access or email charge
is required. Keep this prefix when cleaning scan images. Details persist until an administrator
removes them; review and delete them promptly, including unverifiable/spam requests. The authenticated
`GET /admin/access-requests/` and `DELETE /admin/access-requests/{id}` routes require the configured
allowance-admin identity and delegated scan scope, and all request routes are `no-store`.
Azure Portal storage browsing can also inspect the private JSON if API sign-in is unavailable.
Local development without storage logs an explicit warning and uses a non-durable in-memory inbox.

On a phone, take or select an MTG photo and start the scan. The browser normalizes
orientation, removes metadata and caps the longest edge at 4096 pixels before upload. Use a
browser-readable JPEG/PNG if HEIC is unsupported. Real stages and processed/identified counts
drive the progress display; the grayscale preview and animated sweep indicate activity, not a
fabricated per-card percentage. The current job resumes after a same-tab reload in the same
signed-in session; a reconnect button handles interrupted polling. PNG overlays and a price-ranked
CSV report can be downloaded on mobile. The report includes both EUR prices, the ranking price,
original card position, ownership, resolution, price errors, source and retrieval time. Nonfoil
is the ranking price, with foil used only when nonfoil is unavailable; zero remains a real price.
Unpriced cards sort last and ties retain scan order. This mobile report replaces the previous
five-column download and is not import-compatible. Desktop import CSVs remain unchanged.
Spreadsheet formula prefixes are escaped.

Use **Add identified cards to collection** on a completed scan to save its cards to your private
**scanned collection**. Each identified MTG card with a set and collector number adds one copy;
failed/unidentified cards and cards without a collector number are skipped. Review uncertain
printings before adding them. Copies are counted by **set + collector number + language**, never
by card name: two Swords to Plowshares printings stay separate. Foil finish is not inferred.
Each scan can be added once, including after a page reload or a retry; a separate scan increases
the corresponding quantities. Adding or browsing cards does not consume scan credits.

Alternatively, enable **Automatically save identified cards** before
starting a scan. This switch is off by default and remembers your choice for your signed-in
account in the current browser tab. It applies to new scans only. The worker saves eligible
cards after calculating ownership, even if you close the page, so a scan does not count its own
addition in its ownership overlay. A failed automatic save leaves the scan and downloads
available, with an explicit message and the manual add button to retry safely.

Select **View scanned collection** to browse your added printings, see quantities, and filter by
set. Use **Download collection CSV** in that view to download all saved cards, or only the selected
set. The UTF-8, semicolon-separated CSV contains Name, Set, Number, Language and Quantity, with
one row per saved printing/language and spreadsheet formula protection. Empty collections/sets
download the header only. This exports scanned additions, not uploaded desktop snapshots or
combined ownership totals, and does not consume scan credits. The authenticated endpoint is
`GET /scanned-collection/cards.csv` (optional `?set=CMM`); responses are private and `no-store`.
This collection is stored separately from desktop ownership snapshots: adding cards does not
update desktop databases, uploaded snapshot quantities or previously rendered overlays.
Replacing/deleting a snapshot does not remove scanned additions. New mobile ownership comparisons
add uploaded snapshot quantities and saved scanned quantities together; either source can be used
alone. Ownership combines languages for the same set/collector number, while collection browsing
keeps languages separate. All-sets scanned quantities use canonical catalogue printing identities,
not card-name matching. If the same physical copies are represented in both sources, they will
be counted twice; keep the sources non-overlapping when you want physical inventory totals.

Scanned additions are durable, owner-scoped, immutable receipts in the existing private
`collections` blob container. Atomic create-if-absent writes prevent duplicate additions across
API replicas and simultaneous clicks; different scans cannot overwrite each other.
`GET /scanned-collection/?set=CMM` lists the current user's printings (omit `set` for all),
while `POST /jobs/{jobId}/collection` saves only that user's stored completed scan results.
`GET /jobs/{jobId}/collection` restores the already-added button state. All three routes enforce
the same tenant, delegated permission, pilot allowlist and ownership policy as scans.

To include your desktop inventory in ownership, use **Scan > Export Mobile Collection Snapshot...** in the desktop app, then
upload that JSON under **Desktop ownership snapshot** on the website (a computer signed into the same account
can upload it too). The export reads the same local database pair used by desktop scans.
Only printing keys (set/number and public Gatherer IDs), exact quantities and locally computed all-printing totals leave
the desktop; original databases, card names and personal notes are not uploaded. Replace it
after inventory changes or delete it on the website. Snapshots are private, isolated by validated
tenant/object IDs, and remain until replaced/deleted. The worker reads both collection sources
when rendering an opted-in scan. A source read failure, or removal of the only available source
mid-scan, produces explicit unknown ownership, not partial totals or invented zeros.
When collection comparison is enabled, missing exact printings get red overlay backgrounds;
all cards show exact-printing and all-sets quantities, including zero. With comparison disabled,
neither ownership line is printed and the overlay is shortened to remove that space, with the
confidence dot moved to its new lower-right corner. English cards also omit the name band;
panel height follows the displayed price, foreign-name, printing and ownership lines.
Genuine lookup failures show `unavailable`,
not amber or a question mark.
For small loose-card photos, recovery checks physical outlines at all four rotations. It can
recover a measured neighbouring card or replace supported artwork fragments with complete
outlines, without projecting title bars into unmeasured neighbouring slots.
Confirmed English identifications must also agree with the photographed title; an unrelated
card returned by a number lookup triggers the existing bounded agent re-read instead of acceptance.
Name-only lookups fall back to paper printings when Scryfall's preferred result is digital-only.
Double-faced cards still require two copies. Existing rendered images are
unchanged by later snapshot replacement/deletion. Desktop scans keep their local ownership path.
Older MTG Studio rows can have legacy set codes and no collector number (for example `VI` rather
than `VIS`). The snapshot now also exports Gatherer printing IDs, which the exact Scryfall pricing
response supplies for matching without card-name guesses. Set/number remains the preferred key.
Re-export and upload a fresh snapshot to include these rows; older snapshots remain readable but
cannot recover quantities they omitted. If neither the exact set/number nor a Gatherer printing
ID is present in an available snapshot and no exact scanned printing is saved, exact ownership is zero. These cards use the
normal red missing-card panel with `Owned: 0` and an independently resolved all-sets total.
Unavailable collection sources, conflicting identifiers or unsupported
layout still produces an explicit warning and `unavailable` quantities, not invented zeros.
Missing entries count as zero in the scan lookup; scans do not modify the uploaded snapshot or source databases.
When a printing has no all-sets total, the worker uses Scryfall's canonical Oracle ID to find
other printings and match their set/number or Gatherer IDs in the existing snapshot. Their
precomputed all-sets total is reused (not added repeatedly); their exact quantities are never
assigned to the scanned printing. This supports newer reprints absent from older exports,
without matching scanned names or requiring a fresh upload. Catalogue failures remain explicit warnings.

On the `parallel-card-identification` branch, the worker processes up to **nine cards concurrently**
within each scan. Each task uses its own
source-image stream; results retain reading order and progress writes are serialized. Per-card
failures do not discard the other cards. Set the worker environment variable
`ScanPipeline__MaxConcurrentCards` to a lower value to reduce model quota pressure, or `1` for a sequential baseline.
Values below 1 are rejected. Desktop polling is capped at three seconds during active scans.

Each independent card's Foundry Responses call has its own retry budget and five-minute deadline.
HTTP 429/model throttling gets up to `ScanPipeline__AgentThrottleRetries` retries (default 3);
HTTP 408/500/502/503/504, failed `server_error` responses, and polling transport failures share
two additional transient retries. Polling retries resume the same response ID, not the entire
card or scan. Backoff is exponential (10 seconds initially for throttling, 1 second for service
failures), prefers `Retry-After` headers or service-message advice, caps the base wait at two
minutes, and adds up to 250 ms jitter to spread concurrent retries. Cancellation interrupts
backoff. Authorization, invalid requests, and MCP errors are not retried by this policy; the
existing one-time reading/JSON correction remains unchanged. An unidentified card does not get
extra transient retries. Exhaustion is logged and becomes that card's error without discarding
other results. Ambiguous POST transport failures are not replayed because they may already
have created background work. Nine-way concurrency is deployed to the cost-optimized Azure
environment. Full-photo crop/decode work is serialized independently of the model calls to limit
peak memory; reducing concurrency remains available if quota pressure increases.

GPT-5 mini remains Global Standard, now allocated 300,000 TPM and 300 RPM rather than
100,000 TPM and 100 RPM. This raises throughput limits, not individual model generation speed.
No provisioned-throughput reservation or premium processing tier is used.

Catalogue identification and tool pricing share one paced Scryfall HTTP pipeline. Both that
pipeline and the worker's pricing/title-proof client allow two requests/second per instance,
accounting for their shared outbound IP and up to two replicas of each service. Catalogue GETs
retry transient HTTP/network failures twice, honor both forms of `Retry-After`, and apply the
cooldown to queued calls too. A network attempt has a 30-second timeout; queue/cooldown time uses
a separate two-minute total request budget. Caller cancellation still interrupts both.
Explicitly classified transient catalogue tool errors receive at most two further per-card
retries within the existing agent deadline. Authorization, malformed data and unknown tool
failures remain failures, never model-only successes.
Successful/404 printing searches are reused for up to one minute in a bounded per-catalogue cache;
partial pages and failures are never cached.

Alternate-title cards (for example Final Fantasy FCA printings) retain the photographed character
title and the canonical Magic name separately. A number lookup is confirmed only when its
catalogue name, printed name or `flavor_name` corroborates the read title. Before accepting an
English title mismatch, the worker independently verifies the exact printing's canonical name
and `flavor_name`; a model-supplied alias alone cannot bypass the wrong-card guard. The prompt
distinguishes visible set/language text such as `FCA EN` from franchise labels such as `FFIX`
and `FFXI`, without guessing an unreadable set code.

Worker logs include `token_usage` events for input/output, cached input and reasoning tokens,
plus `response_summary` events with elapsed time, response/approval/correction counts and isolated
throttle/service/catalogue retries. Missing usage is explicitly logged rather than reported as zero.
Worker logs also include `timing` events in milliseconds for source download, boundaries, each card's
crop/store, identification and art verification, all cards, pricing, rendering, image storage,
and total worker processing. Foundry logs time background Responses/tool approval/reply retrieval.
`.\scripts\show-last-scan.ps1` includes job timing events. The worker total
excludes desktop upload, queue wait, polling lag and the final image download.

For operator investigations, `.\scripts\get-scan-diagnostics.ps1` downloads the latest uploaded
pilot scan's original photo, overlay, per-card crops, full job/result JSON and ranked CSV, then
collects Log Analytics evidence. Use `-JobId <id>` for a specific scan or `-Hours 168` to widen the
default 24-hour search. Failed and running attempts are included; pending uploads are not selected
as the latest scan. Downloads go outside the repository under local application data by default.
Run this on an explicit investigation request, not as background collection; use the latest scan
unless the requester specifies another job.
The manifest records missing files explicitly and SHA-256 hashes of downloaded evidence.
Job-correlated logs and time-window context logs are separate because concurrent scans can overlap.
Log ingestion can lag; rerun after a scan finishes for final evidence.

Diagnostics is disabled by default. An explicitly approved operator signs in once with
`.\scripts\get-scan-diagnostics.ps1 -SignIn`; subsequent investigations reuse the OS-encrypted
token cache, without copying secrets into this repository. The ordinary pilot allowlist does not
grant diagnostics access. See [operator setup](docs/azure-setup.md#6-verification-and-cutover-gate) for
the separate operator allowlist, permissions and privacy/retention considerations.

Compare the same photos at concurrency 1, 2, 3 and 9, including identity/printing/language accuracy,
elapsed time, tool failures and throttling. Pricing remains serialized with polite Scryfall pacing.
Lower reasoning effort was benchmarked but not retained: the candidate had catalogue failures,
so the faster completion was not evidence of an accuracy-preserving speedup. Original reasoning,
full-resolution card/footer images and all art-comparison candidates remain unchanged.
Responses carry high-detail inline image bytes, not public blob URLs or persistent Files uploads.
Transient response creation failures retry twice; quota refusals use the configured backoff.
Authentication failures are not retried or downgraded to classic/anonymous calls. Tool failures
remain explicit failures rather than model-only success. Background responses are cancelled on
timeout/cancellation and retained responses are deleted after use; cleanup failures are logged.
Responses HTTP calls use the configured `ScanPipeline__AgentRunTimeout` (five minutes by default)
and caller cancellation, rather than HttpClient's shorter default 100-second timeout. Cleanup
requests have a separate 30-second deadline per retained response.
The legacy classic client remains available for regression checks, not as an automatic fallback.

After final printing/art verification, scans also produce a priced PNG of the original photo.
Half-card-width artwork overlays show euro-symbol prices side by side, nonfoil on the
left and foil (marked with a star) on the right. Text is measured and sized to fill the available
box, with the card name shown only for non-English or unknown-language cards and set code and collector number below.
Name, set/number and price fonts are measured independently, so a long translated name or price
does not shrink the printing label. These lines do not overlap the confidence circle in the
lower right: green for placed printings, yellow for printings needing verification, and red for
unidentified cards or unplaced readings below 0.5 confidence. There is no appended footer or
legend; output retains the photo dimensions (apart from the existing 2400-pixel render cap).
Exactly one gold trophy replaces the rank number on the highest-priced identified card,
irrespective of ownership or shortage. All other identified cards show their one-based rank in
the full downloadable CSV in that same upper-right badge position, with no trophy. Tied values
use scan order; unpriced cards sort last and cannot win. If every price is unavailable, all
identified cards show rank numbers and there is no trophy. Shortage background colours remain
independent of these price badges. Prices are **Cardmarket via Scryfall daily reference prices**, not
live offers or a guaranteed valuation. Each missing foreign-language price independently falls
back to English for the **same set and collector number**, recorded in result price metadata.
A foil-only or nonfoil-only record is
valid; unavailable prices are left blank, never zero, USD, or the other finish's price.
The mobile CSV carries the descending price ranking instead of an image legend.

The desktop downloads the PNG through the authenticated job API, saves it beside the CSV as
`<csv-stem>_<job-id>_priced.png`, and opens a preview. Batch scans save one PNG per source image.
The original upload is unchanged. CSVs remain available if pricing, rendering, or download fails;
the completion summary reports those warnings separately. Both prices are shown for human
comparison; no automatic foil detection is performed. Uncertain printings are explicitly marked as indicative.
Result metadata and the web card list separate the exact price target from identification evidence.
`Printing needs verification` means the photographed printing is not established, not that the
price lookup failed. A lone artwork match is described as `Artwork matches; set/number not established`.
A decisive artwork comparison that separates competing printings can verify either the existing
printing or an art-based correction; shared artwork alone cannot.

Deploy with `.\scripts\update-demo.ps1 -SkipAgents`, then rebuild/restart the desktop app.
The deployment includes the private `outputs` container, API read-only access and worker write
access scoped to that container, and the worker's Linux rendering font. No new agent/project or
public blob URL is needed. Team B's catalogue also exposes `get_printing_prices` as an MCP tool;
the worker calls the same deterministic pricing implementation after art verification rather
than asking a model to copy monetary amounts.

Recognition crops include a bounded 2% margin to recover small footer details clipped by detection.
Measured card geometry and the tight crops used for artwork comparison are unchanged.
For MTG cards, the identifier receives the full crop, an enlarged full-width footer, and closer
left/right footer views. Sideways crops supply both possible quarter-turn footer views rather than
guessing which end is the printed top.
Retro-frame collector numbers may sit beside the copyright line, not in the modern lower-left.
MTG language is read from the photographed footer/title/rules, independently of an English
catalogue name or fallback printing. Results retain the original `readName` and footer
`readLanguage` (`JP`/`JPN` becomes `ja`). Missing language/title evidence or contradictory language
triggers one logged re-reading attempt; persistent contradictions fail rather than defaulting
to English. Unresolved or number-only (`assumed`) catalogue answers also trigger one bounded
re-reading attempt, even when the initial answer contains a plausible name and number. Persistent
uncertainty retains the readable card and its review status rather than discarding the row.
An inconclusive artwork comparison with a different best candidate can request one targeted footer
review. This never lowers artwork acceptance thresholds or automatically chooses the nearest image.
Only an established printing of the same card and photographed language can replace the original;
failed optional reviews are logged and preserve the original reading. Cancellation and access errors
are not hidden. Deploy the worker and identification prompt together with `.\scripts\update-demo.ps1`.
Unplaced, ambiguous, assumed and missing-resolution printings are listed for review; a CSV's
set and number are not automatically confirmed just because the card's name was identified.

Measured card regions are authoritative: the worker runs the shared detector locally and retains
every region even if the boundary agent omits it. Unambiguous agent matches can supply game and
printed orientation, but cannot move measured corners or add regions. Unmatched candidates still
go through identification and may be reported as unidentified rather than silently discarded.
When the detector finds nothing, agent-estimated geometry remains an explicitly logged fallback.
The boundary agent receives the same raw-pixel orientation as the detector and cropper: JPEG
EXIF display-rotation tags are stripped from its vision copy without rotating the pixels.
The stored original is unchanged.

When the normal arrangement covers less than 15% of the photo, an OpenCV recovery pass looks
for larger card contours and title frames, including extended-art cards on dark neutral surfaces.
Measured anchors and their spacing can complete missing neighbours; no set, card name, or fixed
grid is assumed. Recovery must exceed both 15% of the photo and twice the original area.
For overlapping fragments or merged arrangement regions, a second recovery checks contour/title-frame
measurements at all four quarter-turns. Replacement requires consistent whole-card sizes, coverage of
the original regions and sufficient photo coverage; it does not assume a fixed card count. The
recovered corners are mapped back to the original image while preserving their crop orientation.
Bottom-positioned title bars can otherwise project a card-sized rectangle above the actual card.
When opposite-turn contours disagree on that placement, two measured row neighbours and a column
neighbour must support an alternative before it can replace the shifted frame; the row's measured
extent keeps both the title and collector footer. The exact failed bottom-title Swamp upload is
tested at all four quarter-turns, including the leftmost set-code character in the footer.
When a mostly detected binder page omits a card, contour evidence can also fill a single gap
without replacing existing quads. A candidate must match card size, have two measured neighbours
in both its row and column, and pass border and interior-detail checks. Empty slots are not
filled merely because they lie on the page grid. The exact glared Ball Lightning upload is a
regression fixture, alongside the existing sideways and loose-card photographs.
Contour groups also use the existing edge-supported neighbour completion in that upright frame,
so a dark card border lost during browser JPEG re-encoding need not leave a hole in the scan.
EXIF display rotation is handled independently as described above.

The imaging library includes matching Windows and Linux x64 slim OpenCV runtimes. Both the worker
and imaging MCP service must be rebuilt together. Their container builds execute
`--check-imaging-runtime` before succeeding, so missing Linux native dependencies fail the build
rather than the next scan. No additional AI agent or model deployment is required.

For crop diagnosis, run `dotnet run --project tools\Enfolderer.Ai.Imaging.TrainingCheck -- --inspect <image> <crop-directory>`.
To export the shared detector's measured regions and crops without a live agent, run
`dotnet run --project tools\Enfolderer.Ai.CardCatalog.Check -- --measure-cards <image> <crop-directory>`.
Magic printing searches are restricted to paper cards, with digital-only records rejected even
from exact lookups. Printing searches follow catalogue pagination, and comparison carries up to
64 physical candidates rather than the first 12. This includes early promos that may have no
printed set code or collector number. Comparison uses the whole card face (frame, symbol area,
text and artwork), not just the illustration; it is not a dedicated set-symbol recognizer.
Incomplete, failed or capped candidate enumeration is explicitly marked and cannot endorse or
move a printing by image similarity. Shared-image ties still remain inconclusive; thresholds are
unchanged. No card-name-specific printing override is used.
For the original HarperPrism Mana Crypt regression against current public catalogue references,
run `dotnet run --project tools\Enfolderer.Ai.CardCatalog.Check -- --legacy-printing <card-crop.png>`.
This opt-in network check requires PHPR 5 to reach the worker through the catalogue tool and
decisively match the supplied crop; the normal suite covers physical-only filtering, later-page
promos, candidate limits, incomplete evidence and shared-art ambiguity offline.
The `--photo-boundaries` checks cover the supplied six-card sideways phone photo at all four
quarter-turns, including whole-card overlap, printed crop orientation and boundary-adapter preservation,
for both the original camera file and the exact browser-uploaded JPEG.
The live boundary checker accepts an optional crop-output directory and checks annotated physical
card regions when a training photo has a `boundaries.json` file. Photo 07's annotations describe
the ten loose cards on the laptop. The separate `cards.txt` records their names, printings and
language.

Configuration lives in `aiconfig.txt` beside the executable and contains **no secrets** — only the
API URL, tenant id, public client id and scope; you sign in interactively.
Builds and published packages include `aiconfig.defaults.txt` with the public demo settings.
On the first scan, the app copies those defaults to `aiconfig.txt` if it does not already exist,
so another PC needs no manual configuration for the demo. Existing local settings are never
overwritten, even by a newer build. Edit the local file to use another deployment.
If no defaults are shipped, the app writes a template instead.

The services, agent definitions, and infrastructure are in `src/Enfolderer.Ai.*`, `agents/`, and
`infra/`. See [docs/azure-setup.md](docs/azure-setup.md) to deploy this branch's cost-optimized
architecture: one shared ACA Consumption environment, Entra-only public data endpoints and
Cosmos free tier. This branch has no separate-team ownership or private endpoints.
The [security-boundary walkthrough](docs/foundry-demo.md) describes `main`, not this branch.
Existing client defaults are intentionally unchanged until the new deployment passes real scan
validation; use a separate config to test it.

### Blob folder ordering

New scan blobs use a shared creator/date-stamped folder in the `scans`, `crops` and `outputs`
containers, for example `Douglas-261022-1530-<job-id>`. The format is
`firstName-yyMMdd-HHmm-jobId`, with a 24-hour UTC time and minute precision. Folder-name sorting
groups by creator first, then by age within that creator; the full job ID prevents collisions
between scans in the same minute.
The label comes from the authenticated given name, falling back to the first word of the
signed-in display name. If neither supplies a usable name, the API logs a warning and uses
`user-<owner-object-id>` instead, never an email address or request-body name. Names are
accent-normalized, stripped to ASCII letters/digits/hyphens/underscores and capped at 64 characters.
Names are descriptive labels only; tenant/object IDs still control access.
The prefix is fixed at job creation and reused on retries, even if the user changes their name.
Existing timestamp-only and undated folders and their download links remain accessible; they
are not renamed, and public job IDs and download URLs do not change. Collection receipts keep
their existing owner/job keys and are not renamed.

### Guest scan allowances

Approved users also need an explicit scan-credit grant. An invitation or pilot assignment does
not grant credits. The API charges one credit per submitted photo (not per card), after checking
that its upload exists. Submitted scans count even if identification fails. Credits do not
reset or expire; repeated submission of the same job does not charge again. Exhausted users
can still poll and download existing results and manage their collection. The website displays
remaining scans and disables new scanning when exhausted; the API enforces the same rule for
desktop and direct callers.

Allowances, idempotent adjustment audit records and dispatch receipts live in a separate
non-expiring Cosmos container. A recovery loop dispatches reserved work after interruptions.
Queue duplicates are serialized by a renewable execution lease; terminal jobs are not rerun.
Owners must be explicitly granted unlimited access, just like guests must be granted finite
credits. No user is automatically unlimited based on account type.

See [allowance administration and tenant migration](docs/azure-setup.md#5-mobile-website-and-separate-desktop-configuration)
for setup, grants, top-ups, refunds and balance migration.
Use `.\scripts\list-demo-users.ps1 -ConfigFile '<demo-config-path>' -SignIn` to enumerate the live
demo allowlist with names, invitation status and remaining scans; unlimited accounts are labelled
`Unlimited`. Later calls reuse the cached sign-in. This read-only report requires an allowance
administrator and Azure CLI access to the deployment and directory users.
Run `dotnet run --project tools\Enfolderer.Ai.CardCatalog.Check` for API allowance, concurrent
submission, dispatch recovery and worker duplicate-delivery regressions. Run
`dotnet run --project tools\Enfolderer.Ai.Allowance.Check` for Cosmos batch-failure, ETag,
serialization and non-expiring receipt/audit checks.

## Local collection annotations on scan images

After downloading a priced scan image, the desktop reads `mainDb.db` and `mtgstudio.collection`
beside its executable. These databases and ownership counts are not uploaded to Azure.
Collection rows are matched through `Cards.MtgsId = CollectionCards.CardId` and summed across
collection entries. If a mapped collection entry exists, its quantity (including zero) wins;
otherwise the desktop uses `Cards.Qty` in the main database.

A red-tinted price-overlay background marks an exact printing with fewer than one copy, or fewer than two for a card
with distinct front/back faces. Split/adventure/prepare cards with a normal card back require only one.
Every card shows `Owned: N` and `All sets: M` below the prices, including missing
printings with zero copies and double-faced printings with only one copy. These identify the exact set/collector-number
quantity and a separate total across printings with that card name. The all-printings total
never determines whether a card is missing. Collector-number suffixes remain distinct.
All inventory variants within the same set/number count toward that printing.

Set code and collector number are authoritative for ownership. The desktop looks up that exact
printing in `mainDb.db`, then uses its local database name to find the all-printings total.
Scanned and Scryfall names are not compared or fuzzy-matched for ownership, so differences in
diacritics, punctuation, and adventure/sub-card notation cannot block the quantity lookup.
Full local names are retained to avoid combining different cards that share a front-name prefix.

The price lookup also returns the catalogue name and layout; no extra catalogue request is
needed. A worker built before this metadata was added cannot supply the front/back requirement:
the desktop reports an ownership warning rather than guessing. Missing databases and unsupported
layouts likewise produce warnings; missing catalogue entries count as zero without writing to the databases. Where card
geometry is available, unavailable ownership is shown with `Owned: unavailable` and `All sets: unavailable`, never an amber or blank
ownership panel that could be mistaken for confirmed ownership. Cards are not outlined. CSVs and the priced image remain
available. Identification confidence does not suppress ownership counts.
Ownership is evaluated when downloading, not when reopening a saved image.

The selftests use both synthetic databases and the read-only fixtures in `training/testing dbs`
when running from the repository. They never replace or modify the user's working databases.

## Build and run

The project targets .NET 8/WPF:

```powershell
dotnet run --project Enfolderer.App
```

To publish a self-contained Windows executable:

```powershell
dotnet publish Enfolderer.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Attribution and license

Card data and images are provided by [Scryfall](https://scryfall.com) and are © Wizards of the Coast. Enfolderer is unofficial and is not endorsed by Wizards of the Coast.

Licensed under the [MIT License](LICENSE).
