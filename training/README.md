# Training photographs

Each subdirectory holds one photograph and the ground truth for it:

- `image.jpg` — a photograph of a binder page.
- `cards.txt` — one line per card that is actually in the page, in the format

  ```text
  location;name;edition;collector number;foil yes no;language
  ```

`location` is a pair of zero-based pocket coordinates, so a nine-pocket page runs from `0,0` at the
top left to `2,2` at the bottom right. Pockets with no card in them are simply absent from the file.

> **The two orders in this set do not agree.** `01/cards.txt` lists its pockets column-first — Food
> Chain is `1,0` and sits top-middle in the photograph — while `02/cards.txt` only makes sense read
> row-first. `03` and `04` are diagonally symmetric and so cannot tell the two apart. Until that is
> settled, `TrainingCheck` compares the layout it found against the listing **up to transposition**,
> and also up to translation, so that a listing which omits an empty first row still matches.

These are the photographs that count. Every synthetic test written for the detector passed against
code that found two cards out of nine in `01`.

## Running the check

```powershell
dotnet run --project tools/Enfolderer.Ai.Imaging.TrainingCheck
```

It prints the expected and the located pockets for each photograph and exits non-zero when any
photograph disagrees.

`02` is expected to fail: its page is cut off at the right-hand edge of the frame, no nine-pocket
lattice fits inside the photo, and the loose-card fallback cannot segment a binder page. Framing the
whole page is the fix, and that is the advice in `docs/azure-setup.md`.

## Adding a photograph

Create a numbered directory, drop in `image.jpg` and `cards.txt`, and run the check. Nothing needs
registering: the tool walks every subdirectory it finds here.
