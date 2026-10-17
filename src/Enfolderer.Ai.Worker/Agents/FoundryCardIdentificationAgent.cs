using System.Text.Json;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Imaging;
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
        AgentImage? footer = null;
        if (Game == CardGames.Magic)
        {
            try
            {
                footer = new AgentImage(CardFooterCropper.CropToPng(crop.Image.Content), "footer.png", "image/png");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Could not enlarge the footer for card {Index}; using the full crop.", crop.Index);
            }
        }
        var reply = await _client.RunAsync(_agentId, prompt, crop.Image, CatalogueServerLabel, ct, footer,
            text =>
            {
                var parsed = ParseIdentification(text, crop, Game, AgentId);
                ValidateLanguage(parsed);
                if (!parsed.IsIdentified && parsed.Resolution == "unresolved")
                    throw new InvalidOperationException(
                        "Catalogue resolution was unresolved and no usable name/set were returned. "
                        + "Read the title again in the printed language, and inspect the enlarged footer "
                        + "for the set and number. Try lookup_by_set_and_number if both are legible.");
            });

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
            {"set":"","collectorNumber":"","name":"","language":null,"readName":null,"readLanguage":null,
             "confidence":0.0,"resolution":"","readCollectorNumber":"","readSet":"","alternates":[]}

            "alternates" is the list of that name under the same key in the tool's reply. Copy it
            across unchanged — every entry, every field, the URLs character for character — and
            leave it empty only if the tool gave you none. Do not look at the pictures, do not
            shorten the list and do not choose between them: that is done elsewhere, by whoever
            holds the photograph, and it is the only check there is on a reading whose every field
            is plausible and whose printing is still the wrong one.

            "name" is the catalogue's English name, whatever language the card is printed in, and
            "language" is the language the card itself is printed in, not a catalogue fallback's
            language. Transcribe the photographed title into "readName" before translating it.
            For Magic, put the language letters you can read beside the set code in "readLanguage",
            normalized to API codes (JP/JPN means "ja", DE means "de", EN means "en"); otherwise null.
            Determine "language" from that footer or the printed title/rules, and pass it to the
            catalogue tool. Never default to English because the catalogue returns an English name
            or printing. A Japanese title with kana cannot be English. If the photographed language
            cannot be determined, use null, not "en".

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
            Language = game == CardGames.Magic ? NormalizeLanguage(Read("language")) : Read("language") ?? "en",
            ReadName = Read("readName"),
            ReadLanguage = NormalizeLanguage(Read("readLanguage")),
            Resolution = Read("resolution"),
            ReadCollectorNumber = Read("readCollectorNumber"),
            ReadSet = Read("readSet"),
            Confidence = confidence,
            Agent = agentId,
            ArtReferences = ReadArtReferences(root)
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

    internal static void ValidateLanguage(IdentifiedCard card)
    {
        if (card.Game != CardGames.Magic || !card.IsIdentified) return;
        if (card.Language is null || card.ReadName is null)
            throw new InvalidOperationException(
                "The Magic reply must include the photographed language and readName (the original printed title). "
                + "Read the title/rules and enlarged footer; an English catalogue name is not language evidence.");
        if (card.ReadLanguage is not null && card.Language != card.ReadLanguage)
            throw new InvalidOperationException(
                $"The photographed footer language '{card.ReadLanguage}' contradicts language '{card.Language}'. "
                + "Use the photographed language, pass it to the catalogue, and do not copy an English fallback's language.");
        if (card.Language != "ja" && card.ReadName.Any(c =>
                c is >= '\u3041' and <= '\u3096' or >= '\u30a1' and <= '\u30fa' or >= '\uff66' and <= '\uff9d'))
            throw new InvalidOperationException(
                $"The printed title '{card.ReadName}' contains Japanese kana but language is '{card.Language}'. "
                + "Re-read the photographed title and footer, resolve with language 'ja', and retain Japanese "
                + "as the photographed language even if the catalogue falls back to an English printing.");
    }

    private static string? NormalizeLanguage(string? language) => language?.Trim().ToLowerInvariant() switch
    {
        null or "" or "unknown" => null,
        "jp" or "jpn" => "ja",
        var code => code
    };

    /// <summary>
    /// The printings the catalogue offered pictures of, copied through by the agent.
    /// <para>
    /// Every failure here is silent on purpose. The list is the model repeating a tool's output,
    /// so it can arrive truncated, reshaped, renamed or not at all, and none of that is a reason
    /// to reject a card whose name and set were read correctly. A missing list means the art
    /// cannot be checked; it does not mean the reading is wrong.
    /// </para>
    /// </summary>
    private static IReadOnlyList<CardArtReference> ReadArtReferences(JsonElement root)
    {
        if (!root.TryGetProperty("alternates", out var alternates)) return [];
        if (alternates.ValueKind != JsonValueKind.Array) return [];

        var references = new List<CardArtReference>();
        foreach (var entry in alternates.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var url = Text(entry, "imageUrl");
            if (string.IsNullOrWhiteSpace(url)) continue;

            references.Add(new CardArtReference(
                Text(entry, "set") ?? string.Empty,
                Text(entry, "collectorNumber") ?? string.Empty,
                url));
        }

        return references;

        static string? Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
