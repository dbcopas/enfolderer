using System.Net;
using System.Text;
using Enfolderer.Ai.Mcp.CardCatalog.Mtg;
using Enfolderer.Ai.Worker.Agents;

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

    private static int Main()
    {
        NumberReadingChecks();
        ResolutionChecks().GetAwaiter().GetResult();
        PromptChecks();
        ThrottleChecks();

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
            var (catalogue, stub) = Build(url => url.Contains("/cards/named")
                ? Card("lea", "161", "Lightning Bolt")
                : url.Contains("/cards/search")
                    ? Searches(Card("lea", "161", "Lightning Bolt"), Card("m10", "146", "Lightning Bolt"))(url)
                    : null);
            var resolved = await catalogue.ResolveAsync("Lightning Bolt", "", null, null, default);
            Check(resolved.Resolution == "fuzzy", "a set-less name is one card, not a shortlist");
            Check(resolved.Printing?.Name == "Lightning Bolt", "and the card is still named");
            Check(!stub.Urls.Any(u => u.Contains("/cards/search")),
                "no Scryfall-wide multi-candidate search is sent");
        }

        {
            // With no set code, the number is only worth something inside the set the name found.
            var (catalogue, _) = Build(url => url.Contains("/cards/named")
                ? Card("lea", "161", "Lightning Bolt")
                : url.Contains("/cards/lea/161") ? Card("lea", "161", "Lightning Bolt") : null);
            var resolved = await catalogue.ResolveAsync("Lightning Bolt", "", "161", null, default);
            Check(resolved.Resolution == "confirmed",
                "a number corroborating the set the name found is confirmed");
        }

        {
            // A number that lands on a different card says the set was wrong, not the name.
            var (catalogue, _) = Build(url => url.Contains("/cards/named")
                ? Card("lea", "161", "Lightning Bolt")
                : url.Contains("/cards/lea/999") ? Card("lea", "999", "Black Lotus") : null);
            var resolved = await catalogue.ResolveAsync("Lightning Bolt", "", "999", null, default);
            Check(resolved.Printing?.Name == "Lightning Bolt", "the name is kept, the number discarded");
            Check(resolved.Resolution == "fuzzy", "and the printing is flagged as the uncertain part");
        }

        {
            var (catalogue, _) = Build(_ => null);
            var resolved = await catalogue.ResolveAsync(null, "", "438", null, default);
            Check(resolved.Resolution == "unresolved",
                "a number with neither a name nor a set is nothing at all");
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

    private static (ScryfallCatalogue Catalogue, StubScryfall Stub) Build(Func<string, string?> reply)
    {
        var stub = new StubScryfall(reply);
        return (new ScryfallCatalogue(new HttpClient(stub)), stub);
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
