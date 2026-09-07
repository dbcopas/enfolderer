using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Worker.Agents;

/// <summary>
/// Game-specific prompt knowledge for Team B's identification agents. Each agent is deployed
/// separately in the <c>cardid</c> project and is wired to its own catalogue MCP server, so adding
/// a game means adding a definition here plus an agent and an MCP server — no pipeline changes.
/// </summary>
public sealed record GameAgentProfile(string Game, string AgentName, string CatalogueToolHint, string GameNotes)
{
    public static readonly GameAgentProfile Mtg = new(
        CardGames.Magic,
        "MtgCardIdAgent",
        "mcp-cardcatalog-mtg",
        """
        This is a Magic: The Gathering card.
        - The collector number and set code sit in the bottom-left corner, usually as
          "123/281 R" on the first line and "SET · EN" (set code, language) on the second.
          Older printings have no collector number at all.
        - Set codes are 3-5 characters. Promotional and Secret Lair printings often use a
          different code from the set the art is from.
        - Collector numbers may carry a suffix such as "a", "b", "s", "★" or "z".
        - Foil printings show a holographic stamp near the bottom centre on modern frames.
        - Prefer the printed set code and collector number over the artwork: reprints share art.
        """);

    public static readonly GameAgentProfile Pokemon = new(
        CardGames.Pokemon,
        "PokemonCardIdAgent",
        "mcp-cardcatalog-pokemon",
        """
        This is a Pokémon Trading Card Game card.
        - The collector number is in the bottom-right corner as "012/198"; the set symbol sits
          next to it, and the set code (e.g. "sv1", "swsh12") is not always printed.
        - Promo cards use "SWSH###" / "SVP###" style numbers with no denominator.
        - Regulation mark (a single letter) appears in the bottom-left on recent sets.
        - Illustration-rare and special-art cards are full-art: the name may be stylised, so use
          the number plus the set symbol first and the name only to disambiguate.
        """);

    public static readonly GameAgentProfile YuGiOh = new(
        CardGames.YuGiOh,
        "YugiohCardIdAgent",
        "mcp-cardcatalog-yugioh",
        """
        This is a Yu-Gi-Oh! card.
        - The passcode is printed in the bottom-left; the set code (e.g. "LOB-EN005") is in the
          bottom-right of the artwork area.
        - Rarity is conveyed by foiling on the name and artwork rather than by any printed text.
        """);

    public static readonly GameAgentProfile Lorcana = new(
        CardGames.Lorcana,
        "LorcanaCardIdAgent",
        "mcp-cardcatalog-lorcana",
        """
        This is a Disney Lorcana card.
        - The collector number appears bottom-left as "123/204" with the set number beside it.
        - Enchanted cards are full-art with an alternate frame and a number above the set total.
        """);

    /// <summary>Profiles that currently have a deployed agent.</summary>
    public static IReadOnlyList<GameAgentProfile> Live => [Mtg, Pokemon];

    /// <summary>Growth slots: defined, not yet deployed.</summary>
    public static IReadOnlyList<GameAgentProfile> Planned => [YuGiOh, Lorcana];
}

/// <summary>
/// Team B identification agent, reached through the <c>cardid</c> project endpoint. One instance is
/// created per game; the underlying Foundry agent owns the catalogue MCP server for that game.
/// </summary>
public sealed class FoundryCardIdentificationAgent : ICardIdentificationAgent
{
    private readonly FoundryAgentClient _client;
    private readonly GameAgentProfile _profile;
    private readonly string _agentId;
    private readonly ILogger<FoundryCardIdentificationAgent> _log;

    public FoundryCardIdentificationAgent(
        FoundryAgentClient client,
        GameAgentProfile profile,
        string agentId,
        ILogger<FoundryCardIdentificationAgent> log)
    {
        _client = client;
        _profile = profile;
        _agentId = agentId;
        _log = log;
    }

    public string Game => _profile.Game;

    public string AgentId => $"cardid/{_profile.AgentName}";

    public async Task<IdentifiedCard> IdentifyAsync(CardCrop crop, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(_profile);
        var reply = await _client.RunAsync(_agentId, prompt, crop.CropSasUrl, ct);

        try
        {
            return ParseIdentification(reply, crop, Game, AgentId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Agent {AgentId} returned an unusable reply for card {Index}.", AgentId, crop.Index);
            return new IdentifiedCard
            {
                Index = crop.Index,
                Quad = crop.Quad,
                Game = Game,
                Agent = AgentId,
                Error = $"Unparseable agent reply: {AgentJson.Summarize(reply)}"
            };
        }
    }

    internal static string BuildPrompt(GameAgentProfile profile) => $$"""
        Identify the single collectible card in this image.

        {{profile.GameNotes}}

        Read the printed set code, collector number and name from the card, then confirm the
        printing with your catalogue tools ({{profile.CatalogueToolHint}}): try an exact
        set + collector number lookup first, then name + set, then a fuzzy name search.

        Return ONLY a JSON object:
        {"set":"","collectorNumber":"","name":"","language":"en","finish":"nonfoil|foil|etched","confidence":0.0}

        Rules:
        - Use the catalogue's canonical set code, collector number and name, not your own reading,
          whenever a lookup succeeds.
        - If you cannot identify the card, return {"error":"why"} instead.
        - Emit no prose and no markdown fences.
        """;

    /// <summary>Parses an identification reply. Internal so the contract can be self-tested.</summary>
    internal static IdentifiedCard ParseIdentification(string reply, CardCrop crop, string game, string agentId)
    {
        using var doc = JsonDocument.Parse(AgentJson.ExtractJsonObject(reply));
        var root = doc.RootElement;

        string? Read(string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? NullIfBlank(value.GetString())
                : null;

        var error = Read("error");
        if (error is not null)
        {
            return new IdentifiedCard
            {
                Index = crop.Index,
                Quad = crop.Quad,
                Game = game,
                Agent = agentId,
                Error = error
            };
        }

        var confidence = root.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var cv)
            ? Math.Clamp(cv, 0d, 1d)
            : 0d;

        var card = new IdentifiedCard
        {
            Index = crop.Index,
            Quad = crop.Quad,
            Game = game,
            Set = Read("set"),
            CollectorNumber = Read("collectorNumber"),
            Name = Read("name"),
            Language = Read("language") ?? "en",
            Finish = Read("finish") ?? "nonfoil",
            Confidence = confidence,
            Agent = agentId
        };

        return card.IsIdentified
            ? card
            : card with { Error = "Agent reply was missing set, collector number or name." };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
