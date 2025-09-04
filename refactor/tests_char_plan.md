# Characterization Test Plan (Refactor Phase 1)

Planned coverage categories with minimal fixture-driven assertions.

## 1. Binder Parsing
- Input with: ranges, prefix ranges, attached ranges, star variants, language variants (+lang, range+lang), pair ranges (&&), interleave (||), backface directive, name override, alphanumeric suffix (23e), variant plus notation.
- Assert counts per category and specific key presence (Set:Number pairs).

## 2. Placeholder Backfaces
- Ensure `backface` directive produces synthetic set `__BACK__` and number `BACK` entries and mapping registered.

## 3. MFC Pairing & Quantities
- Proper flagged pair (front/back) quantity mapping for logical 0,1,2.
- Fallback heuristic (two front-like faces) triggers split.

## 4. Quantity Enrichment
- Leading zero alignment (007 -> 7)
- Suffix stripping (12a -> base search path)
- Progressive suffix trimming for non-digit tail.
- WAR star JP variant quantity propagation.

## 5. Toggle Logic
- Single-face cycle 0↔1.
- MFC front cycle 0->1->2->0 with back following display mapping.

## 6. Import Logic
- Simulate existing partial set rows; importer inserts missing and patches blanks.
- Auto-import treats partially present set.

## 7. Card Back Resolution
- External PNG overrides embedded JPG.
- Fallback chooses embedded PNG when external absent.

## 8. Ordering / Pair Placement
- MFC pair adjacency preserved.
- Explicit variant base + variant appear together (base before variant).

## 9. Utilities
- Normalization tool updates leading zero records (dry-run test harness invocation).

## 10. Invariants
- Physically two-sided layout list unchanged.
- Synthetic set code constant `__BACK__` present.

Add each as discrete test method; prefer deterministic in-memory DB (SQLite file in temp directory) with minimal schema.
