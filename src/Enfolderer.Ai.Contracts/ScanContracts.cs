using System.Text.Json;
using System.Text.Json.Serialization;

namespace Enfolderer.Ai.Contracts;

/// <summary>
/// Lifecycle of a scan job. Persisted as a string so the value is stable across schema versions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScanJobStatus
{
    /// <summary>Job record created; the client has an upload URL but has not uploaded yet.</summary>
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

    /// <summary>
    /// How the catalogue settled this printing: <c>confirmed</c>, <c>corrected</c>, <c>named</c>,
    /// <c>assumed</c>, <c>fuzzy</c>, <c>unplaced</c>, <c>relocated</c>, <c>ambiguous</c> or
    /// <c>unresolved</c>.
    /// <para>
    /// Null means the agent did not say, which is itself the useful signal: it is what a run looks
    /// like when the resolution tool was never called and the model answered from the crop alone.
    /// That failure is otherwise invisible, because a card identified badly looks exactly like a
    /// card identified well until someone reads the numbers.
    /// </para>
    /// </summary>
    [JsonPropertyName("resolution")] public string? Resolution { get; init; }

    /// <summary>
    /// The collector number as the agent read it off the card, whenever one was read — not only
    /// when the catalogue replaced it. Kept beside the settled number so that a correction can be
    /// seen rather than inferred, and so that agreement is visible too: under <c>relocated</c> a
    /// number matching the printing found in another set is the corroboration for the move.
    /// </summary>
    [JsonPropertyName("readCollectorNumber")] public string? ReadCollectorNumber { get; init; }

    /// <summary>
    /// The set code as the agent read it off the card, null when it could not read one.
    /// <para>
    /// This exists because a set code supplied from recognising the card is indistinguishable, in
    /// the answer, from one read off it — and the catalogue cannot tell either, since a famous
    /// card really is in the famous set it is remembered from. Only the agent knows which it did,
    /// so only the agent can report it.
    /// </para>
    /// </summary>
    [JsonPropertyName("readSet")] public string? ReadSet { get; init; }

    /// <summary>Confidence in the identification, 0..1.</summary>
    [JsonPropertyName("confidence")] public double Confidence { get; init; }

    /// <summary>
    /// Which agent produced this entry, e.g. "cardgeo/CardBoundaryAgent" when only geometry is
    /// known, or "cardid/MtgCardIdAgent" once identified. Central to the Foundry boundary demo.
    /// </summary>
    [JsonPropertyName("agent")] public string? Agent { get; init; }

    /// <summary>Populated when this specific card could not be identified.</summary>
    [JsonPropertyName("error")] public string? Error { get; init; }

    /// <summary>
    /// What comparing the photograph against this card's catalogue pictures showed:
    /// <c>agrees</c>, <c>moved</c>, <c>inconclusive</c>, or null when no comparison was made.
    /// <para>
    /// This is the only check on the identification that does not consult the text. Every other
    /// field can be self-consistent and still wrong — a real name, a real set, and that set's real
    /// number for that name describe a card that exists but may not be the card in the
    /// photograph — and that is precisely what an alternate-art reprint looks like.
    /// </para>
    /// <para>
    /// <c>inconclusive</c> is the ordinary case, not a failure: most reprints share one
    /// illustration, and where they do the pictures cannot separate them. It means the comparison
    /// was made and declined to speak, which is different from it never having run.
    /// </para>
    /// </summary>
    [JsonPropertyName("artVerdict")] public string? ArtVerdict { get; init; }

    /// <summary>
    /// How far the closest catalogue picture was ahead of the next closest, 0..1.
    /// <para>
    /// The margin, not the distance, is what makes the verdict worth anything. A photograph
    /// through a sleeve is never close to a catalogue scan in absolute terms, so the distance on
    /// its own says little; being clearly nearer one printing than all the others is the whole
    /// claim.
    /// </para>
    /// </summary>
    [JsonPropertyName("artMargin")] public double? ArtMargin { get; init; }

    /// <summary>
    /// Where the card was before the pictures moved it, as <c>set number</c>, and null when
    /// nothing moved. Kept so a move can be seen rather than inferred, exactly as
    /// <see cref="ReadCollectorNumber"/> is.
    /// </summary>
    [JsonPropertyName("artMovedFrom")] public string? ArtMovedFrom { get; init; }

    /// <summary>
    /// The printings the catalogue offered for comparison, carried from the agent's reply to the
    /// orchestrator and no further.
    /// <para>
    /// Not serialised: these are addresses of catalogue images, useful only while the photograph
    /// is still in hand. By the time the result reaches the desktop the comparison has been made
    /// and <see cref="ArtVerdict"/> is what survives of it.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<CardArtReference> ArtReferences { get; init; } = [];

    /// <summary>
    /// True when the card has enough catalogue data to be exported: a name and the set it is in.
    /// <para>
    /// The collector number is deliberately <em>not</em> required. It is the least legible thing on
    /// a card and the first thing lost to glare, a sleeve or an oblique angle, and a row that has
    /// the name and the set is one someone can look at and finish in seconds. A row that was
    /// dropped is not: the name goes with it, and the name is both the most legible thing on the
    /// card and the only field a person can check afterwards. The resolution is what says how much
    /// of this came from the card and how much from the catalogue.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public bool IsIdentified =>
        !string.IsNullOrWhiteSpace(Set) && !string.IsNullOrWhiteSpace(Name);

    /// <summary>
    /// True when the printing is pinned down as well as the card: an identified card that also
    /// carries a collector number. The difference is what the export warns about, because a blank
    /// number in a CSV is otherwise a silent gap rather than a visible one.
    /// </summary>
    [JsonIgnore]
    public bool HasPrinting => IsIdentified && !string.IsNullOrWhiteSpace(CollectorNumber);

    /// <summary>
    /// True when something read off this card chose the printing, rather than the catalogue
    /// offering its default printing of a name.
    /// <para>
    /// An unplaced card is not a bad reading — the name is the part that was read, and it is
    /// right. It is a card whose set and number were never evidence, so they are worth exactly
    /// what a guess is worth and need a human eye on the art before the row is believed.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public bool PrintingWasPlaced =>
        !string.Equals(Resolution, "unplaced", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// How many detected cards were identified well enough to export, by the same rule as
    /// <see cref="IdentifiedCard.IsIdentified"/>: a name and a set. Some of these may have no
    /// collector number, so this is a count of cards named, not of printings pinned down — see
    /// <see cref="IdentifiedCard.HasPrinting"/> on the individual cards for that distinction.
    /// </summary>
    [JsonPropertyName("cardsIdentified")] public int CardsIdentified { get; init; }

    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the upload window advertised by <c>POST /jobs</c> closes. Stored rather than recomputed
    /// so that the deadline enforced by <c>PUT /jobs/{id}/content</c> is exactly the one the client
    /// was given, even if the configured lifetime changes in between.
    /// </summary>
    [JsonPropertyName("uploadExpiresAt")] public DateTimeOffset UploadExpiresAt { get; init; }

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

/// <summary>Response for <c>POST /jobs</c>: the job id and the window for sending the image.</summary>
public sealed record CreateJobResponse
{
    [JsonPropertyName("jobId")] public string JobId { get; init; } = string.Empty;

    [JsonPropertyName("blobPath")] public string BlobPath { get; init; } = string.Empty;

    [JsonPropertyName("uploadExpiresAt")] public DateTimeOffset UploadExpiresAt { get; init; }
}

/// <summary>Response for <c>GET /jobs/{id}</c>.</summary>
public sealed record JobStatusResponse
{
    [JsonPropertyName("jobId")] public string JobId { get; init; } = string.Empty;

    [JsonPropertyName("status")] public ScanJobStatus Status { get; init; }

    [JsonPropertyName("cardsDetected")] public int CardsDetected { get; init; }

    /// <summary>
    /// How many detected cards were identified well enough to export, by the same rule as
    /// <see cref="IdentifiedCard.IsIdentified"/>: a name and a set. Some of these may have no
    /// collector number, so this is a count of cards named, not of printings pinned down — see
    /// <see cref="IdentifiedCard.HasPrinting"/> on the individual cards for that distinction.
    /// </summary>
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

/// <summary>
/// One printing of a card and where the catalogue's picture of it lives, as reported by the
/// identification agent.
/// </summary>
/// <param name="ImageUrl">
/// Never trusted as given. The orchestrator fetches it, so it is a caller-supplied address for an
/// outbound request, and the only thing standing between a mangled or invented URL and the
/// worker's network is the host check applied before the fetch.
/// </param>
public sealed record CardArtReference(string Set, string CollectorNumber, string ImageUrl);
