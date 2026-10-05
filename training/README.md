# Training photographs

Each subdirectory holds one photograph and the ground truth for it:

- `image.jpg` — a photograph of some cards. These happen all to be binder pages; they need not be.
- `cards.txt` — one line per card that is actually in the page, in the format

  ```text
  location;name;edition;collector number;foil yes no;language
  ```

`location` is `row,column`, counting from zero at the top left: `0,0` is top left, `0,2` is top
right, `2,0` is bottom left and `2,2` is bottom right of a nine-pocket page. Pockets with no card in
them are simply absent from the file.

The check compares layouts after sliding both to the origin, so a photograph that does not show the
corner of the page is still right when the cards are in the right places relative to each other.
`01/cards.txt` was originally written column-first and has been transposed to match the others.

These are the photographs that count. Every synthetic test written for the detector passed against
code that found two cards out of nine in `01`.

The photographs are deliberately awkward, and each one is here for a reason: `01` is a full page,
`02` is a page cropped so that one column and the bottom row are out of frame, `03` is a page with
two opposite corners empty, and `04` is shot at enough of an angle that the far column is a quarter
narrower than the near one.

## Running the check

```powershell
dotnet run --project tools/Enfolderer.Ai.Imaging.TrainingCheck
```

It prints the expected and the located pockets for each photograph and exits non-zero when any
photograph disagrees. All four should pass, for 27 cards of 27.

Note that a card cut off by the edge of the frame is **not** listed: `02` shows the top of a row of
Annex cards along its bottom edge and they are deliberately absent from its `cards.txt`, because a
card without all four of its sides in the photograph is not something the detector should claim to
have found.

## Adding a photograph

Create a numbered directory, drop in `image.jpg` and `cards.txt`, and run the check. Nothing needs
registering: the tool walks every subdirectory it finds here. Photographs that are hard for a
reason no existing one covers are the useful ones — a twelve-pocket sheet, cards loose on a table,
a page under a window.
