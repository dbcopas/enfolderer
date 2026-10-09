# Training photographs

Each subdirectory holds one photograph and the ground truth for it:

- `image.jpg` — a photograph of some cards. These happen all to be binder pages; they need not be.
- `cards.txt` — one line per card that is actually in the page, in the format

  ```text
  location;name;edition;collector number;foil yes no;language
  ```

Every field is checked for shape, not just the first: a line must have all six, and the `edition`
and `number` columns are the pair that get written the wrong way round, so the check insists an
edition has a letter in it and a collector number has a digit. The listing is read by people as
well as by the tool — it is what a scan's output gets compared against by hand — and a line in the
wrong order makes a correct reading look wrong.

`location` is `row,column`, counting from zero at the top left: `0,0` is top left, `0,2` is top
right, `2,0` is bottom left and `2,2` is bottom right of a nine-pocket page. Pockets with no card in
them are simply absent from the file.

Rows and columns are counted **as the cards read**, not as the photograph is stored. A page
photographed sideways — `05` is one — has its cards lying on their side in the frame, so turn the
photograph upright in your head before you write the file down. The check allows for that quarter
turn when the cards it locates are wider than they are tall.

The check compares layouts after sliding both to the origin, so a photograph that does not show the
corner of the page is still right when the cards are in the right places relative to each other.
`01/cards.txt` was originally written column-first and has been transposed to match the others.

These are the photographs that count. Every synthetic test written for the detector passed against
code that found two cards out of nine in `01`.

The photographs are deliberately awkward, and each one is here for a reason: `01` is a full page,
`02` is a page cropped so that one column and the bottom row are out of frame, `03` is a page with
two opposite corners empty, `04` is shot at enough of an angle that the far column is a quarter
narrower than the near one, `05` is a twelve-pocket page photographed sideways and full of twelve
near-identical Japanese full-art lands, and `06` is a twelve-pocket page showing eight cards.

`05` is the hardest of them and worth understanding. Because its cards lie on their side, each one
is two upright cards wide — and each half of it is therefore card-shaped, four-sided and busy
inside, so the halves outnumber and outvote the whole cards. The detector settles it by sliding the
photograph sideways against itself: a page slid by one card's spacing lands card on card and looks
like itself, while slid by half a card it lands artwork on text box and does not.

## Running the check

```powershell
dotnet run --project tools/Enfolderer.Ai.Imaging.TrainingCheck
```

It prints the expected and the located pockets for each photograph and exits non-zero when any
photograph disagrees. All six should pass, for 47 cards of 47.

The separate printing check exercises the worker's art verifier on all nine cards in `01`,
using the boundary coordinates from the failing live scan:

```powershell
dotnet run --project tools\Enfolderer.Ai.CardCatalog.Check -- --photo-art
```

It must move Sword of Hearth and Home from MH2 238 to TMC 136 and Food Chain from 2X2 147 to
TMC 133, preserve the other seven printings, and reject unrelated-card photographs. The original
distance and separation thresholds are asserted, not relaxed. Unlike the layout check, this
requires internet access to fetch public catalogue pictures; they stay in memory and are not saved.
Omit `--photo-art` to run that tool's offline catalogue, retry, and synthetic-image checks only.

Note that a card cut off by the edge of the frame is **not** listed: `02` shows the top of a row of
Annex cards along its bottom edge and they are deliberately absent from its `cards.txt`, because a
card without all four of its sides in the photograph is not something the detector should claim to
have found.

## Adding a photograph

Create a numbered directory, drop in `image.jpg` and `cards.txt`, and run the check. Nothing needs
registering: the tool walks every subdirectory it finds here. Photographs that are hard for a
reason no existing one covers are the useful ones — a twelve-pocket sheet, cards loose on a table,
a page under a window.
