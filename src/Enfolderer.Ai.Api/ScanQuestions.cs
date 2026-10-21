using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Core;
using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Api;

public sealed class ScanKnowledgeOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public string KnowledgeBase { get; set; } = "scan-cards";
    public string KnowledgeSource { get; set; } = "scryfall";
    public string Index { get; set; } = "scryfall";
    public const string ApiVersion = "2026-08-01-preview";

    public bool IsValid => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host.EndsWith(".search.windows.net", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath == "/" && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && new[] { KnowledgeBase, KnowledgeSource, Index }.All(name => Regex.IsMatch(name, "^[a-z0-9][a-z0-9-]{1,127}$"));
}

public sealed record ScanQuestionRequest(string Question);
public sealed record ScanAnswerSource(string Id, string Title, string Url);
public sealed record ScanAnswer(string Answer, IReadOnlyList<ScanAnswerSource> Sources, bool Grounded,
    string Kind = "knowledge", IReadOnlyList<string>? MissingCards = null);

public interface IScanQuestions
{
    Task<ScanAnswer> AskAsync(ScanResultDocument result, string question, CancellationToken ct);
}

public sealed class FoundryIqScanQuestions(
    HttpClient http, TokenCredential credential, ScanKnowledgeOptions options,
    ILogger<FoundryIqScanQuestions> log) : IScanQuestions
{
    public async Task<ScanAnswer> AskAsync(ScanResultDocument result, string question, CancellationToken ct)
    {
        var local = RecordedScanAnswers.TryAnswer(result, question);
        if (local is not null) return local;
        // Only public identification facts are sent: no owner IDs, blobs, prices, or collection quantities.
        var cards = result.Cards.Select(c => new
        {
            position = c.Index + 1, c.Game, c.Name,
            c.Resolution, printingPlaced = c.PrintingWasPlaced
        }).ToArray();
        var context = JsonSerializer.Serialize(cards);
        var body = new
        {
            messages = new[]
            {
                new { role = "user", content = new[] { new { type = "text", text =
                    $"Scan card facts (JSON):\n{context}\nQuestion:\n{question}" } } }
            },
            outputMode = "answerSynthesis",
            retrievalReasoningEffort = new { kind = "low" },
            maxOutputSize = 6000,
            knowledgeSourceParams = new[]
            {
                new { kind = "searchIndex", knowledgeSourceName = options.KnowledgeSource,
                    alwaysQuerySource = true, includeReferences = true, includeReferenceSourceData = true }
            }
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        using var response = await SendAsync(HttpMethod.Post,
            $"knowledgebases/{options.KnowledgeBase}/retrieve", body, timeout.Token);
        var root = response.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("response", out var messages) || messages.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Foundry IQ returned no answer messages.");
        var parts = new List<string>();
        foreach (var message in messages.EnumerateArray())
        {
            if (message.TryGetProperty("role", out var role)
                && (role.ValueKind != JsonValueKind.String || role.GetString() != "assistant"))
                throw new InvalidDataException("Foundry IQ returned an unexpected answer role.");
            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Foundry IQ returned malformed answer content.");
            foreach (var part in content.EnumerateArray().Where(c => String(c, "type") == "text"))
                if (String(part, "text") is { } value) parts.Add(value);
        }
        var text = string.Join("\n", parts).Trim();
        if (text.Length == 0 || text.Length > 20000)
            throw new InvalidDataException("Foundry IQ returned an empty or oversized answer.");
        var cited = Regex.Matches(text, @"\[ref_id:(\d+)\]").Select(m => m.Groups[1].Value).Distinct().ToArray();
        if (Regex.Replace(text, @"\[ref_id:\d+\]", "").Contains("[ref_id:", StringComparison.Ordinal))
            throw new InvalidDataException("Foundry IQ returned a malformed citation.");
        if (cited.Length == 0)
        {
            log.LogInformation("Scan question retrieval returned no cited evidence.");
            var names = result.Cards.Where(c => CardGames.Normalize(c.Game) == CardGames.Magic)
                .Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(n => n!).ToArray();
            if (names.Length == 0)
                return new("No identified Magic card names are available in this scan. Pokemon and unidentified cards are not covered by this knowledge index.",
                    [], false, "unsupported");
            var missing = new List<string>();
            foreach (var name in names)
            {
                using var coverage = await SendAsync(HttpMethod.Post, $"indexes/{options.Index}/docs/search",
                    new { search = "*", filter = $"names/any(n: n eq '{name.ToLowerInvariant().Replace("'", "''")}')",
                        top = 1, select = "id" }, timeout.Token);
                if (!coverage.RootElement.TryGetProperty("value", out var docs) || docs.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Search returned malformed knowledge coverage.");
                if (docs.GetArrayLength() == 0) missing.Add(name);
            }
            return missing.Count > 0
                ? new($"The knowledge index is missing: {string.Join(", ", missing)}. Refresh the public Oracle catalogue, or ask about another indexed card.",
                    [], false, "missingKnowledge", missing)
                : new("The cards are indexed, but no cited evidence could support this answer. Try asking what a named card does. Exact printing comparisons and official interaction rulings may require other sources.",
                    [], false, "insufficientEvidence");
        }
        if (cited.Length > 12 || !root.TryGetProperty("references", out var references)
            || references.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Foundry IQ citations are missing or exceed the allowed limit.");
        var sources = new List<ScanAnswerSource>();
        foreach (var id in cited)
        {
            var matches = references.EnumerateArray().Where(r => String(r, "id") == id).ToArray();
            if (matches.Length != 1 || String(matches[0], "type") != "searchIndex"
                || !Guid.TryParse(String(matches[0], "docKey"), out var key))
                throw new InvalidDataException("Foundry IQ returned an unverifiable card citation.");
            // Read the canonical indexed document, never fetch a service/model-supplied URL.
            using var document = await SendAsync(HttpMethod.Get,
                $"indexes/{options.Index}/docs/{key:D}", null, timeout.Token);
            var url = String(document.RootElement, "url");
            var title = String(document.RootElement, "title");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || !string.Equals(uri.Host, "scryfall.com", StringComparison.OrdinalIgnoreCase)
                || uri.UserInfo.Length != 0 || !uri.IsDefaultPort || string.IsNullOrWhiteSpace(title))
                throw new InvalidDataException("The indexed card citation has no safe Scryfall source.");
            sources.Add(new(id, title, uri.AbsoluteUri));
        }
        return new(text, sources, true);
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(["https://search.azure.com/.default"]), ct);
        using var request = new HttpRequestMessage(method,
            $"{options.Endpoint.TrimEnd('/')}/{path}?api-version={ScanKnowledgeOptions.ApiVersion}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Foundry IQ request failed ({(int)response.StatusCode}).", null, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct)
            ?? throw new InvalidDataException("Foundry IQ returned an empty response.");
    }

    private static string? String(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
}
