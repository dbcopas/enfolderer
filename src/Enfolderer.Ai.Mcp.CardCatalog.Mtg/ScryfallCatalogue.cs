using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Enfolderer.Ai.Mcp.CardCatalog.Mtg;

/// <summary>A printing as returned by the catalogue.</summary>
/// <param name="PrintedName">
/// The name in the language the card was printed in, where that differs from the English name. A
/// Japanese card carries no English text at all, so this is the only name a reader can check.
/// </param>
public sealed record CataloguePrinting(
    string Set,
    string CollectorNumber,
    string Name,
    string Language,
    string PrintedName = "");

/// <summary>
/// How a reading was settled. Every answer carries one, so a scan can be diagnosed from the log
/// rather than by eye: these say which evidence was believed, and therefore what to doubt.
/// </summary>
public static class Resolutions
{
    /// <summary>The name and the number agreed. Both were read correctly.</summary>
    public const string Confirmed = "confirmed";

    /// <summary>
    /// They disagreed, and the name won — so the number in the answer is the catalogue's, not the
    /// one that was read. This is the system working, not failing.
    /// </summary>
    public const string Corrected = "corrected";

    /// <summary>
    /// Only the name was read; the number comes from the catalogue and nothing corroborated it.
    /// Safe when the set holds the name once, which is why ambiguity is a separate answer.
    /// </summary>
    public const string Named = "named";

    /// <summary>
    /// Only the number was read, so there was no name to check it against. The weakest answer the
    /// catalogue gives: a misread digit is indistinguishable from a correct one here.
    /// </summary>
    public const string Assumed = "assumed";

    /// <summary>
    /// The name matched only approximately — a letter slipped, an accent was dropped, a comma
    /// landed somewhere else — or it matched exactly but no set code was read to place it in.
    /// The name is right; which printing it is deserves less trust than the rest.
    /// </summary>
    public const string Fuzzy = "fuzzy";

    /// <summary>
    /// The set holds this name more than once and no number separated them. The printing returned
    /// is the lowest-numbered of them, which is the ordinary printing rather than a variant far
    /// more often than not — but <c>Candidates</c> lists the others, and the caller is the only
    /// one that can see the card.
    /// <para>
    /// This is not a failure. Every candidate carries the same name — that is what made them
    /// candidates — so the name, which is the most legible thing on a card, is certain here even
    /// though the printing is not. Discarding the whole answer throws away the one field that was
    /// never in doubt.
    /// </para>
    /// </summary>
    public const string Ambiguous = "ambiguous";

    /// <summary>
    /// The card was found somewhere other than the set code that was read, so whatever else is
    /// right, the set in the reply is not the set that was read off the card.
    /// <para>
    /// It is reached two ways. Either the name was not in the read set at all and was found
    /// elsewhere; or the name was in the read set, but the number read off the card belonged to a
    /// different printing of that same name. The second is the one that catches a recognised card
    /// — see <c>RelocateByNumberAsync</c> for why a number landing on another printing of the same
    /// name is believed over the set code.
    /// </para>
    /// <para>
    /// A set code is a symbol, not text: an expansion symbol is a few dozen pixels of engraved
    /// glyph, and the three letters beside it are the smallest print on the card after the
    /// copyright line. The name is the largest. So when the two disagree it is nearly always the
    /// set that was misread, and a search confined to it finds nothing however well the name was
    /// read. This is the answer that stops one unreadable symbol discarding a legible card.
    /// </para>
    /// <para>
    /// It stays the answer even when the number that was read happens to match the printing found
    /// elsewhere, which is real corroboration and is visible in <c>ReadCollectorNumber</c>. The
    /// headline has to remain that the set in the reply is not the set on the card: a reader who
    /// is told <c>confirmed</c> has no reason to look at the set again, and that is the one field
    /// here that was definitely misread.
    /// </para>
    /// </summary>
    public const string Relocated = "relocated";

    /// <summary>
    /// The name was read and is certain, but nothing read off the card said which printing it is:
    /// no set code, and no collector number that matched one. The set and number in the answer are
    /// the catalogue's default printing of that name, and they are a guess.
    /// <para>
    /// This is the answer for the card the model recognised rather than read. A famous card has
    /// the most printings and the most alternate art, so recognition is at its most confident on
    /// exactly the cards where it settles the least: the art of a borderless Commander-deck
    /// printing shares nothing with the original but the name. Reporting <c>named</c> here would
    /// say the set was read and happened to hold the card, which is the opposite of what happened.
    /// </para>
    /// <para>
    /// A printing is still returned, because the name is certain and a named card can be checked
    /// and corrected while a dropped one cannot. The resolution is what stops it being believed.
    /// </para>
    /// </summary>
    public const string Unplaced = "unplaced";

    /// <summary>Neither the name nor the number found a printing.</summary>
    public const string Unresolved = "unresolved";
}

/// <summary>What the catalogue made of a card someone read off a photograph.</summary>
/// <param name="Printing">The printing the catalogue settled on, or null when it settled on none.</param>
/// <param name="Resolution">How it was settled; see <see cref="Resolutions"/>.</param>
/// <param name="ReadCollectorNumber">The number as it was read, kept so a correction is visible.</param>
/// <param name="Candidates">
/// The printings that matched when the answer is <see cref="Resolutions.Ambiguous"/>, so the caller
/// can say what it could not choose between. <c>Printing</c> is still populated with the most
/// likely of them: they all share the name, so the name is certain either way.
/// </param>
public sealed record ResolvedPrinting(
    CataloguePrinting? Printing,
    string Resolution,
    string? ReadCollectorNumber,
    IReadOnlyList<CataloguePrinting>? Candidates = null);

/// <summary>
/// Scryfall-backed catalogue lookups, ported from the desktop app's <c>BinderScanService</c> so the
/// identification agent — not the client — owns card resolution.
/// </summary>
public sealed class ScryfallCatalogue
{
    public const string ApiRoot = "https://api.scryfall.com";

    private readonly HttpClient _http;

    public ScryfallCatalogue(HttpClient http) => _http = http;

    /// <summary>
    /// Builds the exact-printing URL. Collector numbers are normalized by stripping internal
    /// whitespace per segment, so "5 J-b" becomes "5J-b" — behaviour carried over from the desktop
    /// app's <c>ScryfallUrlHelper</c>.
    /// </summary>
    public static string BuildCardApiUrl(string setCode, string number, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(setCode)) return string.Empty;
        setCode = setCode.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(number)) return $"{ApiRoot}/cards/{Uri.EscapeDataString(setCode)}";

        static string NormalizeSegment(string s) =>
            string.IsNullOrEmpty(s) ? s : new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

        var segments = number
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSegment)
            .Select(Uri.EscapeDataString);

        var url = $"{ApiRoot}/cards/{Uri.EscapeDataString(setCode)}/" + string.Join('/', segments);

        // Scryfall takes the language as a further path segment. English is the default and asking
        // for it explicitly is a 404 on sets that were never printed in another language, so it is
        // only appended when the card is in one.
        language = language?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(language) || language == "en"
            ? url
            : $"{url}/{Uri.EscapeDataString(language)}";
    }

    public async Task<CataloguePrinting?> LookupBySetAndNumberAsync(
        string setCode, string collectorNumber, string? language, CancellationToken ct)
    {
        var localised = await GetPrintingAsync(BuildCardApiUrl(setCode, collectorNumber, language), ct);
        if (localised is not null) return localised;

        // A card photographed in Japanese may still be catalogued only in English — promos and
        // Secret Lairs especially — so the number is worth asking about without the language.
        return string.IsNullOrWhiteSpace(language) || language.Trim().ToLowerInvariant() == "en"
            ? null
            : await GetPrintingAsync(BuildCardApiUrl(setCode, collectorNumber), ct);
    }

    public Task<CataloguePrinting?> LookupBySetAndNumberAsync(string setCode, string collectorNumber, CancellationToken ct) =>
        LookupBySetAndNumberAsync(setCode, collectorNumber, null, ct);

    public Task<CataloguePrinting?> LookupByNameAndSetAsync(string name, string setCode, CancellationToken ct) =>
        GetPrintingAsync(
            $"{ApiRoot}/cards/named?fuzzy={Uri.EscapeDataString(name)}&set={Uri.EscapeDataString(setCode)}",
            ct);

    public Task<CataloguePrinting?> LookupByNameAsync(string name, CancellationToken ct) =>
        GetPrintingAsync($"{ApiRoot}/cards/named?fuzzy={Uri.EscapeDataString(name)}", ct);

    /// <summary>
    /// One printing from a name that may not have been transcribed perfectly.
    /// <para>
    /// This is the rung that absorbs a reader's slips, and the reason it exists is that everything
    /// above it demands the name was read character for character. Scryfall's <c>named</c>
    /// endpoint forgives a wrong letter, a dropped accent, a comma in the wrong place or a
    /// half-read second word — which is exactly what a name transcribed from a photograph by a
    /// vision model looks like. Removing it is what turned "the numbers are sometimes wrong" into
    /// "nothing is identified": one slipped character stopped matching anything at all.
    /// </para>
    /// <para>
    /// It answers with a single card however many printings share the name, so it can never
    /// produce an ambiguous result — and it knows English names only, so a correctly read Japanese
    /// name will not find itself here. Both are why it is the last rung and not the first.
    /// </para>
    /// </summary>
    private Task<CataloguePrinting?> FuzzyAsync(string name, string? setCode, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(setCode)
            ? LookupByNameAsync(name, ct)
            : LookupByNameAndSetAsync(name, setCode.Trim(), ct);

    /// <summary>
    /// Every printing in <paramref name="setCode"/> whose name is <paramref name="name"/>, as the
    /// name appears on the card and in whatever language that is.
    /// <para>
    /// The <c>named</c> endpoint cannot be used for this. It answers with one card however many
    /// printings share the name, and it only knows English names — so a Japanese card read
    /// correctly finds nothing there, and a set that prints a name twice silently loses one of
    /// them. Search returns all of them, and knows printed names given a language to look in.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<CataloguePrinting>> SearchPrintingsAsync(
        string name, string? setCode, string? language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return [];

        // English is what search looks in by default, so asking for it spells the same query as
        // asking for nothing. Collapsing it here is what keeps the ladder below from sending each
        // query twice and waiting for both answers.
        if (string.Equals(language?.Trim(), "en", StringComparison.OrdinalIgnoreCase)) language = null;

        // Exact first, then as free text. `!"..."` is Scryfall's exact-name operator and is what
        // keeps "Bolt" from matching "Lightning Bolt"; the text form is the fallback for a name
        // transcribed with a stray comma or a missing accent, which the exact form rejects outright.
        foreach (var exact in new[] { true, false })
        {
            // Both with the language and without it. A card photographed in Japanese may be
            // catalogued only in English — promos and Secret Lairs especially — and dropping the
            // language is what finds it; keeping the language first is what makes the Japanese
            // printing, with its own printed name, the one that is returned when there is one.
            foreach (var lang in Distinct(language, null))
            {
                var found = await SearchOnceAsync(name, setCode, lang, exact, ct);
                if (found.Count > 0) return found;
            }
        }

        return [];
    }

    private static IEnumerable<string?> Distinct(string? first, string? second)
    {
        yield return first;
        if (!string.Equals(first?.Trim(), second?.Trim(), StringComparison.OrdinalIgnoreCase)) yield return second;
    }

    private async Task<IReadOnlyList<CataloguePrinting>> SearchOnceAsync(
        string name, string? setCode, string? language, bool exact, CancellationToken ct)
    {
        var query = new StringBuilder();
        // A double quote would end the quoted term and change what is being asked; there is no
        // escape for it in Scryfall's syntax, and no card name contains one.
        var term = name.Replace("\"", string.Empty).Trim();
        if (term.Length == 0) return [];

        if (exact) query.Append('!');
        query.Append('"').Append(term).Append('"');
        if (!string.IsNullOrWhiteSpace(setCode)) query.Append(" set:").Append(setCode.Trim());

        var localised = !string.IsNullOrWhiteSpace(language)
                     && !string.Equals(language.Trim(), "en", StringComparison.OrdinalIgnoreCase);
        if (localised) query.Append(" lang:").Append(language!.Trim());

        var url = $"{ApiRoot}/cards/search?unique=prints&order=collector&q={Uri.EscapeDataString(query.ToString())}";
        // Search is English-only unless this is asked for, so a lang: filter without it matches
        // nothing at all rather than matching the printings in that language.
        if (localised) url += "&include_multilingual=true";

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        // A search that matches nothing is a 404 here, not an empty list, so this is the ordinary
        // "no such card in that set" path and not an error.
        if (!response.IsSuccessStatusCode) return [];

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];

        var printings = data.EnumerateArray().Select(Parse).OfType<CataloguePrinting>().ToList();

        // The free-text form matches a name that *contains* the term, so "Delta" would bring back
        // every card in the set whose name has that word in it. Exact matching is Scryfall's job
        // in the other branch; here it has to be done on the way out, or the fallback that exists
        // to forgive a stray accent quietly widens into a substring search.
        return exact ? printings : printings.Where(p => NameMatches(name, p)).ToList();
    }

    /// <summary>
    /// Finds one printing from the name as it appears on the card. Kept for the awkward cases; use
    /// <see cref="SearchPrintingsAsync"/> when it matters that a name can have several printings.
    /// </summary>
    public async Task<CataloguePrinting?> SearchByPrintedNameAsync(
        string name, string? setCode, string? language, CancellationToken ct) =>
        (await SearchPrintingsAsync(name, setCode, language, ct)).FirstOrDefault();

    /// <summary>
    /// Settles what printing a card is, given what was read off it, and says how it was settled.
    /// <para>
    /// The name is the primary key here and the collector number is only corroboration. That is
    /// the opposite of how a card is normally looked up, and it is deliberate: a collector number
    /// is the least legible thing on a card — six point type in the bottom corner, behind a
    /// sleeve, at whatever angle the photograph was taken — while the name is the most legible
    /// thing on it. Reading the number is working against the physics, and looking it up does not
    /// check it: a set has a card at almost every number, so a digit misread returns a real
    /// printing of the wrong card. The lookup that was meant to catch the error confirms it.
    /// </para>
    /// <para>
    /// The number does still earn its keep, in the one case where the name is not enough: a set
    /// can print the same name more than once — basic lands, Secret Lairs, borderless and showcase
    /// variants — and then the number is the only thing that separates them. When it separates
    /// them, that is <c>confirmed</c>; when nothing does, the answer is <c>ambiguous</c>, which
    /// returns the most likely printing and lists the rest. The name is not in doubt there: every
    /// candidate carries it, which is what made them candidates.
    /// </para>
    /// <para>
    /// Every path ends in a printing wherever one can honestly be named, because the caller cannot
    /// use what it is not given: a row dropped for want of a certain printing loses the name too,
    /// and the name was the part that was read reliably. The resolution is what carries the doubt.
    /// </para>
    /// </summary>
    public async Task<ResolvedPrinting> ResolveAsync(
        string? name, string setCode, string? collectorNumber, string? language, CancellationToken ct)
    {
        var hasName = !string.IsNullOrWhiteSpace(name);
        var hasNumber = !string.IsNullOrWhiteSpace(collectorNumber);
        var hasSet = !string.IsNullOrWhiteSpace(setCode);

        if (!hasName)
        {
            // Nothing to check the number against. Worth answering anyway — a card whose name is
            // obscured by a sleeve's glare still has a number — but the resolution says plainly
            // that nothing corroborated it. A number without a set cannot be looked up at all:
            // every set has a card at that number.
            if (!hasNumber || !hasSet) return new ResolvedPrinting(null, Resolutions.Unresolved, collectorNumber);
            var only = await LookupBySetAndNumberAsync(setCode, collectorNumber!, language, ct);
            return only is null
                ? new ResolvedPrinting(null, Resolutions.Unresolved, collectorNumber)
                : new ResolvedPrinting(only, Resolutions.Assumed, collectorNumber);
        }

        // A name with no set code behind it must not become a search of all 573 sets at once.
        // Most cards in a binder are reprints, so that search returns a dozen printings of the
        // right name and nothing to choose between them — which used to mean the card was dropped
        // for having been read *too* well. One fuzzy lookup answers with a single card instead.
        if (!hasSet) return await ResolveWithoutSetAsync(name!, collectorNumber, language, ct);

        var candidates = await SearchPrintingsAsync(name!, setCode, language, ct);

        if (candidates.Count == 0)
        {
            // Nothing matched the name as transcribed. Before believing the number — the least
            // legible thing on the card — ask whether the name was merely read imperfectly, which
            // is far likelier and is what fuzzy matching is for.
            var approximate = await FuzzyAsync(name!, setCode, ct);
            if (approximate is not null)
                return new ResolvedPrinting(
                    await LocaliseAsync(approximate, language, ct), Resolutions.Fuzzy, collectorNumber);

            // The name found nothing even approximately *inside this set*. Before giving up, try it
            // without the set at all: the set code is the smallest print on the card and the name
            // is the largest, so a disagreement between them is far more likely to be a misread
            // symbol than a misread name. Doing this before the number matters, because the number
            // is read from the same small print as the set code and would be looked up inside the
            // very set that has already been shown not to hold this card.
            // This costs two or three further Scryfall calls on a card whose set was misread, on
            // top of the rungs already spent inside it. That is the right trade: it is paid only
            // on cards that would otherwise have been dropped outright, and the rate limiter in
            // front of the client keeps it polite.
            var elsewhere = await ResolveWithoutSetAsync(name!, collectorNumber, language, ct);
            if (elsewhere.Printing is not null)
            {
                // Answer, but say plainly that the set is the catalogue's and not the card's. This
                // overrides even a 'confirmed' from the lookup above: the number agreeing is worth
                // knowing, and it survives in ReadCollectorNumber, but the set code having been
                // misread is the more important thing to report and the easier one to hide.
                return elsewhere with { Resolution = Resolutions.Relocated };
            }

            // The name was past saving. Fall back to the number, now the only evidence there is.
            if (hasNumber)
            {
                var byNumber = await LookupBySetAndNumberAsync(setCode, collectorNumber!, language, ct);
                if (byNumber is not null) return new ResolvedPrinting(byNumber, Resolutions.Assumed, collectorNumber);
            }
            return new ResolvedPrinting(null, Resolutions.Unresolved, collectorNumber);
        }

        if (candidates.Count == 1)
        {
            var only = await LocaliseAsync(candidates[0], language, ct);
            if (!hasNumber) return new ResolvedPrinting(only, Resolutions.Named, collectorNumber);

            if (NumbersMatch(collectorNumber!, only.CollectorNumber))
                return new ResolvedPrinting(only, Resolutions.Confirmed, collectorNumber);

            // The name and the set found one printing, and the number read off the card is not
            // its number. Before concluding the number was misread, ask the other question.
            var moved = await RelocateByNumberAsync(name!, setCode, collectorNumber!, language, ct);
            if (moved is not null) return new ResolvedPrinting(moved, Resolutions.Relocated, collectorNumber);

            return new ResolvedPrinting(only, Resolutions.Corrected, collectorNumber);
        }

        // Several printings of this name in this set, so the number is the tiebreak it exists to be.
        if (hasNumber)
        {
            var picked = candidates.FirstOrDefault(c => NumbersMatch(collectorNumber!, c.CollectorNumber));
            if (picked is not null)
                return new ResolvedPrinting(
                    await LocaliseAsync(picked, language, ct), Resolutions.Confirmed, collectorNumber);

            // None of this set's printings carries that number, so the same question arises as
            // above: the set may be the thing that was wrong.
            var moved = await RelocateByNumberAsync(name!, setCode, collectorNumber!, language, ct);
            if (moved is not null) return new ResolvedPrinting(moved, Resolutions.Relocated, collectorNumber);
        }

        // Nothing separated them, so say so — but still answer. The candidates are ordered by
        // collector number, and the lowest-numbered printing of a name is the ordinary one far
        // more often than it is a showcase or borderless variant. The caller gets that, plus the
        // list, plus a resolution telling it the printing is the doubtful part and the name is not.
        return new ResolvedPrinting(
            await LocaliseAsync(candidates[0], language, ct), Resolutions.Ambiguous, collectorNumber, candidates);
    }

    /// <summary>
    /// Looks for a printing of <paramref name="name"/> outside <paramref name="setCode"/> that
    /// carries <paramref name="collectorNumber"/>, and returns it when exactly one does.
    /// <para>
    /// This exists because a disagreement between a read set code and a read number has two
    /// explanations, and the ladder used to consider only one of them. The number may have been
    /// misread — that is <see cref="Resolutions.Corrected"/>, and it is common, because the number
    /// is six-point type in a corner. Or the <em>set</em> may be wrong and the number right, which
    /// happens whenever a model recognises a famous card and supplies the set it remembers it from
    /// rather than reading the symbol. Assuming the first explanation silently returns the wrong
    /// printing of the right card, and says <c>corrected</c>, which reads as nothing being wrong.
    /// </para>
    /// <para>
    /// What tells them apart is where the number lands. A misread digit lands on a different card:
    /// a set has something at nearly every number, so being wrong costs nothing to arrange. For it
    /// to land on <em>another printing of the same name</em> is a different matter — a name has a
    /// handful of printings scattered across tens of thousands of numbers, so the coincidence is
    /// remote and the far likelier reading is that the number was right all along and the set was
    /// not. That is strong enough evidence to move the card, and it is why this is checked before
    /// the number is written off.
    /// </para>
    /// <para>
    /// Exactly one, because two printings of a name sharing a number give nothing to choose
    /// between; the ordinary answers below handle that case honestly and this one would not.
    /// </para>
    /// </summary>
    private async Task<CataloguePrinting?> RelocateByNumberAsync(
        string name, string setCode, string collectorNumber, string? language, CancellationToken ct)
    {
        var everywhere = await SearchPrintingsAsync(name, null, language, ct);

        var matches = everywhere
            .Where(p => !string.Equals(p.Set, setCode.Trim(), StringComparison.OrdinalIgnoreCase)
                     && NumbersMatch(collectorNumber, p.CollectorNumber))
            .ToList();

        return matches.Count == 1 ? await LocaliseAsync(matches[0], language, ct) : null;
    }

    /// <summary>
    /// Settles a card whose set code could not be read, from the name alone.
    /// <para>
    /// The number cannot lead here: it only means anything inside a set, and every set has a card
    /// at almost every number. So the name finds the card first, and the number — if one was read
    /// — then chooses between that name's printings, which is the one place a number is worth
    /// something without a set code beside it. A name narrows the field to a handful of printings,
    /// and a number is very unlikely to collide inside a field that small; that is the opposite of
    /// looking a number up in a set, where it always lands on something.
    /// </para>
    /// <para>
    /// When nothing chooses, the answer is <see cref="Resolutions.Unplaced"/> rather than a
    /// printing stated as fact. The alternative was reporting whichever printing the catalogue
    /// happens to prefer, which is wrong for every alternate-art reprint and wrong silently.
    /// </para>
    /// </summary>
    private async Task<ResolvedPrinting> ResolveWithoutSetAsync(
        string name, string? collectorNumber, string? language, CancellationToken ct)
    {
        var hasNumber = !string.IsNullOrWhiteSpace(collectorNumber);

        // Every printing of this name, in any set. This is the search that used to be avoided for
        // returning a dozen cards with nothing to choose between them; a number is exactly the
        // thing that chooses, and the printing it picks is the card in the photograph rather than
        // the catalogue's favourite. It is what finds an alt-art reprint whose set symbol is a
        // new design the reader has never seen.
        if (hasNumber)
        {
            var everywhere = await SearchPrintingsAsync(name, null, language, ct);
            var picked = everywhere.FirstOrDefault(p => NumbersMatch(collectorNumber!, p.CollectorNumber));
            if (picked is not null)
                return new ResolvedPrinting(
                    await LocaliseAsync(picked, language, ct), Resolutions.Confirmed, collectorNumber);
        }

        var found = await FuzzyAsync(name, null, ct);
        if (found is null) return new ResolvedPrinting(null, Resolutions.Unresolved, collectorNumber);

        // The name was read well enough that the catalogue knows the card, but nothing placed it:
        // no set code, and no number that matched any of its printings. Say so rather than dress
        // the catalogue's default printing up as a reading. Fuzzy is kept for its own meaning —
        // the name itself only matched approximately — so that the two stay tellable apart.
        var resolution = NameMatches(name, found) ? Resolutions.Unplaced : Resolutions.Fuzzy;

        return new ResolvedPrinting(
            await LocaliseAsync(found, language, ct), resolution, collectorNumber);
    }

    /// <summary>
    /// Swaps a printing for the same printing in <paramref name="language"/>, when there is one.
    /// It is the same card at the same number; what changes is that its own printed name comes
    /// back, which is the only name a reader of that card can check the answer against.
    /// </summary>
    private async Task<CataloguePrinting> LocaliseAsync(
        CataloguePrinting printing, string? language, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(language)
            || string.Equals(printing.Language, language.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(printing.CollectorNumber))
        {
            return printing;
        }

        return await LookupBySetAndNumberAsync(printing.Set, printing.CollectorNumber, language, ct) ?? printing;
    }

    /// <summary>
    /// Whether a collector number read off a card is the catalogue's number for a printing.
    /// <para>
    /// A reader transcribes the number with the set total attached ("438/462"), with the leading
    /// zeros a small set prints ("007"), or with the rarity letter that follows it; the catalogue
    /// has none of that. Everything but the number itself and any suffix is therefore stripped
    /// from both sides before they are compared.
    /// </para>
    /// </summary>
    internal static bool NumbersMatch(string read, string known)
    {
        var a = NormalizeNumber(read);
        var b = NormalizeNumber(known);
        return a.Length > 0 && a == b;
    }

    private static string NormalizeNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        // "438/462" is the number and the set total; only the part before the slash is the number.
        var number = value.Split('/')[0];

        var builder = new StringBuilder(number.Length);
        foreach (var character in number)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }

        // "007" and "7" are the same card: a small set prints the zeros and the catalogue does not.
        var text = builder.ToString();
        var trimmed = text.TrimStart('0');
        return trimmed.Length == 0 ? text : trimmed;
    }

    /// <summary>
    /// Whether a name read off a card is the name of a printing. Both are reduced to their letters
    /// and digits first, because a reader transcribes an apostrophe, an accent or a comma however
    /// they like, and a double-faced card is named for its front face alone on the card itself.
    /// </summary>
    internal static bool NameMatches(string read, CataloguePrinting printing)
    {
        var candidate = Simplify(read);
        if (candidate.Length == 0) return false;

        foreach (var known in new[] { printing.Name, printing.PrintedName })
        {
            if (string.IsNullOrEmpty(known)) continue;
            foreach (var face in known.Split("//", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var simplified = Simplify(face);
                if (simplified.Length == 0) continue;
                if (simplified == candidate) return true;
                if (candidate.Length >= 6 && (simplified.StartsWith(candidate, StringComparison.Ordinal)
                                           || candidate.StartsWith(simplified, StringComparison.Ordinal))) return true;
            }
        }

        return false;
    }

    private static string Simplify(string value)
    {
        var normalised = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalised.Length);
        foreach (var character in normalised)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private async Task<CataloguePrinting?> GetPrintingAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url)) return null;

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(document.RootElement);
    }

    /// <summary>Projects a Scryfall card object onto the catalogue shape. Internal for testing.</summary>
    internal static CataloguePrinting? Parse(JsonElement root)
    {
        string Read(string property) =>
            root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        var name = Read("name");
        if (string.IsNullOrEmpty(name)) return null;

        var language = Read("lang");
        return new CataloguePrinting(
            Read("set"),
            Read("collector_number"),
            name,
            string.IsNullOrEmpty(language) ? "en" : language,
            Read("printed_name"));
    }
}
