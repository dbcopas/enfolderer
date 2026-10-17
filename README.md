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

The API requires Entra authentication and records each job's creator tenant/object identity.
Only that identity can upload, submit, poll or download its job. Jobs created before ownership
was recorded are inaccessible through these routes; create a new scan rather than claiming an
old job by its ID. The API refuses to start without its tenant/client configuration, and public
MCP servers refuse missing tenant/audience or caller allow-lists, including local runs.
The worker uses named Foundry agent Responses endpoints. Each agent has its own Entra Agent ID;
Foundry obtains short-lived MCP tokens through `AgenticIdentityToken` project connections.
Geometry, Magic and Pokemon servers accept only their corresponding agent principal, not worker
or project managed identities. Run `.\scripts\update-demo.ps1` for the first migration; it provisions
identities/connections before deploying images and persists the audience and caller IDs.
See [setup and migration](docs/azure-setup.md#4-create-the-agents).

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

The worker processes up to **two cards concurrently** within each scan. Each task uses its own
source-image stream; results retain reading order and progress writes are serialized. Per-card
failures do not discard the other cards. Set the worker environment variable
`ScanPipeline__MaxConcurrentCards` to `1` for a sequential baseline, or `3` for a measured trial.
Values below 1 are rejected. Desktop polling is capped at three seconds during active scans.

Worker logs include `timing` events in milliseconds for source download, boundaries, each card's
crop/store, identification and art verification, all cards, pricing, rendering, image storage,
and total worker processing. Foundry logs time background Responses/tool approval/reply retrieval.
`.\scripts\show-last-scan.ps1` includes job timing events. The worker total
excludes desktop upload, queue wait, polling lag and the final image download.

Deploy the updated worker and rebuild/restart the desktop before comparing the same photos at
concurrency 1, 2 and 3. Compare identity/printing accuracy as well as elapsed time and
throttling. Pricing remains serialized with polite Scryfall pacing. No service SKU, model,
replica count or quota is increased by this change; consider upgrades only after measurement.
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
box, with the set code and collector number in a smaller line below, without overlapping the confidence circle in the
lower right: green for placed printings, yellow for printings needing verification, and red for
unidentified cards or unplaced readings below 0.5 confidence. An appended
legend includes card identity, photographed language, price-source language, printing uncertainty,
and retrieval time. Prices are **Cardmarket via Scryfall daily reference prices**, not
live offers or a guaranteed valuation. Each missing foreign-language price independently falls
back to English for the **same set and collector number**, marked `[en fallback]` in the legend.
A foil-only or nonfoil-only record is
valid; unavailable prices are left blank, never zero, USD, or the other finish's price.
The legend is ordered by descending nonfoil price, falling back to foil only when nonfoil is
unavailable. Cards with neither price are last, with
ties in original card order. Card numbers still refer to the original scan; CSV order is unchanged.

The desktop downloads the PNG through the authenticated job API, saves it beside the CSV as
`<csv-stem>_<job-id>_priced.png`, and opens a preview. Batch scans save one PNG per source image.
The original upload is unchanged. CSVs remain available if pricing, rendering, or download fails;
the completion summary reports those warnings separately. Both prices are shown for human
comparison; no automatic foil detection is performed. Uncertain printings are explicitly marked as indicative.
The image legend separates the exact price target (`Price for ONS 62`) from identification evidence.
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
This path is currently tuned for approximately upright cards in the raw pixel axes, not arbitrary
physical rotations. EXIF display rotation is handled independently as described above.

The imaging library includes matching Windows and Linux x64 slim OpenCV runtimes. Both the worker
and imaging MCP service must be rebuilt together. Their container builds execute
`--check-imaging-runtime` before succeeding, so missing Linux native dependencies fail the build
rather than the next scan. No additional AI agent or model deployment is required.

For crop diagnosis, run `dotnet run --project tools\Enfolderer.Ai.Imaging.TrainingCheck -- --inspect <image> <crop-directory>`.
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
`infra/`. See [docs/azure-setup.md](docs/azure-setup.md) to deploy the Azure resources, and
[docs/foundry-demo.md](docs/foundry-demo.md) for the architecture and the security boundary
walkthrough.

## Local collection annotations on scan images

After downloading a priced scan image, the desktop reads `mainDb.db` and `mtgstudio.collection`
beside its executable. These databases and ownership counts are not uploaded to Azure.
Collection rows are matched through `Cards.MtgsId = CollectionCards.CardId` and summed across
collection entries. If a mapped collection entry exists, its quantity (including zero) wins;
otherwise the desktop uses `Cards.Qty` in the main database.

A pink-tinted price-overlay background marks an exact printing with fewer than one copy, or fewer than two for a card
with distinct front/back faces. Split/adventure/prepare cards with a normal card back require only one.
Missing cards do not show a quantity, including double-faced printings with only one copy.
Otherwise, `Owned: N | All: M` below the prices identifies the exact set/collector-number
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
the desktop reports an ownership warning rather than guessing. Missing databases, missing local
catalogue entries and unsupported layouts likewise produce warnings. Where card
geometry is available, unknown ownership is shown with an amber overlay and `Owned: ?`, never a blank
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
