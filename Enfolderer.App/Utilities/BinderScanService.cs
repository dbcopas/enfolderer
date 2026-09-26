using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Enfolderer.App.Infrastructure;

namespace Enfolderer.App.Utilities;

/// <summary>
/// Drives the Azure-hosted, multi-agent scan pipeline from the desktop app and writes the results
/// in the CSV shape the importer already understands.
/// All card recognition happens server-side: Team A's boundary agent finds the cards, Team B's
/// per-game agents identify them via their catalogue MCP servers.
/// </summary>
public static class BinderScanService
{
    public record ScannedCard(string Set, string Number, string Name);
    public record ScanResult(int ImagesProcessed, int CardsFound, int LookupFailures, string OutputPath);

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

        for (var i = 0; i < imageFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = imageFiles[i];
            progressCallback?.Invoke(i + 1, imageFiles.Count);

            var prefix = imageFiles.Count > 1 ? $"[{i + 1}/{imageFiles.Count}] {Path.GetFileName(file)}: " : string.Empty;
            var result = await client.ScanImageAsync(
                file,
                gameHint,
                msg => statusCallback?.Invoke(prefix + msg),
                ct);

            allCards.AddRange(AiScanClient.MapCards(result));
            unidentified += result.Cards.Count(c => !c.IsIdentified);
        }

        File.WriteAllLines(outputPath, allCards.Select(c => $"{c.Set};{c.Number};;en;{c.Name}"));

        return new ScanResult(imageFiles.Count, allCards.Count, unidentified, outputPath);
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
