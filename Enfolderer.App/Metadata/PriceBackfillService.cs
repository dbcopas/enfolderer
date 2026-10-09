using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
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
    private readonly Func<string>? _getDisplayMode;
    private readonly Action? _notifyPriceSummaryChanged;
    private string _priceDisplayMode => _getDisplayMode?.Invoke() ?? "Missing";

    public PriceBackfillService(
        ICardMetadataResolver resolver,
        Action<string>? setStatus = null,
        Func<string>? getDisplayMode = null,
        Action? notifyPriceSummaryChanged = null)
    {
        _resolver = resolver;
        _setStatus = setStatus;
        _getDisplayMode = getDisplayMode;
        _notifyPriceSummaryChanged = notifyPriceSummaryChanged;
    }

    /// <summary>
    /// For each visible card slot that is missing (qty 0) and has no cached price,
    /// fetch the price from Scryfall and update the per-card cache + ordered faces list.
    /// </summary>
    public async Task BackfillVisibleAsync(
        IEnumerable<CardSlot> visibleSlots,
        List<CardEntry> orderedFaces,
        HttpClient http,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return;

        var mode = _priceDisplayMode;
        if (mode == "None") return;

        var candidates = visibleSlots
            .Where(s => !s.IsPlaceholderBack && !string.IsNullOrEmpty(s.Set))
            .Where(s => mode == "All" || s.Quantity == 0)
            .ToList();

        LogHost.Sink?.Log($"[PriceBackfill] Visible slots: {visibleSlots.Count()}, candidates ({mode}): {candidates.Count}", "Price");

        if (candidates.Count == 0) return;

        // Check which ones already have prices in orderedFaces or the price store
        var needPrice = new List<(CardSlot Slot, int GlobalIndex, string FlightKey)>();
        var now = DateTime.UtcNow;
        var maxAge = TimeSpan.FromDays(7);
        foreach (var slot in candidates)
        {
            if (slot.GlobalIndex < 0 || slot.GlobalIndex >= orderedFaces.Count) continue;
            var entry = orderedFaces[slot.GlobalIndex];

            // Check the price store (has timestamp)
            var cached = CardPriceStore.GetWithTimestamp(entry.Set, entry.Number);
            if (cached.HasValue)
            {
                var age = now - cached.Value.FetchedUtc;
                if (age < maxAge)
                {
                    // Fresh enough — just make sure the slot shows it
                    if (string.IsNullOrEmpty(slot.PriceDisplay) && slot.RawPriceEur.HasValue)
                    {
                        var cachedMode = _priceDisplayMode;
                        System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() => slot.RefreshPriceDisplay(cachedMode));
                    }
                    LogHost.Sink?.Log($"[PriceBackfill] Skip {entry.Set}/{entry.Number} - cached {age.TotalDays:0.0}d ago: {cached.Value.Price}€", "Price");
                    continue;
                }
                LogHost.Sink?.Log($"[PriceBackfill] Stale {entry.Set}/{entry.Number} - cached {age.TotalDays:0.0}d ago, re-fetching", "Price");
            }
            else if (entry.PriceEur.HasValue)
            {
                // Has price on entry but not in store (shouldn't happen often) — treat as fresh
                LogHost.Sink?.Log($"[PriceBackfill] Skip {entry.Set}/{entry.Number} - on entry: {entry.PriceEur.Value}€", "Price");
                continue;
            }

            var key = $"{entry.Set}|{entry.Number}".ToLowerInvariant();
            lock (_inFlight)
            {
                if (_inFlight.Contains(key)) continue;
                _inFlight.Add(key);
            }
            needPrice.Add((slot, slot.GlobalIndex, key));
        }

        if (needPrice.Count == 0)
        {
            LogHost.Sink?.Log("[PriceBackfill] All candidates already have prices.", "Price");
            return;
        }

        LogHost.Sink?.Log($"[PriceBackfill] Fetching prices for {needPrice.Count} cards...", "Price");
        NotifyStatus($"Fetching prices for {needPrice.Count} missing card(s)...");
        int fetched = 0;

        foreach (var (slot, gi, flightKey) in needPrice)
        {
            if (ct.IsCancellationRequested)
            {
                LogHost.Sink?.Log("[PriceBackfill] Cancelled — newer Refresh() superseded this run.", "Price");
                // Release remaining in-flight keys so the next run can fetch them
                foreach (var (_, _, k) in needPrice) { lock (_inFlight) { _inFlight.Remove(k); } }
                return;
            }
            try
            {
                var entry = orderedFaces[gi];
                LogHost.Sink?.Log($"[PriceBackfill] Fetching {entry.Set}/{entry.Number} ({entry.Name})...", "Price");
                var result = await FetchPriceEurAsync(http, entry.Set!, entry.Number);
                if (result.HasValue)
                {
                    var (price, currency) = result.Value;
                    fetched++;
                    LogHost.Sink?.Log($"[PriceBackfill] Got {entry.Set}/{entry.Number} = {price} {currency}", "Price");
                    var updated = entry with { PriceEur = price, PriceCurrency = currency };
                    orderedFaces[gi] = updated;
                    CardPriceStore.Set(entry.Set, entry.Number, price, currency: currency);
                    _resolver.UpdateCardPriceInCache(entry.Set!, entry.Number, price);
                    // Update the visible slot's price display on the UI thread
                    System.Windows.Application.Current?.Dispatcher?.BeginInvoke(() =>
                    {
                        slot.RawPriceEur = price;
                        slot.PriceCurrency = currency;
                        slot.RefreshPriceDisplay(_priceDisplayMode);
                    });
                }
                else
                {
                    LogHost.Sink?.Log($"[PriceBackfill] No price for {entry.Set}/{entry.Number}", "Price");
                }
            }
            catch (Exception ex)
            {
                LogHost.Sink?.Log($"[PriceBackfill] FAILED {slot.Set}/{slot.Number}: {ex.GetType().Name}: {ex.Message}", "Price");
            }
            finally
            {
                lock (_inFlight) { _inFlight.Remove(flightKey); }
            }
        }
        NotifyStatus($"Prices fetched: {fetched}/{needPrice.Count} cards.");
        if (fetched > 0)
        {
            CardPriceStore.SaveToDisk();
            try { _notifyPriceSummaryChanged?.Invoke(); } catch { }
        }
    }

    private void NotifyStatus(string msg)
    {
        try { _setStatus?.Invoke(msg); } catch { }
    }

    private static async Task<(decimal Price, string Currency)?> FetchPriceEurAsync(HttpClient http, string setCode, string number)
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
                return (eurVal, "EUR");
            if (prices.TryGetProperty("eur_foil", out var eurFoilProp) && eurFoilProp.ValueKind == JsonValueKind.String
                && decimal.TryParse(eurFoilProp.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var eurFoilVal))
                return (eurFoilVal, "EUR");
            if (prices.TryGetProperty("usd", out var usdProp) && usdProp.ValueKind == JsonValueKind.String
                && decimal.TryParse(usdProp.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var usdVal))
                return (usdVal, "USD");
            if (prices.TryGetProperty("usd_foil", out var usdFoilProp) && usdFoilProp.ValueKind == JsonValueKind.String
                && decimal.TryParse(usdFoilProp.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var usdFoilVal))
                return (usdFoilVal, "USD");
        }
        else
        {
            LogHost.Sink?.Log($"[PriceBackfill] No prices object in response for {setCode}/{number}", "Price");
        }
        return null;
    }
}
