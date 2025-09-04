# Refactor Plan: Streamline Core Logic

Branch: `refactor/streamline-core` (to be created in VCS by user)

## Objectives
1. Preserve all documented behavior (see BEHAVIOR.md) while reducing complexity & duplication.
2. Establish clear pipelines: Parse -> ResolveMetadata -> EnrichQuantities -> ArrangeFaces -> Render.
3. Isolate infrastructure concerns (HTTP, DB, Caching) behind narrow interfaces.
4. Centralize configuration & constants; remove scattered environment variable checks.
5. Improve test surface so future changes are safe & intentional.

## Phase Breakdown

### Phase 0: Baseline
- [x] Behavior snapshot (BEHAVIOR.md)
- [ ] Add characterization tests for critical paths.

### Phase 1: Testing Foundation
- Create `Enfolderer.Tests` (separate project if needed) or expand existing self-tests.
- Add tests:
  - Binder parsing fixtures (one input -> expected spec list summary counts).
  - MFC quantity mapping matrix (logical qty 0,1,2).
  - Fallback pairing heuristic (two front-like faces).
  - Import supplement: starting DB state + post-import delta.
  - Card back resolution priority.
  - Toggle cycle scenarios (single vs MFC front).

### Phase 2: Modularization
- Extract interfaces:
  - `IBinderParser`
  - `IMetadataProvider` (wraps Scryfall & cache)
  - `IQuantityRepository` (collection + mainDb operations)
  - `IImportService` (unify import paths)
  - `ICardArrangementService` (pairing, ordering, fallback heuristics)
- Move heuristic logic from `CardQuantityService` & `MainWindow` into dedicated services.

### Phase 3: Cleanup & Consolidation
- Remove legacy `ScryfallImportService` if fully superseded.
- Collapse duplicated fallback logic for MFC into arrangement service.
- Replace `Environment.GetEnvironmentVariable` calls with `IRuntimeFlags` (single resolution at startup).
- Normalize logging through `ILogSink` with category tags.

### Phase 4: Performance / Allocation Review
- Profile large binder parse; minimize temporary list allocations in parser.
- Batch DB updates where currently repeating single row writes (imports already partially optimized).

### Phase 5: UX Safety & Finalization
- Introduce optional dry-run mode for normalization & imports.
- Add command to dump current pipeline state (debug menu) for a selected set.
- Update docs and produce final diff summary.

## Risk Mitigation
- Commit small, reviewable changes.
- After each phase run full self-tests + manual smoke (open binder, toggle, import set, view MFC display, placeholder backs).

## Deferred / Nice-To-Have
- Async image fetch cancellation tokens per page.
- Virtualized page rendering.
- Pluggable variant rules (e.g., custom language tags).

## Open Questions
- Should quantities >2 ever display differently? (Currently capped.)
- Any desire to support partial backface artwork mapping beyond static card back?

Update this file as phases progress.
