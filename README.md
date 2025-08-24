# Enfolderer MTG Binder

WPF application for visualizing a Magic: The Gathering collection in virtual quad binders (4×3 = 12 slots per page). Each physical binder = 20 double‑sided pages (40 displayed sides). The app auto‑adds binders as your list grows beyond the 480 face capacity of one binder.

Physical pagination is emulated:
* Page 1: single right page (front cover opened)
* Interior: two‑page spreads (left + right simultaneously)
* Final page of a binder: single left page (back cover)

## Key Features (Current)
* Unlimited binders with correct cover / spread pagination
* Deterministic global ordering with adjacency & alignment constraints
	* Physical two‑sided (transform / modal_dfc / battle etc.) cards auto‑inject synthetic back immediately after the front
	* Duplicate consecutive names treated as a pair
	* Pairs start only at columns 0 or 2 (never split across rows)
	* Ordinary singles may be pulled forward to fix misalignment (backface placeholders act as hard barriers and are never moved)
* Rich declarative input format (set sections + powerful collector number expressions)
	* Simple numbers & numeric ranges
	* Paired ranges (A-B&&C-D) producing composite display numbers like `296(361)` while still fetching canonical first number
	* Interleaving of multiple sequences with `||`
	* Generalized prefixes (attached or spaced) and suffixes (e.g. `J1-5`, `2024-07`, `2J-b`, `5J-b`, `ABC 01-03`)
	* Star syntax (`★1-36` => `1★..36★`, or `★12` => `12★`)
	* Backface placeholders: `N;backface` injects N binder back slots using a local / fallback card back image
	* Explicit custom entries using `Name;SET;Number` (bypasses API)
	* Name overrides via `Number;Custom Name`
* Local custom card back image support (drop `Magic_card_back.jpg` in collection folder / app folder / Pictures/Enfolderer / images subfolder)
* Lazy metadata resolution:
	* Initial minimal fetch (current + look‑ahead pages)
	* Background resolution of remaining specs
	* Progress status updates (e.g. 12/120 resolved)
* Scryfall integration (names, layout classification, images) with improved multi‑face heuristics & optional env override `ENFOLDERER_FORCE_TWO_SIDED_ALL_FACES=1`
* Multi‑layer caching & reuse
	* In‑memory + on‑disk image cache
	* Per‑card JSON cache (layout & image URLs)
	* File‑hash (SHA‑256) metadata cache with completion sentinel
* Robust zero‑padding preservation for ranges (e.g. `001-010` renders `001..010`)
* Single-file self‑contained publish option (Win x64)
* MIT licensed

## Input File Format (Declarative)

Names (and multi‑face classification) come from Scryfall unless you explicitly supply them. You define structure with set sections and collector number expressions.

Core rules:
1. Set section: `=SETCODE` (e.g. `=STA`). Applies down to the next `=` or EOF.
2. Single number: `123`
3. Numeric range: `10-25` (inclusive). Zero padding preserved when both ends share width (e.g. `001-010`).
4. Interleaving: `1-5||30-34||100` -> `1,30,100,2,31,3,32,4,33,5,34`
5. Paired range (composite display): `296-340&&361-405` -> slots show `296(361)`, `297(362)` ... fetch uses first number only.
6. Star syntax: Leading star moves to trailing: `★1-3` => `1★,2★,3★`; `★12` => `12★`.
7. Prefix forms:
	* Spaced: `RA 1-8` -> `RA1..RA8`
	* Attached: `J1-5` -> `J1..J5`
	* Complex / mixed alphanumerics with hyphen: `2024-0 7-8` -> `2024-07,2024-08`; attached variant `2024-07` (single)
8. Suffix forms:
	* Single: `2J-b`
	* Range with suffix: `2-5J-b` -> `2J-b,3J-b,4J-b,5J-b`
9. Explicit placeholder (bypasses API): `Some Token;TOKEN;1`
10. Name override (still fetch metadata): `123;Custom Name`
11. Backface placeholders: `N;backface` (e.g. `5;backface`) creates N card-back slots, never reordered.
12. Comments: lines starting with `#`
13. Blank lines: ignored

Order is preserved except normal singles may be internally shifted forward to satisfy pair alignment; backface placeholders and their relative positions act as ordering barriers.

Example:
```
# Strixhaven Mystical Archive (STA) + Tokens
=STA
1-10
11-20||50-55   # interleaves two ranges
100;Special Showcase Placeholder

# Explicit token / custom placeholder (no API call)
Dragon Token;TOKEN;1

=BOT
1-15

 =REX
 1-5||30-32

# Paired range with composite display numbers
296-298&&361-363

# Star syntax: displays 1★..5★
★1-5

# Backface placeholders (5 empty back slots using custom / fallback back image)
5;backface

# Complex prefix / suffix forms
J1-3
2024-07
2-4J-b
```

Legacy CSV style (Name;Number;Set) is still parsed by the older loader, but the declarative format is now preferred.

### Adjacency & Layout Rules
* Physical two‑sided (transform / modal_dfc / battle / etc.) fronts + synthetic backs form a locked pair.
* Consecutive identical names form a pair.
* Pair start columns: 0 or 2 only (ensures each pair lives fully inside a row).
* Singles may be advanced to repair alignment (never leap over placeholder backfaces).
* Backface placeholders (`N;backface`) are immovable barriers.

### Lazy Loading Flow
1. Parse specs into an ordered list of unresolved entries.
2. Perform an initial small batch resolution (enough for first two pages worth of faces including MFC backs).
3. Build ordering (placeholders have provisional names) and render.
4. As you navigate, background resolution fills in missing specs for the active/next pages; views redraw incrementally.
5. After all specs resolve, metadata + image URLs are persisted and a `.done` sentinel written.

### Caching Details
Cache Root: `%LocalAppData%/Enfolderer/cache`
* `meta/<hash>.json`  — serialized faces (fronts + backs) including image URLs
* `meta/<hash>.done`  — presence means cache complete (safe to reuse)
* `<hash-of-url>.img` — raw image bytes (one per face variant)
* In‑memory dictionaries layer on top for fast session reuse

On load:
* Compute SHA‑256 of the exact file contents (normalized with `\n`).
* If `meta/<hash>.done` exists and JSON loads => skip all metadata HTTP.
* Otherwise perform lazy resolution; when complete write JSON + `.done`.

## Navigation
Toolbar / UI offers:
* First / Prev / Next / Last
* Prev Binder / Next Binder
* Jump to Binder + Page (1‑based)
Page label displays binder number and local page numbers (covers annotated).

## Image Fetching & Card Back Placeholders
`https://api.scryfall.com/cards/{set}/{collector_number}`
* Metadata calls rate‑limited (<10/sec)
* Image URLs stored; subsequent face loads skip metadata request
* Disk + memory cache for image bytes
* Tokens (set `TOKEN`) are skipped (placeholder only)
* Backface placeholder image resolution order (first match wins):
	1. Collection file directory (`Magic_card_back.jpg` or variants: case / .png / .jpeg / `card_back.jpg`, `back.jpg`)
	2. Application base directory
	3. `%USERPROFILE%/Pictures/Enfolderer`
	4. `images` subfolder under application base
	If none found, falls back to Scryfall standard back image.

Environment override for debugging two‑sided classification: set `ENFOLDERER_FORCE_TWO_SIDED_ALL_FACES=1` to treat every multi‑face card as physically two‑sided.

## Build & Run
Requires .NET 8 SDK.
```
dotnet run --project Enfolderer.App
```
Open a declarative collection file (`File > Open`).

### Release (Single EXE)
Self‑contained, single file (win-x64):
```
dotnet publish Enfolderer.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
Framework‑dependent (smaller, requires user‑installed runtime):
```
dotnet publish Enfolderer.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
```

## Roadmap Ideas
* Quantity / inventory tracking
* Search & filters
* Export spreads / PDF
* Advanced trimming (size reduction) with descriptor
* UI theming (dark / high contrast) & token styling
* Optional offline mode using full cache only

## Attribution
Card data & images © Scryfall (https://scryfall.com). Unofficial; not endorsed by Wizards of the Coast.

## License
MIT
