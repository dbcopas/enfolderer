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
1. Introduce provider interface.
2. Implement Pokémon provider + minimal JSON parsing (name, number, set, image.large).
3. Toggle via config or command-line flag.
4. Adjust cache keying + schema version bump for Pokémon path.
5. Validate performance with lazy loading as in MTG flow.

## Notes
- Pokémon API may require an API key for higher rate limits; keep optional.
- Image size: use `images.large` for consistency.
- Consider adding a game selector UI later.

