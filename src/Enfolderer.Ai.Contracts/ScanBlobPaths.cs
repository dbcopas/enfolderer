using System.Text;

namespace Enfolderer.Ai.Contracts;

/// <summary>
/// Blob layout shared by the API (which mints upload SAS tokens) and the worker (which reads the
/// uploads and writes crops). Kept here so the two tiers cannot drift apart.
/// </summary>
public static class ScanBlobPaths
{
    /// <summary>Container holding client uploads. Team A's agent gets read-only access here.</summary>
    public const string ScansContainer = "scans";

    /// <summary>Container holding per-card crops produced by the worker.</summary>
    public const string CropsContainer = "crops";

    private const int MaxStemLength = 64;

    /// <summary>Builds the blob name (within <see cref="ScansContainer"/>) for an uploaded image.</summary>
    public static string BuildScanBlobName(string jobId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("jobId is required.", nameof(jobId));
        return $"{SanitizeSegment(jobId)}/{SanitizeFileName(fileName)}";
    }

    /// <summary>Builds the blob name (within <see cref="CropsContainer"/>) for one card crop.</summary>
    public static string BuildCropBlobName(string jobId, int cardIndex) =>
        $"{SanitizeSegment(jobId)}/card-{cardIndex:D3}.png";

    /// <summary>
    /// Reduces a caller-supplied file name to a safe <c>stem.ext</c> form. Path separators, traversal
    /// segments and unusual characters are stripped so a hostile file name cannot escape the job prefix.
    /// </summary>
    public static string SanitizeFileName(string? fileName)
    {
        var candidate = fileName ?? string.Empty;
        // Strip any directory component regardless of the separator style the caller used.
        var lastSlash = candidate.LastIndexOfAny(['/', '\\']);
        if (lastSlash >= 0) candidate = candidate[(lastSlash + 1)..];

        var dot = candidate.LastIndexOf('.');
        var stem = dot > 0 ? candidate[..dot] : candidate;
        var ext = dot > 0 ? candidate[(dot + 1)..] : string.Empty;

        stem = SanitizeSegment(stem);
        if (stem.Length == 0) stem = "scan";
        if (stem.Length > MaxStemLength) stem = stem[..MaxStemLength];

        ext = SanitizeSegment(ext).ToLowerInvariant();
        if (ext.Length is 0 or > 8) ext = "jpg";

        return $"{stem}.{ext}";
    }

    /// <summary>Keeps only characters that are unambiguous in a blob path segment.</summary>
    public static string SanitizeSegment(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_') sb.Append(c);
        }
        return sb.ToString();
    }
}
