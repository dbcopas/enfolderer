using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Enfolderer.Ai.Mcp.CardCatalog.Mtg;

/// <summary>
/// Catalogue tools for Magic: The Gathering, owned by Team B and bound to <c>MtgCardIdAgent</c>.
/// Nothing here can see storage, the job store or the geometry project.
/// </summary>
[McpServerToolType]
public sealed class MtgCatalogueTools
{
    private readonly ScryfallCatalogue _catalogue;

    public MtgCatalogueTools(ScryfallCatalogue catalogue) => _catalogue = catalogue;

    [McpServerTool(Name = "lookup_by_set_and_number")]
    [Description("Look up the exact Magic printing for a set code and collector number, e.g. set 'bro', number '167'.")]
    public async Task<string> LookupBySetAndNumberAsync(
        [Description("Set code as printed on the card, e.g. 'bro' or 'sld'.")] string set,
        [Description("Collector number as printed, e.g. '167' or '5J-b'.")] string collectorNumber,
        CancellationToken ct = default) =>
        Format(await _catalogue.LookupBySetAndNumberAsync(set, collectorNumber, ct));

    [McpServerTool(Name = "lookup_by_name_and_set")]
    [Description("Look up a Magic printing by card name constrained to a set code. Use when the collector number is unreadable.")]
    public async Task<string> LookupByNameAndSetAsync(
        [Description("Card name, fuzzy matching is applied.")] string name,
        [Description("Set code as printed on the card.")] string set,
        CancellationToken ct = default) =>
        Format(await _catalogue.LookupByNameAndSetAsync(name, set, ct));

    [McpServerTool(Name = "lookup_by_name")]
    [Description("Fuzzy Magic card name search. Last resort: it returns an arbitrary printing, so prefer the set-constrained tools.")]
    public async Task<string> LookupByNameAsync(
        [Description("Card name, fuzzy matching is applied.")] string name,
        CancellationToken ct = default) =>
        Format(await _catalogue.LookupByNameAsync(name, ct));

    internal static string Format(CataloguePrinting? printing) => printing is null
        ? """{"found":false}"""
        : JsonSerializer.Serialize(new
        {
            found = true,
            game = "mtg",
            set = printing.Set,
            collectorNumber = printing.CollectorNumber,
            name = printing.Name,
            language = printing.Language
        });
}
