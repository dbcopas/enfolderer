using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Enfolderer.App.Core;
using Enfolderer.App.Core.Abstractions;
using Enfolderer.App.Core.Logging;
using Enfolderer.App.Imaging;
using Enfolderer.App.Importing;
using Enfolderer.App.Infrastructure;

namespace Enfolderer.App.Metadata;

/// <summary>
/// On-demand backfill of EUR prices for visible missing cards that lack cached price data.
/// Called after page navigation to lazily populate prices from Scryfall.
/// </summary>
public class PriceBackfillService
{
    private readonly ICardMetadataResolver _resolver;
    private readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string>? _setStatus;

    public PriceBackfillService(ICardMetadataResolver resolver, Action<string>? setStatus = null)
    {
        _resolver = resolver;
        _setStatus = setStatus;
    }

    /// <summary>
    /// For each visible card slot that is missing (qty 0) and has no cached price,
    /// fetch the price from Scryfall and update the per-card cache + ordered faces list.
    /// </summary>
    public async Task BackfillVisibleAsync(
        IEnumerable<CardSlot> visibleSlots,
        List<CardEntry> orderedFaces,
        HttpClient http)
    {
        var candidates = visibleSlots
            .Where(s => s.Quantity == 0 && !s.IsPlaceholderBack && !string.IsNullOrEmpty(s.Set))
            .ToList();

        LogHost.Sink?.Log($"[PriceBackfill] Visible slots: {visibleSlots.Count()}, missing (qty=0): {candidates.Count}", "Price");

        if (candidates.Count == 0) return;

        // Check which ones already have prices in orderedFaces or the price store
        var needPrice = new List<(CardSlot Slot, int GlobalIndex)>();
        foreach (var slot in candidates)
        {
            if (slot.GlobalIndex < 0 || slot.GlobalIndex >= orderedFaces.Count) continue;
            var entry = orderedFaces[slot.GlobalIndex];
            if (entry.PriceEur.HasValue)
            {
                LogHost.Sink?.Log($"[PriceBackfill] Skip {entry.Set}/{entry.Number} - already on entry: {entry.PriceEur.Value}€", "Price");
                continue;
            }
            // Already fetched on a different page visit
            var storePrice = CardPriceStore.Get(entry.Set, entry.Number);
            if (storePrice.HasValue)
            {
                LogHost.Sink?.Log($"[PriceBackfill] Skip {entry.Set}/{entry.Number} - in price store: {storePrice.Value}€", "Price");
                continue;
            }

            var key = $"{entry.Set}|{entry.Number}".ToLowerInvariant();
            lock (_inFlight)
            {
                if (_inFlight.Contains(key)) continue;
                _inFlight.Add(key);
            }
            needPrice.Add((slot, slot.GlobalIndex));
        }

        if (needPrice.Count == 0)
        {
            LogHost.Sink?.Log("[PriceBackfill] All candidates already have prices.", "Price");
            return;
        }

        LogHost.Sink?.Log($"[PriceBackfill] Fetching prices for {needPrice.Count} cards...", "Price");
        NotifyStatus($"Fetching prices for {needPrice.Count} missing card(s)...");
        int fetched = 0;

        foreach (var (slot, gi) in needPrice)
        {
            try
            {
                var entry = orderedFaces[gi];
                LogHost.Sink?.Log($"[PriceBackfill] Fetching {entry.Set}/{entry.Number} ({entry.Name})...", "Price");
                var price = await FetchPriceEurAsync(http, entry.Set!, entry.Number);
                if (price.HasValue)
                {
                    fetched++;
                    LogHost.Sink?.Log($"[PriceBackfill] Got {entry.Set}/{entry.Number} = {price.Value}€", "Price");
                    var updated = entry with { PriceEur = price };
                    orderedFaces[gi] = updated;
                    CardPriceStore.Set(entry.Set, entry.Number, price.Value);
                    _resolver.UpdateCardPriceInCache(entry.Set!, entry.Number, price.Value);
                    // Update the visible slot's price display on the UI thread
                    var display = $"€{price.Value:0.00}";
                    System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() => slot.PriceDisplay = display);
                }
                else
                {
                    LogHost.Sink?.Log($"[PriceBackfill] No EUR price for {entry.Set}/{entry.Number}", "Price");
                }
            }
            catch (Exception ex)
            {
                LogHost.Sink?.Log($"[PriceBackfill] FAILED {slot.Set}/{slot.Number}: {ex.GetType().Name}: {ex.Message}", "Price");
            }
            finally
            {
                var entry = orderedFaces[gi];
                var key = $"{entry.Set}|{entry.Number}".ToLowerInvariant();
                lock (_inFlight) { _inFlight.Remove(key); }
            }
        }
        NotifyStatus($"Prices fetched: {fetched}/{needPrice.Count} cards.");
    }

    private void NotifyStatus(string msg)
    {
        try { _setStatus?.Invoke(msg); } catch { }
    }

    private static async Task<decimal?> FetchPriceEurAsync(HttpClient http, string setCode, string number)
    {
        var url = ScryfallUrlHelper.BuildCardApiUrl(setCode, number);
        if (string.IsNullOrEmpty(url))
        {
            LogHost.Sink?.Log($"[PriceBackfill] Empty URL for {setCode}/{number}", "Price");
            return null;
        }

        LogHost.Sink?.Log($"[PriceBackfill] GET {url}", "Price");
        await ApiRateLimiter.WaitAsync();
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode)
        {
            LogHost.Sink?.Log($"[PriceBackfill] HTTP {(int)resp.StatusCode} for {url}", "Price");
            return null;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;

        if (root.TryGetProperty("prices", out var prices) && prices.ValueKind == JsonValueKind.Object)
        {
            // Log all available price keys for diagnostics
            var keys = new List<string>();
            foreach (var p in prices.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(p.Value.GetString()))
                    keys.Add($"{p.Name}={p.Value.GetString()}");
            LogHost.Sink?.Log($"[PriceBackfill] Prices for {setCode}/{number}: {(keys.Count > 0 ? string.Join(", ", keys) : "(all null)")}", "Price");

            if (prices.TryGetProperty("eur", out var eurProp) && eurProp.ValueKind == JsonValueKind.String
                && decimal.TryParse(eurProp.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var eurVal))
                return eurVal;
        }
        else
        {
            LogHost.Sink?.Log($"[PriceBackfill] No prices object in response for {setCode}/{number}", "Price");
        }
        return null;
    }
}
