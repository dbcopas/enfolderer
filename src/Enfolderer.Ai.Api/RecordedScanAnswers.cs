using System.Globalization;
using System.Text.RegularExpressions;
using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Api;

public static class RecordedScanAnswers
{
    public static ScanAnswer? TryAnswer(ScanResultDocument result, string question)
    {
        var ownership = Regex.IsMatch(question, @"\b(own|owned|ownership|collection|missing)\b|\bdo I have\b|\bhow many copies\b|\bwhat do I (still )?need\b", RegexOptions.IgnoreCase);
        var prices = Regex.IsMatch(question, @"\b(price|prices|value|valuable|worth|expensive|cheapest)\b", RegexOptions.IgnoreCase);
        var evidence = Regex.IsMatch(question, @"\b(identified|identification|confidence|evidence)\b|why.*(printing|select|choos|chose|match|identify)", RegexOptions.IgnoreCase);
        if (!ownership && !prices && !evidence) return null;
        var named = result.Cards.Where(c => !string.IsNullOrWhiteSpace(c.Name)
            && c.Name.Split(" // ").Any(name => question.Contains(name, StringComparison.OrdinalIgnoreCase))).ToArray();
        var number = Regex.Match(question, @"\bcard\s+(\d+)\b", RegexOptions.IgnoreCase);
        var cards = named.Length > 0 ? named : number.Success
            ? result.Cards.Where(c => int.TryParse(number.Groups[1].Value, out var index) && c.Index + 1 == index).ToArray()
            : result.Cards.ToArray();
        if (cards.Length == 0) return new("That card position is not present in this recorded scan.", [], false, "scanEvidence");
        var lines = new List<string> { "Recorded scan facts only; not a live lookup or an AI interpretation of your question." };
        if (prices && Regex.IsMatch(question, @"\b(most|highest|expensive|valuable)\b", RegexOptions.IgnoreCase))
        {
            var highest = CardValueRanking.HighestPriced(cards);
            lines.Add(highest is null ? "No identified card has a recorded comparable price."
                : $"Highest recorded reference value: {highest.Name} ({Money(CardValueRanking.Price(highest))}). Ranking uses nonfoil when available, otherwise foil; the finish in the photo is not inferred.");
        }
        foreach (var card in cards)
        {
            lines.Add($"{card.Index + 1}. {card.Name ?? "Unidentified card"} ({card.Set ?? "?"} {card.CollectorNumber ?? "?"})");
            if (ownership)
                lines.Add(card.Ownership is not { Error: null } owned ? "Collection comparison unavailable or disabled for this scan; this does not mean zero owned."
                    : $"Snapshot at scan time: exact printing {owned.Quantity}; all printings {owned.AllQuantity}; target {owned.RequiredQuantity}. Not your current live collection.");
            if (prices)
                lines.Add(card.Prices is not { } price ? "No price was recorded."
                    : $"Nonfoil {Money(price.Nonfoil)}; foil {Money(price.Foil)}. Source: {price.Source}; retrieved {price.RetrievedAt:O}. These are recorded reference prices, not current offers. {price.Error}");
            if (evidence)
            {
                lines.Add($"Read: {card.ReadName ?? "not recorded"}; set {card.ReadSet ?? "not recorded"}; number {card.ReadCollectorNumber ?? "not recorded"}.");
                lines.Add($"Resolution: {card.Resolution ?? "not recorded"}; printing {(card.PrintingWasPlaced ? "placed" : "needs review")}; confidence {card.Confidence.ToString("0.###", CultureInfo.InvariantCulture)}.");
                lines.Add($"Artwork check: {card.ArtVerdict ?? "not recorded"}. {card.ArtReason} Open Explain this scan for the full recorded evidence and crop.");
            }
        }
        return new(string.Join("\n", lines), [], true, "scanEvidence");
    }

    private static string Money(decimal? amount) => amount.HasValue
        ? amount.Value.ToString("0.00", CultureInfo.InvariantCulture) + " EUR" : "unavailable";
}
