# Enfolderer MTG Binder

WPF application for visualizing a Magic: The Gathering collection in virtual quad binders (4×3 = 12 slots per page). Each physical binder = 20 double‑sided pages (40 displayed sides). The app auto‑adds binders as your list grows beyond the 480 face capacity of one binder.

Physical pagination is emulated:
* Page 1: single right page (front cover opened)
* Interior: two‑page spreads (left + right simultaneously)
* Final page of a binder: single left page (back cover)

## Key Features (Current)
* Unlimited binders with correct cover / spread pagination
* Deterministic global ordering with adjacency constraints
	* Modal / Double‑Faced (MFC/DFC) cards automatically inject their back face immediately after the front
	* Duplicate name pairs (two consecutive identical front names) also forced to be adjacent
	* Pair starts aligned to even columns (0 or 2) so the second half sits to the right
	* Singles (including tokens) may be pulled forward to repair misalignment—no gaps
* New declarative input format with set sections, ranges, interleaving and optional overrides (details below)
* Lazy metadata resolution:
	* Initial minimal fetch (current + look‑ahead pages)
	* On‑demand background resolution as you navigate
	* Progress status updates (e.g. 12/120 resolved)
* Scryfall integration (names, MFC detection, images)
	* Automatic rate limiting (<10 requests/sec) + bounded parallelism
* Multi‑layer caching:
	* In‑memory image cache
	* Disk image cache (hashed filenames under LocalAppData)
	* Metadata + image URL cache keyed by SHA‑256 hash of the input file
	* Completion sentinel (.done) avoids partial-cache reuse
	* On full cache hit: instant layout with no metadata refetch
* Smart image URL reuse (skip extra metadata calls once URLs known)
* Token / custom placeholders supported (no image fetch for TOKEN)
* Binder & page navigation: First / Prev / Next / Last / Prev Binder / Next Binder / direct jump
* Responsive UI (no blocking while fetching)
* Single-file self‑contained publish option (for releases)
* MIT licensed

## Input File Format (Declarative)

The newer format removes the need to list names for normal set cards; names & MFC flags are pulled from Scryfall. You define structure with set blocks and numeric ranges.

Rules:
1. A set section starts with `=SETCODE` (e.g. `=STA`). Everything until the next `=` belongs to that set.
2. Single collector numbers: `123`
3. Ranges: `10-25` (inclusive)
4. Interleaving (round‑robin across sequences): `1-5||30-34||100` produces: 1,30,100,2,31,3,32,4,33,5,34
5. Name override (rare; for alt arts / tokens that still need API fetch): `123;Custom Name`
6. Explicit fixed entry (bypass API entirely—used for tokens or custom cards). Provide at least two semicolons: `Some Token;TOKEN;1`
7. Comments: lines starting with `#` are ignored.
8. Blank lines ignored.

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
```

Legacy CSV style (Name;Number;Set) is still parsed by the older loader, but the declarative format is now preferred.

### Adjacency & Layout Rules
* MFC front + synthetic back are treated as a 2‑card group.
* Two consecutive identical front names form a duplicate pair group.
* Group start must land at column 0 or 2 (avoid splitting a pair over a row boundary). If misaligned, a future single is pulled forward.
* Tokens can be moved just like other singles to preserve pair alignment.

### Lazy Loading Flow
1. Parse specs into an ordered list of unresolved entries.
2. Perform an initial small batch resolution (enough for first two pages worth of faces including MFC backs).
3. Build ordering (placeholders have provisional names) and render.
4. As you navigate, background resolution fills in missing specs for the active/next pages; views redraw incrementally.
5. After all specs resolve, metadata + image URLs are persisted and a `.done` sentinel written.

### Caching Details
Cache Root (MTG): `%LocalAppData%/Enfolderer/cache`
Cache Root (Pokémon branch): `%LocalAppData%/EnfoldererPokemon/cache`
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

## Image Fetching
`https://api.scryfall.com/cards/{set}/{collector_number}`
* Metadata calls rate‑limited (<10/sec)
* Image URLs stored; subsequent face loads skip metadata request
* Disk + memory cache for image bytes
* Tokens (set `TOKEN`) are skipped (placeholder only)

## Build & Run
Requires .NET 8 SDK.
```
dotnet run --project Enfolderer.App
```
Open a declarative collection file (`File > Open`).

## Pokémon API Key (Optional)
When running the Pokémon branch you can supply an API key for https://api.pokemontcg.io to potentially improve reliability (fewer 5xx / throttling responses) and attribute requests.

Environment Variable:
* `POKEMON_TCG_API_KEY` — key value from your pokemontcg.io account.

Set it (current PowerShell session):
```
$Env:POKEMON_TCG_API_KEY = "your_key_here"
```
Persist for your Windows user:
```
setx POKEMON_TCG_API_KEY "your_key_here"
```
Restart your IDE / shell after setting permanently.

UI Indicator:
* Status bar shows `API Key✓` (green) when detected, `API Key✗` (red) when absent.

Security Note: Key is only read from process environment; it is not written to disk or logged (except the presence boolean). Avoid checking it into source.

## Pocket Layout Toggle (4 / 9 / 12)
You can switch the binder page layout at runtime between:
* 12‑pocket: 4×3 (default quad page)
* 9‑pocket: 3×3 (traditional Pokémon page)
* 4‑pocket: 2×2 (small / showcase)

How:
* Use the `Layout:` ComboBox in the navigation bar to choose 4, 9 or 12.
* The grid reflows immediately for both left and right pages; pagination & ordering recompute.

Current Pair Alignment Behavior:
* The existing pair alignment logic (ensuring two‑card groups start on an even column) adapts to the active column count. For 2×2 and 3×3 layouts this still prevents a pair from being split across rows where possible.
* If no single can be pulled forward, a pair may straddle a row (fallback) rather than leaving a gap.

Limitations / Future Ideas:
* Persist selected layout across sessions (not implemented yet).
* Independent layout per binder (currently global).
* Optional scaling / padding tweaks per layout size.

## Caching (Pokémon Branch Additions)
* Separate cache root: `%LocalAppData%/EnfoldererPokemon/cache` to avoid collision with MTG cache.
* Set‑level bulk JSON (up to 12h TTL) + per‑card cache files prefixed with `pkm_`.

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
