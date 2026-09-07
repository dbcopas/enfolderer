using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Enfolderer.Ai.Mcp.CardCatalog.Pokemon;

/// <summary>
/// Catalogue tools for the Pokémon TCG, owned by Team B and bound to <c>PokemonCardIdAgent</c>.
/// The tool names mirror the Magic server so agents share a single calling convention.
/// </summary>
[McpServerToolType]
public sealed class PokemonCatalogueTools
{
    private readonly PokemonTcgCatalogue _catalogue;

    public PokemonCatalogueTools(PokemonTcgCatalogue catalogue) => _catalogue = catalogue;

    [McpServerTool(Name = "lookup_by_set_and_number")]
    [Description("Look up the exact Pokemon printing for a set id and collector number, e.g. set 'sv1', number '012'.")]
    public async Task<string> LookupBySetAndNumberAsync(
        [Description("Set id, e.g. 'sv1' or 'swsh12'.")] string set,
        [Description("Collector number without the denominator, e.g. '012'.")] string collectorNumber,
        CancellationToken ct = default) =>
        Format(await _catalogue.LookupBySetAndNumberAsync(set, collectorNumber, ct));

    [McpServerTool(Name = "lookup_by_name_and_set")]
    [Description("Look up a Pokemon printing by card name constrained to a set id. Use when the collector number is unreadable.")]
    public async Task<string> LookupByNameAndSetAsync(
        [Description("Card name as printed.")] string name,
        [Description("Set id, e.g. 'sv1'.")] string set,
        CancellationToken ct = default) =>
        Format(await _catalogue.LookupByNameAndSetAsync(name, set, ct));

    [McpServerTool(Name = "lookup_by_name")]
    [Description("Pokemon card name search. Last resort: many cards share a name across sets, so prefer the set-constrained tools.")]
    public async Task<string> LookupByNameAsync(
        [Description("Card name as printed.")] string name,
        CancellationToken ct = default) =>
        Format(await _catalogue.LookupByNameAsync(name, ct));

    internal static string Format(CataloguePrinting? printing) => printing is null
        ? """{"found":false}"""
        : JsonSerializer.Serialize(new
        {
            found = true,
            game = "pokemon",
            set = printing.Set,
            collectorNumber = printing.CollectorNumber,
            name = printing.Name,
            language = printing.Language
        });
}
