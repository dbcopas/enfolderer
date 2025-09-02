using System;
using System.IO;
using System.Linq;

namespace Enfolderer.App.Imaging;

public sealed class CardBackImageService
{
    private static readonly string[] CandidateNames =
    {
        "Magic_card_back.jpg","magic_card_back.jpg","card_back.jpg","back.jpg","Magic_card_back.jpeg","Magic_card_back.png"
    };

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
                    if (File.Exists(path)) return path;
                }
            }
            catch { }
        }
        if (logIfMissing) System.Diagnostics.Debug.WriteLine("[BackImage] No local card back image found.");
    // Fallback to embedded resource (pack URI) if present
    return "pack://application:,,,/Enfolderer.App;component/Magic_card_back.jpg";
    }
}
