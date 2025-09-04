using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using Enfolderer.App.Collection;
using Enfolderer.App.Core;
using Microsoft.Data.Sqlite;
using Enfolderer.App.Core.Abstractions;

namespace Enfolderer.App.Quantity;

/// <summary>
/// Encapsulates quantity enrichment, MFC display adjustment, and toggle persistence logic.
/// Extracted from MainWindow.xaml.cs (Phase 2 refactor).
/// </summary>
public sealed class CardQuantityService
{
    private readonly IRuntimeFlags _flags;
    public CardQuantityService(IRuntimeFlags? flags = null) { _flags = flags ?? RuntimeFlags.Default; }

    public void EnrichQuantities(CardCollectionData collection, List<CardEntry> cards)
    {
        bool qtyDebug = _flags.QtyDebug;
        if (collection.Quantities.Count == 0)
        {
            if (qtyDebug)
            {
                string reason = collection.IsLoaded ? "loaded-but-empty (no >0 Qty values?)" : "collection-not-loaded";
                var firstFive = string.Join(", ", cards.Take(5).Select(c => c.Set+":"+c.Number));
                Debug.WriteLine($"[QtyEnrich] Skip: Quantities empty - {reason}. Sample cards: {firstFive}");
                Console.WriteLine($"[QtyEnrich] Skip: Quantities empty - {reason}. Sample cards: {firstFive}");
            }
            return;
        }
        int updated = 0;
        int unmatchedCount = 0;
        List<string> unmatchedSamples = new();
        if (qtyDebug)
        {
            Debug.WriteLine($"[QtyEnrich] Start: quantityKeys={collection.Quantities.Count} cards={cards.Count}");
            Console.WriteLine($"[QtyEnrich] Start: quantityKeys={collection.Quantities.Count} cards={cards.Count}");
            try
            {
                var firstSets = collection.Quantities.Keys
                    .GroupBy(k => k.Item1)
                    .Take(5)
                    .Select(g => g.Key + ":" + string.Join("|", g.Take(8).Select(t => t.Item2)));
                Debug.WriteLine("[QtyEnrich][KeysSample] " + string.Join(" || ", firstSets));
            }
            catch { }
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
                        System.Diagnostics.Debug.WriteLine($"[Collection][VARIANT] WAR star authoritative {c.Number} -> base={starBaseRaw}/{starTrim} JP qty={qtyVariant}");
                    else
                        System.Diagnostics.Debug.WriteLine($"[Collection][VARIANT-MISS] WAR star authoritative {c.Number} attempted base={starBaseRaw}/{starTrim} JP (flex) defaulting 0");
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
                Debug.WriteLine($"[QtyEnrich][Trace] Card[{i}] set={c.Set} rawNum={c.Number} base={baseNum} trimmed={trimmed}");
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
                    try
                    {
                        var sampleKeys = string.Join(", ", collection.Quantities.Keys.Where(k => k.Item1 == setLower).Take(25).Select(k => k.Item1+":"+k.Item2));
                        Debug.WriteLine($"[Collection][MISS] {c.Set} {baseNum} (trim {trimmed}) not found. Sample keys for set: {sampleKeys}");
                        Console.WriteLine($"[Collection][MISS] {c.Set} {baseNum} (trim {trimmed}) not found.");
                        if (unmatchedSamples.Count < 15)
                        {
                            unmatchedSamples.Add($"{c.Set}:{baseNum} trim={trimmed} raw='{c.Number}'");
                        }
                        unmatchedCount++;
                    }
                    catch { }
                }
                if (c.Quantity != 0) { cards[i] = c with { Quantity = 0 }; updated++; }
                continue;
            }
            if (qty >= 0 && c.Quantity != qty) { cards[i] = c with { Quantity = qty }; updated++; }
        }
        if (updated > 0)
        {
            Debug.WriteLine($"[Collection] Quantities applied to {updated} faces");
            if (qtyDebug) Console.WriteLine($"[Collection] Quantities applied to {updated} faces");
        }
        else if (qtyDebug)
        {
            Console.WriteLine("[Collection] No quantities applied (possible mismatch of keys).");
        }
        if (qtyDebug && unmatchedCount > 0)
        {
            Debug.WriteLine($"[Collection][MISS-SUMMARY] unmatched={unmatchedCount} firstSamples={string.Join(" | ", unmatchedSamples)}");
            Console.WriteLine($"[Collection][MISS-SUMMARY] unmatched={unmatchedCount} (see Debug output for samples)");
        }
    }

    public void AdjustMfcQuantities(List<CardEntry> cards)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            var front = cards[i];
            if (!front.IsModalDoubleFaced || front.IsBackFace) continue;
            int q = front.Quantity; if (q < 0) continue;
            int frontDisplay, backDisplay;
            if (q <= 0) { frontDisplay = 0; backDisplay = 0; }
            else if (q == 1) { frontDisplay = 1; backDisplay = 0; }
            else { frontDisplay = 2; backDisplay = 2; }
            if (front.Quantity != frontDisplay) cards[i] = front with { Quantity = frontDisplay };
            int backIndex = -1;
            if (i + 1 < cards.Count)
            {
                var candidate = cards[i + 1];
                if (candidate.IsModalDoubleFaced && candidate.IsBackFace && candidate.Set == front.Set && candidate.Number == front.Number)
                    backIndex = i + 1;
            }
            if (backIndex == -1)
            {
                for (int j = i + 1; j < cards.Count; j++)
                {
                    var cand = cards[j];
                    if (cand.IsModalDoubleFaced && cand.IsBackFace && cand.Set == front.Set && cand.Number == front.Number)
                    { backIndex = j; break; }
                }
            }
            if (backIndex >= 0)
            {
                var back = cards[backIndex];
                if (back.Quantity != backDisplay) cards[backIndex] = back with { Quantity = backDisplay };
            }
        }

        var modalGroups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int gi = 0; gi < cards.Count; gi++)
        {
            var c = cards[gi];
            bool treatAsModal = c.IsModalDoubleFaced || (!c.IsBackFace && !string.IsNullOrEmpty(c.FrontRaw) && !string.IsNullOrEmpty(c.BackRaw));
            if (!treatAsModal) continue;
            string setKey = c.Set ?? string.Empty;
            string numKey = c.Number?.Split('/')[0] ?? string.Empty;
            string composite = setKey + "|" + numKey;
            if (!modalGroups.TryGetValue(composite, out var list)) { list = new List<int>(); modalGroups[composite] = list; }
            list.Add(gi);
        }
        foreach (var list in modalGroups.Values)
        {
            if (list.Count != 2) continue;
            var a = cards[list[0]]; var b = cards[list[1]];
            if (a.IsBackFace || b.IsBackFace) continue;
            int qLogical = Math.Max(a.Quantity, b.Quantity);
            int frontDisplay, backDisplay;
            if (qLogical <= 0) { frontDisplay = 0; backDisplay = 0; }
            else if (qLogical == 1) { frontDisplay = 1; backDisplay = 0; }
            else { frontDisplay = 2; backDisplay = 2; }
            if (a.Quantity != frontDisplay) cards[list[0]] = a with { Quantity = frontDisplay };
            if (b.Quantity != backDisplay) cards[list[1]] = b with { Quantity = backDisplay };
            if (_flags.QtyDebug)
                Debug.WriteLine($"[MFC][FallbackAdjust] Applied heuristic split (broad) set={a.Set} num={a.Number} q={qLogical} front={frontDisplay} back={backDisplay} flags aMfc={a.IsModalDoubleFaced} bMfc={b.IsModalDoubleFaced}");
        }

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
                    System.Diagnostics.Debug.WriteLine($"[Quantity][Synthetic->Real] Replaced synthetic cardId {cardId} with real {realId.Value} set={slot.Set} num={slot.Number} base={baseNum}/{trimmed}");
                cardId = realId.Value;
            }
            else
            {
                if (_flags.QtyDebug)
                    System.Diagnostics.Debug.WriteLine($"[Quantity][Synthetic][NoRealId] set={slot.Set} num={slot.Number} base={baseNum}/{trimmed} toggle will not persist.");
            }
        }

        int logicalQty = slot.Quantity;
        bool isMfcFront = false;
        var entry = cards.FirstOrDefault(c => c.Set != null && string.Equals(c.Set, slot.Set, StringComparison.OrdinalIgnoreCase) && string.Equals(c.Number.Split('/')[0], baseNum.Split('/')[0], StringComparison.OrdinalIgnoreCase) && c.Name == slot.Name);
        if (entry != null && entry.IsModalDoubleFaced && !entry.IsBackFace) { isMfcFront = true; logicalQty = slot.Quantity; }
        int newLogicalQty = !isMfcFront ? (logicalQty == 0 ? 1 : 0) : (logicalQty == 0 ? 1 : (logicalQty == 1 ? 2 : 0));
        bool isCustom = collection.CustomCards.Contains(cardId);
        bool qtyDebug = _flags.QtyDebug;
        if (qtyDebug) System.Diagnostics.Debug.WriteLine($"[Quantity][Toggle] Begin set={slot.Set} num={slot.Number} baseNum={baseNum} targetQty={newLogicalQty} isCustom={isCustom} warStar={warStar}");

        int? persistedQty = null;
        if (cardId >= 0 && isCustom)
        {
            string mainDbPath = Path.Combine(currentCollectionDir, "mainDb.db");
            if (!File.Exists(mainDbPath)) { setStatus("mainDb missing"); return -1; }
            try
            {
                using var conMain = new SqliteConnection($"Data Source={mainDbPath}");
                conMain.Open();
                using var cmd = conMain.CreateCommand();
                cmd.CommandText = "UPDATE Cards SET Qty=@q WHERE id=@id";
                cmd.Parameters.AddWithValue("@q", newLogicalQty);
                cmd.Parameters.AddWithValue("@id", cardId);
                cmd.ExecuteNonQuery();
                using var verify = conMain.CreateCommand();
                verify.CommandText = "SELECT Qty FROM Cards WHERE id=@id";
                verify.Parameters.AddWithValue("@id", cardId);
                var obj = verify.ExecuteScalar();
                if (obj != null && obj != DBNull.Value) persistedQty = Convert.ToInt32(obj);
            }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[CustomQty] mainDb write failed: {ex.Message}"); setStatus("Write failed"); return -1; }
        }
        else if (cardId >= 0 && !isCustom)
        {
            string collectionPath = Path.Combine(currentCollectionDir, "mtgstudio.collection");
            if (!File.Exists(collectionPath)) { setStatus("Collection file missing"); return -1; }
            try
            {
                using var con = new SqliteConnection($"Data Source={collectionPath}");
                con.Open();
                using (var cmd = con.CreateCommand())
                {
                    cmd.CommandText = "UPDATE CollectionCards SET Qty=@q WHERE CardId=@id";
                    cmd.Parameters.AddWithValue("@q", newLogicalQty);
                    cmd.Parameters.AddWithValue("@id", cardId);
                    int rows = cmd.ExecuteNonQuery();
                    if (_flags.QtyDebug)
                        System.Diagnostics.Debug.WriteLine($"[Quantity][DB] UPDATE rows={rows} cardId={cardId} qty={newLogicalQty}");
                    if (rows == 0)
                    {
                        using var ins = con.CreateCommand();
                        ins.CommandText = @"INSERT INTO CollectionCards 
                            (CardId,Qty,Used,BuyAt,SellAt,Price,Needed,Excess,Target,ConditionId,Foil,Notes,Storage,DesiredId,[Group],PrintTypeId,Buy,Sell,Added)
                            VALUES (@id,@q,0,0.0,0.0,0.0,0,0,0,0,0,'','',0,'',1,0,0,@added)";
                        ins.Parameters.AddWithValue("@id", cardId);
                        ins.Parameters.AddWithValue("@q", newLogicalQty);
                        var added = DateTime.Now.ToString("s").Replace('T',' ');
                        ins.Parameters.AddWithValue("@added", added);
                        try 
                        { 
                            int insRows = ins.ExecuteNonQuery();
                            if (insRows == 0) System.Diagnostics.Debug.WriteLine($"[Collection] Insert failed for CardId {cardId}");
                            else if (_flags.QtyDebug)
                                System.Diagnostics.Debug.WriteLine($"[Quantity][DB] INSERT rows={insRows} cardId={cardId} qty={newLogicalQty}");
                        } 
                        catch (Exception exIns) 
                        { 
                            System.Diagnostics.Debug.WriteLine($"[Collection] Insert exception for CardId {cardId}: {exIns.Message}");
                        }
                    }
                    using var verify = con.CreateCommand();
                    verify.CommandText = "SELECT Qty FROM CollectionCards WHERE CardId=@id";
                    verify.Parameters.AddWithValue("@id", cardId);
                    var obj = verify.ExecuteScalar();
                    if (obj != null && obj != DBNull.Value) persistedQty = Convert.ToInt32(obj);
                }
            }
            catch (Exception ex)
            { System.Diagnostics.Debug.WriteLine($"[Collection] Toggle write failed: {ex.Message}"); setStatus("Write failed"); return -1; }
        }

        if (persistedQty.HasValue && persistedQty.Value != newLogicalQty)
        {
            if (qtyDebug) System.Diagnostics.Debug.WriteLine($"[Quantity][Mismatch] CardId={cardId} intended={newLogicalQty} persisted={persistedQty.Value} set={slot.Set} num={slot.Number}");
            newLogicalQty = persistedQty.Value;
        }
        if (slot.Quantity != newLogicalQty) slot.Quantity = newLogicalQty;
        if (cardId < 0 && qtyDebug)
            System.Diagnostics.Debug.WriteLine($"[Quantity][Warning] Using synthetic cardId for UI-only update; value will revert on refresh set={slot.Set} num={slot.Number}");

        if (newLogicalQty > 0) collection.Quantities[(setLower, baseNum)] = newLogicalQty; else collection.Quantities.Remove((setLower, baseNum));
        if (!string.Equals(trimmed, baseNum, StringComparison.Ordinal))
        {
            if (newLogicalQty > 0) collection.Quantities[(setLower, trimmed)] = newLogicalQty; else collection.Quantities.Remove((setLower, trimmed));
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
                if (string.Equals(cBase, baseNum, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cBase.TrimStart('0'), trimmed, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cBaseStarless, baseNum, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cBaseStarless.TrimStart('0'), trimmed, StringComparison.OrdinalIgnoreCase))
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
                if (string.Equals(oBase, baseNum, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(oBase.TrimStart('0'), trimmed, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(oBaseStarless, baseNum, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(oBaseStarless.TrimStart('0'), trimmed, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(o.Number, originalNumberWithStar, StringComparison.OrdinalIgnoreCase))
                {
                    var updated = cards.FirstOrDefault(c => c.Set != null && c.Set.Equals(o.Set, StringComparison.OrdinalIgnoreCase) && c.Number == o.Number && c.IsBackFace == o.IsBackFace);
                    if (updated != null && updated.Quantity != o.Quantity) orderedFaces[i] = updated;
                }
            }
        }
        setStatus($"Set {slot.Set} #{slot.Number} => {newLogicalQty}");
        return newLogicalQty;
    }
}
