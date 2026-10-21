using System.Text;
using System.Text.Json;
using System.Globalization;
using Enfolderer.Ai.Contracts;
using Enfolderer.Ai.Infrastructure.Storage;
using Microsoft.AspNetCore.Http.Features;

namespace Enfolderer.Ai.Api;

public static class MobileWebEndpoints
{
    public const int MaxSnapshotBytes = 32 * 1024 * 1024;

    public static void MapMobileWeb(this WebApplication app)
    {
        app.MapGet("/web-config", (IConfiguration config, ScanKnowledgeOptions knowledge) => Results.Ok(new
        {
            enabled = !string.IsNullOrWhiteSpace(config["MobileWeb:ClientId"]),
            clientId = config["MobileWeb:ClientId"],
            scanQuestionsEnabled = knowledge.Enabled,
            tenantId = config["AzureAd:TenantId"],
            scope = $"api://{config["AzureAd:ClientId"]}/Scan.Submit"
        }));

        var collection = app.MapGroup("/collection").RequireAuthorization();
        collection.AddEndpointFilter<JobOwnerFilter>();
        collection.MapGet("/", async (HttpContext context, ICollectionSnapshotStore store, CancellationToken ct) =>
        {
            var owner = JobOwnerFilter.ReadOwner(context.User)!.Value;
            var snapshot = await store.ReadAsync(owner.TenantId, owner.ObjectId, ct);
            return Results.Ok(new { available = snapshot is not null, exportedAt = snapshot?.ExportedAt,
                printings = (snapshot?.Printings.Count ?? 0) + (snapshot?.GathererPrintings.Count ?? 0) });
        });
        collection.MapPut("/", async (HttpContext context, ICollectionSnapshotStore store,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var size = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (size is { IsReadOnly: false }) size.MaxRequestBodySize = MaxSnapshotBytes;
            if (!context.Request.HasJsonContentType()) return Results.BadRequest(new { error = "Upload a JSON ownership snapshot, not a database." });
            if (context.Request.ContentLength > MaxSnapshotBytes) return Results.StatusCode(413);
            using var bytes = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, ct)) > 0)
            {
                if (bytes.Length + count > MaxSnapshotBytes) return Results.StatusCode(413);
                await bytes.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            CollectionSnapshot snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<CollectionSnapshot>(bytes.ToArray(),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    ?? throw new InvalidDataException("Snapshot is empty.");
                snapshot.Validate();
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                logs.CreateLogger("Collection").LogWarning("Rejected invalid ownership snapshot: {Error}", ex.Message);
                return Results.BadRequest(new { error = "Invalid collection snapshot. Export a fresh snapshot from the desktop Scan menu." });
            }
            var owner = JobOwnerFilter.ReadOwner(context.User)!.Value;
            await store.WriteAsync(owner.TenantId, owner.ObjectId, snapshot, ct);
            return Results.Ok(new { exportedAt = snapshot.ExportedAt, printings = snapshot.Printings.Count + snapshot.GathererPrintings.Count });
        });
        collection.MapDelete("/", async (HttpContext context, ICollectionSnapshotStore store, CancellationToken ct) =>
        {
            var owner = JobOwnerFilter.ReadOwner(context.User)!.Value;
            await store.DeleteAsync(owner.TenantId, owner.ObjectId, ct);
            return Results.NoContent();
        });
    }

    public static string Csv(ScanResultDocument result)
    {
        static string Cell(string? value)
        {
            var text = value ?? "";
            // Downloads may be opened in a spreadsheet; names must not become executable formulas.
            if (text.TrimStart().StartsWithAny('=', '+', '-', '@')) text = "'" + text;
            return text.IndexOfAny([';', '"', '\r', '\n']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;
        }
        static string Amount(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? "";
        var rows = CardValueRanking.Order(result.Cards.Where(c => c.IsIdentified)).Select((c, i) =>
            string.Join(';', (i + 1).ToString(CultureInfo.InvariantCulture),
                (c.Index + 1).ToString(CultureInfo.InvariantCulture), Cell(c.Name), Cell(c.Set?.ToUpperInvariant()),
                Cell(c.CollectorNumber), Cell(c.Language), Amount(c.Prices?.Nonfoil), Amount(c.Prices?.Foil),
                Amount(CardValueRanking.Price(c)),
                c.Ownership is { Error: null } owned ? owned.Quantity.ToString(CultureInfo.InvariantCulture) : "",
                c.Ownership is { Error: null } all ? all.AllQuantity.ToString(CultureInfo.InvariantCulture) : "",
                Cell(c.Ownership?.Error), Cell(c.Resolution), Cell(c.Prices?.Error),
                Cell(c.Prices?.Source), Cell(c.Prices?.RetrievedAt.ToString("u", CultureInfo.InvariantCulture))));
        return "Rank;Card position;Name;Set;Number;Language;Nonfoil EUR;Foil EUR;Ranking EUR;Owned;All printings;Ownership error;Resolution;Price error;Price source;Retrieved UTC\r\n"
            + string.Join("\r\n", rows) + "\r\n";
    }

    private static bool StartsWithAny(this string value, params char[] choices) =>
        value.Length > 0 && choices.Contains(value[0]);
}
