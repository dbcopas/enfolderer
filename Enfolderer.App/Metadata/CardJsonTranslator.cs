using System;
using System.Diagnostics;
using System.Text.Json;
using Enfolderer.App.Imaging;
using Enfolderer.App.Core;

namespace Enfolderer.App.Metadata;

/// <summary>
/// Translates Scryfall card JSON into CardEntry + registers image/layout data.
/// (Logic extracted from former BinderViewModel.FetchCardMetadataAsync)
/// </summary>
public static class CardJsonTranslator
{
    private static readonly string[] PhysicallyTwoSidedLayouts = new[]{"transform","modal_dfc","battle","double_faced_token","double_faced_card","prototype","reversible_card"};
    private static readonly string[] SingleFaceMultiLayouts = new[]{"split","aftermath","adventure","meld","flip","leveler","saga","class","plane","planar","scheme","vanguard","token","emblem","art_series"};

    public static CardEntry? Translate(JsonElement root, string setCode, string number, string? overrideName)
    {
        try
        {
            string? displayName = overrideName;
            string? frontRaw = null; string? backRaw = null; bool isMfc = false;
            string? frontImg = null; string? backImg = null;
            bool hasRootImageUris = root.TryGetProperty("image_uris", out var rootImgs);
            string? layout = null; if (root.TryGetProperty("layout", out var layoutProp) && layoutProp.ValueKind==JsonValueKind.String) layout = layoutProp.GetString();
            bool physTwoSided = layout != null && Array.Exists(PhysicallyTwoSidedLayouts, l => l.Equals(layout, StringComparison.OrdinalIgnoreCase));
            bool forcedSingle = layout != null && Array.Exists(SingleFaceMultiLayouts, l => l.Equals(layout, StringComparison.OrdinalIgnoreCase));
            if (root.TryGetProperty("card_faces", out var faces) && faces.ValueKind==JsonValueKind.Array && faces.GetArrayLength() >= 2)
            {
                var f0 = faces[0]; var f1 = faces[1]; int faceCount = faces.GetArrayLength(); bool facesDistinctArt = false;
                try
                {
                    if (f0.TryGetProperty("illustration_id", out var ill0) && f1.TryGetProperty("illustration_id", out var ill1) && ill0.ValueKind==JsonValueKind.String && ill1.ValueKind==JsonValueKind.String && ill0.GetString()!=ill1.GetString())
                        facesDistinctArt = true;
                } catch { }
                bool forceAllTwo = Environment.GetEnvironmentVariable("ENFOLDERER_FORCE_TWO_SIDED_ALL_FACES") == "1";
                bool heuristicTwo = !forcedSingle && (physTwoSided || (!hasRootImageUris) || facesDistinctArt || (layout==null && faceCount==2));
                bool treatTwo = physTwoSided || forceAllTwo || heuristicTwo;
                if (!treatTwo && !forcedSingle)
                    Debug.WriteLine($"[MFC Heuristic] Unexpected single-face classification for {setCode} {number} layout={layout} faces={faceCount} rootImgs={hasRootImageUris} distinctArt={facesDistinctArt}");
                if (treatTwo)
                {
                    frontRaw = f0.TryGetProperty("name", out var f0Name) && f0Name.ValueKind==JsonValueKind.String ? f0Name.GetString() : null;
                    backRaw  = f1.TryGetProperty("name", out var f1Name) && f1Name.ValueKind==JsonValueKind.String ? f1Name.GetString() : null;
                    isMfc = true;
                    if (displayName == null) displayName = $"{frontRaw} ({backRaw})";
                    if (f0.TryGetProperty("image_uris", out var f0Imgs))
                    {
                        if (f0Imgs.TryGetProperty("normal", out var f0Norm) && f0Norm.ValueKind==JsonValueKind.String) frontImg = f0Norm.GetString();
                        else if (f0Imgs.TryGetProperty("large", out var f0Large) && f0Large.ValueKind==JsonValueKind.String) frontImg = f0Large.GetString();
                    }
                    if (f1.TryGetProperty("image_uris", out var f1Imgs))
                    {
                        if (f1Imgs.TryGetProperty("normal", out var f1Norm) && f1Norm.ValueKind==JsonValueKind.String) backImg = f1Norm.GetString();
                        else if (f1Imgs.TryGetProperty("large", out var f1Large) && f1Large.ValueKind==JsonValueKind.String) backImg = f1Large.GetString();
                    }
                    if (frontImg == null && hasRootImageUris)
                    {
                        if (rootImgs.TryGetProperty("normal", out var rootNorm) && rootNorm.ValueKind==JsonValueKind.String) frontImg = rootNorm.GetString();
                        else if (rootImgs.TryGetProperty("large", out var rootLarge) && rootLarge.ValueKind==JsonValueKind.String) frontImg = rootLarge.GetString();
                    }
                    if (backImg == null && frontImg != null) backImg = frontImg;
                }
                else
                {
                    if (displayName == null && root.TryGetProperty("name", out var nSplit) && nSplit.ValueKind==JsonValueKind.String) displayName = nSplit.GetString();
                    if (hasRootImageUris)
                    {
                        if (rootImgs.TryGetProperty("normal", out var rootNorm2) && rootNorm2.ValueKind==JsonValueKind.String) frontImg = rootNorm2.GetString();
                        else if (rootImgs.TryGetProperty("large", out var rootLarge2) && rootLarge2.ValueKind==JsonValueKind.String) frontImg = rootLarge2.GetString();
                    }
                    else if (f0.TryGetProperty("image_uris", out var f0Imgs2))
                    {
                        if (f0Imgs2.TryGetProperty("normal", out var f0Norm2) && f0Norm2.ValueKind==JsonValueKind.String) frontImg = f0Norm2.GetString();
                        else if (f0Imgs2.TryGetProperty("large", out var f0Large2) && f0Large2.ValueKind==JsonValueKind.String) frontImg = f0Large2.GetString();
                    }
                }
            }
            else
            {
                if (displayName == null && root.TryGetProperty("name", out var nprop) && nprop.ValueKind==JsonValueKind.String) displayName = nprop.GetString();
                if (root.TryGetProperty("image_uris", out var singleImgs))
                {
                    if (singleImgs.TryGetProperty("normal", out var singleNorm) && singleNorm.ValueKind==JsonValueKind.String) frontImg = singleNorm.GetString();
                    else if (singleImgs.TryGetProperty("large", out var singleLarge) && singleLarge.ValueKind==JsonValueKind.String) frontImg = singleLarge.GetString();
                }
            }
            if (string.IsNullOrWhiteSpace(displayName)) displayName = number;
            CardImageUrlStore.Set(setCode, number, frontImg, backImg);
            CardLayoutStore.Set(setCode, number, layout);
            return new CardEntry(displayName!, number, setCode, isMfc, false, frontRaw, backRaw);
        }
        catch { return null; }
    }
}
