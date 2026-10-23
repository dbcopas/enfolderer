using System.Text.Json;

namespace Enfolderer.Ai.Contracts;

public sealed record ScanEvidenceEvent(string Kind, JsonElement Details, DateTimeOffset At)
{
    public static ScanEvidenceEvent Create(string kind, object details) =>
        new(kind, JsonSerializer.SerializeToElement(details, ScanJson.Options), DateTimeOffset.UtcNow);
}
