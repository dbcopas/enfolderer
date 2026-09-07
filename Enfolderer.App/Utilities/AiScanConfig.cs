using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Enfolderer.App.Utilities;

/// <summary>
/// Client configuration for the Azure-hosted scan API. Deliberately contains no secrets: the app
/// signs the user in with Entra ID and sends the resulting user token. A config file containing a
/// client secret is rejected so an old-style file cannot silently keep working.
/// </summary>
public sealed class AiScanConfig
{
    public const string FileName = "aiconfig.txt";

    public required Uri ApiBaseUrl { get; init; }

    public required string TenantId { get; init; }

    /// <summary>Client id of the desktop app registration (a public client — no secret).</summary>
    public required string ClientId { get; init; }

    /// <summary>Scopes requested for the scan API, e.g. <c>api://&lt;api-app-id&gt;/Scan.Submit</c>.</summary>
    public required string[] Scopes { get; init; }

    /// <summary>Use device code flow instead of the interactive browser.</summary>
    public bool UseDeviceCode { get; init; }

    /// <summary>Optional game hint sent with each job, e.g. <c>mtg</c> or <c>pokemon</c>.</summary>
    public string? GameHint { get; init; }

    /// <summary>Invoked with the device code message when <see cref="UseDeviceCode"/> is set.</summary>
    public Action<string>? DeviceCodePrompt { get; set; }

    /// <summary>Template written when no configuration file exists yet.</summary>
    public static string SampleContent =>
        """
        # Enfolderer scan configuration. Contains no secrets.
        # Base URL of the deployed Enfolderer.Ai.Api.
        api_base_url=https://enfolderer-scan-api.azurewebsites.net
        # Entra ID tenant and the desktop app's public-client registration.
        tenant_id=00000000-0000-0000-0000-000000000000
        client_id=00000000-0000-0000-0000-000000000000
        # Scope exposed by the scan API app registration.
        scope=api://00000000-0000-0000-0000-000000000000/Scan.Submit
        # Optional: mtg, pokemon, yugioh, lorcana. Leave unset to let the boundary agent decide.
        #game_hint=mtg
        # Optional: use the device code flow instead of an interactive browser window.
        #use_device_code=true
        """;

    /// <summary>Reads and validates <c>aiconfig.txt</c> next to the executable.</summary>
    public static AiScanConfig Load(string configPath)
    {
        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Config file not found: {configPath}", configPath);

        var values = File.ReadAllLines(configPath)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
            .Select(l => l.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());

        return Parse(values);
    }

    /// <summary>Validates already-parsed key/value pairs. Exposed for the self-tests.</summary>
    public static AiScanConfig Parse(IReadOnlyDictionary<string, string> values)
    {
        if (values.ContainsKey("client_secret"))
        {
            throw new InvalidOperationException(
                $"{FileName} contains 'client_secret'. The desktop app now signs in interactively and must not " +
                "store secrets: remove that line, and revoke the secret in Entra ID if it was ever used.");
        }

        string Required(string key)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value;
            throw new InvalidOperationException($"Missing or empty '{key}' in {FileName}.");
        }

        var apiBaseUrl = Required("api_base_url");
        if (!Uri.TryCreate(apiBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var parsedUrl))
            throw new InvalidOperationException($"'api_base_url' in {FileName} is not a valid absolute URL.");

        var scopes = Required("scope")
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new AiScanConfig
        {
            ApiBaseUrl = parsedUrl,
            TenantId = Required("tenant_id"),
            ClientId = Required("client_id"),
            Scopes = scopes,
            UseDeviceCode = values.TryGetValue("use_device_code", out var useDeviceCode)
                            && bool.TryParse(useDeviceCode, out var parsedFlag) && parsedFlag,
            GameHint = values.TryGetValue("game_hint", out var hint) && !string.IsNullOrWhiteSpace(hint) ? hint : null
        };
    }
}
