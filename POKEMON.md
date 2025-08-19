# Pokémon Binder Branch

This branch will adapt Enfolderer for Pokémon TCG cards.

## Planned Changes
- Abstract card metadata provider (Scryfall -> Pokémon TCG API).
- Implement `PokemonTcgProvider` using https://api.pokemontcg.io/v2/cards/{setId}-{number} or search endpoint.
- Input format: continue using `=SETCODE` with collector number lists. Add mapping file for Pokémon set codes if needed.
- Simplify multi-face logic (mostly single-face). Support special multipart (V-UNION) later.
- Cache segregation: prefix cache files with `pkm_` to avoid collisions with MTG.
- Display additions: rarity, type icons (optional future), set symbol placeholder.
- Remove MFC pairing alignment rules unless variant pairing feature added.

## Incremental Steps
1. (In progress) Introduce provider interface (currently inlined PokemonMode bool).
2. (Done basic) Pokémon fetch logic + minimal JSON parsing (name, number, image.large).
3. TODO: Toggle via config or command-line flag (now hardcoded PokemonMode=true on branch).
4. TODO: Adjust cache keying + schema version bump segregation.
5. TODO: Validate performance & rate limiting (may need API key for heavy use).

## Notes
- Pokémon API may require an API key for higher rate limits; keep optional.
- Image size: use `images.large` for consistency.
- Consider adding a game selector UI later.

