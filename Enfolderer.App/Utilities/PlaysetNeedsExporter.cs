using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

using System.Text;
namespace Enfolderer.App.Utilities
{
    /// <summary>
    /// Exports a CSV indicating into which ORIGINAL (older) edition binder each card from a target set needing copies should go.
    /// Logic (generalized):
    /// 1. Identify all unique card names in the target set code.
    /// 2. For each name, sum owned qty across ALL printings (any set) via MtgsId->CollectionCards.
    /// 3. If owned < playsetSize, determine a target older edition ("origin edition") to file the card under (earliest non-target printing).
    ///    Strategy: choose the earliest printing outside DMR by minimum internal Id (proxy for earliest import); if none, fallback to DMR itself.
    /// 4. Output rows grouped (sort order) by target edition, then by source-set rarity, then Name.
    /// 5. Both the source-set rarity and the chosen placement (target edition) rarity are emitted so differences are visible.
    /// Assumptions:
    /// - CollectionCards.CardId == Cards.MtgsId.
    /// - Cards table has columns: Id, Name, SetCode, MtgsId, Rarity.
    /// - Using MIN(Id) as approximation for earliest printing if proper release date not available.
    /// </summary>
    public static class PlaysetNeedsExporter
    {
    public sealed record PlaysetNeed(string Name,
                                     string TargetEdition,
                                     string SourceSetRarity,
                                     string TargetEditionRarity,
                                     string CollectorNumber,
                                     string Color,
                                     int Have,
                                     int Need);

    public static string ExportPlaysetNeedsForSet(string setCode, string? outputPath = null, int playsetSize = 4, bool includeZeroNeeds = false, bool debugPlacement = false)
    {
            if (string.IsNullOrWhiteSpace(setCode)) throw new ArgumentException("Set code required", nameof(setCode));
            // Normalize user-provided set code: trim and keep an original form for messaging; use uppercase canonical internally
            setCode = setCode.Trim();
            string canonicalSetCode = setCode.ToUpperInvariant();
        bool sourceIsListSet = string.Equals(canonicalSetCode, "PLST", StringComparison.OrdinalIgnoreCase) || string.Equals(canonicalSetCode, "PLIST", StringComparison.OrdinalIgnoreCase);
            // Allow environment variable override
            if (!debugPlacement)
            {
                var env = Environment.GetEnvironmentVariable("ENFOLDERER_DEBUG_PLACEMENT");
                if (!string.IsNullOrWhiteSpace(env) && (env.Equals("1") || env.Equals("true", StringComparison.OrdinalIgnoreCase)))
                {
                    debugPlacement = true;
                }
            }
            string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string mainDbPath = Path.Combine(exeDir, "mainDb.db");
            string collectionPath = Path.Combine(exeDir, "mtgstudio.collection");
            if (!File.Exists(mainDbPath)) throw new FileNotFoundException("mainDb.db not found", mainDbPath);

            // Utilities for name normalization (moved to top so we can use during allRows population)
            static string NormalizeName(string n)
            {
                if (string.IsNullOrWhiteSpace(n)) return string.Empty;
                // Collapse spaced double slash variants and strip surrounding whitespace
                var collapsed = n.Replace(" // ", "/", StringComparison.OrdinalIgnoreCase)
                                 .Replace(" / ", "/", StringComparison.OrdinalIgnoreCase)
                                 .Replace("/ ", "/", StringComparison.OrdinalIgnoreCase)
                                 .Replace(" /", "/", StringComparison.OrdinalIgnoreCase);
                return collapsed.Trim();
            }
            static string FrontFace(string n)
            {
                var norm = NormalizeName(n);
                var idx = norm.IndexOf('/');
                return idx >= 0 ? norm.Substring(0, idx) : norm;
            }
            // Edition alias groups (keys normalized upper). Add more codes here if future discrepancies arise.
            var editionAliases = new List<HashSet<string>>
            {
                new HashSet<string>(new[]{"CHK","COK"}, StringComparer.OrdinalIgnoreCase), // Champions of Kamigawa (Scryfall=CHK, MtGS=COK)
                new HashSet<string>(new[]{"MIR","MR","MI"}, StringComparer.OrdinalIgnoreCase),  // Mirage (long/short/alt code)
                new HashSet<string>(new[]{"USG","US"}, StringComparer.OrdinalIgnoreCase)   // Urza's Saga (Scryfall=USG, DB=US)
            };
            bool EditionEquals(string a, string b)
            {
                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
                if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
                foreach (var grp in editionAliases)
                {
                    if (grp.Contains(a) && grp.Contains(b)) return true;
                }
                return false;
            }
            bool IsAliasOfSource(string candidateEdition, string sourceEdition)
            {
                return EditionEquals(candidateEdition, sourceEdition);
            }

            // Core set codes (older numbered core sets + modern core + special reprint cores) we want to deprioritize for placement
            var coreSetCodes = new HashSet<string>(new [] {
                // Classic numbered cores and later base sets
                "4E","5E","6E","7E","8E","9E","10E",
                // Magic 2010+ core sequence
                "M10","M11","M12","M13","M14","M15",
                // Later annual cores / pivot sets
                "M19","M20","M21","ORI", // ORI (Magic Origins) often filed with core sets for binder purposes
                // Premium reprint / broad supplemental 'core-like' sets we also want to deprioritize
                "A25","IMA","2XM","2X2","CM2","CMA","UMA","MM2","MM3","MMQ","MB1","MB2" // include some broad reprint products
            }, StringComparer.OrdinalIgnoreCase);

            // Fixed schema as provided: table "cards" columns: id, name, edition, rarity, MtgsId, Qty (Qty may be null / used earlier)
            var rarityByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var numberByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var colorByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var dmrInternalIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var normalizedSourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Track front-face names for multi-face source cards so we can include single-face DB rows (e.g., Budoka Gardener // Dokai ... -> Budoka Gardener)
            var frontFaceSourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var con = new SqliteConnection($"Data Source={mainDbPath};Mode=ReadOnly"))
            {
                con.Open();
                using var cmdR = con.CreateCommand();
                // Case-insensitive match on edition via COLLATE NOCASE so mixed-case entries unify
                cmdR.CommandText = "SELECT name, rarity, id, collectorNumberValue, color FROM cards WHERE edition = @pSet COLLATE NOCASE";
                cmdR.Parameters.AddWithValue("@pSet", canonicalSetCode);
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
                    normalizedSourceNames.Add(NormalizeName(name));
                    var ff = FrontFace(name);
                    if (!string.IsNullOrWhiteSpace(ff)) frontFaceSourceNames.Add(ff);
                }
            }

            if (rarityByName.Count == 0)
            {
                throw new InvalidOperationException($"No cards found for set code '{setCode}'.");
            }

            // Build quantities: for all cards whose Name is in DMR list aggregate total owned across all sets.
            var totals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var targetEditionByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var targetEditionRarityByName = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); // hoisted so accessible after data collection
            // Will hold all candidate printings for names in the source set for color fallback and placement logic
            var allRows = new List<(long Id, string Name, string Edition, int? MtgsId, string Rarity, string Color, string CollectorNumber)>();
            using (var con = new SqliteConnection($"Data Source={mainDbPath};Mode=ReadOnly"))
            {
                con.Open();
                using var cmdAll = con.CreateCommand();
                cmdAll.CommandText = "SELECT id, name, edition, MtgsId, rarity, color, collectorNumberValue FROM cards";
                using var rdr = cmdAll.ExecuteReader();
                while (rdr.Read())
                {
                    if (rdr.IsDBNull(1)) continue;
                    var nm = rdr.GetString(1);
                    var normNm = NormalizeName(nm);
                    // Accept row if:
                    //  - exact source name
                    //  - normalized form matches a normalized source name
                    //  - front-face form matches a front-face source (handles DB storing only front face)
                    var ffNm = FrontFace(nm);
                    if (!rarityByName.ContainsKey(nm)
                        && !normalizedSourceNames.Contains(normNm)
                        && !frontFaceSourceNames.Contains(nm)
                        && !frontFaceSourceNames.Contains(normNm)
                        && !frontFaceSourceNames.Contains(ffNm))
                        continue;
                    long idVal = rdr.IsDBNull(0) ? 0 : rdr.GetInt64(0);
                    string edition = rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2) ?? string.Empty;
                    int? mid = rdr.IsDBNull(3) ? (int?)null : rdr.GetInt32(3);
                    string rar = rdr.IsDBNull(4) ? string.Empty : rdr.GetString(4) ?? string.Empty;
                    string clr = rdr.IsDBNull(5) ? string.Empty : rdr.GetString(5) ?? string.Empty;
                    string colNum = rdr.IsDBNull(6) ? string.Empty : rdr.GetString(6) ?? string.Empty;
                    allRows.Add((idVal, nm, edition, mid, rar, clr, colNum));
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
                var excludedEditions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "AS", "A25", "CM2", "IMA", "MP2", "PLIST", "PRM", "PZ2",
                    "BD", "BR", "C19", "P2", "PJ21", "REN", "A22", "AFC", "ANB", "C14", "C20", "CMA", "DC", "UMA", "DRC", "CMM", "CLU", "DVD", "EMA",
                    "C16", "C17", "C18", "CMD", "DK", "GNT", "MAT", "ME2", "ME4", "P3", "PR", "PT", "S1", "U", "UNF", "VMA", "EOC", "FIC", "GVL", "WOT",
                    "2X2", "2XM", "40K", "A", "B", "BBD", "C13", "C15", "C21", "CC1", "CN2", "CNS", "DDN", "DDR", "E02", "G18", "GN3", "HBG", "J22", "V17",
                    "JMP", "ME3", "MIC", "MKC", "MOC", "MPR", "MPS", "PIP", "OTC", "PAGL", "PC2", "PCA", "SLD", "WOC", "ZNC", "PUMA", "BLC", "BRC", "SIR",
                    "CC2", "CM1" , "J25", "LCC", "LTC", "MB1", "MB2", "NEC", "NCC", "ONC", "PF25", "PZ1", "SPG", "SS3", "TDC", "V11", "VOC", "P30A" };

                // Determine target edition (earliest non-target edition, excluding specific codes) for each source set name.
                // If no name-based candidates found, perform generic fallback:
                // 1. Try front-face (for multi-face names containing ' // ')
                // 2. Try parsing the source collector number pattern PREFIX-REST to locate edition+collector number directly (name-independent)
                StreamWriter? debugLog = null;
                try
                {
                    if (debugPlacement)
                    {
                        string dbgPath = Path.Combine(AppContext.BaseDirectory, $"placement_debug_{canonicalSetCode.ToLowerInvariant()}.log");
                        debugLog = new StreamWriter(dbgPath, append: false, Encoding.UTF8);
                        debugLog.WriteLine($"# Placement debug log for {canonicalSetCode} generated {DateTime.Now:O}");
                    }

                    foreach (var name in rarityByName.Keys)
                {
                    string normalizedName = NormalizeName(name);

                    if (debugPlacement) debugLog?.WriteLine($"CARD: {name} | Detail=NormalizedName | Value={normalizedName}");
                    var candidates = allRows
                        .Where(r => NormalizeName(r.Name).Equals(normalizedName, StringComparison.OrdinalIgnoreCase)
                                    && !IsAliasOfSource(r.Edition, setCode)
                                    && !excludedEditions.Contains(r.Edition))
                        .OrderBy(r => r.Id)
                        .ToList();
                    // Keep a snapshot of the broad initial candidate pool (by normalized name) for later core override decisions
                    var initialBroadCandidates = candidates.ToList();
                    if (debugPlacement)
                    {
                        debugLog?.WriteLine($"CARD: {name} | Stage=InitialNormalizedMatch | Candidates={candidates.Count}");
                    }

                    // Multi-face normalization: retry using front-face segment
                    if (candidates.Count == 0 && (name.Contains(" // ") || name.Contains('/')))
                    {
                        var front = FrontFace(name);
                        candidates = allRows
                            .Where(r => FrontFace(r.Name).Equals(front, StringComparison.OrdinalIgnoreCase)
                                        && !IsAliasOfSource(r.Edition, setCode)
                                        && !excludedEditions.Contains(r.Edition))
                            .OrderBy(r => r.Id)
                            .ToList();
                        if (debugPlacement)
                        {
                            debugLog?.WriteLine($"CARD: {name} | Stage=FrontFaceMatch | Candidates={candidates.Count}");
                        }
                    }

                    // Edition + collector number inference if still none
                    if (candidates.Count == 0 && numberByName.TryGetValue(name, out var rawNumber) && !string.IsNullOrWhiteSpace(rawNumber))
                    {
                        var rawTrim = rawNumber.Trim();
                        int dash = rawTrim.IndexOf('-');
                        if (dash > 0)
                        {
                            string inferredEdition = rawTrim.Substring(0, dash).Trim();
                            string remainder = rawTrim.Substring(dash + 1).Trim();
                            if (debugPlacement) debugLog?.WriteLine($"CARD: {name} | Stage=EditionInference | InferredEdition={inferredEdition} | Remainder={remainder}");
                            if (!string.IsNullOrWhiteSpace(inferredEdition) && !string.IsNullOrWhiteSpace(remainder))
                            {
                                // Exact match on edition + full collector number
                                var exact = allRows
                                    .Where(r => EditionEquals(r.Edition, inferredEdition)
                                                && r.CollectorNumber.Equals(remainder, StringComparison.OrdinalIgnoreCase))
                                    .OrderBy(r => r.Id)
                                    .ToList();
                                if (exact.Count == 0)
                                {
                                    // Relaxed numeric core match (e.g., 296 matches 296a)
                                    var numericCore = new string(remainder.TakeWhile(char.IsDigit).ToArray());
                                    if (debugPlacement) debugLog?.WriteLine($"CARD: {name} | Stage=EditionInference | NumericCoreAttempt={numericCore}");
                                    if (!string.IsNullOrWhiteSpace(numericCore))
                                    {
                                        exact = allRows
                                            .Where(r => EditionEquals(r.Edition, inferredEdition)
                                                        && r.CollectorNumber.StartsWith(numericCore, StringComparison.OrdinalIgnoreCase))
                                            .OrderBy(r => r.Id)
                                            .ToList();
                                    }
                                }
                                if (exact.Count > 0)
                                {
                                    candidates = exact; // adopt these as candidates
                                    if (debugPlacement) debugLog?.WriteLine($"CARD: {name} | Stage=EditionInferenceResult | ExactOrNumericCandidates={candidates.Count}");
                                }
                                else if (sourceIsListSet)
                                {
                                    // New PLST fallback: use edition+collector to fetch canonical DB name then re-search by that name (normalized)
                                    var canonicalRow = allRows
                                        .FirstOrDefault(r => EditionEquals(r.Edition, inferredEdition)
                                                             && r.CollectorNumber.Equals(remainder, StringComparison.OrdinalIgnoreCase));
                                    if (canonicalRow.Id == 0)
                                    {
                                        // Try numeric core in case variant letter difference
                                        var numericCore = new string(remainder.TakeWhile(char.IsDigit).ToArray());
                                        if (!string.IsNullOrWhiteSpace(numericCore))
                                        {
                                            canonicalRow = allRows.FirstOrDefault(r => EditionEquals(r.Edition, inferredEdition)
                                                && r.CollectorNumber.StartsWith(numericCore, StringComparison.OrdinalIgnoreCase));
                                        }
                                    }
                                    if (canonicalRow.Id != 0)
                                    {
                                        var canonicalNorm = NormalizeName(canonicalRow.Name);
                                        // Gather every printing (any edition) whose normalized name matches canonicalNorm and is not source excluded
                                        var crossNameCandidates = allRows
                                            .Where(r => NormalizeName(r.Name).Equals(canonicalNorm, StringComparison.OrdinalIgnoreCase))
                                            .OrderBy(r => r.Id)
                                            .ToList();
                                        // Add all inferred edition printings first (ordered) then other editions (excluding source or excluded)
                                        candidates = crossNameCandidates
                                            .Where(r => EditionEquals(r.Edition, inferredEdition))
                                            .Concat(crossNameCandidates.Where(r => !r.Edition.Equals(inferredEdition, StringComparison.OrdinalIgnoreCase)
                                                                                   && !IsAliasOfSource(r.Edition, setCode)
                                                                                   && !excludedEditions.Contains(r.Edition)))
                                            .ToList();
                                        if (debugPlacement)
                                        {
                                            debugLog?.WriteLine($"CARD: {name} | Stage=PLSTCanonicalNameLookup | Canonical={canonicalRow.Name} | InferredEdition={inferredEdition} | NewCandidates={candidates.Count}");
                                        }
                                    }
                                }
                                else if (debugPlacement)
                                {
                                    debugLog?.WriteLine($"CARD: {name} | Stage=EditionInferenceResult | Candidates=0");
                                }
                            }
                        }
                    }

                    if (candidates.Count == 0)
                    {
                        // For PLST/PLIST we attempt a late inference using the source card's own collector number prefix before defaulting to PLST.
                        if (sourceIsListSet && numberByName.TryGetValue(name, out var srcListCol) && !string.IsNullOrWhiteSpace(srcListCol))
                        {
                            var trimmedList = srcListCol.Trim();
                            int dashIdx = trimmedList.IndexOf('-');
                            if (dashIdx > 0)
                            {
                                string inferredPref = trimmedList.Substring(0, dashIdx).Trim();
                                if (!string.IsNullOrWhiteSpace(inferredPref) && !EditionEquals(inferredPref, canonicalSetCode))
                                {
                                    // Choose earliest (by Id) non-excluded printing in the inferred edition (excluding alias-equal to source list set, though unlikely)
                                    var inferredPrint = allRows
                                        .Where(r => EditionEquals(r.Edition, inferredPref) && !IsAliasOfSource(r.Edition, canonicalSetCode) && !excludedEditions.Contains(r.Edition))
                                        .OrderBy(r => r.Id)
                                        .FirstOrDefault();
                                    if (inferredPrint.Id != 0)
                                    {
                                        targetEditionByName[name] = inferredPrint.Edition;
                                        targetEditionRarityByName[name] = string.IsNullOrWhiteSpace(inferredPrint.Rarity) ? rarityByName[name] : inferredPrint.Rarity;
                                        if (debugPlacement) debugLog?.WriteLine($"CARD: {name} | Stage=FallbackInferredFromPLST | ChosenEdition={inferredPrint.Edition} | FromPrefix={inferredPref}");
                                        continue;
                                    }
                                }
                            }
                        }
                        // Final fallback: stay in source set (PLST or normal set)
                        targetEditionByName[name] = canonicalSetCode;
                        targetEditionRarityByName[name] = rarityByName[name];
                        if (debugPlacement) debugLog?.WriteLine($"CARD: {name} | Stage=FallbackSource | ChosenEdition={canonicalSetCode}");
                        continue;
                    }
                    // Narrowing based on collector number pattern. For most sets prefer exact match; for List source sets prefer earliest (lowest numeric) printing in inferred edition.
                    if (numberByName.TryGetValue(name, out var srcColRaw) && !string.IsNullOrWhiteSpace(srcColRaw))
                    {
                        var trimmed = srcColRaw.Trim();
                        int dash = trimmed.IndexOf('-');
                        if (dash > 0)
                        {
                            string pref = trimmed[..dash].Trim();
                            string rest = trimmed[(dash + 1)..].Trim();
                            if (debugPlacement) debugLog?.WriteLine($"CARD: {name} | Stage=Narrowing | Pref={pref} | Rest={rest} | CandidateCount={candidates.Count}");
                            if (!string.IsNullOrWhiteSpace(pref) && !string.IsNullOrWhiteSpace(rest))
                            {
                                if (sourceIsListSet)
                                {
                                    // For PLST/PLIST choose the lowest numeric core candidate within the inferred edition.
                                    var inEdition = candidates.Where(c => EditionEquals(c.Edition, pref)).ToList();
                                    if (debugPlacement) debugLog?.WriteLine($"CARD: {name} | Stage=NarrowingListEditionSubset | InEditionCount={inEdition.Count}");
                                    if (inEdition.Count > 0)
                                    {
                                        int NumericCore(string cn)
                                        {
                                            if (string.IsNullOrWhiteSpace(cn)) return int.MaxValue;
                                            var digits = new string(cn.TakeWhile(char.IsDigit).ToArray());
                                            if (int.TryParse(digits, out var val)) return val; else return int.MaxValue;
                                        }
                                        // Order by numeric core, then by presence of variant letter 'a' (preferred), then by Id for determinism
                                        var chosenList = inEdition
                                            .OrderBy(c => NumericCore(c.CollectorNumber))
                                            .ThenByDescending(c => c.CollectorNumber.Any(ch => char.IsLetter(ch))) // prioritize those with letter (e.g., 150a) AFTER numeric core sort; descending so true before false
                                            .ThenBy(c => c.Id)
                                            .ToList();
                                        candidates = new List<(long Id, string Name, string Edition, int? MtgsId, string Rarity, string Color, string CollectorNumber)>{ chosenList.First() };
                                        if (debugPlacement) debugLog?.WriteLine($"CARD: {name} | Stage=NarrowingListChosen | ChosenCollector={candidates[0].CollectorNumber}");
                                    }
                                }
                                else
                                {
                                    var exactCandidate = candidates
                                        .FirstOrDefault(c => EditionEquals(c.Edition, pref)
                                                             && c.CollectorNumber.Equals(rest, StringComparison.OrdinalIgnoreCase));
                                    if (exactCandidate.Id != 0)
                                    {
                                        candidates = new List<(long Id, string Name, string Edition, int? MtgsId, string Rarity, string Color, string CollectorNumber)>{ exactCandidate };
                                        if (debugPlacement) debugLog?.WriteLine($"CARD: {name} | Stage=NarrowingExact | Collector={rest}");
                                    }
                                    else
                                    {
                                        string numericCore = new string(rest.TakeWhile(char.IsDigit).ToArray());
                                        if (debugPlacement) debugLog?.WriteLine($"CARD: {name} | Stage=NarrowingNumericCoreAttempt | NumericCore={numericCore}");
                                        if (!string.IsNullOrWhiteSpace(numericCore))
                                        {
                                            var numericVariant = candidates
                                                .FirstOrDefault(c => EditionEquals(c.Edition, pref)
                                                                     && c.CollectorNumber.StartsWith(numericCore, StringComparison.OrdinalIgnoreCase));
                                            if (numericVariant.Id != 0)
                                            {
                                                candidates = new List<(long Id, string Name, string Edition, int? MtgsId, string Rarity, string Color, string CollectorNumber)>{ numericVariant };
                                                if (debugPlacement) debugLog?.WriteLine($"CARD: {name} | Stage=NarrowingNumericVariant | ChosenCollector={numericVariant.CollectorNumber}");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    var chosen = candidates.First();
                    // Core override: if narrowing forced a single core-set candidate but earlier there existed any non-core candidate, prefer earliest non-core.
                    if (initialBroadCandidates.Count > 0 && coreSetCodes.Contains(chosen.Edition))
                    {
                        var nonCore = initialBroadCandidates.Where(c => !coreSetCodes.Contains(c.Edition)).OrderBy(c => c.Id).FirstOrDefault();
                        if (nonCore.Id != 0)
                        {
                            if (debugPlacement) debugLog?.WriteLine($"CARD: {name} | Stage=CorePreferenceOverrideExact | OldCore={chosen.Edition} | NewNonCore={nonCore.Edition}");
                            chosen = nonCore;
                        }
                    }
                    // If more than one candidate remains (rare except earlier broad match), prefer non-core sets over core sets
                    if (candidates.Count > 1)
                    {
                        var before = candidates.First();
                        candidates = candidates
                            .OrderBy(c => coreSetCodes.Contains(c.Edition) ? 1 : 0) // non-core (0) before core (1)
                            .ThenBy(c => c.Id) // then earliest Id for determinism
                            .ToList();
                        if (debugPlacement && (before.Id != candidates.First().Id))
                        {
                            debugLog?.WriteLine($"CARD: {name} | Stage=CorePreferenceReorder | NewFirstEdition={candidates.First().Edition} | OldFirstEdition={before.Edition}");
                        }
                        chosen = candidates.First();
                    }
                    if (debugPlacement)
                    {
                        debugLog?.WriteLine($"CARD: {name} | Stage=Chosen | Edition={chosen.Edition} | Collector={chosen.CollectorNumber} | SourceIsList={sourceIsListSet}");
                    }
                    targetEditionByName[name] = chosen.Edition;
                    targetEditionRarityByName[name] = string.IsNullOrWhiteSpace(chosen.Rarity) ? rarityByName[name] : chosen.Rarity;
                    if ((!colorByName.ContainsKey(name) || string.IsNullOrWhiteSpace(colorByName[name])) && !string.IsNullOrWhiteSpace(chosen.Color))
                    {
                        colorByName[name] = chosen.Color;
                    }
                }
                }
                finally
                {
                    debugLog?.Dispose();
                }
            }

            // Collect all entries (even if complete) so we can output required sections.
            var allEntries = new List<PlaysetNeed>();
            foreach (var kvp in rarityByName)
            {
                string name = kvp.Key;
                totals.TryGetValue(name, out var have);
                int need = playsetSize - have;
                string targetEdition = targetEditionByName.TryGetValue(name, out var te) ? te : canonicalSetCode;
                numberByName.TryGetValue(name, out var num);
                colorByName.TryGetValue(name, out var col);
                if (string.IsNullOrWhiteSpace(col))
                {
                    // Fallback 1: color from chosen target edition printing if present
                    var targetColor = allRows
                        .Where(r => r.Edition.Equals(targetEdition, StringComparison.OrdinalIgnoreCase) && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        .Select(r => r.Color)
                        .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
                    if (string.IsNullOrWhiteSpace(targetColor) && name.Contains(" // "))
                    {
                        var front = name.Split(new[]{" // "}, StringSplitOptions.None)[0];
                        targetColor = allRows
                            .Where(r => r.Edition.Equals(targetEdition, StringComparison.OrdinalIgnoreCase)
                                        && r.Name.Split(new[]{" // "}, StringSplitOptions.None)[0].Equals(front, StringComparison.OrdinalIgnoreCase))
                            .Select(r => r.Color)
                            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
                    }
                    if (!string.IsNullOrWhiteSpace(targetColor))
                    {
                        col = targetColor;
                    }
                    else
                    {
                        // Fallback 2: earliest (by Id) non-empty color among any printing of this name
                        var earliestColor = allRows
                            .Where(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.Color))
                            .OrderBy(r => r.Id)
                            .Select(r => r.Color)
                            .FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(earliestColor)) col = earliestColor;
                    }
                }
                string targetRarity = targetEdition.Equals(canonicalSetCode, StringComparison.OrdinalIgnoreCase)
                    ? kvp.Value
                    : (targetEditionRarityByName.TryGetValue(name, out var tr) && !string.IsNullOrWhiteSpace(tr) ? tr : kvp.Value);
                allEntries.Add(new PlaysetNeed(name, targetEdition, kvp.Value, targetRarity, num ?? string.Empty, col ?? string.Empty, have, need > 0 ? need : 0));
            }

            // Partition
            var needsWithPlacement = allEntries.Where(e => e.Need > 0 && !string.Equals(e.TargetEdition, canonicalSetCode, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.TargetEdition, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.SourceSetRarity, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var needsNoPlacement = allEntries.Where(e => e.Need > 0 && string.Equals(e.TargetEdition, canonicalSetCode, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.SourceSetRarity, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var completePlaysets = allEntries.Where(e => e.Need == 0)
                .OrderBy(e => e.SourceSetRarity, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                outputPath = Path.Combine(AppContext.BaseDirectory, $"{canonicalSetCode.ToLowerInvariant()}_playset_needs_{stamp}.csv");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using (var sw = new StreamWriter(outputPath))
            {
                sw.WriteLine("TargetEdition;TargetRarity;SourceRarity;Number;Color;Name;Have;Need");

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
                    sw.WriteLine(string.Join(';', new [] {
                        Csv(n: n.TargetEdition), Csv(n: n.TargetEditionRarity), Csv(n: n.SourceSetRarity), Csv(n: n.CollectorNumber), Csv(n: n.Color), Csv(n: n.Name), n.Have.ToString(CultureInfo.InvariantCulture), n.Need.ToString(CultureInfo.InvariantCulture)
                    }));
                }

                // Section 2: Needs but no placement (fallback to DMR)
                sw.WriteLine();
                sw.WriteLine("# SECTION: NEEDS (NO PLACEMENT FOUND)" );
                foreach (var n in needsNoPlacement)
                {
                    sw.WriteLine(string.Join(';', new [] {
                        Csv(n: n.TargetEdition), Csv(n: n.TargetEditionRarity), Csv(n: n.SourceSetRarity), Csv(n: n.CollectorNumber), Csv(n: n.Color), Csv(n: n.Name), n.Have.ToString(CultureInfo.InvariantCulture), n.Need.ToString(CultureInfo.InvariantCulture)
                    }));
                }

                // Section 3: Already complete
                sw.WriteLine();
                sw.WriteLine("# SECTION: COMPLETE (HAVE FULL PLAYSET)" );
                foreach (var n in completePlaysets)
                {
                    sw.WriteLine(string.Join(';', new [] {
                        Csv(n: n.TargetEdition), Csv(n: n.TargetEditionRarity), Csv(n: n.SourceSetRarity), Csv(n: n.CollectorNumber), Csv(n: n.Color), Csv(n: n.Name), n.Have.ToString(CultureInfo.InvariantCulture), n.Need.ToString(CultureInfo.InvariantCulture)
                    }));
                }
            }
            return outputPath!;
    }

    // Backwards compatibility method preserving original entry point for DMR specifically.
    public static string ExportDmrPlaysetNeeds(string? outputPath = null, int playsetSize = 4, bool includeZeroNeeds = false)
        => ExportPlaysetNeedsForSet("DMR", outputPath, playsetSize, includeZeroNeeds);

        private static string Csv(string n)
        {
            if (n == null) return string.Empty;
            if (n.Contains('"')) n = n.Replace("\"", "\"\"");
            if (n.IndexOfAny(new[]{';','"','\n','\r'}) >= 0) return "\"" + n + "\""; // quote if field contains delimiter or special chars
            return n;
        }
    }
}
