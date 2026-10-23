using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Worker.Agents;

internal static class FoundryEvidence
{
    private static readonly HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        "found", "game", "name", "set", "collectorNumber", "language", "printedName", "flavorName",
        "readName", "readSet", "readCollectorNumber", "readLanguage", "promoLabel", "readPromoLabel", "readFooter",
        "confidence", "resolution", "identificationSummary", "candidates", "alternates", "alternatesComplete",
        "imageUrl", "image_url", "error", "code", "message", "reason",
        "prices", "eur", "eur_foil", "nonfoil", "foil", "source", "retrievedAt",
        "content", "isError", "type", "text", "id"
    };

    public static object ReadJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new { unavailable = "empty" };
        try
        {
            using var json = JsonDocument.Parse(AgentJson.ExtractJsonObject(text));
            return Project(json.RootElement)!;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new
            {
                unavailable = "not_structured_json",
                length = text.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            };
        }
    }

    private static object? Project(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Where(p => Fields.Contains(p.Name))
            .ToDictionary(p => p.Name, p => p.Name is "imageUrl" or "image_url" ? ImageUrl(p.Value)
                : p.Name == "text" && p.Value.ValueKind == JsonValueKind.String ? ReadJson(p.Value.GetString()) : Project(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Select(Project).ToArray(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null => null,
        _ => value.Clone()
    };

    private static object? ImageUrl(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps && uri.Host is "cards.scryfall.io" or "api.scryfall.com"
                ? uri.GetLeftPart(UriPartial.Path) : null;

    public static void RecordResponse(JsonElement root, string agent, string? approvedLabel,
        Action<ScanEvidenceEvent>? record)
    {
        if (record is null) return;
        var responseId = Text(root, "id");
        record(ScanEvidenceEvent.Create("response", new
        {
            agent, responseId, status = Text(root, "status"),
            usage = FoundryAgentClient.ReadResponseUsage(root),
            errorCode = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                ? Text(error, "code") : null
        }));
        if (!root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array) return;
        foreach (var item in output.EnumerateArray())
        {
            var type = Text(item, "type");
            if (type is not ("mcp_call" or "mcp_approval_request")) continue;
            var label = Text(item, "server_label");
            var allowed = approvedLabel is not null && string.Equals(label, approvedLabel, StringComparison.Ordinal);
            record(ScanEvidenceEvent.Create(type, new
            {
                agent, responseId, tool = Text(item, "name"), serverLabel = label, allowed,
                arguments = allowed ? ReadJson(Text(item, "arguments")) : null,
                output = allowed ? ReadJson(Text(item, "output")) : null,
                error = item.TryGetProperty("error", out var toolError) ? Project(toolError) : null
            }));
        }
    }

    private static string? Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
