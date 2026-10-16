using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Enfolderer.App.Infrastructure;
using Enfolderer.Ai.Contracts;

namespace Enfolderer.App.Utilities;

/// <summary>
/// Drives the Azure-hosted, multi-agent scan pipeline from the desktop app and writes the results
/// in the CSV shape the importer already understands.
/// All card recognition happens server-side: Team A's boundary agent finds the cards, Team B's
/// per-game agents identify them via their catalogue MCP servers.
/// </summary>
public static class BinderScanService
{
    /// <param name="Language">
    /// The language the card itself is printed in. <paramref name="Name"/> is always the
    /// catalogue's English name, so this is the only record of which printing it was: a Japanese
    /// card and its English reprint share a name, a set and a number.
    /// </param>
    public record ScannedCard(string Set, string Number, string Name, string Language = "en", string? Finish = null);
    /// <param name="CardsWithoutNumber">
    /// How many of <paramref name="CardsFound"/> were exported with an empty number column. Those
    /// rows name a real card and are worth keeping, but the number is the one field nobody can
    /// recover later without the photograph, so the count is surfaced rather than left to be
    /// noticed in the file.
    /// </param>
    /// <param name="UnplacedCards">
    /// Names of the cards whose set and number came from the catalogue's default printing rather
    /// than from anything read off the card. These are the rows to check against the art: the name
    /// is right, but an alternate-art reprint is exported as whichever printing the catalogue
    /// prefers, and nothing in the CSV itself shows which rows those are. They are named rather
    /// than counted because there are usually two or three, and checking two named rows is work a
    /// person will actually do.
    /// </param>
    /// <param name="CardsMovedByArt">
    /// Rows where the printing the agent settled on did not look like the photograph, and a
    /// different printing of the same card did, so the row was moved to it. The card is the same
    /// card either way — only the set and number changed — but a move is a claim made by software
    /// about a photograph, and a claim nobody is told about is one nobody can overrule.
    /// </param>
    public record ScanResult(
        int ImagesProcessed, int CardsFound, int LookupFailures, string OutputPath, int CardsWithoutNumber = 0,
        IReadOnlyList<string>? UnplacedCards = null, IReadOnlyList<string>? CardsMovedByArt = null,
        IReadOnlyList<string>? CardsWithoutFinish = null,
        IReadOnlyList<string>? AnnotatedImages = null, IReadOnlyList<string>? AnnotationWarnings = null);

    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".webp", ".gif"];

    /// <summary>Scans a single image and writes the CSV. This is the primary entry point.</summary>
    public static Task<ScanResult> ScanImageAsync(
        string imagePath,
        AiScanConfig config,
        string? outputPath = null,
        string? gameHint = null,
        Action<string>? statusCallback = null,
        Action<int, int>? progressCallback = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Image not found.", imagePath);

        outputPath ??= Path.ChangeExtension(imagePath, ".scanned_cards.csv");
        return ScanImagesAsync([imagePath], config, outputPath, gameHint, statusCallback, progressCallback, ct);
    }

    /// <summary>
    /// Scans every image in a folder as a batch of independent jobs against the same API, keeping
    /// the previous folder-based workflow available.
    /// </summary>
    public static Task<ScanResult> ScanFolderAsync(
        string folderPath,
        AiScanConfig config,
        string? outputPath = null,
        string? gameHint = null,
        Action<string>? statusCallback = null,
        Action<int, int>? progressCallback = null,
        CancellationToken ct = default)
    {
        var imageFiles = Directory.GetFiles(folderPath)
            .Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (imageFiles.Count == 0)
            throw new InvalidOperationException("No image files found in the selected folder.");

        outputPath ??= Path.Combine(folderPath, "scanned_cards.csv");
        return ScanImagesAsync(imageFiles, config, outputPath, gameHint, statusCallback, progressCallback, ct);
    }

    private static async Task<ScanResult> ScanImagesAsync(
        IReadOnlyList<string> imageFiles,
        AiScanConfig config,
        string outputPath,
        string? gameHint,
        Action<string>? statusCallback,
        Action<int, int>? progressCallback,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        using var http = BinderViewModelHttpFactory.Create();
        var client = new AiScanClient(http, config.ApiBaseUrl, CreateCredential(config), config.Scopes);

        var allCards = new List<ScannedCard>();
        var unidentified = 0;
        var withoutNumber = 0;
        var unplaced = new List<string>();
        var movedByArt = new List<string>();
        var withoutFinish = new List<string>();
        var annotatedImages = new List<string>();
        var annotationWarnings = new List<string>();
        var imageResults = new List<(string Source, ScanResultDocument Result)>();

        for (var i = 0; i < imageFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = imageFiles[i];
            progressCallback?.Invoke(i, imageFiles.Count);

            var prefix = imageFiles.Count > 1 ? $"[{i + 1}/{imageFiles.Count}] {Path.GetFileName(file)}: " : string.Empty;
            var result = await client.ScanImageAsync(
                file,
                gameHint,
                msg => statusCallback?.Invoke(prefix + msg),
                ct);
            imageResults.Add((file, result));

            allCards.AddRange(AiScanClient.MapCards(result));
            withoutFinish.AddRange(result.Cards.Where(c => c.IsIdentified
                && c.Finish is not ("nonfoil" or "foil" or "etched")).Select(c => c.Name!));
            unidentified += result.Cards.Count(c => !c.IsIdentified);
            withoutNumber += result.Cards.Count(c => c.IsIdentified && !c.HasPrinting);
            unplaced.AddRange(result.Cards
                .Where(c => c.IsIdentified && !c.PrintingWasPlaced)
                .Select(c => $"{c.Name} (exported as {c.Set} {c.CollectorNumber})"));
            movedByArt.AddRange(result.Cards
                .Where(c => string.Equals(c.ArtVerdict, "moved", StringComparison.OrdinalIgnoreCase))
                .Select(c => $"{c.Name}: {c.ArtMovedFrom} → {c.Set} {c.CollectorNumber}"));
            progressCallback?.Invoke(i + 1, imageFiles.Count);
        }

        statusCallback?.Invoke($"Saving {allCards.Count} card(s) to {Path.GetFileName(outputPath)}...");
        File.WriteAllLines(outputPath, allCards.Select(BuildCsvRow));

        // Save the CSV first: an optional download must not lose successfully identified cards.
        foreach (var (file, result) in imageResults)
        {
            if (result.AnnotationError is not null)
                annotationWarnings.Add($"{Path.GetFileName(file)}: {result.AnnotationError}");
            if (result.Cards.Any(c => c.Prices?.Error is not null))
                annotationWarnings.Add($"{Path.GetFileName(file)}: some price lookups failed; image legend marks lookup incomplete.");
            if (result.AnnotatedImagePath is null) continue;
            var imageOutput = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!,
                $"{Path.GetFileNameWithoutExtension(outputPath)}_{ScanBlobPaths.SanitizeSegment(result.JobId)}_priced.png");
            statusCallback?.Invoke($"Downloading priced image for {Path.GetFileName(file)}...");
            try
            {
                await client.DownloadAnnotatedImageAsync(result, imageOutput, ct);
                annotatedImages.Add(imageOutput);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException
                || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                annotationWarnings.Add($"{Path.GetFileName(file)}: image download failed: {ex.Message}");
            }
        }

        return new ScanResult(
            imageFiles.Count, allCards.Count, unidentified, outputPath, withoutNumber, unplaced, movedByArt, withoutFinish,
            annotatedImages, annotationWarnings);
    }

    internal static string DescribeResult(ScanResult result) =>
        $"Scan saved: {result.CardsFound} cards from {result.ImagesProcessed} image(s); "
        + $"{result.LookupFailures} unidentified, {result.CardsWithoutNumber} missing numbers, "
        + $"{result.UnplacedCards?.Count ?? 0} printings need review, "
        + $"{result.CardsWithoutFinish?.Count ?? 0} finishes unknown, "
        + $"{result.CardsMovedByArt?.Count ?? 0} moved by art; {result.AnnotatedImages?.Count ?? 0} priced images, "
        + $"{result.AnnotationWarnings?.Count ?? 0} image/price warnings. {Path.GetFileName(result.OutputPath)}";

    /// <summary>
    /// One CSV row in the shape the importer already understands:
    /// <c>edition;number;modifier;language;name</c>.
    /// </summary>
    internal static string BuildCsvRow(ScannedCard card) =>
        $"{card.Set};{card.Number};{FinishModifier(card.Finish)};{Normalise(card.Language)};{card.Name}";

    private static string FinishModifier(string? finish) => finish?.Trim().ToLowerInvariant() switch
    {
        "foil" => "foil",
        "etched" => "etched",
        null or "" or "unknown" or "nonfoil" => string.Empty,
        _ => throw new InvalidOperationException($"Unsupported scan finish '{finish}'.")
    };

    /// <summary>
    /// The language column only ever holds a two-letter code; anything else — a blank, or a model
    /// answering "Japanese" — falls back to English, which is what the column held before it was
    /// carried through at all.
    /// </summary>
    private static string Normalise(string? language)
    {
        var code = language?.Trim().ToLowerInvariant();
        return code is { Length: 2 } && code.All(char.IsAsciiLetterLower) ? code : "en";
    }

    /// <summary>
    /// Builds the credential used against the scan API. Interactive browser sign-in is preferred;
    /// device code is offered as a fallback for machines without a usable default browser.
    /// No client secret is ever read or stored by the desktop app.
    /// </summary>
    private static TokenCredential? CreateCredential(AiScanConfig config)
    {
        if (config.Scopes.Length == 0) return null;

        var store = AuthenticationRecordStore.BesideExecutable();
        var record = store.Load(config.TenantId, config.ClientId);

        // Naming the cache keeps this app's tokens apart from those of any other Azure SDK tool on
        // the machine, so signing out of one does not disturb the other.
        // UnsafeAllowUnencryptedStorage is deliberately left off: on Windows the cache is encrypted
        // per user by DPAPI, and where encryption is unavailable the SDK declines to write it at
        // all. That costs a sign-in per scan on such a machine, which is the failure this demo
        // should prefer over writing tokens to disk in the clear.
        var cache = new TokenCachePersistenceOptions { Name = "Enfolderer.Scan" };

        if (config.UseDeviceCode)
        {
            // The prompt's dismissal handle is captured here and disposed by the wrapper below once
            // the token arrives, so the prompt can be modeless: a modal one would block the
            // library's polling until it was dismissed, which is the opposite of what is needed.
            IDisposable? prompt = null;
            var deviceCode = new DeviceCodeCredential(new DeviceCodeCredentialOptions
            {
                TenantId = config.TenantId,
                ClientId = config.ClientId,
                TokenCachePersistenceOptions = cache,
                AuthenticationRecord = record,
                DisableAutomaticAuthentication = true,
                DeviceCodeCallback = (info, _) =>
                {
                    prompt?.Dispose();
                    prompt = config.DeviceCodePrompt?.Invoke(
                        new DeviceCodeDetails(info.UserCode, info.VerificationUri.ToString(), info.Message));
                    return Task.CompletedTask;
                }
            });

            var remembered = new RememberingCredential(
                deviceCode,
                (ctx, ct) => deviceCode.Authenticate(ctx, ct),
                (ctx, ct) => deviceCode.AuthenticateAsync(ctx, ct),
                store,
                record is not null);

            return new PromptDismissingCredential(remembered, () =>
            {
                prompt?.Dispose();
                prompt = null;
            });
        }

        var browser = new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions
        {
            TenantId = config.TenantId,
            ClientId = config.ClientId,
            TokenCachePersistenceOptions = cache,
            AuthenticationRecord = record,
            DisableAutomaticAuthentication = true
        });

        return new RememberingCredential(
            browser,
            (ctx, ct) => browser.Authenticate(ctx, ct),
            (ctx, ct) => browser.AuthenticateAsync(ctx, ct),
            store,
            record is not null);
    }
}
