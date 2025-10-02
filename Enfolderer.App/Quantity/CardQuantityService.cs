using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using Enfolderer.App.Core.Logging;
using Enfolderer.App.Collection;
using Enfolderer.App.Core;
using Microsoft.Data.Sqlite;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Quantity;

/// <summary>
/// Encapsulates quantity enrichment, MFC display adjustment, and toggle persistence logic.
/// Extracted from MainWindow.xaml.cs (Phase 2 refactor).
/// </summary>
public sealed class CardQuantityService : IQuantityService
{
    // Tracks (set:number) miss occurrences to suppress endless logging loops when enrichment runs repeatedly.
    private static readonly Dictionary<(string set,string num), int> _missCounts = new();
    private const int MissLogLimitPerKey = 3; // after this, additional misses for the same key are suppressed
    private readonly IRuntimeFlags _flags;
    private readonly IRuntimeFlagService? _flagService;
    private readonly IQuantityRepository? _quantityRepo;
    private readonly ILogSink? _log;
    private readonly IMfcQuantityAdjustmentService _mfcAdjust;
    public CardQuantityService(IRuntimeFlags? flags = null, IQuantityRepository? quantityRepository = null, ILogSink? log = null, IMfcQuantityAdjustmentService? mfcAdjustment = null, IRuntimeFlagService? flagService = null)
    {
        _flags = flags ?? RuntimeFlags.Default;
        _quantityRepo = quantityRepository;
        _log = log;
        _mfcAdjust = mfcAdjustment ?? new MfcQuantityAdjustmentService(_flags, _log);
        _flagService = flagService;
    }

    public ILogSink? LogSink => _log;

    public void EnrichQuantities(CardCollectionData collection, List<CardEntry> cards)
    {
    bool qtyDebug = _flagService?.QtyDebug ?? _flags.QtyDebug;
        if (collection.Quantities.Count == 0)
        {
            if (qtyDebug)
            {
                string reason = collection.IsLoaded ? "loaded-but-empty (no >0 Qty values?)" : "collection-not-loaded";
                var firstFive = string.Join(", ", cards.Take(5).Select(c => c.Set+":"+c.Number));
                _log?.Log($"Skip: Quantities empty - {reason}. Sample cards: {firstFive}", LogCategories.QtyEnrich);
                Console.WriteLine($"[QtyEnrich] Skip: Quantities empty - {reason}. Sample cards: {firstFive}");
            }
            return;
        }
        int updated = 0;
        int unmatchedCount = 0;
        List<string> unmatchedSamples = new();
        if (qtyDebug)
        {
            _log?.Log($"Start: quantityKeys={collection.Quantities.Count} cards={cards.Count}", "QtyEnrich");
            Console.WriteLine($"[QtyEnrich] Start: quantityKeys={collection.Quantities.Count} cards={cards.Count}");
            try
            {
                // Snapshot keys defensively to avoid InvalidOperationException if underlying dictionary mutates mid-enumeration.
                var keySnapshot = collection.Quantities.Keys.ToArray();
                var firstSets = keySnapshot
                    .GroupBy(k => k.Item1)
                    .Take(5)
                    .Select(g => g.Key + ":" + string.Join("|", g.Take(8).Select(t => t.Item2)));
                _log?.Log("KeysSample " + string.Join(" || ", firstSets), LogCategories.QtyEnrich);
            }
            catch (System.InvalidOperationException ioe)
            {
                _log?.Log("KeysSample enumeration race (continuing): " + ioe.Message, LogCategories.QtyCoordinator);
            }
            catch (System.Exception ex)
            {
                _log?.Log("KeysSample unexpected error (continuing): " + ex.Message, LogCategories.QtyCoordinator);
            }
        }
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (c.IsBackFace && string.Equals(c.Set, "__BACK__", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(c.Set) || string.IsNullOrEmpty(c.Number)) continue;
            // WAR Japanese alt-art star planeswalkers authoritative variant path
            if (string.Equals(c.Set, "WAR", StringComparison.OrdinalIgnoreCase) && c.Number.Contains('★'))
            {
                string starBaseRaw = c.Number.Replace("★", string.Empty);
                string starTrim = starBaseRaw.TrimStart('0'); if (starTrim.Length == 0) starTrim = "0";
                int qtyVariant = 0; bool variantFound = false;
                if (int.TryParse(starBaseRaw, out _))
                {
                    if (collection.TryGetVariantQuantityFlexible(c.Set, starBaseRaw, "Art JP", out var artQty) ||
                        collection.TryGetVariantQuantityFlexible(c.Set, starTrim, "Art JP", out artQty) ||
                        collection.TryGetVariantQuantityFlexible(c.Set, starBaseRaw, "JP", out artQty) ||
                        collection.TryGetVariantQuantityFlexible(c.Set, starTrim, "JP", out artQty))
                    { qtyVariant = artQty; variantFound = true; }
                }
                if (_flags.QtyDebug)
                {
                    if (variantFound)
                        _log?.Log($"WAR star authoritative {c.Number} -> base={starBaseRaw}/{starTrim} JP qty={qtyVariant}", LogCategories.QtyVariant);
                    else
                        _log?.Log($"WAR star authoritative {c.Number} attempted base={starBaseRaw}/{starTrim} JP (flex) defaulting 0", LogCategories.QtyVariant);
                }
                if (c.Quantity != qtyVariant) { cards[i] = c with { Quantity = qtyVariant }; updated++; }
                continue;
            }
            string numTokenCard = c.Number.Split('/')[0];
            int parenIndex = numTokenCard.IndexOf('(');
            if (parenIndex > 0) numTokenCard = numTokenCard.Substring(0, parenIndex);
            string baseNum = numTokenCard;
            string trimmed = baseNum.TrimStart('0'); if (trimmed.Length == 0) trimmed = "0";
            var setLower = c.Set.ToLowerInvariant();
            if (qtyDebug && i < 50)
            {
                        _log?.Log($"Card[{i}] set={c.Set} rawNum={c.Number} base={baseNum} trimmed={trimmed}", "QtyEnrich.Trace");
            }
            int qty; bool found = collection.Quantities.TryGetValue((setLower, baseNum), out qty);
            if (!found && !string.Equals(trimmed, baseNum, StringComparison.Ordinal))
                found = collection.Quantities.TryGetValue((setLower, trimmed), out qty);
            if (!found)
            {
                string candidate = baseNum;
                while (candidate.Length > 0 && !found && !char.IsDigit(candidate[^1]))
                {
                    candidate = candidate.Substring(0, candidate.Length - 1);
                    if (candidate.Length == 0) break;
                    found = collection.Quantities.TryGetValue((setLower, candidate), out qty);
                }
            }
            if (!found)
            {
                if (qtyDebug)
                {
                    var missKey = (setLower, baseNum);
                    bool shouldLog = true;
                    if (_missCounts.TryGetValue(missKey, out var count))
                    {
                        if (count >= MissLogLimitPerKey) shouldLog = false; else _missCounts[missKey] = count + 1;
                    }
                    else _missCounts[missKey] = 1;
                    if (shouldLog)
                    {
                        try
                        {
                            var sampleKeys = string.Join(", ", collection.Quantities.Keys.Where(k => k.Item1 == setLower).Take(25).Select(k => k.Item1+":"+k.Item2));
                            _log?.Log($"MISS {c.Set} {baseNum} (trim {trimmed}) sampleKeys={sampleKeys}", LogCategories.Collection);
                            Console.WriteLine($"[Collection][MISS] {c.Set} {baseNum} (trim {trimmed}) not found.");
                        }
                        catch (System.InvalidOperationException) { /* ignore sampling race */ }
                    }
                    if (unmatchedSamples.Count < 15)
                        unmatchedSamples.Add($"{c.Set}:{baseNum} trim={trimmed} raw='{c.Number}'");
                    unmatchedCount++;
                }
                if (c.Quantity != 0) { cards[i] = c with { Quantity = 0 }; updated++; }
                continue;
            }
            if (qty >= 0 && c.Quantity != qty) { cards[i] = c with { Quantity = qty }; updated++; }
        }
        if (updated > 0)
        {
            _log?.Log($"Quantities applied to {updated} faces", "Collection");
            if (qtyDebug) Console.WriteLine($"[Collection] Quantities applied to {updated} faces");
        }
        else if (qtyDebug)
        {
            Console.WriteLine("[Collection] No quantities applied (possible mismatch of keys).");
        }
        if (qtyDebug && unmatchedCount > 0)
        {
            _log?.Log($"MISS-SUMMARY unmatched={unmatchedCount} firstSamples={string.Join(" | ", unmatchedSamples)}", LogCategories.CollectionDebug);
            Console.WriteLine($"[Collection][MISS-SUMMARY] unmatched={unmatchedCount} (see Debug output for samples)");
        }
    }

    public void AdjustMfcQuantities(List<CardEntry> cards)
    {
    // Delegated to injected specialized service.
        _mfcAdjust.Adjust(cards);
    }

    // Convenience combined operation
    public void ApplyAll(CardCollectionData collection, List<CardEntry> cards)
    {
        EnrichQuantities(collection, cards);
    // Apply MFC display adjustment to the canonical card list first.
    AdjustMfcQuantities(cards);
        // Merge paired-number quantities (e.g., 296(361)) so ownership reflects either number having quantity
        MergePairedNumberQuantities(collection, cards);
    }

    private void MergePairedNumberQuantities(CardCollectionData collection, List<CardEntry> cards)
    {
        if (collection.Quantities.Count == 0) return;
        bool qtyDebug = _flagService?.QtyDebug ?? _flags.QtyDebug;
        int updates = 0;
        // Take a snapshot to avoid InvalidOperationException if the underlying list is structurally modified (background metadata resolution)
        var snapshot = cards.ToArray();
        int originalCount = snapshot.Length;
        for (int idx = 0; idx < snapshot.Length; idx++)
        {
            if (idx >= cards.Count) break; // underlying list shrank; abort safely
            var card = snapshot[idx];
            if (card.IsBackFace) continue;
            if (string.IsNullOrEmpty(card.Set) || string.IsNullOrEmpty(card.Number)) continue;
            // Only interested in entries created from the && pairing: EffectiveNumber is something like 296(361)
            // Pattern: primary(secondary) with both numeric
            string eff = card.EffectiveNumber;
            int open = eff.IndexOf('(');
            int close = eff.EndsWith(')') ? eff.Length - 1 : -1;
            if (open <= 0 || close <= open + 1) continue;
            var primary = eff.Substring(0, open);
            var secondary = eff.Substring(open + 1, close - open - 1);
            if (!IsAllDigits(primary) || !IsAllDigits(secondary)) continue;
            string setLower = card.Set.ToLowerInvariant();
            int primaryQty = card.Quantity;
            int secondaryQty = 0;
            // Look up secondary number in collection quantities (using trimmed-leading-zero fallback like enrichment does)
            string secondaryTrim = secondary.TrimStart('0'); if (secondaryTrim.Length == 0) secondaryTrim = "0";
            if (!collection.Quantities.TryGetValue((setLower, secondary), out secondaryQty))
            {
                if (!string.Equals(secondaryTrim, secondary, StringComparison.Ordinal) && collection.Quantities.TryGetValue((setLower, secondaryTrim), out int secondaryTrimQty))
                    secondaryQty = secondaryTrimQty;
            }
            // Treat negative (un-enriched) quantities as zero for paired display purposes so x(y) always renders
            if (primaryQty < 0) primaryQty = 0;
            if (secondaryQty < 0) secondaryQty = 0;
            int merged = primaryQty >= secondaryQty ? primaryQty : secondaryQty;
            // Always annotate paired component quantities if missing OR if values differ OR if merged value differs from stored Quantity
            if (merged != card.Quantity || card.PrimaryPairedQuantity != primaryQty || card.SecondaryPairedQuantity != secondaryQty)
            {
                if (idx < cards.Count) // defensive bounds check
                    cards[idx] = card with { Quantity = merged, PrimaryPairedQuantity = primaryQty, SecondaryPairedQuantity = secondaryQty };
                updates++;
                if (qtyDebug) _log?.Log($"PairedQtyMerge {card.Set} {eff} primary={primaryQty} secondary={secondaryQty} merged={merged}", LogCategories.QtyEnrich);
            }
            else if (card.PrimaryPairedQuantity == null && card.SecondaryPairedQuantity == null)
            {
                // Force population even when both are zero and merged already equals existing quantity.
                if (idx < cards.Count)
                    cards[idx] = card with { PrimaryPairedQuantity = primaryQty, SecondaryPairedQuantity = secondaryQty };
                if (qtyDebug) _log?.Log($"PairedQtyAnnotateOnly {card.Set} {eff} primary={primaryQty} secondary={secondaryQty}", LogCategories.QtyEnrich);
            }
        }
        if (qtyDebug && updates>0) _log?.Log($"PairedQtyMerge applied to {updates} entries", LogCategories.QtyEnrich);
        else if (qtyDebug)
        {
            // Diagnostics: list any paired EffectiveNumbers still missing component annotations
            var missing = cards.Where(c => c.EffectiveNumber.IndexOf('(') > 0 && c.EffectiveNumber.EndsWith(")") && c.PrimaryPairedQuantity == null).Take(10).ToList();
            if (missing.Count > 0)
                _log?.Log("PairedQtyDiag missingAnnotations=" + string.Join(",", missing.Select(m => m.Set+":"+m.EffectiveNumber)), LogCategories.QtyEnrich);
        }
    }

    private static bool IsAllDigits(string s)
    {
        for (int i=0;i<s.Length;i++) if (!char.IsDigit(s[i])) return false; return s.Length>0;
    }

    public int ToggleQuantity(
        CardSlot slot,
        string currentCollectionDir,
        CardCollectionData collection,
        List<CardEntry> cards,
        List<CardEntry> orderedFaces,
        Func<string,string,string,int?> resolveCardIdFromDb,
        Action<string> setStatus)
    {
        if (slot == null) { setStatus("No slot"); return -1; }
        if (slot.IsPlaceholderBack) { setStatus("Back face placeholder"); return -1; }
        if (string.IsNullOrEmpty(slot.Set) || string.IsNullOrEmpty(slot.Number)) { setStatus("No set/number"); return -1; }
        if (!collection.IsLoaded) { setStatus("Collection not loaded"); return -1; }

        string numToken = slot.Number.Split('/')[0];
        int parenIdx = numToken.IndexOf('('); if (parenIdx > 0) numToken = numToken.Substring(0, parenIdx);
        string baseNum = numToken;
        string trimmed = baseNum.TrimStart('0'); if (trimmed.Length == 0) trimmed = "0";
        string setLower = slot.Set.ToLowerInvariant();
        (int cardId, int? gatherer) foundEntry = default; bool indexFound = false;
        bool warStar = slot.Set.Equals("WAR", StringComparison.OrdinalIgnoreCase) && slot.Number.IndexOf('★') >= 0;
        string originalNumberWithStar = slot.Number;
        bool variantAttemptedEarly = false;
        if (warStar)
        {
            var starStripped = baseNum.Replace("★", string.Empty);
            if (starStripped.Length == 0) starStripped = "0";
            var starTrimmed = starStripped.TrimStart('0'); if (starTrimmed.Length == 0) starTrimmed = "0";
            int variantId;
            if (collection.TryGetVariantCardIdFlexible(slot.Set, starStripped, "Art JP", out variantId) ||
                collection.TryGetVariantCardIdFlexible(slot.Set, starTrimmed, "Art JP", out variantId) ||
                collection.TryGetVariantCardIdFlexible(slot.Set, starStripped, "JP", out variantId) ||
                collection.TryGetVariantCardIdFlexible(slot.Set, starTrimmed, "JP", out variantId))
            {
                foundEntry = (variantId, null); indexFound = true; baseNum = starStripped; trimmed = starTrimmed;
            }
            variantAttemptedEarly = true;
            if (!indexFound)
            {
                if (collection.MainIndex.TryGetValue((setLower, starStripped), out foundEntry)) { indexFound = true; baseNum = starStripped; trimmed = starTrimmed; }
                else if (!string.Equals(starTrimmed, starStripped, StringComparison.Ordinal) && collection.MainIndex.TryGetValue((setLower, starTrimmed), out foundEntry)) { indexFound = true; baseNum = starStripped; trimmed = starTrimmed; }
            }
        }
        if (!indexFound)
        {
            if (collection.MainIndex.TryGetValue((setLower, baseNum), out foundEntry)) indexFound = true;
            else if (!string.Equals(trimmed, baseNum, StringComparison.Ordinal) && collection.MainIndex.TryGetValue((setLower, trimmed), out foundEntry)) indexFound = true;
            else
            {
                string candidate = baseNum;
                while (candidate.Length > 0 && !indexFound && !char.IsDigit(candidate[^1]))
                {
                    candidate = candidate.Substring(0, candidate.Length - 1);
                    if (collection.MainIndex.TryGetValue((setLower, candidate), out foundEntry)) { indexFound = true; break; }
                }
            }
        }
        if (!indexFound && warStar && !variantAttemptedEarly)
        {
            var baseStar = baseNum.Replace("★", string.Empty);
            var baseStarTrim = baseStar.TrimStart('0'); if (baseStarTrim.Length == 0) baseStarTrim = "0";
            int variantId;
            if (collection.TryGetVariantCardIdFlexible(slot.Set, baseStar, "Art JP", out variantId) ||
                collection.TryGetVariantCardIdFlexible(slot.Set, baseStarTrim, "Art JP", out variantId) ||
                collection.TryGetVariantCardIdFlexible(slot.Set, baseStar, "JP", out variantId) ||
                collection.TryGetVariantCardIdFlexible(slot.Set, baseStarTrim, "JP", out variantId))
            { foundEntry = (variantId, null); indexFound = true; baseNum = baseStar; trimmed = baseStarTrim; }
        }
        if (!indexFound)
        {
            int? directId = resolveCardIdFromDb(slot.Set, baseNum, trimmed);
            if (directId == null) { setStatus("Card not found"); return -1; }
            foundEntry = (directId.Value, null);
        }
        int cardId = foundEntry.cardId;

        if (cardId < 0)
        {
            int? realId = resolveCardIdFromDb(slot.Set, baseNum, trimmed);
            if (realId.HasValue && realId.Value >= 0)
            {
                if (_flags.QtyDebug)
                    _log?.Log($"Synthetic->Real replaced {cardId} with {realId.Value} set={slot.Set} num={slot.Number} base={baseNum}/{trimmed}", LogCategories.CollectionDebug);
                cardId = realId.Value;
            }
            else
            {
                if (_flags.QtyDebug)
                    _log?.Log($"Synthetic NoRealId set={slot.Set} num={slot.Number} base={baseNum}/{trimmed} toggle will not persist", LogCategories.CollectionWarn);
            }
        }

        int logicalQty = slot.Quantity;
        bool isMfcFront = false;
        var entry = cards.FirstOrDefault(c => c.Set != null && string.Equals(c.Set, slot.Set, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Number.Split('/')[0], baseNum.Split('/')[0], StringComparison.OrdinalIgnoreCase) && c.Name == slot.Name);
        if (entry != null && entry.IsModalDoubleFaced && !entry.IsBackFace) { isMfcFront = true; logicalQty = slot.Quantity; }
        int newLogicalQty = !isMfcFront ? (logicalQty == 0 ? 1 : 0) : (logicalQty == 0 ? 1 : (logicalQty == 1 ? 2 : 0));
        bool isCustom = collection.CustomCards.Contains(cardId);
        bool qtyDebug = _flags.QtyDebug;
    if (qtyDebug) _log?.Log($"Toggle begin set={slot.Set} num={slot.Number} baseNum={baseNum} targetQty={newLogicalQty} isCustom={isCustom} warStar={warStar}", LogCategories.CollectionDebug);

        int? persistedQty = null;
        if (cardId >= 0 && _quantityRepo != null)
        {
            if (isCustom)
            {
                string mainDbPath = Path.Combine(currentCollectionDir, "mainDb.db");
                if (!File.Exists(mainDbPath)) { setStatus("mainDb missing"); return -1; }
                persistedQty = _quantityRepo.UpdateCustomCardQuantity(mainDbPath, cardId, newLogicalQty, _flags.QtyDebug);
                if (persistedQty == null) { setStatus("Write failed"); return -1; }
            }
            else
            {
                string collectionPath = Path.Combine(currentCollectionDir, "mtgstudio.collection");
                if (!File.Exists(collectionPath)) { setStatus("Collection file missing"); return -1; }
                persistedQty = _quantityRepo.UpsertStandardCardQuantity(collectionPath, cardId, newLogicalQty, _flags.QtyDebug);
                if (persistedQty == null) { setStatus("Write failed"); return -1; }
            }
        }

        if (persistedQty.HasValue && persistedQty.Value != newLogicalQty)
        {
            if (qtyDebug) _log?.Log($"Toggle mismatch CardId={cardId} intended={newLogicalQty} persisted={persistedQty.Value} set={slot.Set} num={slot.Number}", LogCategories.CollectionWarn);
            newLogicalQty = persistedQty.Value;
        }
        if (slot.Quantity != newLogicalQty) slot.Quantity = newLogicalQty;
        if (cardId < 0 && qtyDebug)
            _log?.Log($"Using synthetic cardId for UI-only update; value will revert on refresh set={slot.Set} num={slot.Number}", LogCategories.CollectionWarn);

        if (newLogicalQty > 0) collection.Quantities[(setLower, baseNum)] = newLogicalQty; else collection.Quantities.Remove((setLower, baseNum));
        if (!string.Equals(trimmed, baseNum, StringComparison.Ordinal))
        {
            if (newLogicalQty > 0) collection.Quantities[(setLower, trimmed)] = newLogicalQty; else collection.Quantities.Remove((setLower, trimmed));
        }
        // If baseNum contains trailing non-digit variant letters (e.g., 13a), also update the pure numeric base so enrichment maps future rebuilds.
        string numericBaseOnly = baseNum;
        while (numericBaseOnly.Length > 0 && !char.IsDigit(numericBaseOnly[^1])) numericBaseOnly = numericBaseOnly.Substring(0, numericBaseOnly.Length - 1);
        if (numericBaseOnly.Length > 0 && !string.Equals(numericBaseOnly, baseNum, StringComparison.OrdinalIgnoreCase))
        {
            if (newLogicalQty > 0) collection.Quantities[(setLower, numericBaseOnly)] = newLogicalQty; else collection.Quantities.Remove((setLower, numericBaseOnly));
        }
        // For WAR star variant cards, also update VariantQuantities so subsequent enrichment passes reflect new value without a full DB reload.
        if (warStar)
        {
            var starStripped = baseNum; // baseNum already star-stripped at this point
            var starTrimmed = trimmed;
            foreach (var mod in new[] { "art jp", "jp" })
            {
                if (newLogicalQty > 0)
                {
                    collection.VariantQuantities[(setLower, starStripped, mod)] = newLogicalQty;
                    collection.VariantQuantities[(setLower, starTrimmed, mod)] = newLogicalQty;
                }
                else
                {
                    collection.VariantQuantities.Remove((setLower, starStripped, mod));
                    collection.VariantQuantities.Remove((setLower, starTrimmed, mod));
                }
            }
        }
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (c.Set != null && string.Equals(c.Set, slot.Set, StringComparison.OrdinalIgnoreCase))
            {
                string cBase = c.Number.Split('/')[0];
                string cBaseStarless = cBase.IndexOf('★') >= 0 ? cBase.Replace("★", string.Empty) : cBase;
                string slotBaseForCompare = baseNum;
                // If the slot base (from DisplayNumber/EffectiveNumber) has trailing non-digits but the card's underlying number does not,
                // trim trailing non-digit characters for comparison (mirrors enrichment fallback logic).
                if (!string.Equals(cBase, slotBaseForCompare, StringComparison.OrdinalIgnoreCase))
                {
                    string trimmedVariant = slotBaseForCompare;
                    while (trimmedVariant.Length > 0 && !char.IsDigit(trimmedVariant[^1])) trimmedVariant = trimmedVariant.Substring(0, trimmedVariant.Length - 1);
                    if (trimmedVariant.Length > 0) slotBaseForCompare = trimmedVariant;
                }
                bool matches =
                    string.Equals(cBase, baseNum, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cBase.TrimStart('0'), trimmed, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cBaseStarless, baseNum, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cBaseStarless.TrimStart('0'), trimmed, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cBase, slotBaseForCompare, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cBase.TrimStart('0'), slotBaseForCompare.TrimStart('0'), StringComparison.OrdinalIgnoreCase);
                if (matches)
                {
                    if (c.Quantity != newLogicalQty)
                        cards[i] = c with { Quantity = newLogicalQty };
                }
            }
        }
        AdjustMfcQuantities(cards);
    for (int i = 0; i < orderedFaces.Count; i++)
        {
            var o = orderedFaces[i];
            if (o.Set != null && string.Equals(o.Set, slot.Set, StringComparison.OrdinalIgnoreCase))
            {
                string oBase = o.Number.Split('/')[0];
                string oBaseStarless = oBase.IndexOf('★') >= 0 ? oBase.Replace("★", string.Empty) : oBase;
                string slotBaseForCompare = baseNum;
                if (!string.Equals(oBase, slotBaseForCompare, StringComparison.OrdinalIgnoreCase))
                {
                    string trimmedVariant = slotBaseForCompare;
                    while (trimmedVariant.Length > 0 && !char.IsDigit(trimmedVariant[^1])) trimmedVariant = trimmedVariant.Substring(0, trimmedVariant.Length - 1);
                    if (trimmedVariant.Length > 0) slotBaseForCompare = trimmedVariant;
                }
                bool matches =
                    string.Equals(oBase, baseNum, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(oBase.TrimStart('0'), trimmed, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(oBaseStarless, baseNum, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(oBaseStarless.TrimStart('0'), trimmed, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(oBase, slotBaseForCompare, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(oBase.TrimStart('0'), slotBaseForCompare.TrimStart('0'), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(o.Number, originalNumberWithStar, StringComparison.OrdinalIgnoreCase);
                if (matches)
                {
                    var updated = cards.FirstOrDefault(c => c.Set != null && c.Set.Equals(o.Set, StringComparison.OrdinalIgnoreCase) && c.Number == o.Number && c.IsBackFace == o.IsBackFace);
                    if (updated != null && updated.Quantity != o.Quantity) orderedFaces[i] = updated;
                }
            }
            // Synchronize any remaining MFC front/back display split even if not the toggled set (ensures immediate UI correctness)
            if (o.IsModalDoubleFaced)
            {
                var m = cards.FirstOrDefault(c => c.IsModalDoubleFaced == o.IsModalDoubleFaced && c.IsBackFace == o.IsBackFace && c.Set == o.Set && c.Number == o.Number && c.Name == o.Name);
                if (m != null && m.Quantity != o.Quantity) orderedFaces[i] = m;
            }
        }
        setStatus($"Set {slot.Set} #{slot.Number} => {newLogicalQty}");
        return newLogicalQty;
    }
}
