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

- Choose a 4×3, 3×3, or 2×2 layout and set pages per binder from **Tools**.
- Move through pages and binders with the navigation controls, or jump directly to a binder and page.
- Search cards by name with **Ctrl+F**.
- Click a card to cycle its quantity between 0, 1, and 2. Quantities come from `mtgstudio.collection` for MTG Studio-backed cards and `mainDb.db` for custom/imported cards.
- Enfolderer caches metadata and images in `%LocalAppData%\Enfolderer\cache`, allowing subsequent loads of the same file to avoid repeated metadata requests.

For empty back slots, Enfolderer first looks for a local card-back image beside the collection file or executable, then in `%USERPROFILE%\Pictures\Enfolderer` and the executable's `images` directory. Supported names include `Magic_card_back.jpg`, `card_back.jpg`, and `back.jpg`; an embedded fallback is used otherwise.

## Supporting tools

The application also includes utilities for collection maintenance and exports:

- **Update mainDb from CSV** maps MTG Studio or compatible CSV data to `mainDb.db`, with a review step before applying updates.
- **Import Scryfall Set Into mainDb** imports a set by code. Hold **Shift** while invoking it to replace existing rows for that set.
- **Auto Import Missing Sets** imports binder-file set codes not yet found in the local database.
- **Export Playset Needs**, **Export Want List (Moxfield)**, and **Export Collection (Moxfield)** create inventory and Moxfield-oriented exports.
- **Match Wants CSV** compares Moxfield collection and wants exports, including price lookup.
- **Batch Export Binder Wants (Moxfield)** produces Moxfield want-list CSVs for each `binder*.txt` file in a selected folder.
- **Deck Pull Report** creates a pull/missing-card report from a Goldfish deck-list export.
- **Lands Viewer** and **Tokens Viewer** open CSV-based, searchable 3×3 binder views and let you mark entries as owned.

The remaining File-menu maintenance actions modify the local `mtgstudio.collection`; use them only when you understand the intended database operation. **Restore collection backup** restores its adjacent `.bak` file.

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
