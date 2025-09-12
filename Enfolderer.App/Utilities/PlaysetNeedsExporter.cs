using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Enfolderer.App.Utilities
{
    /// <summary>
    /// Exports a CSV indicating into which ORIGINAL (older) edition binder each DMR card needing copies should go.
    /// Logic:
    /// 1. Identify all unique DMR card names (SetCode = 'DMR').
    /// 2. For each name, sum owned qty across ALL printings (any SetCode) via MtgsId->CollectionCards.
    /// 3. If owned < playsetSize, determine a target older edition ("origin edition") to file the card under.
    ///    Strategy: choose the earliest printing outside DMR by minimum internal Id (proxy for earliest import); if none, fallback to DMR itself.
    /// 4. Output rows grouped (sort order) by target edition, then by DMR rarity, then Name.
    /// 5. Rarity reported is the DMR rarity to satisfy the requested grouping by rarity within each edition section.
    /// Assumptions:
    /// - CollectionCards.CardId == Cards.MtgsId.
    /// - Cards table has columns: Id, Name, SetCode, MtgsId, Rarity.
    /// - Using MIN(Id) as approximation for earliest printing if proper release date not available.
    /// </summary>
    public static class PlaysetNeedsExporter
    {
    public sealed record PlaysetNeed(string Name, string TargetEdition, string DmrRarity, string CollectorNumber, string Color, int Have, int Need);

    public static string ExportDmrPlaysetNeeds(string? outputPath = null, int playsetSize = 4, bool includeZeroNeeds = false)
        {
            string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string mainDbPath = Path.Combine(exeDir, "mainDb.db");
            string collectionPath = Path.Combine(exeDir, "mtgstudio.collection");
            if (!File.Exists(mainDbPath)) throw new FileNotFoundException("mainDb.db not found", mainDbPath);

            // Fixed schema as provided: table "cards" columns: id, name, edition, rarity, MtgsId, Qty (Qty may be null / used earlier)
            var rarityByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var numberByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var colorByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dmrInternalIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using (var con = new SqliteConnection($"Data Source={mainDbPath};Mode=ReadOnly"))
            {
                con.Open();
                using var cmdR = con.CreateCommand();
                cmdR.CommandText = "SELECT name, rarity, id, collectorNumberValue, color FROM cards WHERE edition='DMR'";
                using var rdr = cmdR.ExecuteReader();
                while (rdr.Read())
                {
                    var name = rdr.IsDBNull(0) ? null : rdr.GetString(0);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var rarity = rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1) ?? string.Empty;
                    if (!rarityByName.ContainsKey(name)) rarityByName[name] = rarity;
                    if (!dmrInternalIds.ContainsKey(name)) dmrInternalIds[name] = rdr.IsDBNull(2) ? 0 : rdr.GetInt64(2);
                    if (!numberByName.ContainsKey(name)) numberByName[name] = rdr.IsDBNull(3) ? string.Empty : rdr.GetString(3) ?? string.Empty;
                    if (!colorByName.ContainsKey(name)) colorByName[name] = rdr.IsDBNull(4) ? string.Empty : rdr.GetString(4) ?? string.Empty;
                }
            }

            if (rarityByName.Count == 0)
            {
                throw new InvalidOperationException("No DMR cards found (SetCode='DMR').");
            }

            // Build quantities: for all cards whose Name is in DMR list aggregate total owned across all sets.
            var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var targetEditionByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var con = new SqliteConnection($"Data Source={mainDbPath};Mode=ReadOnly"))
            {
                con.Open();
                using var cmdAll = con.CreateCommand();
                cmdAll.CommandText = "SELECT id, name, edition, MtgsId FROM cards";
                using var rdr = cmdAll.ExecuteReader();
                var allRows = new List<(long Id, string Name, string Edition, int? MtgsId)>();
                while (rdr.Read())
                {
                    if (rdr.IsDBNull(1)) continue;
                    var nm = rdr.GetString(1);
                    if (!rarityByName.ContainsKey(nm)) continue; // only process names appearing in DMR
                    long idVal = rdr.IsDBNull(0) ? 0 : rdr.GetInt64(0);
                    string edition = rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2) ?? string.Empty;
                    int? mid = rdr.IsDBNull(3) ? (int?)null : rdr.GetInt32(3);
                    allRows.Add((idVal, nm, edition, mid));
                }
                // Load collection quantities into dictionary for fast lookup
                var qtyByMtgs = new Dictionary<int, int>();
                if (File.Exists(collectionPath))
                {
                    using var conCol = new SqliteConnection($"Data Source={collectionPath};Mode=ReadOnly");
                    conCol.Open();
                    using var cmdCol = conCol.CreateCommand();
                    cmdCol.CommandText = "SELECT CardId, Qty FROM CollectionCards";
                    using var rCol = cmdCol.ExecuteReader();
                    while (rCol.Read())
                    {
                        if (rCol.IsDBNull(0) || rCol.IsDBNull(1)) continue;
                        int cid = rCol.GetInt32(0); // this is MtgsId by rule
                        int q = rCol.GetInt32(1);
                        qtyByMtgs[cid] = q;
                    }
                }

                foreach (var row in allRows)
                {
                    if (!totals.ContainsKey(row.Name)) totals[row.Name] = 0;
                    if (row.MtgsId.HasValue && qtyByMtgs.TryGetValue(row.MtgsId.Value, out var qv)) totals[row.Name] += qv;
                }
                // Editions we do not want to place cards into as target binders
                var excludedEditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BD", "BR", "C19", "P2", "PJ21", "REN" };

                // Determine target edition (earliest non-DMR edition, excluding specific codes) for each DMR name
                foreach (var name in rarityByName.Keys)
                {
                    var candidates = allRows
                        .Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                    && !string.Equals(r.Edition, "DMR", StringComparison.OrdinalIgnoreCase)
                                    && !excludedEditions.Contains(r.Edition))
                        .ToList();
                    if (candidates.Count == 0)
                    {
                        targetEditionByName[name] = "DMR"; // fallback if only DMR
                        continue;
                    }
                    var earliest = candidates.OrderBy(r => r.Id).First();
                    targetEditionByName[name] = earliest.Edition;
                }
            }

            // Collect all entries (even if complete) so we can output required sections.
            var allEntries = new List<PlaysetNeed>();
            foreach (var kvp in rarityByName)
            {
                string name = kvp.Key;
                totals.TryGetValue(name, out var have);
                int need = playsetSize - have;
                string targetEdition = targetEditionByName.TryGetValue(name, out var te) ? te : "DMR";
                numberByName.TryGetValue(name, out var num);
                colorByName.TryGetValue(name, out var col);
                allEntries.Add(new PlaysetNeed(name, targetEdition, kvp.Value, num ?? string.Empty, col ?? string.Empty, have, need > 0 ? need : 0));
            }

            // Partition
            var needsWithPlacement = allEntries.Where(e => e.Need > 0 && !string.Equals(e.TargetEdition, "DMR", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.TargetEdition, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.DmrRarity, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var needsNoPlacement = allEntries.Where(e => e.Need > 0 && string.Equals(e.TargetEdition, "DMR", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.DmrRarity, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var completePlaysets = allEntries.Where(e => e.Need == 0)
                .OrderBy(e => e.DmrRarity, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                outputPath = Path.Combine(AppContext.BaseDirectory, $"dmr_playset_needs_{stamp}.csv");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using (var sw = new StreamWriter(outputPath))
            {
                sw.WriteLine("TargetEdition,Rarity,Number,Color,Name,Have,Need");

                // Section 1: Needs with placement
                sw.WriteLine("# SECTION: NEEDS WITH PLACEMENT");
                string? currentEdition = null;
                foreach (var n in needsWithPlacement)
                {
                    if (!string.Equals(currentEdition, n.TargetEdition, StringComparison.OrdinalIgnoreCase))
                    {
                        currentEdition = n.TargetEdition;
                        sw.WriteLine($"# Edition: {currentEdition}");
                    }
                    sw.WriteLine(string.Join(',', new [] {
                        Csv(n: n.TargetEdition), Csv(n: n.DmrRarity), Csv(n: n.CollectorNumber), Csv(n: n.Color), Csv(n: n.Name), n.Have.ToString(CultureInfo.InvariantCulture), n.Need.ToString(CultureInfo.InvariantCulture)
                    }));
                }

                // Section 2: Needs but no placement (fallback to DMR)
                sw.WriteLine();
                sw.WriteLine("# SECTION: NEEDS (NO PLACEMENT FOUND)" );
                foreach (var n in needsNoPlacement)
                {
                    sw.WriteLine(string.Join(',', new [] {
                        Csv(n: n.TargetEdition), Csv(n: n.DmrRarity), Csv(n: n.CollectorNumber), Csv(n: n.Color), Csv(n: n.Name), n.Have.ToString(CultureInfo.InvariantCulture), n.Need.ToString(CultureInfo.InvariantCulture)
                    }));
                }

                // Section 3: Already complete
                sw.WriteLine();
                sw.WriteLine("# SECTION: COMPLETE (HAVE FULL PLAYSET)" );
                foreach (var n in completePlaysets)
                {
                    sw.WriteLine(string.Join(',', new [] {
                        Csv(n: n.TargetEdition), Csv(n: n.DmrRarity), Csv(n: n.CollectorNumber), Csv(n: n.Color), Csv(n: n.Name), n.Have.ToString(CultureInfo.InvariantCulture), n.Need.ToString(CultureInfo.InvariantCulture)
                    }));
                }
            }
            return outputPath!;
        }

        private static string Csv(string n)
        {
            if (n == null) return string.Empty;
            if (n.Contains('"')) n = n.Replace("\"", "\"\"");
            if (n.IndexOfAny(new[]{',','"','\n','\r'}) >= 0) return "\"" + n + "\"";
            return n;
        }
    }
}
