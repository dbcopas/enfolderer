using System.Globalization;

namespace Enfolderer.Ai.Contracts;

public static class ScanCsv
{
    public static string Format(ScanResultDocument result)
    {
        static string Amount(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        var rows = CardValueRanking.Order(result.Cards.Where(c => c.IsIdentified)).Select((c, i) =>
            string.Join(';', (i + 1).ToString(CultureInfo.InvariantCulture),
                (c.Index + 1).ToString(CultureInfo.InvariantCulture), Cell(c.Name), Cell(c.Set?.ToUpperInvariant()),
                Cell(c.CollectorNumber), Cell(c.Language), Amount(c.Prices?.Nonfoil), Amount(c.Prices?.Foil),
                Amount(CardValueRanking.Price(c)),
                c.Ownership is { Error: null } owned ? owned.Quantity.ToString(CultureInfo.InvariantCulture) : "",
                c.Ownership is { Error: null } all ? all.AllQuantity.ToString(CultureInfo.InvariantCulture) : "",
                Cell(c.Ownership?.Error), Cell(c.Resolution), Cell(c.Prices?.Error),
                Cell(c.Prices?.Source), Cell(c.Prices?.RetrievedAt.ToString("u", CultureInfo.InvariantCulture))));
        return "Rank;Card position;Name;Set;Number;Language;Nonfoil EUR;Foil EUR;Ranking EUR;Owned;All printings;Ownership error;Resolution;Price error;Price source;Retrieved UTC\r\n"
            + string.Join("\r\n", rows) + "\r\n";
    }

    public static string Format(IReadOnlyList<ScannedCollectionPrinting> printings) =>
        "Name;Set;Number;Language;Quantity\r\n"
        + string.Join("\r\n", printings.Select(p => string.Join(';',
            Cell(p.Name), Cell(p.Set), Cell(p.Number), Cell(p.Language),
            p.Quantity.ToString(CultureInfo.InvariantCulture))))
        + (printings.Count > 0 ? "\r\n" : "");

    private static string Cell(string? value)
    {
        var text = value ?? "";
        var trimmed = text.TrimStart();
        if (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@') text = "'" + text;
        return text.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
    }
}
