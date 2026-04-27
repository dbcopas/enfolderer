using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace Enfolderer.App;

/// <summary>
/// BinderViewModel partial: Price display logic and calculations.
/// Handles price display mode switching, price segment building, and outlier summarization.
/// </summary>
public partial class BinderViewModel
{
    // Price display mode: "None", "Missing", "All"
    private string _priceDisplayMode = "Missing";
    public string PriceDisplayMode
    {
        get => _priceDisplayMode;
        set
        {
            if (!string.Equals(_priceDisplayMode, value, StringComparison.OrdinalIgnoreCase))
            {
                _priceDisplayMode = value;
                OnPropertyChanged();
                RefreshVisiblePrices();
            }
        }
    }

    private void RefreshVisiblePrices()
    {
        foreach (var s in LeftSlots) s.RefreshPriceDisplay(_priceDisplayMode);
        foreach (var s in RightSlots) s.RefreshPriceDisplay(_priceDisplayMode);
    }

    private static readonly Brush PriceRedBrush = CreateFrozenBrush(Colors.Red);
    private static readonly Brush PriceGreenBrush = CreateFrozenBrush(Color.FromRgb(0x32, 0xCD, 0x32));
    private static readonly Brush PriceWhiteBrush = CreateFrozenBrush(Colors.White);
    private static readonly Brush PriceAmberBrush = CreateFrozenBrush(Color.FromRgb(0xFF, 0xC1, 0x07));
    
    private static Brush CreateFrozenBrush(Color c) 
    { 
        var b = new SolidColorBrush(c); 
        if (b.CanFreeze) b.Freeze(); 
        return b; 
    }

    private static MissingPriceOutlierSummary SummarizeMissingPriceOutliers(IReadOnlyList<decimal> prices)
    {
        if (prices.Count == 0)
            return new MissingPriceOutlierSummary(0, 0, 0, 0m, 0m, 0m);

        var sorted = prices.OrderBy(p => p).ToList();
        decimal total = sorted.Sum();
        if (sorted.Count < 4)
            return new MissingPriceOutlierSummary(sorted.Count, sorted.Count, 0, total, total, 0m);

        decimal q1 = GetPercentile(sorted, 0.25m);
        decimal q3 = GetPercentile(sorted, 0.75m);
        decimal iqr = q3 - q1;
        decimal upperFence = q3 + (1.5m * iqr);

        var outliers = sorted.Where(price => price > upperFence).ToList();
        if (outliers.Count == 0)
            return new MissingPriceOutlierSummary(sorted.Count, sorted.Count, 0, total, total, 0m);

        decimal outlierTotal = outliers.Sum();
        return new MissingPriceOutlierSummary(
            sorted.Count,
            sorted.Count - outliers.Count,
            outliers.Count,
            total,
            total - outlierTotal,
            outlierTotal);
    }

    private static decimal GetPercentile(IReadOnlyList<decimal> sortedValues, decimal percentile)
    {
        if (sortedValues.Count == 0) return 0m;
        if (sortedValues.Count == 1) return sortedValues[0];

        decimal position = (sortedValues.Count - 1) * percentile;
        int lowerIndex = (int)decimal.Floor(position);
        int upperIndex = (int)decimal.Ceiling(position);
        if (lowerIndex == upperIndex) return sortedValues[lowerIndex];

        decimal fraction = position - lowerIndex;
        decimal lower = sortedValues[lowerIndex];
        decimal upper = sortedValues[upperIndex];
        return lower + ((upper - lower) * fraction);
    }

    private void UpdateSetMissingPrice()
    {
        SetMissingPriceSegments.Clear();

        // Collect distinct sets visible on the current pages (skip back-face / placeholder)
        var visibleSets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var slot in LeftSlots.Concat(RightSlots))
        {
            if (slot.IsBackFace || slot.IsPlaceholderBack) continue;
            if (!string.IsNullOrEmpty(slot.Set))
                visibleSets.Add(slot.Set);
        }
        if (visibleSets.Count == 0) return;

        bool showAll = string.Equals(_priceDisplayMode, "All", StringComparison.OrdinalIgnoreCase);
        bool first = true;

        foreach (var setCode in visibleSets)
        {
            decimal missingTotal = 0m;
            int missingCount = 0;
            int pricedMissingCount = 0;
            decimal collectedTotal = 0m;
            int collectedCount = 0;
            string? currency = null;
            var missingPrices = new List<decimal>();

            foreach (var card in _cards)
            {
                if (card.IsBackFace) continue;
                if (!string.Equals(card.Set, setCode, StringComparison.OrdinalIgnoreCase)) continue;
                var price = card.PriceEur ?? Imaging.CardPriceStore.Get(card.Set, card.Number);
                if (card.Quantity == 0)
                {
                    missingCount++;
                    if (price.HasValue)
                    {
                        pricedMissingCount++;
                        missingTotal += price.Value;
                        missingPrices.Add(price.Value);
                    }
                }
                else if (showAll)
                {
                    collectedCount++;
                    if (price.HasValue) collectedTotal += price.Value;
                }
                if (currency == null && price.HasValue)
                    currency = Imaging.CardPriceStore.GetCurrency(card.Set, card.Number);
            }

            currency ??= "EUR";
            string symbol = string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase) ? "$" : "\u20ac";
            var label = setCode.ToUpperInvariant();

            if (!first)
                SetMissingPriceSegments.Add(new PriceSegment(" | ", null, System.Windows.FontWeights.Normal));
            first = false;

            SetMissingPriceSegments.Add(new PriceSegment($"{label} missing: {missingCount}, ", PriceRedBrush, System.Windows.FontWeights.Normal));
            SetMissingPriceSegments.Add(new PriceSegment($"{symbol}{missingTotal:0.00}", PriceRedBrush, System.Windows.FontWeights.Normal));

            if (pricedMissingCount > 0)
            {
                var summary = SummarizeMissingPriceOutliers(missingPrices);
                if (summary.OutlierCount > 0)
                {
                    SetMissingPriceSegments.Add(new PriceSegment("  core ", null, System.Windows.FontWeights.Normal));
                    SetMissingPriceSegments.Add(new PriceSegment($"{summary.InlierCount}/{summary.PricedCount}", PriceRedBrush, System.Windows.FontWeights.Bold));
                    SetMissingPriceSegments.Add(new PriceSegment(", ", null, System.Windows.FontWeights.Normal));
                    SetMissingPriceSegments.Add(new PriceSegment($"{symbol}{summary.TrimmedTotal:0.00}", PriceRedBrush, System.Windows.FontWeights.Bold));
                    SetMissingPriceSegments.Add(new PriceSegment($"  outliers {summary.OutlierCount}, ", null, System.Windows.FontWeights.Normal));
                    SetMissingPriceSegments.Add(new PriceSegment($"{symbol}{summary.OutlierTotal:0.00}", PriceRedBrush, System.Windows.FontWeights.Normal));
                }
                else if (pricedMissingCount != missingCount)
                {
                    SetMissingPriceSegments.Add(new PriceSegment($"  priced ", null, System.Windows.FontWeights.Normal));
                    SetMissingPriceSegments.Add(new PriceSegment($"{pricedMissingCount}/{missingCount}", PriceRedBrush, System.Windows.FontWeights.Bold));
                }
            }
            else if (missingCount > 0)
            {
                SetMissingPriceSegments.Add(new PriceSegment("  priced ", null, System.Windows.FontWeights.Normal));
                SetMissingPriceSegments.Add(new PriceSegment($"0/{missingCount}", PriceRedBrush, System.Windows.FontWeights.Bold));
            }

            if (showAll)
            {
                SetMissingPriceSegments.Add(new PriceSegment($"  have: {collectedCount}, ", null, System.Windows.FontWeights.Normal));
                SetMissingPriceSegments.Add(new PriceSegment($"{symbol}{collectedTotal:0.00}", PriceGreenBrush, System.Windows.FontWeights.Normal));
            }
        }
    }
}
