using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Enfolderer.Ai.Contracts;
using Enfolderer.App.Utilities;

namespace Enfolderer.App.Tests;

/// <summary>
/// Contract-level tests for the Azure scan pipeline client: result JSON deserialisation, the
/// polling state machine, card mapping onto the CSV shape, and configuration validation.
/// </summary>
public static class AiScanClientTests
{
    public static int RunAll()
    {
        int failures = 0;
        failures += TestResultDocumentDeserialization();
        failures += TestCardMapping();
        failures += TestPollBackoff();
        failures += TestStatusDescriptions();
        failures += TestConfigParsing();
        failures += TestQuadBoundingBox();
        failures += TestAuthenticationRecordStore();
        failures += TestStaleRecordFallsBackToPrompt();
        return failures;
    }

    private static int Check(bool condition, string message)
    {
        if (condition) return 0;
        Console.WriteLine($"[AiScanClientTests] FAIL: {message}");
        return 1;
    }

    private static int TestResultDocumentDeserialization()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "jobId": "abc123",
          "status": "Completed",
          "imageWidth": 4032,
          "imageHeight": 3024,
          "cards": [
            {
              "index": 0,
              "quad": {"points":[{"x":10,"y":20},{"x":110,"y":25},{"x":112,"y":165},{"x":8,"y":160}]},
              "game": "mtg",
              "set": "bro",
              "collectorNumber": "167",
              "name": "Ancient Silver Dragon",
              "language": "en",
              "finish": "nonfoil",
              "confidence": 0.94,
              "agent": "cardid/MtgCardIdAgent"
            },
            {
              "index": 1,
              "quad": {"points":[{"x":200,"y":20},{"x":300,"y":25},{"x":302,"y":165},{"x":198,"y":160}]},
              "game": "pokemon",
              "confidence": 0.2,
              "agent": "cardgeo/CardBoundaryAgent",
              "error": "Card face was obscured."
            }
          ]
        }
        """;

        var result = JsonSerializer.Deserialize<ScanResultDocument>(json, ScanJson.Options);
        int failures = 0;
        failures += Check(result != null, "result document deserialized");
        if (result == null) return failures;

        failures += Check(result.SchemaVersion == ScanResultDocument.CurrentSchemaVersion, "schema version is 1");
        failures += Check(result.JobId == "abc123", "job id round-tripped");
        failures += Check(result.Status == ScanJobStatus.Completed, "status enum parsed from string");
        failures += Check(result.ImageWidth == 4032 && result.ImageHeight == 3024, "image dimensions parsed");
        failures += Check(result.Cards.Count == 2, "both cards parsed");

        var first = result.Cards[0];
        failures += Check(first.IsIdentified, "first card is identified");
        failures += Check(first.Quad != null && first.Quad.IsValid, "first card has a four-point quad");
        failures += Check(Math.Abs(first.Confidence - 0.94) < 1e-9, "confidence parsed");
        failures += Check(first.Agent == "cardid/MtgCardIdAgent", "identifying agent recorded");

        var second = result.Cards[1];
        failures += Check(!second.IsIdentified, "second card is not identified");
        failures += Check(second.Error == "Card face was obscured.", "per-card error preserved");
        failures += Check(second.Agent == "cardgeo/CardBoundaryAgent", "boundary agent recorded for unidentified card");

        // A future server may add fields; the client must not fail on them.
        var forwardCompatible = JsonSerializer.Deserialize<ScanResultDocument>(
            """{"schemaVersion":1,"jobId":"x","status":"Failed","cards":[],"somethingNew":true}""",
            ScanJson.Options);
        failures += Check(forwardCompatible != null && forwardCompatible.Status == ScanJobStatus.Failed,
            "unknown properties are ignored");

        return failures;
    }

    private static int TestCardMapping()
    {
        var result = new ScanResultDocument
        {
            JobId = "j",
            Status = ScanJobStatus.Completed,
            Cards =
            [
                new IdentifiedCard { Index = 0, Set = "bro", CollectorNumber = "167", Name = "Ancient Silver Dragon" },
                new IdentifiedCard { Index = 1, Set = "bro", Name = "Missing number" },
                new IdentifiedCard { Index = 2, Error = "unreadable" },
                new IdentifiedCard { Index = 3, Set = "sld", CollectorNumber = "1500", Name = "Lightning Bolt" }
            ]
        };

        var mapped = AiScanClient.MapCards(result);
        int failures = 0;
        failures += Check(mapped.Count == 2, "only fully identified cards are exported");
        failures += Check(mapped[0].Set == "bro" && mapped[0].Number == "167" && mapped[0].Name == "Ancient Silver Dragon",
            "first mapped card matches the contract fields");
        failures += Check(mapped[1].Name == "Lightning Bolt", "second mapped card preserves order");

        var csv = mapped.Select(c => $"{c.Set};{c.Number};;en;{c.Name}").ToList();
        failures += Check(csv[0] == "bro;167;;en;Ancient Silver Dragon", "CSV row format is unchanged");

        failures += Check(AiScanClient.MapCards(new ScanResultDocument()).Count == 0, "empty result maps to no rows");

        return failures;
    }

    private static int TestPollBackoff()
    {
        int failures = 0;
        var delay = AiScanClient.InitialPollInterval;
        failures += Check(delay == TimeSpan.FromSeconds(2), "initial poll interval is 2s");

        delay = AiScanClient.NextDelay(delay);
        failures += Check(delay == TimeSpan.FromSeconds(4), "backoff doubles");

        // Iterate well past the cap and confirm it never exceeds it.
        for (int i = 0; i < 20; i++) delay = AiScanClient.NextDelay(delay);
        failures += Check(delay == AiScanClient.MaxPollInterval, "backoff saturates at the maximum");

        return failures;
    }

    private static int TestStatusDescriptions()
    {
        int failures = 0;

        failures += Check(
            AiScanClient.DescribeStatus(new JobStatusResponse { Status = ScanJobStatus.DetectingBoundaries })
                .Contains("cardgeo", StringComparison.Ordinal),
            "boundary phase names the geometry project");

        failures += Check(
            AiScanClient.DescribeStatus(new JobStatusResponse { Status = ScanJobStatus.Identifying, CardsDetected = 9 })
                .Contains("9", StringComparison.Ordinal),
            "identification phase reports the detected card count");

        failures += Check(
            AiScanClient.DescribeStatus(new JobStatusResponse { Status = ScanJobStatus.Failed, Error = "boom" })
                .Contains("boom", StringComparison.Ordinal),
            "failure surfaces the server error");

        return failures;
    }

    private static int TestConfigParsing()
    {
        int failures = 0;

        var config = AiScanConfig.Parse(new Dictionary<string, string>
        {
            ["api_base_url"] = "https://scan.example.com",
            ["tenant_id"] = "tenant",
            ["client_id"] = "client",
            ["scope"] = "api://app-id/Scan.Submit",
            ["game_hint"] = "mtg"
        });

        failures += Check(config.ApiBaseUrl.ToString() == "https://scan.example.com/",
            "api base url gets a trailing slash so relative paths resolve");
        failures += Check(config.Scopes.Length == 1 && config.Scopes[0] == "api://app-id/Scan.Submit", "scope parsed");
        failures += Check(config.GameHint == "mtg", "game hint parsed");
        failures += Check(!config.UseDeviceCode, "device code defaults to off");

        // Relative paths must resolve under the base URL, not replace it.
        failures += Check(new Uri(config.ApiBaseUrl, "jobs").ToString() == "https://scan.example.com/jobs",
            "relative job path resolves under the base url");

        var secretRejected = false;
        try
        {
            AiScanConfig.Parse(new Dictionary<string, string>
            {
                ["api_base_url"] = "https://scan.example.com",
                ["tenant_id"] = "tenant",
                ["client_id"] = "client",
                ["scope"] = "api://app-id/Scan.Submit",
                ["client_secret"] = "leftover"
            });
        }
        catch (InvalidOperationException)
        {
            secretRejected = true;
        }
        failures += Check(secretRejected, "a config file containing client_secret is rejected");

        var missingRejected = false;
        try
        {
            AiScanConfig.Parse(new Dictionary<string, string> { ["api_base_url"] = "https://scan.example.com" });
        }
        catch (InvalidOperationException)
        {
            missingRejected = true;
        }
        failures += Check(missingRejected, "missing required keys are rejected");

        return failures;
    }

    private static int TestQuadBoundingBox()
    {
        var quad = new CardQuad([
            new ImagePoint(10, 20),
            new ImagePoint(110, 25),
            new ImagePoint(112, 165),
            new ImagePoint(8, 160)
        ]);

        var (x, y, width, height) = quad.BoundingBox();
        int failures = 0;
        failures += Check(quad.IsValid, "four-point quad is valid");
        failures += Check(Math.Abs(x - 8) < 1e-9 && Math.Abs(y - 20) < 1e-9, "bounding box origin");
        failures += Check(Math.Abs(width - 104) < 1e-9 && Math.Abs(height - 145) < 1e-9, "bounding box size");
        failures += Check(!new CardQuad([new ImagePoint(0, 0)]).IsValid, "a one-point quad is invalid");

        return failures;
    }
    /// <summary>
    /// Builds an <see cref="AuthenticationRecord"/> without signing in. The type has no public
    /// constructor, but its serialised form is documented by its own round trip, so deserialising
    /// one is the supported way to obtain a fixture.
    /// </summary>
    private static AuthenticationRecord MakeRecord(string tenantId, string clientId)
    {
        var json = $$"""
        {"username":"user@example.com","authority":"login.microsoftonline.com","homeAccountId":"home-id","tenantId":"{{tenantId}}","clientId":"{{clientId}}","version":"1.0"}
        """;
        return AuthenticationRecord.Deserialize(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }

    private static int TestAuthenticationRecordStore()
    {
        int failures = 0;
        var path = Path.Combine(Path.GetTempPath(), $"enfolderer_authrecord_{Guid.NewGuid():N}.json");
        var store = new AuthenticationRecordStore(path);

        failures += Check(store.Load("tenant", "client") is null, "no record before the first sign-in");

        store.SaveAsync(MakeRecord("tenant", "client")).GetAwaiter().GetResult();
        var loaded = store.Load("tenant", "client");
        failures += Check(loaded is not null, "the saved record is read back");
        failures += Check(loaded?.Username == "user@example.com", "the account survives the round trip");
        failures += Check(loaded?.HomeAccountId == "home-id", "the home account id survives the round trip");

        // A record from another tenant or app registration can never match a cache entry, so it
        // must be treated as absent rather than handed to the credential.
        failures += Check(store.Load("other-tenant", "client") is null, "a record from another tenant is discarded");
        failures += Check(store.Load("tenant", "other-client") is null, "a record from another app is discarded");

        File.WriteAllText(path, "not json");
        failures += Check(store.Load("tenant", "client") is null, "a corrupt record is discarded, not thrown");

        store.Clear();
        failures += Check(!File.Exists(path), "clearing removes the record");
        store.Clear();

        return failures;
    }

    /// <summary>
    /// A saved record whose cached token has expired or been revoked must prompt again rather than
    /// fail the scan, and the replacement record must be saved.
    /// </summary>
    private static int TestStaleRecordFallsBackToPrompt()
    {
        int failures = 0;
        var path = Path.Combine(Path.GetTempPath(), $"enfolderer_authrecord_{Guid.NewGuid():N}.json");
        var store = new AuthenticationRecordStore(path);
        store.SaveAsync(MakeRecord("tenant", "client")).GetAwaiter().GetResult();

        var inner = new ScriptedCredential();
        var prompts = 0;
        AuthenticationRecord Prompt()
        {
            prompts++;
            inner.SilentFails = false;
            return MakeRecord("tenant", "client");
        }

        var credential = new RememberingCredential(
            inner,
            (_, _) => Prompt(),
            (_, _) => Task.FromResult(Prompt()),
            store,
            hasRecord: true);

        var token = credential.GetToken(new TokenRequestContext(["scope"]), CancellationToken.None);

        failures += Check(prompts == 1, "a stale record prompts exactly once");
        failures += Check(token.Token == "token", "the scan gets its token despite the stale record");
        failures += Check(store.Load("tenant", "client") is not null, "the replacement record is saved");

        // With a usable record there must be no prompt at all: that is the whole point.
        prompts = 0;
        var silent = new RememberingCredential(
            new ScriptedCredential { SilentFails = false },
            (_, _) => { prompts++; return MakeRecord("tenant", "client"); },
            (_, _) => { prompts++; return Task.FromResult(MakeRecord("tenant", "client")); },
            store,
            hasRecord: true);
        silent.GetToken(new TokenRequestContext(["scope"]), CancellationToken.None);
        failures += Check(prompts == 0, "a usable record signs in silently");

        store.Clear();
        return failures;
    }

    /// <summary>Stands in for a cached credential that may or may not be able to authenticate silently.</summary>
    private sealed class ScriptedCredential : TokenCredential
    {
        public bool SilentFails { get; set; } = true;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            if (SilentFails) throw new AuthenticationRequiredException("interactive authentication is required", requestContext);
            return new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }
}
