using System.Text.Json;
using System.Text.Json.Serialization;

namespace Enfolderer.Ai.Contracts;

/// <summary>
/// Lifecycle of a scan job. Persisted as a string so the value is stable across schema versions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScanJobStatus
{
    /// <summary>Job record created; the client has a write-only SAS but has not uploaded yet.</summary>
    Pending,
    /// <summary>Client confirmed the upload and the job has been queued for the worker.</summary>
    Uploaded,
    /// <summary>Team A's boundary agent is locating card quadrilaterals.</summary>
    DetectingBoundaries,
    /// <summary>Team B's per-game identification agents are running over the crops.</summary>
    Identifying,
    /// <summary>Result document is available.</summary>
    Completed,
    /// <summary>Terminal failure; <see cref="ScanJobDocument.Error"/> explains why.</summary>
    Failed
}

/// <summary>Games that have a dedicated identification agent (or a reserved growth slot).</summary>
public static class CardGames
{
    public const string Magic = "mtg";
    public const string Pokemon = "pokemon";
    public const string YuGiOh = "yugioh";
    public const string Lorcana = "lorcana";
    public const string Unknown = "unknown";

    /// <summary>Games that currently have a live identification agent.</summary>
    public static readonly string[] Supported = [Magic, Pokemon];

    public static bool IsSupported(string? game) =>
        game is not null && Array.Exists(Supported, g => string.Equals(g, game, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string? game) => (game ?? Unknown).Trim().ToLowerInvariant() switch
    {
        "mtg" or "magic" or "magicthegathering" or "magic-the-gathering" => Magic,
        "pokemon" or "pokémon" or "ptcg" => Pokemon,
        "yugioh" or "yu-gi-oh" or "ygo" => YuGiOh,
        "lorcana" => Lorcana,
        _ => Unknown
    };
}

/// <summary>A point in source-image pixel coordinates.</summary>
public sealed record ImagePoint(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y);

/// <summary>
/// A card boundary as returned by the boundary agent: four corners in source-image pixel
/// coordinates, ordered top-left, top-right, bottom-right, bottom-left of the card as printed
/// (so an upside-down card in the photo still reports its own top-left first).
/// </summary>
public sealed record CardQuad(
    [property: JsonPropertyName("points")] IReadOnlyList<ImagePoint> Points)
{
    public const int RequiredPointCount = 4;

    public bool IsValid => Points is { Count: RequiredPointCount };

    /// <summary>Axis-aligned bounding box (x, y, width, height) that contains the quad.</summary>
    public (double X, double Y, double Width, double Height) BoundingBox()
    {
        if (!IsValid) throw new InvalidOperationException("Quad must have exactly four points.");
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in Points)
        {
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        return (minX, minY, maxX - minX, maxY - minY);
    }
}

/// <summary>One card detected by Team A and (where possible) identified by Team B.</summary>
public sealed record IdentifiedCard
{
    /// <summary>Zero-based index of the card within the image, in boundary-agent output order.</summary>
    [JsonPropertyName("index")] public int Index { get; init; }

    /// <summary>Card geometry in the source image. Always present — it comes from the boundary agent.</summary>
    [JsonPropertyName("quad")] public CardQuad? Quad { get; init; }

    /// <summary>Normalized game identifier; see <see cref="CardGames"/>.</summary>
    [JsonPropertyName("game")] public string Game { get; init; } = CardGames.Unknown;

    /// <summary>Set / expansion code, e.g. "BRO" or "sv1". Null when identification failed.</summary>
    [JsonPropertyName("set")] public string? Set { get; init; }

    [JsonPropertyName("collectorNumber")] public string? CollectorNumber { get; init; }

    [JsonPropertyName("name")] public string? Name { get; init; }

    /// <summary>Two-letter language code of the printing, e.g. "en".</summary>
    [JsonPropertyName("language")] public string? Language { get; init; }

    /// <summary>Printing finish, e.g. "nonfoil", "foil", "etched".</summary>
    [JsonPropertyName("finish")] public string? Finish { get; init; }

    /// <summary>Confidence in the identification, 0..1.</summary>
    [JsonPropertyName("confidence")] public double Confidence { get; init; }

    /// <summary>
    /// Which agent produced this entry, e.g. "cardgeo/CardBoundaryAgent" when only geometry is
    /// known, or "cardid/MtgCardIdAgent" once identified. Central to the Foundry boundary demo.
    /// </summary>
    [JsonPropertyName("agent")] public string? Agent { get; init; }

    /// <summary>Populated when this specific card could not be identified.</summary>
    [JsonPropertyName("error")] public string? Error { get; init; }

    /// <summary>True when the card has enough catalogue data to be exported.</summary>
    [JsonIgnore]
    public bool IsIdentified =>
        !string.IsNullOrWhiteSpace(Set) && !string.IsNullOrWhiteSpace(CollectorNumber) && !string.IsNullOrWhiteSpace(Name);
}

/// <summary>Versioned result document returned by <c>GET /jobs/{id}</c> once a job completes.</summary>
public sealed record ScanResultDocument
{
    /// <summary>Current schema version. Bump only for breaking changes.</summary>
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    [JsonPropertyName("jobId")] public string JobId { get; init; } = string.Empty;

    [JsonPropertyName("status")] public ScanJobStatus Status { get; init; }

    [JsonPropertyName("imageWidth")] public int ImageWidth { get; init; }

    [JsonPropertyName("imageHeight")] public int ImageHeight { get; init; }

    [JsonPropertyName("cards")] public IReadOnlyList<IdentifiedCard> Cards { get; init; } = [];
}

/// <summary>Job state persisted in Cosmos DB. Partition key is <c>/jobId</c>.</summary>
public sealed record ScanJobDocument
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;

    /// <summary>Partition key. Always equal to <see cref="Id"/>.</summary>
    [JsonPropertyName("jobId")] public string JobId { get; init; } = string.Empty;

    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = ScanResultDocument.CurrentSchemaVersion;

    [JsonPropertyName("status")] public ScanJobStatus Status { get; init; } = ScanJobStatus.Pending;

    /// <summary>Blob path of the uploaded image, e.g. <c>scans/{jobId}/page1.jpg</c>.</summary>
    [JsonPropertyName("blobPath")] public string BlobPath { get; init; } = string.Empty;

    /// <summary>Optional caller hint restricting which game agents run.</summary>
    [JsonPropertyName("gameHint")] public string? GameHint { get; init; }

    [JsonPropertyName("cardsDetected")] public int CardsDetected { get; init; }

    [JsonPropertyName("cardsIdentified")] public int CardsIdentified { get; init; }

    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")] public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("error")] public string? Error { get; init; }

    [JsonPropertyName("result")] public ScanResultDocument? Result { get; init; }

    /// <summary>Cosmos time-to-live in seconds; keeps demo data from accumulating.</summary>
    [JsonPropertyName("ttl")] public int Ttl { get; init; } = 7 * 24 * 60 * 60;
}

/// <summary>Request body for <c>POST /jobs</c>.</summary>
public sealed record CreateJobRequest
{
    /// <summary>Original file name; only the extension and a sanitized stem are used.</summary>
    [JsonPropertyName("fileName")] public string FileName { get; init; } = "scan.jpg";

    /// <summary>Optional game hint, see <see cref="CardGames"/>.</summary>
    [JsonPropertyName("gameHint")] public string? GameHint { get; init; }
}

/// <summary>Response for <c>POST /jobs</c>: where and how to upload the image.</summary>
public sealed record CreateJobResponse
{
    [JsonPropertyName("jobId")] public string JobId { get; init; } = string.Empty;

    /// <summary>Full write-only SAS URL for the blob the client must PUT the image to.</summary>
    [JsonPropertyName("uploadUrl")] public string UploadUrl { get; init; } = string.Empty;

    [JsonPropertyName("blobPath")] public string BlobPath { get; init; } = string.Empty;

    [JsonPropertyName("uploadExpiresAt")] public DateTimeOffset UploadExpiresAt { get; init; }
}

/// <summary>Response for <c>GET /jobs/{id}</c>.</summary>
public sealed record JobStatusResponse
{
    [JsonPropertyName("jobId")] public string JobId { get; init; } = string.Empty;

    [JsonPropertyName("status")] public ScanJobStatus Status { get; init; }

    [JsonPropertyName("cardsDetected")] public int CardsDetected { get; init; }

    [JsonPropertyName("cardsIdentified")] public int CardsIdentified { get; init; }

    [JsonPropertyName("error")] public string? Error { get; init; }

    /// <summary>Populated only when <see cref="Status"/> is <see cref="ScanJobStatus.Completed"/>.</summary>
    [JsonPropertyName("result")] public ScanResultDocument? Result { get; init; }
}

/// <summary>Shared serializer settings so every tier agrees on the wire format.</summary>
public static class ScanJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
}
