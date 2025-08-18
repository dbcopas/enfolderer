# Enfolderer MTG Binder

WPF application to visualize a Magic: The Gathering collection in a virtual 20-page quad binder (4 columns x 3 rows per page = 12 slots, 480 total).

## Features (v1)
* 20-page binder navigation (first page on right, last page on left, middle pages shown in spreads)
* Loads a plain text list of card names (`Name` or `Name|MFC` for modal double-faced cards)
* Double-faced (MFC/DFC) entries allocate two consecutive faces
* Placeholder colored backgrounds with card name centered
* Basic navigation (First / Prev / Next / Last)

## Input File Format (CSV with semicolons)
Each line: `Name;CardNumber;Set(optional);Extra(optional)`

Indicate a modal/double-faced card by appending `|MFC` or `|DFC` to the name OR adding an additional `;MFC` field. The back face is auto-created.

Example:
```
Island;271;LTR
Lightning Bolt;125;2ED
Brutal Cathar|MFC;19;MID
Delver of Secrets;56;ISD;MFC
Forest;300;LTR
```

Rules:
* Name and CardNumber are mandatory.
* Set is optional.
* MFC fronts are forced into column 1 or 3 (index 0 or 2) so their back appears to the right.
* Non‑MFC cards may shift up/down within the page to accommodate this constraint.
* Malformed lines are skipped silently.

## Future Ideas
* Click-to-track owned quantity
* Filtering / searching
* Actual card images via Scryfall API
* Export / import collection metadata

## Running
Open the solution in Visual Studio or run:
```
dotnet build
dotnet run --project Enfolderer.App
```

Use File > Open Collection to load your text list.

## License
MIT (add a LICENSE file if distributing publicly).
