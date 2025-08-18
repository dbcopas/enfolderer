# Enfolderer MTG Binder

WPF application to visualize a Magic: The Gathering collection in one or more virtual 20‑page quad binders (4 columns x 3 rows per page = 12 slots per page). When your collection exceeds 480 card faces the app automatically creates additional binders and gives you binder‑level navigation.

Physical layout rules are emulated:
* Page 1 is a single right‑hand page (front cover open)
* Last page of each binder is a single left‑hand page (back cover)
* Interior pages display as two‑page spreads (left + right simultaneously)

## Current Features
* Unlimited binder support (every 20 pages becomes a new binder automatically)
* Accurate physical pagination (front cover / interior spreads / back cover)
* Modal / Double‑Faced (MFC / DFC) card handling with automatic back face slot
* MFC placement rules: front face forced into column 1 or 3 (zero‑based 0 or 2) so the back face sits immediately to its right
* Intelligent compaction / relocation so MFC constraints do not leave large gaps
* Scryfall REST API integration for card face images (front/back) with in‑memory caching
* API rate limiting (< 10 requests / second) and bounded parallelism to stay polite
* Skips image fetch for TOKEN set entries automatically (still shows placeholder)
* Comment lines starting with `#` in the input are ignored
* Binder navigation: First / Previous / Next / Last page AND Previous / Next Binder
* Direct jump UI: enter binder number + page number and press Go
* Async image loading with robust error handling (app stays responsive)
* Global exception handlers to avoid hard crashes
* MIT licensed

## Input File Format
Semicolon separated values per line:

```
Name;CollectorNumber;Set
```

Indicate a modal/double‑faced card using either name suffix or a trailing marker:
* `FrontName/BackName|MFC;123;SET`
* `FrontName/BackName|DFC;123;SET`
* Or `FrontName/BackName;123;SET;MFC` (legacy support)

The secondary (back) face is auto‑generated and placed immediately to the right of the front face.

Special parsing rules:
* Lines beginning with `#` are treated as comments and skipped.
* Empty or whitespace‑only lines are skipped.
* Malformed lines are skipped silently.
* If `Set` equals `TOKEN` the card is shown with a placeholder (no API call).
* Name + CollectorNumber are required; Set may be omitted (image lookup may then fail).

Example file:
```
# Lands
Island;271;LTR
Forest;300;LTR

# Creatures (includes a double-faced card)
Brutal Cathar/Moonrage Brute|MFC;19;MID
Delver of Secrets/Insectile Aberration;56;ISD;MFC

# A token we don't fetch
Spirit Token;1;TOKEN
```

### MFC Placement Constraint
An MFC front must start in column 0 or 2 so its back fits in 1 or 3. If a front would otherwise land in an odd column, the algorithm relocates it (and may shift other cards) to honor the constraint while keeping earlier ordering as intact as possible.

## Navigation
Toolbar provides:
* First / Prev / Next / Last page buttons
* Prev Binder / Next Binder buttons (when more than one binder exists)
* Jump fields: enter Binder (1‑based) and Page (1‑based within that binder) then Go

Displayed page indicator reflects global view index (covers and spreads) while the jump uses binder‑relative numbering.

## Image Fetching & Caching
Images are fetched via the Scryfall REST endpoint:
```
https://api.scryfall.com/cards/{set}/{collector_number}
```
* Responses are cached in‑memory by URL; repeated faces are instant.
* Parallel fetches are limited and total throughput is capped below 10/sec.
* Failure to fetch leaves a placeholder (no crash).
* Double‑faced cards select the appropriate face artwork for front/back.

## Building / Running
Requires .NET 8 SDK.

```
dotnet build
dotnet run --project Enfolderer.App
```
Then use File > Open (or the provided button) to choose your collection CSV.

## Roadmap / Future Ideas
* Quantity tracking / collection stats
* Search & filtering
* Persist cache to disk between sessions
* Configurable rate limit & parallelism
* Visual differentiation / theming for token & missing images
* Export binder as printable PDF spread

## Attribution
Card data & images provided by Scryfall (https://scryfall.com). This project is unofficial and not endorsed by Wizards of the Coast.

## License
MIT
