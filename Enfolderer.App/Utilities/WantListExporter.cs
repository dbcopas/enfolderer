using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Enfolderer.App.Core;
using Enfolderer.App.Imaging;

namespace Enfolderer.App.Utilities;

public static class WantListExporter
{
    private static string FormatPrice(CardEntry card)
    {
        var price = card.PriceEur ?? CardPriceStore.Get(card.Set, card.Number);
        return price.HasValue ? price.Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "";
    }
    private static List<CardEntry> GetWantedCards(IReadOnlyList<CardEntry> cards)
    {
        if (cards == null || cards.Count == 0)
            throw new InvalidOperationException("No binder loaded.");

        var wanted = cards
            .Where(c => !c.IsBackFace)
            .Where(c => c.Quantity == 0 || (c.IsModalDoubleFaced && c.Quantity == 1))
            .ToList();

        if (wanted.Count == 0)
            throw new InvalidOperationException("No cards match want-list criteria (qty 0, or MFC with qty 1).");

        return wanted;
    }

    public static string Export(IReadOnlyList<CardEntry> cards, string? outputPath = null)
    {
        var wanted = GetWantedCards(cards);

        var grouped = wanted
            .GroupBy(c => c.Set ?? "UNKNOWN", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        string path = outputPath
            ?? Path.Combine(
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                $"want_list_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        using var writer = File.CreateText(path);
        writer.WriteLine("set,name,collector number,price (EUR)");
        foreach (var setGroup in grouped)
        {
            foreach (var card in setGroup.OrderBy(c => c.EffectiveNumber, StringComparer.OrdinalIgnoreCase))
            {
                string price = FormatPrice(card);
                writer.WriteLine($"{setGroup.Key},\"{card.Name}\",{card.EffectiveNumber},{price}");
            }
        }

        return path;
    }

    /// <summary>
    /// Exports in Moxfield text format: 1 Card Name (SET) collector_number
    /// Limited to 1000 cards (Moxfield wishlist cap).
    /// </summary>
    public static string ExportMoxfield(IReadOnlyList<CardEntry> cards, string? outputPath = null)
    {
        var wanted = GetWantedCards(cards);

        string path = outputPath
            ?? Path.Combine(
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                $"want_list_moxfield_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

        using var writer = File.CreateText(path);
        foreach (var card in wanted.OrderBy(c => c.Set ?? "", StringComparer.OrdinalIgnoreCase)
                                   .ThenBy(c => c.Number, StringComparer.OrdinalIgnoreCase)
                                   .Take(1000))
        {
            string name = card.FrontRaw ?? card.Name;
            string set = (card.Set ?? "").ToUpperInvariant();
            writer.WriteLine($"1 {name} ({set}) {card.Number}");
        }

        return path;
    }

    /// <summary>
    /// Exports in Moxfield collection CSV format (no card count limit).
    /// </summary>
    public static string ExportMoxfieldCsv(IReadOnlyList<CardEntry> cards, string? outputPath = null)
    {
        var wanted = GetWantedCards(cards);

        string path = outputPath
            ?? Path.Combine(
                AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                $"want_list_moxfield_{DateTime.Now:yyyyMMdd_HHmmss}.csv");

        using var writer = File.CreateText(path);
        writer.WriteLine("Count,Tradelist Count,Name,Edition,Condition,Language,Foil,Tags,Last Modified,Collector Number,Alter,Proxy,Purchase Price");
        foreach (var card in wanted.OrderBy(c => c.Set ?? "", StringComparer.OrdinalIgnoreCase)
                                   .ThenBy(c => c.Number, StringComparer.OrdinalIgnoreCase))
        {
            string name = card.FrontRaw ?? card.Name;
            string set = (card.Set ?? "").ToLowerInvariant();
            string price = FormatPrice(card);
            writer.WriteLine($"1,0,\"{name}\",{set},Near Mint,English,,,,{card.Number},false,false,{price}");
        }

        return path;
    }
}
