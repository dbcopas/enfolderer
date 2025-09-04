# Behavior Characterization (Initial Snapshot)

This document captures current observable rules prior to refactor. Each line is an invariant or rule we must preserve unless explicitly changed.

## Parsing / Binder Syntax
- Lines starting with `=` change current set code.
- `count;backface` lines create that many placeholder backface entries using synthetic set `__BACK__` number `BACK`.
- Lines with three semicolon-separated fields `Name;SET;Number` treated as explicit entries (numeric Number required to short-circuit lookup heuristics).
- Prefix ranges: `<PFX> <start>-<end>` expands to `<PFX><n>` for n in range.
- Attached prefix ranges: `<PFX><start>-<end>` with zero padding preserved.
- Mixed pair notation `A&&B` pairs parallel numeric ranges.
- Interleave operator `||` merges multiple range or single segments round-robin.
- Suffix ranges `<start>-<end><SUFFIX>` append suffix to each zero-padded base.
- Star (★) prefix notation: `★X-Y` or `★N` expands to numbers with trailing star.
- Variant plus notation: `BASE+lang` => base + variant `base/lang` with display `base (lang)`.
- Language variant paired ranges: `start-end+lang` creates base and variant entries.
- Simple numeric range `N-M` expands sequential numbers (zero padding preserved when both ends share width).
- Number plus name override: `123; Custom Name` sets OverrideName for display.
- Entries can include alphanumeric suffixes (e.g., `23e`).

## Placeholder Backfaces
- Synthetic entries: set `__BACK__`, number `BACK`, flagged as placeholder; UI hides info panel and uses card back image mapping.

## MFC (Modal Double-Faced) Handling
- Front face flagged `IsModalDoubleFaced && !IsBackFace`.
- Quantity mapping: logical qty 0 => front/back display 0/0 (dim both), 1 => 1/0 (front normal, back dim), >=2 => 2/2 (both normal).
- Fallback heuristic: if two faces share set+number, both have front/back raw text but neither flagged back, treat second as back.
- Pair detection also supports layout inference from Scryfall `layout` values (transform, modal_dfc, battle, double_faced_token, double_faced_card, prototype, reversible_card).

## Quantity Enrichment & Persistence
- Quantities sourced from `mtgstudio.collection` (CollectionCards) for normal cards; custom cards (negative cardId) persisted to `mainDb.db`.
- Matching tries (set, number), then trimmed leading zeros, then progressively strips non-digit suffix characters.
- WAR Japanese star variants (★) map to variant quantities via keys with variant tags `Art JP` or `JP`.
- Toggle cycle: single-face card 0↔1; MFC front cycles 0→1→2→0.
- Variant enrichment updates both base and trimmed keys and variant quantity dictionaries.

## Import Logic
- Single set import (ScryfallSetImporter): inserts missing collector numbers; patches blank name/rarity/multiverse ID fields; force reimport deletes existing rows for set.
- Auto import now supplements every binder set except `__BACK__` (even partially present sets) using same logic.
- Embedded fallback card back image resolution: prefer external PNG/JPG in exe dir, else embedded PNG, else embedded JPG.

## Image Handling
- Card back mapping stored in CardImageUrlStore for `__BACK__/BACK` entries (front and back image identical).
- Transparent PNG card backs supported and displayed with rounded corners.

## UI/Display Rules
- Quantity 0 cards (non-placeholder) get 50% opacity and red border.
- Placeholder backface hides info panel and quantity badge if blank.
- Rounded corners applied to card images.

## Utilities
- Normalization tool strips leading zeros from purely numeric `collectorNumberValue` preserving suffix after '/'.
- CSV updater merges inserts/updates into mainDb respecting variable schema columns (name/version/modifier presence).

## Invariants / Tokens
- Synthetic placeholder set code: `__BACK__`.
- Physically two-sided layouts: transform, modal_dfc, battle, double_faced_token, double_faced_card, prototype, reversible_card.

## Pending Clarifications (mark during refactor)
- Confirm whether non-MFC reversible layouts should always synthesize back face.
- Confirm desired behavior for quantities when logical quantity >2 (currently clamps to 2/2 display).

---
Generated initial snapshot. Extend with edge cases as discovered.
