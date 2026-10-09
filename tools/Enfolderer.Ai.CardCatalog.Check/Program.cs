using System.Net;
using System.Text;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Mcp.CardCatalog.Mtg;
using Enfolderer.Ai.Imaging;
using Enfolderer.Ai.Worker.Agents;
using Enfolderer.Ai.Worker.Pipeline;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Enfolderer.Ai.CardCatalog.Check;

/// <summary>
/// Checks the card resolution ladder against a stubbed Scryfall.
/// <para>
/// Run it with <c>dotnet run --project tools/Enfolderer.Ai.CardCatalog.Check</c> after touching
/// <see cref="ScryfallCatalogue"/> or the identification prompt. It exits non-zero on the first
/// failure, so it can be used as a gate.
/// </para>
/// <para>
/// Nothing here reaches the network. That is the point: <c>api.scryfall.com</c> is not reachable
/// from every environment this is built in, so a check that needed it would simply not be run, and
/// this ladder has already been broken once by a change that looked obviously correct. The stub
/// answers by URL, which also lets the checks assert on the <em>queries</em> — that a non-English
/// search asks for multilingual results, that no query is sent twice — and those are the bugs that
/// leave no trace in the result.
/// </para>
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] != "--photo-art" || args.Length > 2))
        {
            Console.Error.WriteLine("Usage: Enfolderer.Ai.CardCatalog.Check [--photo-art [training-directory]]");
            return 2;
        }
        NumberReadingChecks();
        ResolutionChecks().GetAwaiter().GetResult();
        PromptChecks();
        ThrottleChecks();
        ExportGateChecks();
        ArtImageUrlChecks().GetAwaiter().GetResult();
        ArtHashChecks();
        ArtAdjudicationChecks();
        ArtFetchChecks();
        RecalledSetChecks().GetAwaiter().GetResult();
        ScanReliabilityChecks.RunOfflineAsync(Check).GetAwaiter().GetResult();
        if (args.Length > 0)
            ScanReliabilityChecks.RunPhotoAsync(Check, args.Length == 2 ? args[1] : null)
                .GetAwaiter().GetResult();

        Console.WriteLine(_failures == 0
            ? "\nAll checks passed."
            : $"\n{_failures} check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(bool passed, string what)
    {
        Console.WriteLine((passed ? "  ok   " : "  FAIL ") + what);
        if (!passed) _failures++;
    }

    private static void Section(string title) => Console.WriteLine($"\n{title}");

    private static void NumberReadingChecks()
    {
        Section("A collector number as a reader transcribes it");

        Check(ScryfallCatalogue.NumbersMatch("438/462", "438"), "the set total is stripped");
        Check(ScryfallCatalogue.NumbersMatch("007", "7"), "leading zeros are ignored");
        Check(ScryfallCatalogue.NumbersMatch("5J-b", "5Jb"), "punctuation is ignored");
        Check(ScryfallCatalogue.NumbersMatch("123 R", "123r"), "the rarity letter survives, the spacing does not");
        Check(!ScryfallCatalogue.NumbersMatch("438", "436"), "a different number does not match");
        Check(!ScryfallCatalogue.NumbersMatch("", "436"), "a blank reading never matches");
        Check(ScryfallCatalogue.NumbersMatch("0", "0"), "an all-zero number survives trimming");
    }

    private static async Task ResolutionChecks()
    {
        Section("The name is the primary key");

        {
            // The case the whole inversion exists for: a digit misread returns a real printing of
            // a different card, and only the name catches it.
            var (catalogue, _) = Build(Searches(Card("mh3", "438", "Flare of Denial")));
            var resolved = await catalogue.ResolveAsync("Flare of Denial", "mh3", "436", "en", default);
            Check(resolved.Resolution == "corrected", "a number naming a different card is corrected");
            Check(resolved.Printing?.CollectorNumber == "438", "the catalogue's number is what comes back");
            Check(resolved.ReadCollectorNumber == "436", "the number read is kept beside it");
        }

        {
            var (catalogue, _) = Build(Searches(Card("mh3", "438", "Flare of Denial")));
            var resolved = await catalogue.ResolveAsync("Flare of Denial", "mh3", "438/462", "en", default);
            Check(resolved.Resolution == "confirmed", "agreement is confirmed, set total and all");
        }

        {
            var (catalogue, _) = Build(Searches(Card("mh3", "438", "Flare of Denial")));
            var resolved = await catalogue.ResolveAsync("Flare of Denial", "mh3", null, null, default);
            Check(resolved.Resolution == "named" && resolved.Printing?.CollectorNumber == "438",
                "a name alone yields the catalogue's number, marked named");
        }

        Section("A name transcribed imperfectly");

        {
            // The regression this tool was written for. "Juzam" for "Juzám" matches nothing exactly
            // and nothing as a substring; before fuzzy matching was restored the card was dropped.
            var (catalogue, stub) = Build(url => url.Contains("/cards/named")
                ? Card("arn", "27", "Juz\u00e1m Djinn")
                : null);
            var resolved = await catalogue.ResolveAsync("Juzam Djinn", "arn", "27", "en", default);
            Check(resolved.Resolution == "fuzzy", "a dropped accent is forgiven, and says so");
            Check(resolved.Printing?.Name == "Juz\u00e1m Djinn", "the catalogue's spelling is returned");
            Check(stub.Urls.Any(u => u.Contains("fuzzy=")), "the fuzzy endpoint was the rung that answered");
        }

        {
            // Fuzzy must be preferred over believing the number: a name read nearly right is far
            // likelier than a number read exactly right.
            var (catalogue, _) = Build(url => url.Contains("/cards/named")
                ? Card("mh3", "438", "Flare of Denial")
                : url.Contains("/cards/mh3/436") ? Card("mh3", "436", "Something Else") : null);
            var resolved = await catalogue.ResolveAsync("Flare of Denail", "mh3", "436", "en", default);
            Check(resolved.Printing?.Name == "Flare of Denial",
                "an approximate name beats an exact number");
            Check(resolved.Resolution == "fuzzy", "and is reported as the weaker match it is");
        }

        {
            var (catalogue, _) = Build(url => url.Contains("/cards/mh3/436") ? Card("mh3", "436", "Something Else") : null);
            var resolved = await catalogue.ResolveAsync("Unreadable Gibberish", "mh3", "436", "en", default);
            Check(resolved.Resolution == "assumed",
                "a name past saving falls back to the number, marked assumed");
        }

        Section("A set code that could not be read");

        {
            // Lightning Bolt is in dozens of sets. Searching for it without a set code used to
            // return every printing and then refuse to choose, dropping a card that was read well.
            // It still answers with one card — but as "unplaced", because nothing read off the
            // card said which of those dozens it is.
            var (catalogue, stub) = Build(url => url.Contains("/cards/named")
                ? Card("lea", "161", "Lightning Bolt")
                : url.Contains("/cards/search")
                    ? Searches(Card("lea", "161", "Lightning Bolt"), Card("m10", "146", "Lightning Bolt"))(url)
                    : null);
            var resolved = await catalogue.ResolveAsync("Lightning Bolt", "", null, null, default);
            Check(resolved.Resolution == "unplaced", "a set-less name with no number is unplaced, not fuzzy");
            Check(resolved.Printing?.Name == "Lightning Bolt", "and the card is still named");
            Check(!stub.Urls.Any(u => u.Contains("/cards/search")),
                "with no number to choose with, no Scryfall-wide search is sent");
        }

        {
            // The failure this section exists for. An alternate-art reprint: the set symbol is a
            // design the reader has never seen, but the number is printed plainly. Looking that
            // number up inside the set the name happened to land in finds the wrong card or none;
            // matching it against the name's own printings finds the card in the photograph.
            var (catalogue, _) = Build(url => url.Contains("/cards/named")
                ? Card("mh2", "238", "Sword of Hearth and Home")
                : url.Contains("/cards/search")
                    ? Searches(
                        Card("mh2", "238", "Sword of Hearth and Home"),
                        Card("tmc", "136", "Sword of Hearth and Home"))(url)
                    : null);
            var resolved = await catalogue.ResolveAsync("Sword of Hearth and Home", "", "136", null, default);
            Check(resolved.Printing?.Set == "tmc", "a number picks the printing out of the name's own printings");
            Check(resolved.Printing?.CollectorNumber == "136", "and it is that printing's number, not the default's");
            Check(resolved.Resolution == "confirmed", "a number that chose a printing is corroboration");
        }

        {
            // Recognising the card is not reading it. With no set code and no number, the answer
            // is the catalogue's default printing, and saying so is the whole point: this is the
            // alternate-art card that comes back as the original and looks perfectly right.
            var (catalogue, _) = Build(url => url.Contains("/cards/named")
                ? Card("2x2", "147", "Food Chain")
                : url.Contains("/cards/search")
                    ? Searches(Card("2x2", "147", "Food Chain"), Card("tmc", "133", "Food Chain"))(url)
                    : null);
            var resolved = await catalogue.ResolveAsync("Food Chain", "", null, null, default);
            Check(resolved.Resolution == "unplaced",
                "the catalogue's default printing is reported as a guess, not as a reading");
            Check(resolved.Printing?.Name == "Food Chain", "the name, which was read, is still certain");
        }

        {
            // A number that matches none of the name's printings has told us nothing, so the card
            // is unplaced rather than confirmed against a stranger.
            var (catalogue, _) = Build(url => url.Contains("/cards/named")
                ? Card("lea", "161", "Lightning Bolt")
                : url.Contains("/cards/search")
                    ? Searches(Card("lea", "161", "Lightning Bolt"))(url)
                    : url.Contains("/cards/lea/999") ? Card("lea", "999", "Black Lotus") : null);
            var resolved = await catalogue.ResolveAsync("Lightning Bolt", "", "999", null, default);
            Check(resolved.Printing?.Name == "Lightning Bolt", "the name is kept, the number discarded");
            Check(resolved.Resolution == "unplaced", "and nothing placed the printing");
            Check(resolved.ReadCollectorNumber == "999", "the number that was read survives for the log");
        }

        {
            // Fuzzy keeps its own meaning: the name itself only matched approximately. If unplaced
            // swallowed that case the log could no longer tell a misread name from an unread set.
            var (catalogue, _) = Build(url => url.Contains("/cards/named")
                ? Card("lea", "161", "Lightning Bolt")
                : null);
            var resolved = await catalogue.ResolveAsync("Lightnin Bolt", "", null, null, default);
            Check(resolved.Resolution == "fuzzy", "an approximate name is still fuzzy, not unplaced");
        }

        {
            var (catalogue, _) = Build(_ => null);
            var resolved = await catalogue.ResolveAsync(null, "", "438", null, default);
            Check(resolved.Resolution == "unresolved",
                "a number with neither a name nor a set is nothing at all");
        }

        Section("A set code that was read as the wrong set");

        {
            // The case this exists for: a name read perfectly and a set symbol read as the wrong
            // three letters. Every rung is confined to that set, so all of them miss, and the card
            // used to be dropped for the sake of the least legible thing on it.
            var (catalogue, stub) = Build(url =>
                url.Contains("set%3Admr") || url.Contains("set=dmr") ? null
                : url.Contains("/cards/named") ? Card("dmu", "28", "Historian's Boon")
                : null);
            var resolved = await catalogue.ResolveAsync("Historian's Boon", "dmr", null, null, default);
            Check(resolved.Resolution == "relocated",
                "a name found outside the set code that was read is relocated, not unresolved");
            Check(resolved.Printing?.Set == "dmu", "and the set in the answer is the catalogue's");
            Check(resolved.Printing?.Name == "Historian's Boon", "the name survives the wrong set code");
            Check(stub.Urls.Any(u => u.Contains("set%3Admr") || u.Contains("set=dmr")),
                "the set code that was read is still tried first");
        }

        {
            // The number is read from the same small print as the set code, so it must not be
            // believed over a name once that set has been shown not to hold the card.
            var (catalogue, _) = Build(url =>
                url.Contains("set%3Admr") || url.Contains("set=dmr") ? null
                : url.Contains("/cards/named") ? Card("dmu", "28", "Historian's Boon")
                : url.Contains("/cards/dmr/28") ? Card("dmr", "28", "Shivan Dragon")
                : null);
            var resolved = await catalogue.ResolveAsync("Historian's Boon", "dmr", "28", null, default);
            Check(resolved.Printing?.Name == "Historian's Boon",
                "a number inside the disproved set does not out-rank the name");
        }

        {
            // A number that agrees with the printing found elsewhere corroborates the move, but
            // the answer must still say the set was not the one read: 'confirmed' would invite a
            // reader to stop looking at the one field that is known to be wrong.
            var (catalogue, _) = Build(url =>
                url.Contains("set%3Admr") || url.Contains("set=dmr") ? null
                : url.Contains("/cards/named") ? Card("dmu", "28", "Historian's Boon")
                : url.Contains("/cards/dmu/28") ? Card("dmu", "28", "Historian's Boon")
                : null);
            var resolved = await catalogue.ResolveAsync("Historian's Boon", "dmr", "28", null, default);
            Check(resolved.Resolution == "relocated",
                "a corroborating number does not downgrade the warning about the set");
            Check(resolved.ReadCollectorNumber == "28", "and the number that was read is still carried");
        }

        {
            // Relocating must not rescue a name that is simply not a card.
            var (catalogue, _) = Build(_ => null);
            var resolved = await catalogue.ResolveAsync("Nonesuch Card", "dmr", null, null, default);
            Check(resolved.Resolution == "unresolved", "a name found nowhere at all is still unresolved");
        }

        Section("A spell printed inside the card's text box");

        {
            // The case from image 1: SOS 80 is one card, Emeritus of Woe, with a second spell
            // printed in its text box. The reader wrote both names joined with //, which matches
            // nothing whole — so without the face fallback a perfectly legible card falls through
            // the whole ladder on the strength of the thing it read best.
            var (catalogue, _) = Build(url =>
                !url.Contains("/cards/search") ? null
                : url.Contains("Emeritus%20of%20Woe%20%2F%2F") ? null
                : url.Contains("Emeritus") ? $$"""{"data":[{{Card("sos", "80", "Emeritus of Woe")}}]}"""
                : null);
            var resolved = await catalogue.ResolveAsync(
                "Emeritus of Woe // Demonic Tutor", "sos", "80", null, default);
            Check(resolved.Printing?.Name == "Emeritus of Woe",
                "a name assembled from the card and a spell in its text box still finds the card");
            Check(resolved.Resolution == "confirmed", "and the printing the number named is confirmed");
        }

        {
            // The title bar is above the text box, so a reader who appends writes the card's own
            // name first. Both parts here are real cards and the first one has to win.
            var (catalogue, stub) = Build(url =>
                !url.Contains("/cards/search") ? null
                : url.Contains("%2F%2F") ? null
                : url.Contains("Emeritus") ? $$"""{"data":[{{Card("sos", "80", "Emeritus of Woe")}}]}"""
                : url.Contains("Demonic") ? $$"""{"data":[{{Card("lea", "97", "Demonic Tutor")}}]}"""
                : null);
            var resolved = await catalogue.ResolveAsync(
                "Emeritus of Woe // Demonic Tutor", "", null, null, default);
            Check(resolved.Printing?.Name == "Emeritus of Woe",
                "the part written first wins, because the card's own name is the title bar");
            Check(resolved.Resolution == "unplaced",
                "and with no set and no number the printing is still only a guess");
            Check(!stub.Urls.Any(u => u.Contains("Demonic") && !u.Contains("Emeritus")),
                "and the second part is never asked for on its own");
        }

        {
            // A card that really is in two named parts is catalogued under the joined name, so it
            // matches whole and must never reach the fallback.
            var (catalogue, stub) = Build(Searches(Card("mh2", "26", "Fire // Ice")));
            var resolved = await catalogue.ResolveAsync("Fire // Ice", "mh2", null, null, default);
            Check(resolved.Printing?.Name == "Fire // Ice", "a real split card keeps its joined name");
            Check(!stub.Urls.Any(u => u.Contains("%22Fire%22") || u.Contains("%22Ice%22")),
                "and is never broken into its faces");
        }

        {
            // Nothing here may rescue two names that are both nonsense.
            var (catalogue, _) = Build(_ => null);
            var resolved = await catalogue.ResolveAsync("Nonesuch // Alsononesuch", "mh2", null, null, default);
            Check(resolved.Resolution == "unresolved", "two names that find nothing are still unresolved");
        }

        Section("A number that disagrees with the set it was read beside");

        {
            // The case from image 1: an alternate-art Sword of Hearth and Home, whose number was
            // read correctly and whose set was supplied from memory of the original printing. The
            // name and that set find exactly one card, its number is not the one on the photo, and
            // the ladder used to blame the number and hand back the wrong art under 'corrected'.
            var (catalogue, _) = Build(url =>
                !url.Contains("/cards/search") ? null
                : url.Contains("set%3Amh2") || url.Contains("set=mh2")
                    ? $$"""{"data":[{{Card("mh2", "238", "Sword of Hearth and Home")}}]}"""
                    : $$"""{"data":[{{Card("mh2", "238", "Sword of Hearth and Home")}},{{Card("tmc", "136", "Sword of Hearth and Home")}}]}""");
            var resolved = await catalogue.ResolveAsync("Sword of Hearth and Home", "mh2", "136", null, default);
            Check(resolved.Resolution == "relocated",
                "a number landing on another printing of the same name moves the card");
            Check(resolved.Printing?.Set == "tmc" && resolved.Printing?.CollectorNumber == "136",
                "and the printing returned is the one the number picked out");
        }

        {
            // The ordinary misreading, which must keep its ordinary answer. A wrong digit lands on
            // a different card, not on another printing of this one, so nothing corroborates it
            // and the set stands.
            var (catalogue, _) = Build(url =>
                !url.Contains("/cards/search") ? null
                : $$"""{"data":[{{Card("mh2", "238", "Sword of Hearth and Home")}}]}""");
            var resolved = await catalogue.ResolveAsync("Sword of Hearth and Home", "mh2", "236", null, default);
            Check(resolved.Resolution == "corrected",
                "a number matching no printing of the name is still a misread number");
            Check(resolved.Printing?.CollectorNumber == "238", "and the set's printing is still the answer");
        }

        {
            // Two printings of one name sharing a number give nothing to choose between, so the
            // move is not made: a coincidence that happens twice is no longer evidence.
            var (catalogue, _) = Build(url =>
                !url.Contains("/cards/search") ? null
                : url.Contains("set%3A2x2") || url.Contains("set=2x2")
                    ? $$"""{"data":[{{Card("2x2", "147", "Food Chain")}}]}"""
                    : $$"""{"data":[{{Card("2x2", "147", "Food Chain")}},{{Card("tmc", "133", "Food Chain")}},{{Card("plst", "133", "Food Chain")}}]}""");
            var resolved = await catalogue.ResolveAsync("Food Chain", "2x2", "133", null, default);
            Check(resolved.Resolution == "corrected",
                "a number matching two printings of the name moves nothing");
        }

        {
            // The same question arises when the read set holds several printings and the number
            // matches none of them, and it must be asked there too.
            var (catalogue, _) = Build(url =>
                !url.Contains("/cards/search") ? null
                : url.Contains("set%3Amh2") || url.Contains("set=mh2")
                    ? $$"""{"data":[{{Card("mh2", "238", "Sword of Hearth and Home")}},{{Card("mh2", "441", "Sword of Hearth and Home")}}]}"""
                    : $$"""{"data":[{{Card("mh2", "238", "Sword of Hearth and Home")}},{{Card("mh2", "441", "Sword of Hearth and Home")}},{{Card("tmc", "136", "Sword of Hearth and Home")}}]}""");
            var resolved = await catalogue.ResolveAsync("Sword of Hearth and Home", "mh2", "136", null, default);
            Check(resolved.Resolution == "relocated",
                "several printings in the read set do not stop the number moving the card");
            Check(resolved.Printing?.Set == "tmc", "and it still lands on the printing the number names");
        }

        Section("One name, several printings");

        {
            var (catalogue, _) = Build(Searches(
                Card("sld", "1500", "Lightning Bolt"), Card("sld", "1501", "Lightning Bolt")));
            var resolved = await catalogue.ResolveAsync("Lightning Bolt", "sld", "1501", "en", default);
            Check(resolved.Resolution == "confirmed" && resolved.Printing?.CollectorNumber == "1501",
                "the number is the tiebreak it exists to be");
        }

        {
            // Ambiguous must not throw the card away: every candidate carries the same name, so
            // the name — the part read most reliably — was never in doubt.
            var (catalogue, _) = Build(Searches(
                Card("sld", "1500", "Lightning Bolt"), Card("sld", "1501", "Lightning Bolt")));
            var resolved = await catalogue.ResolveAsync("Lightning Bolt", "sld", null, null, default);
            Check(resolved.Resolution == "ambiguous", "no tiebreak is reported as ambiguous");
            Check(resolved.Printing is not null, "but an answer is still given");
            Check(resolved.Printing?.Name == "Lightning Bolt", "carrying the name every candidate shares");
            Check(resolved.Printing?.CollectorNumber == "1500", "the lowest-numbered printing is the pick");
            Check(resolved.Candidates?.Count == 2, "and the others are listed, not hidden");
        }

        {
            var (catalogue, _) = Build(Searches(
                Card("sld", "1500", "Lightning Bolt"), Card("sld", "1501", "Lightning Bolt")));
            var resolved = await catalogue.ResolveAsync("Lightning Bolt", "sld", "9999", "en", default);
            Check(resolved.Resolution == "ambiguous" && resolved.Candidates?.Count == 2,
                "a number matching no candidate does not pretend to have chosen");
        }

        Section("Cards printed in another language");

        {
            var (catalogue, stub) = Build(Searches(
                Card("neo", "268", "Boseiju, Who Endures", "ja", "\u4e26\u6728")));
            var resolved = await catalogue.ResolveAsync("\u4e26\u6728", "neo", null, "ja", default);
            Check(stub.Urls.Any(u => u.Contains("include_multilingual=true")),
                "a non-English search asks for multilingual results");
            Check(stub.Urls.Any(u => u.Contains("lang%3Aja")), "the language reaches the query");
            Check(resolved.Printing?.Name == "Boseiju, Who Endures", "the English name is what comes back");
            Check(resolved.Printing?.PrintedName == "\u4e26\u6728", "the printed name rides beside it");
            Check(resolved.Printing?.Language == "ja", "the printing's own language is reported");
        }

        {
            var (catalogue, stub) = Build(Searches(Card("mh3", "438", "Flare of Denial")));
            await catalogue.ResolveAsync("Flare of Denial", "mh3", "438", "en", default);
            Check(!stub.Urls.Any(u => u.Contains("include_multilingual")),
                "an English search is left as the default");
            Check(!stub.Urls.Any(u => u.Contains("lang%3A")), "no lang: filter is sent for English");
            Check(stub.Urls.Distinct().Count() == stub.Urls.Count, "no query is sent twice");
        }

        Section("The search ladder itself");

        {
            var (catalogue, stub) = Build(url =>
                url.Contains("%21%22") ? Searches(Card("mh3", "438", "Flare of Denial"))(url) : null);
            var resolved = await catalogue.ResolveAsync("Flare of Denial", "mh3", "438", "en", default);
            Check(resolved.Printing is not null, "the exact-name operator is tried first");
            Check(stub.Urls.Count == 1, "and an exact hit asks nothing further");
        }

        {
            // The free-text rung forgives punctuation; it must not widen into a substring search.
            var (catalogue, _) = Build(url => url.Contains("%21%22") || url.Contains("/cards/named")
                ? null
                : url.Contains("/cards/search")
                    ? Searches(Card("mh3", "100", "Flare of Denial Deluxe Edition"))(url)
                    : null);
            var resolved = await catalogue.ResolveAsync("Flare", "mh3", null, null, default);
            Check(resolved.Resolution == "unresolved", "a loose text match is rejected by the name check");
        }
    }

    private static void PromptChecks()
    {
        Section("The per-run prompt");

        var mtg = FoundryCardIdentificationAgent.BuildPrompt(GameAgentProfile.Mtg);
        Check(mtg.Contains("resolve_printing"), "Magic is told which tool settles a printing");
        Check(mtg.Contains("\"resolution\""), "and to carry the resolution back");
        Check(mtg.Contains("confidence"), "and to answer with what it read rather than decline");

        // The trap this prompt exists to avoid: a second copy of the agent's standing instructions
        // here would be sent as the user message, which is what a model follows when the two
        // disagree — and it is the copy that cannot be changed without rebuilding the image.
        Check(!mtg.Contains("bottom-left corner"), "the card-reading instructions are not duplicated here");
        Check(!mtg.Contains("lookup first"), "the old number-first instruction is gone");

        var pokemon = FoundryCardIdentificationAgent.BuildPrompt(GameAgentProfile.Pokemon);
        Check(!pokemon.Contains("resolve_printing"), "a game without the tool is not told to call it");
        Check(pokemon.Contains("mcp-cardcatalog-pokemon"), "but is still pointed at its own catalogue");
    }

    /// <summary>
    /// A model deployment's tokens-per-minute quota is spent per card, so it runs out part-way
    /// through a page: the first crops identify and the rest are refused. Retrying is only right
    /// for that one failure — retrying a card the model genuinely could not read would spend a
    /// minute to fail it again — so which failures count as throttling is worth pinning down.
    /// </summary>
    private static void ThrottleChecks()
    {
        Section("A model deployment that has run out of tokens");

        Check(FoundryAgentClient.IsThrottled("rate_limit_exceeded", "exceeded token rate limit"),
            "the service's own code is enough to retry on");
        Check(FoundryAgentClient.IsThrottled("RATE_LIMIT_EXCEEDED", "whatever"),
            "and is matched whatever its case");
        Check(FoundryAgentClient.IsThrottled(null, "Requests to gpt-4o have exceeded token rate limit."),
            "a run that gave no code is read from its message instead");

        Check(!FoundryAgentClient.IsThrottled("server_error", "something went wrong"),
            "a server error is not retried as throttling");
        Check(!FoundryAgentClient.IsThrottled("invalid_request", "rate limit"),
            "and a message mentioning rate limits cannot override an explicit other code");
        Check(!FoundryAgentClient.IsThrottled(null, null), "a failure with nothing in it is not retried");

        Check(FoundryAgentClient.RetryAfter("Please retry after 37 seconds.") == TimeSpan.FromSeconds(37),
            "the wait the service asked for is preferred to a guess");
        Check(FoundryAgentClient.RetryAfter("retry after 500 ms") == TimeSpan.FromMilliseconds(500),
            "milliseconds are read as milliseconds, not as half an hour");
        Check(FoundryAgentClient.RetryAfter("exceeded token rate limit.") is null,
            "a message with no figure in it falls back to the backoff");
        // One card must not be able to stall the whole page behind it.
        Check(FoundryAgentClient.RetryAfter("Please retry after 3600 seconds") == TimeSpan.FromMinutes(2),
            "an implausible wait is capped");
    }

    /// <summary>
    /// What counts as identified enough to export. This is the last gate a card passes through and
    /// the quietest place to lose one: a row that fails here is not an error anywhere, it simply
    /// never appears, taking the name with it.
    /// </summary>
    private static void ExportGateChecks()
    {
        Section("What is worth exporting");

        static IdentifiedCard Card(string? set, string? number, string? name) =>
            new() { Set = set, CollectorNumber = number, Name = name };

        Check(Card("dmu", "28", "Historian's Boon").IsIdentified, "a whole reading is identified");
        Check(Card("dmu", "28", "Historian's Boon").HasPrinting, "and its printing is pinned down");

        // The case that prompted this: a legible name, a legible set, an unreadable number.
        Check(Card("dmr", null, "Historian's Boon").IsIdentified,
            "a name and a set are enough without the number");
        Check(!Card("dmr", null, "Historian's Boon").HasPrinting,
            "but the missing number is still visible to the caller");

        Check(!Card("dmr", "28", null).IsIdentified, "a set and a number without a name are not a card");
        Check(!Card(null, "28", "Historian's Boon").IsIdentified, "nor is a name with nowhere to put it");
        Check(!Card("   ", null, "  ").IsIdentified, "and whitespace is not a reading");

        // The export gate does not change for an unplaced card — the row is still worth writing —
        // but the caller has to be able to find it, because the CSV itself shows nothing.
        static IdentifiedCard Settled(string resolution) =>
            new() { Set = "2x2", CollectorNumber = "147", Name = "Food Chain", Resolution = resolution };

        Check(Settled("unplaced").IsIdentified, "an unplaced card is still exported");
        Check(!Settled("unplaced").PrintingWasPlaced, "but its printing is marked as unplaced");
        Check(Settled("UNPLACED").PrintingWasPlaced is false, "whatever case the agent echoed it in");
        Check(Settled("confirmed").PrintingWasPlaced, "a confirmed printing was placed");
        Check(Settled("relocated").PrintingWasPlaced,
            "and so was a relocated one: the name found it, the set was merely corrected");
    }

    private static (ScryfallCatalogue Catalogue, StubScryfall Stub) Build(Func<string, string?> reply)
    {
        var stub = new StubScryfall(reply);
        return (new ScryfallCatalogue(new HttpClient(stub)), stub);
    }


    /// <summary>
    /// The address of each printing's picture, which is the only field the catalogue carries that
    /// a reader cannot check against the card — and so the only one that can contradict a reading
    /// whose every other field agrees with itself.
    /// </summary>
    private static async Task ArtImageUrlChecks()
    {
        Section("Carrying each printing's picture");

        {
            var printing = ScryfallCatalogue.Parse(System.Text.Json.JsonDocument.Parse(
                """
                {"set":"mh3","collector_number":"438","name":"Flare of Denial","lang":"en",
                 "image_uris":{"small":"https://cards.scryfall.io/small/a.jpg",
                               "normal":"https://cards.scryfall.io/normal/a.jpg"}}
                """).RootElement);

            Check(printing?.ImageUrl == "https://cards.scryfall.io/normal/a.jpg",
                "a card's picture is taken at normal size, not small");
        }

        {
            // A double-faced card carries no image_uris of its own: each face has its own set.
            var printing = ScryfallCatalogue.Parse(System.Text.Json.JsonDocument.Parse(
                """
                {"set":"mid","collector_number":"49","name":"Delver // Insectile","lang":"en",
                 "card_faces":[{"image_uris":{"normal":"https://cards.scryfall.io/normal/front.jpg"}},
                               {"image_uris":{"normal":"https://cards.scryfall.io/normal/back.jpg"}}]}
                """).RootElement);

            Check(printing?.ImageUrl == "https://cards.scryfall.io/normal/front.jpg",
                "a double-faced card is compared by its front, which is the side a binder shows");
        }

        {
            var printing = ScryfallCatalogue.Parse(System.Text.Json.JsonDocument.Parse(
                """{"set":"lea","collector_number":"1","name":"Animate Wall","lang":"en"}""").RootElement);

            Check(printing is not null && printing.ImageUrl.Length == 0,
                "a printing with no picture resolves anyway, with an empty address");
        }

        {
            // Every printing of the name, so an alternate-art reprint can be caught: the resolved
            // printing alone would only ever confirm itself.
            var (catalogue, _) = Build(Searches(
                CardWithArt("mh2", "238", "Ragavan", "https://cards.scryfall.io/normal/a.jpg"),
                CardWithArt("sld", "1289", "Ragavan", "https://cards.scryfall.io/normal/b.jpg")));

            var all = await catalogue.SearchPrintingsAsync("Ragavan", null, "en", default);
            Check(all.Count == 2 && all.All(p => p.ImageUrl.Length > 0),
                "every printing of a name carries its own picture");
        }
    }

    /// <summary>
    /// The hash, checked on pictures built here rather than on cards. Card art cannot be committed
    /// to a public repository, and these properties — that the hash follows structure and ignores
    /// exposure — are exactly what makes it survive the trip from a catalogue scan to a photograph
    /// through a sleeve, so they can be checked without any card at all.
    /// </summary>
    private static void ArtHashChecks()
    {
        Section("A perceptual hash of a card face");

        using var original = Gradient(240, 336, seed: 7);
        using var brighter = Gradient(240, 336, seed: 7, brightness: 25);
        using var smaller = Gradient(120, 168, seed: 7);
        using var different = Gradient(240, 336, seed: 19);

        var hash = CardArtHash.Compute(original);

        Check(CardArtHash.Distance(hash, hash) == 0, "a picture is identical to itself");

        Check(CardArtHash.NormalisedDistance(hash, CardArtHash.Compute(brighter)) < 0.02,
            "a picture photographed under brighter light is still the same picture");

        Check(CardArtHash.NormalisedDistance(hash, CardArtHash.Compute(smaller)) < 0.12,
            "and so is one at half the resolution, which is the difference between a scan and a crop");

        Check(CardArtHash.NormalisedDistance(hash, CardArtHash.Compute(different)) > 0.25,
            "a different picture is far away, which is what makes closeness mean anything");

        Check(CardArtHash.BitCount == CardArtHash.Size * CardArtHash.Size,
            "the hash is one bit per cell of the grid");
    }

    /// <summary>
    /// What the comparison is allowed to conclude. The case that matters most is the one where it
    /// must say nothing: most reprints share an illustration, and there the closest candidate is
    /// closest by noise.
    /// </summary>
    private static void ArtAdjudicationChecks()
    {
        Section("Deciding which printing a photograph shows");

        {
            var verdict = ArtAdjudicator.Adjudicate(("mh2", "238"), []);
            Check(verdict.Verdict == ArtVerdict.NotChecked,
                "with no pictures to compare, nothing is concluded");
        }

        {
            // The shared-art case: four printings of one illustration, separated only by noise.
            var verdict = ArtAdjudicator.Adjudicate(("mh2", "238"), [
                new ArtCandidate("mh2", "238", 0.17),
                new ArtCandidate("sld", "1289", 0.18),
                new ArtCandidate("mul", "40", 0.18),
                new ArtCandidate("plst", "MH2-238", 0.19)]);

            Check(verdict.Verdict == ArtVerdict.Inconclusive,
                "printings sharing one illustration cannot be told apart, and the art declines to try");
        }

        {
            var verdict = ArtAdjudicator.Adjudicate(("mh2", "238"), [
                new ArtCandidate("mh2", "238", 0.08),
                new ArtCandidate("sld", "1289", 0.34)]);

            Check(verdict.Verdict == ArtVerdict.Agrees && verdict.Best!.Set == "mh2",
                "a printing whose art plainly matches is confirmed");
        }

        {
            // The failure this exists for: every field self-consistent, wrong printing.
            var verdict = ArtAdjudicator.Adjudicate(("mh2", "238"), [
                new ArtCandidate("mh2", "238", 0.33),
                new ArtCandidate("sld", "1289", 0.07)]);

            Check(verdict.Verdict == ArtVerdict.Moved && verdict.Best!.Set == "sld",
                "an alternate-art printing that matches instead takes the card");
        }

        {
            var verdict = ArtAdjudicator.Adjudicate(("mh2", "238"), [
                new ArtCandidate("mh2", "238", 0.44),
                new ArtCandidate("sld", "1289", 0.47)]);

            Check(verdict.Verdict == ArtVerdict.Inconclusive,
                "a field where nothing matches says the card was misnamed, not which printing it is");
        }

        {
            var verdict = ArtAdjudicator.Adjudicate(("lea", "1"), [new ArtCandidate("lea", "1", 0.11)]);
            Check(verdict.Verdict == ArtVerdict.Agrees,
                "a card printed only once has nothing to be confused with");
        }

        {
            var verdict = ArtAdjudicator.Adjudicate(("mh2", "238"), [
                new ArtCandidate("mh2", "238", 0.30),
                new ArtCandidate("sld", "1289", 0.05)]);

            Check(verdict.Separation > 0 && verdict.Ranked[0].Set == "sld",
                "the margin over the runner-up is reported, because that is the whole claim");
        }
    }

    /// <summary>
    /// Which addresses the orchestrator will fetch, and how the list reaches it. The URLs arrive
    /// by way of a language model repeating a tool's output, so they are caller-supplied input to
    /// an outbound request and the host check is the whole of the protection around it.
    /// </summary>
    private static void ArtFetchChecks()
    {
        Section("Fetching a catalogue picture safely");

        Check(CardArtVerifier.IsFetchable("https://cards.scryfall.io/normal/a.jpg"),
            "the catalogue's own image host is fetched");
        Check(!CardArtVerifier.IsFetchable("http://cards.scryfall.io/normal/a.jpg"),
            "plain http is not, whatever the host");
        Check(!CardArtVerifier.IsFetchable("https://cards.scryfall.io.evil.test/normal/a.jpg"),
            "a host that merely contains the allowed name is refused");
        Check(!CardArtVerifier.IsFetchable("https://169.254.169.254/metadata"),
            "and so is the instance metadata address, which is what an invented URL would reach for");
        Check(!CardArtVerifier.IsFetchable("not a url"), "so is anything that will not parse");
        Check(!CardArtVerifier.IsFetchable(""), "and nothing at all");

        {
            var card = FoundryCardIdentificationAgent.ParseIdentification(
                """
                {"set":"mh2","collectorNumber":"238","name":"Ragavan","resolution":"confirmed",
                 "alternates":[{"set":"mh2","collectorNumber":"238","imageUrl":"https://cards.scryfall.io/normal/a.jpg"},
                               {"set":"sld","collectorNumber":"1289","imageUrl":"https://cards.scryfall.io/normal/b.jpg"}]}
                """,
                StubCrop(), CardGames.Magic, "cardid/MtgCardIdAgent");

            Check(card.ArtReferences.Count == 2 && card.ArtReferences[1].Set == "sld",
                "the printings the agent copied through reach the orchestrator");
        }

        {
            // An agent that drops the list is not an agent that read the card wrongly.
            var card = FoundryCardIdentificationAgent.ParseIdentification(
                """{"set":"mh2","collectorNumber":"238","name":"Ragavan","resolution":"confirmed"}""",
                StubCrop(), CardGames.Magic, "cardid/MtgCardIdAgent");

            Check(card.IsIdentified && card.ArtReferences.Count == 0,
                "a reply with no pictures in it is still a good identification");
        }

        {
            var card = FoundryCardIdentificationAgent.ParseIdentification(
                """
                {"set":"mh2","collectorNumber":"238","name":"Ragavan","alternates":[{"set":"mh2"},"rubbish",
                 {"set":"sld","collectorNumber":"1289","imageUrl":"https://cards.scryfall.io/normal/b.jpg"}]}
                """,
                StubCrop(), CardGames.Magic, "cardid/MtgCardIdAgent");

            Check(card.ArtReferences.Count == 1 && card.ArtReferences[0].Set == "sld",
                "entries the model mangled are dropped one by one, not all at once");
        }

        Check(FoundryCardIdentificationAgent.BuildPrompt(GameAgentProfile.Mtg).Contains("alternates"),
            "and the agent is asked for them in the first place");
    }

    /// <summary>A card crop with no image in it, for checks that only parse a reply.</summary>
    private static CardCrop StubCrop() =>
        new(0,
            new AgentImage(ReadOnlyMemory<byte>.Empty, "card-000.png", "image/png"),
            new CardQuad([new ImagePoint(0, 0), new ImagePoint(1, 0), new ImagePoint(1, 1), new ImagePoint(0, 1)]),
            null);

    /// <summary>A Scryfall card object that also carries a picture.</summary>
    private static string CardWithArt(string set, string number, string name, string imageUrl) =>
        $$$"""
           {"set":"{{{set}}}","collector_number":"{{{number}}}","name":"{{{name}}}","lang":"en",
            "printed_name":"","image_uris":{"normal":"{{{imageUrl}}}"}}
           """;

    /// <summary>
    /// A synthetic picture with structure in it, as a PNG stream. <paramref name="seed"/> chooses
    /// the picture; <paramref name="brightness"/> shifts every pixel without changing which parts
    /// are lighter than their neighbours, which is what a different exposure does to a photograph.
    /// </summary>
    private static MemoryStream Gradient(int width, int height, int seed, int brightness = 0)
    {
        using var image = new Image<Rgba32>(width, height);
        var random = new Random(seed);
        var blobs = Enumerable.Range(0, 12)
            .Select(_ => (X: random.NextDouble(), Y: random.NextDouble(), Weight: random.NextDouble() * 2 - 1))
            .ToArray();

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var fx = (double)x / width;
                var fy = (double)y / height;
                var value = blobs.Sum(b => b.Weight / (0.02 + ((fx - b.X) * (fx - b.X)) + ((fy - b.Y) * (fy - b.Y))));
                // Squashed rather than clipped: a clipped picture loses its structure in the
                // bright and dark corners, and shifting the exposure would then genuinely change
                // it, which is the opposite of what this picture is for.
                var level = (byte)Math.Clamp((Math.Tanh(value / 12) * 90) + 128 + brightness, 0, 255);
                image[x, y] = new Rgba32(level, level, level);
            }
        }

        var stream = new MemoryStream();
        image.SaveAsPng(stream);
        stream.Position = 0;
        return stream;
    }


    /// <summary>
    /// Photo 1, cards 0,0 and 0,1: two TMC cards that kept exporting as <c>mh2 238</c> and
    /// <c>2x2 147</c> — the original printing of each, which is what recall returns.
    /// <para>
    /// Both are the most reprinted cards in that photograph, and that is not a coincidence. A set
    /// code is supplied from memory when the card is recognised, and the recognisable cards are
    /// the ones with a dozen printings, so the chance of the remembered one being the one in the
    /// binder is worst exactly where the memory is most confident.
    /// </para>
    /// <para>
    /// A recalled set code does not fail. It is a real set that really does print that card, so
    /// every rung of the ladder filters on it happily and hands back a real printing of the right
    /// card from the wrong set. These checks pin down which readings survive that and which do
    /// not, because the difference is invisible in the answer.
    /// </para>
    /// </summary>
    private static async Task RecalledSetChecks()
    {
        Section("A set code that was remembered rather than read");

        // Sword of Hearth and Home: the photographed card is TMC 136; mh2 238 is the printing a
        // model remembers it by.
        const string Sword = "Sword of Hearth and Home";
        var printings = Searches(
            CardWithArt("mh2", "238", Sword, "https://cards.scryfall.io/normal/mh2.jpg"),
            CardWithArt("tmc", "136", Sword, "https://cards.scryfall.io/normal/tmc.jpg"));

        // The set is filtered server-side, so the stub has to do it too or every query looks
        // like a search of everything and the set code appears never to narrow anything.
        Func<string, string?> catalogue = url =>
        {
            if (!url.Contains("/cards/search")) return null;
            var asked = Uri.UnescapeDataString(url);
            var set = asked.Contains("set:mh2") ? "mh2" : asked.Contains("set:tmc") ? "tmc" : null;
            var body = printings(url);
            if (set is null || body is null) return body;
            return body.Contains($"\"{set}\"")
                ? $$"""{"data":[{{(set == "mh2"
                        ? CardWithArt("mh2", "238", Sword, "https://cards.scryfall.io/normal/mh2.jpg")
                        : CardWithArt("tmc", "136", Sword, "https://cards.scryfall.io/normal/tmc.jpg"))}}]}"""
                : null;
        };

        async Task<ResolvedPrinting> Resolve(string set, string? number)
        {
            var (cat, _) = Build(catalogue);
            return await cat.ResolveAsync(Sword, set, number, "en", default);
        }

        {
            // The number was read and disagrees with the remembered set. It lands on another
            // printing of the same name, which is remote enough to be evidence, so the card moves.
            var r = await Resolve("mh2", "136");
            Check(r.Printing?.Set == "tmc" && r.Resolution == Resolutions.Relocated,
                "a read number overrules the set code it was read beside");
        }

        {
            // Nothing was read that could disagree. The printing cannot be improved on here — but
            // it must not be reported as settled, because the set code chose it unaided.
            var r = await Resolve("mh2", null);
            Check(r.Printing?.Set == "mh2" && r.Resolution == Resolutions.Unverified,
                "with no number, a set code that chose between printings is not treated as proof");
        }

        {
            var r = await Resolve("mh2", null);
            var card = new IdentifiedCard { Set = r.Printing!.Set, Name = Sword, Resolution = r.Resolution };
            Check(card.IsIdentified && !card.PrintingWasPlaced,
                "so the row is still exported, and still sent to a human to check against the art");
        }

        {
            // The one reading text cannot save: set and number both remembered, agreeing with each
            // other and with a real printing. Only the picture is left to object.
            var r = await Resolve("mh2", "238");
            Check(r.Printing?.Set == "mh2" && r.Resolution == Resolutions.Confirmed,
                "a recalled set and a recalled number corroborate each other, and only the art can object");
        }

        {
            // Which is why leaving the set code out is better than filling it in from memory.
            var r = await Resolve("", "136");
            Check(r.Printing?.Set == "tmc",
                "no set code plus a read number finds the card outright");
        }

        {
            var r = await Resolve("tmc", "136");
            Check(r.Printing?.Set == "tmc" && r.Resolution == Resolutions.Confirmed,
                "and a set code actually read off the card needs none of this");
        }

        {
            // A card printed in one set only: the set code had nothing to choose, so a missing
            // number costs nothing and this must stay `named` rather than being swept up as doubt.
            var (cat, _) = Build(Searches(Card("blb", "61", "Mockingbird")));
            var r = await cat.ResolveAsync("Mockingbird", "blb", null, "en", default);
            Check(r.Resolution == Resolutions.Named,
                "a card printed only once is still settled by its name alone");
        }
    }

    /// <summary>A Scryfall card object, as much of one as the catalogue reads.</summary>
    private static string Card(string set, string number, string name, string language = "en", string printed = "") =>
        $$"""
          {"set":"{{set}}","collector_number":"{{number}}","name":"{{name}}",
           "lang":"{{language}}","printed_name":"{{printed}}"}
          """;

    /// <summary>Answers any <c>/cards/search</c> with these cards, and everything else with a 404.</summary>
    private static Func<string, string?> Searches(params string[] cards) =>
        url => url.Contains("/cards/search") ? $$"""{"data":[{{string.Join(",", cards)}}]}""" : null;

    /// <summary>
    /// Stands in for Scryfall, answering by URL and recording what was asked. A null reply is a
    /// 404, which is what Scryfall returns for a search that matches nothing.
    /// </summary>
    private sealed class StubScryfall : HttpMessageHandler
    {
        private readonly Func<string, string?> _reply;

        public StubScryfall(Func<string, string?> reply) => _reply = reply;

        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Urls.Add(url);

            var body = _reply(url);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                });
        }
    }
}
