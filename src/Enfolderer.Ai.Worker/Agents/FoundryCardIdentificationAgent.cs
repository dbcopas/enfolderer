using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Microsoft.Extensions.Logging;

namespace Enfolderer.Ai.Worker.Agents;

/// <summary>
/// Identifies one of Team B's identification agents, and says which catalogue tool settles a
/// printing for that game. Each agent is deployed separately in the <c>cardid</c> project and is
/// wired to its own catalogue MCP server, so adding a game means adding a definition here plus an
/// agent and an MCP server — no pipeline changes.
/// </summary>
/// <param name="ResolutionTool">
/// The tool that settles a printing from the name, or null for a game whose catalogue has none
/// yet. Only Magic has one; the rest still look a number up, with everything that costs.
/// </param>
/// <remarks>
/// What this deliberately no longer carries is the agent's instructions. They live in the agent
/// YAML under <c>agents/cardid/</c> and nowhere else. Holding a second copy here is what caused
/// the collector numbers to stay wrong after the YAML was corrected: the YAML is the system
/// message, this was the user message, and the user message is what the model followed. A prompt
/// in two places is a prompt that will disagree with itself, and the copy in the image is the one
/// that cannot be fixed without a rebuild.
/// </remarks>
public sealed record GameAgentProfile(
    string Game, string AgentName, string CatalogueToolHint, string GameNotes, string? ResolutionTool = null)
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
        """,
        ResolutionTool: "resolve_printing");

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

    /// <summary>The <c>server_label</c> each game's catalogue MCP server is attached under.</summary>
    private const string CatalogueServerLabel = "catalogue";

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
        // Each identification agent attaches its own game's catalogue server under this label.
        // The label is the same for every game; the server behind it is not, and an agent can only
        // ever be handed its own, so approving by label does not widen anything.
        var reply = await _client.RunAsync(_agentId, prompt, crop.Image, CatalogueServerLabel, ct);

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

    /// <summary>
    /// The message sent with the crop on every run.
    /// <para>
    /// It is deliberately thin. How to read a card, which tool to reach for and what to answer are
    /// the agent's standing instructions, and those live in <c>agents/cardid/*.yaml</c>, which can
    /// be edited and re-provisioned without rebuilding this image. A second copy of them here
    /// would be sent as the user message, and the user message is what a model follows when the
    /// two disagree — so the copy that is hardest to change would win.
    /// </para>
    /// <para>
    /// What remains is the shape of the answer, which the pipeline — not the agent — depends on,
    /// and a reminder of which tool settles the printing, because that is the step a model skips.
    /// </para>
    /// </summary>
    internal static string BuildPrompt(GameAgentProfile profile)
    {
        var settle = profile.ResolutionTool is null
            ? $"Confirm the printing with your catalogue tools ({profile.CatalogueToolHint}) before answering."
            : $"""
               Settle the printing by calling {profile.ResolutionTool} ({profile.CatalogueToolHint}) with the
               name and set code you read, and the collector number only if you could read it. Pass the set
               code only if you read it off this card; leave it empty if you did not, however sure you are
               which set the card is from. Answer with the set, collector number and name it gives back,
               never with the number you read, and copy its "resolution", "readCollectorNumber" and
               "readSet" into your reply.

               If the tool answers "unresolved", or you cannot call it, answer with what you read off the card
               and lower "confidence" to say so. A card recorded from your own reading can be checked later; a
               card you decline to name is simply lost, and the name is the part you read most reliably.
               """;

        return $$"""
            Identify the single collectible card in this image.

            {{settle}}

            Return ONLY a JSON object, with no prose and no markdown fences:
            {"set":"","collectorNumber":"","name":"","language":"en","finish":"nonfoil|foil|etched",
             "confidence":0.0,"resolution":"","readCollectorNumber":"","readSet":""}

            "name" is the catalogue's English name, whatever language the card is printed in, and
            "language" is the language the card itself is printed in.

            If you can read the name but not the collector number, leave "collectorNumber" null and
            answer anyway: a name and a set are enough for the card to be recorded.

            Return {"error":"why"} only when the card itself cannot be read — a crop that is blurred,
            cut off, face down or empty. Not being certain of the printing is not such a case.
            """;
    }

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
            Resolution = Read("resolution"),
            ReadCollectorNumber = Read("readCollectorNumber"),
            ReadSet = Read("readSet"),
            Confidence = confidence,
            Agent = agentId
        };

        // The reply goes into the error because this is the case that looks like success from the
        // outside: valid JSON, a 200 from every call, and nothing identified. Without the text
        // there is no way to tell a model that refused from one that answered a different shape.
        return card.IsIdentified
            ? card
            : card with
            {
                Error = "Agent reply was missing the set or the name: " + AgentJson.Summarize(reply)
            };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
