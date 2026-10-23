using System.Globalization;
using System.Text;

namespace Enfolderer.Ai.Contracts;

/// <summary>
/// Blob layout shared by the API (which relays client uploads) and the worker (which reads the
/// uploads and writes crops). Kept here so the two tiers cannot drift apart.
/// </summary>
public static class ScanBlobPaths
{
    /// <summary>Container holding client uploads. Team A's agent gets read-only access here.</summary>
    public const string ScansContainer = "scans";

    /// <summary>Container holding per-card crops produced by the worker.</summary>
    public const string CropsContainer = "crops";
    public const string OutputsContainer = "outputs";

    public static string BuildJobPrefix(string jobId, DateTimeOffset createdAt, string firstName)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("jobId is required.", nameof(jobId));
        var name = SanitizePersonName(firstName);
        if (name.Length == 0) throw new ArgumentException("A usable first name is required.", nameof(firstName));
        return $"{name}-{createdAt.UtcDateTime.ToString("yyMMdd-HHmm", CultureInfo.InvariantCulture)}-{SanitizeSegment(jobId)}";
    }

    public static string SanitizePersonName(string? value)
    {
        var name = SanitizeSegment(value?.Normalize(NormalizationForm.FormD));
        return name.Length > MaxStemLength ? name[..MaxStemLength] : name;
    }

    public static string BuildAnnotatedBlobPath(string jobId) =>
        $"{OutputsContainer}/{SanitizeSegment(jobId)}/priced.png";

    public static string BuildAnnotatedBlobPath(ScanJobDocument job) =>
        BuildAnnotatedBlobPath(job.BlobPrefix ?? job.JobId);

    public static string BuildCsvBlobPath(ScanJobDocument job) =>
        $"{OutputsContainer}/{SanitizeSegment(job.BlobPrefix ?? job.JobId)}/cards.csv";

    public static string BuildEvidenceBlobPath(ScanJobDocument job, int? cardIndex = null) =>
        $"{OutputsContainer}/{SanitizeSegment(job.BlobPrefix ?? job.JobId)}/"
        + (cardIndex is { } index ? $"identification/card-{index:D3}.json" : "identification.json");

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

    public static string BuildCropBlobName(ScanJobDocument job, int cardIndex) =>
        BuildCropBlobName(job.BlobPrefix ?? job.JobId, cardIndex);

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

    /// <summary>
    /// Image content type implied by a file name or blob path. The worker needs it when uploading
    /// the image to a Foundry project, where the part's content type is all the service has to go
    /// on. Anything unrecognised is reported as JPEG, which is what the upload endpoint already
    /// defaults an extensionless name to.
    /// </summary>
    public static string ContentTypeFor(string? fileNameOrPath)
    {
        var dot = (fileNameOrPath ?? string.Empty).LastIndexOf('.');
        var ext = dot >= 0 ? fileNameOrPath![(dot + 1)..].ToLowerInvariant() : string.Empty;
        return ext switch
        {
            "png" => "image/png",
            "gif" => "image/gif",
            "bmp" => "image/bmp",
            "webp" => "image/webp",
            "heic" => "image/heic",
            _ => "image/jpeg"
        };
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
