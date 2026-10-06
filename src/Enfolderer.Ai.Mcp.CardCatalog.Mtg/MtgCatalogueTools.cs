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

    [McpServerTool(Name = "resolve_printing")]
    [Description("""
        Settle a Magic printing from what was read off the card. This is the tool to use: the
        others are for awkward cases.

        The name and the set code are what it works from. Give the collector number too when it
        can be read, but as corroboration — the number is the least legible thing on a card and
        looking it up does not check it, because a set has a card at nearly every number, so a
        misread digit returns a real printing of the wrong card. The number returned is the
        catalogue's and is the one to answer with.

        Read "resolution" in the reply: confirmed (name and number agreed), corrected (they did
        not, and the name won), named (no number was read), assumed (no name was read, so nothing
        corroborated the number), ambiguous (the set holds this name more than once and no number
        separated them - the candidates are listed, do not pick one at random), unresolved.
        """)]
    public async Task<string> ResolvePrintingAsync(
        [Description("Card name exactly as printed on the card, in whatever language it is printed in.")] string name,
        [Description("Set code as printed on the card, e.g. 'mh3' or 'sld'.")] string set,
        [Description("Collector number as read, e.g. '438' or '5J-b'. Omit when it cannot be read with confidence.")] string? collectorNumber = null,
        [Description("Two-letter language code of the printing, e.g. 'en', 'ja', 'de'. Omit when unsure.")] string? language = null,
        CancellationToken ct = default)
    {
        var resolved = await _catalogue.ResolveAsync(name, set, collectorNumber, language, ct);

        if (resolved.Printing is null)
            return JsonSerializer.Serialize(new
            {
                found = false,
                resolution = resolved.Resolution,
                readCollectorNumber = resolved.ReadCollectorNumber,
                // Listed rather than chosen between: whichever is picked here would look exactly
                // like an answer, and the caller is the only one that can see the card.
                candidates = (resolved.Candidates ?? []).Select(c => new
                {
                    set = c.Set,
                    collectorNumber = c.CollectorNumber,
                    name = c.Name,
                    printedName = c.PrintedName,
                    language = c.Language
                })
            });

        return JsonSerializer.Serialize(new
        {
            found = true,
            game = "mtg",
            resolution = resolved.Resolution,
            readCollectorNumber = resolved.ReadCollectorNumber,
            set = resolved.Printing.Set,
            collectorNumber = resolved.Printing.CollectorNumber,
            // Always the English name, whatever language the card is printed in; printedName is
            // the localised one and is here only so the reading can be checked against the card.
            name = resolved.Printing.Name,
            printedName = resolved.Printing.PrintedName,
            language = resolved.Printing.Language
        });
    }

    [McpServerTool(Name = "lookup_by_set_and_number")]
    [Description("Look up the exact Magic printing for a set code and collector number, e.g. set 'bro', number '167'.")]
    public async Task<string> LookupBySetAndNumberAsync(
        [Description("Set code as printed on the card, e.g. 'bro' or 'sld'.")] string set,
        [Description("Collector number as printed, e.g. '167' or '5J-b'.")] string collectorNumber,
        [Description("Two-letter language code of the printing, e.g. 'ja'. Omit for English.")] string? language = null,
        CancellationToken ct = default) =>
        Format(await _catalogue.LookupBySetAndNumberAsync(set, collectorNumber, language, ct));

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
            printedName = printing.PrintedName,
            language = printing.Language
        });
}
