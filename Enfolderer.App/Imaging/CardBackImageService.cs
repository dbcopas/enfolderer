using System;
using System.IO;
using System.Linq;
using System.Diagnostics;

namespace Enfolderer.App.Imaging;

public sealed class CardBackImageService
{
    private static readonly string[] CandidateNames =
    {
        // Prefer PNG first so users can supply a transparent-corner image.
        "Magic_card_back.png"};

    public string? Resolve(string? currentCollectionDir, bool logIfMissing)
    {
        var dirs = new[]
        {
            currentCollectionDir,
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Enfolderer"),
            Directory.Exists(Path.Combine(AppContext.BaseDirectory, "images")) ? Path.Combine(AppContext.BaseDirectory, "images") : null
        };
        foreach (var dir in dirs.Where(d => !string.IsNullOrWhiteSpace(d)))
        {
            try
            {
                foreach (var name in CandidateNames)
                {
                    var path = Path.Combine(dir!, name);
                    if (File.Exists(path))
                    {
                        // Debug.WriteLine($"[CardBackImageService] Found back image candidate: {path}");
                        return path;
                    }
                }
            }
            catch { }
        }
        if (logIfMissing) System.Diagnostics.Debug.WriteLine("[BackImage] No local card back image found.");
        // Fallback to embedded resource (PNG preferred) if present
        var embedded = GetEmbeddedFallback();
        if (!string.IsNullOrEmpty(embedded))
        {
            //Debug.WriteLine($"[CardBackImageService] Using embedded card back resource: {embedded}");
            return embedded;
        }
        //Debug.WriteLine("[CardBackImageService] No embedded card back resource found.");
        return null;
    }

    public static string? GetEmbeddedFallback()
    {
        // Prefer PNG then JPG
        string[] candidates =
        {
            "pack://application:,,,/Enfolderer.App;component/Magic_card_back.png",
            "pack://application:,,,/Enfolderer.App;component/Magic_card_back.jpg"
        };
        foreach (var c in candidates)
        {
            try
            {
                // Probe via Application.GetResourceStream (relative form) by stripping pack prefix
                var rel = c.Contains("component/") ? c[(c.IndexOf("component/", StringComparison.OrdinalIgnoreCase)+10)..] : null;
                if (!string.IsNullOrEmpty(rel))
                {
                    var uri = new Uri(rel, UriKind.Relative);
                    var streamInfo = System.Windows.Application.GetResourceStream(uri);
                    if (streamInfo != null)
                    {
                        return c;
                    }
                }
            }
            catch { }
        }
        return null;
    }
}
