using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;
using Azure.Core;

namespace Enfolderer.Ai.Infrastructure;

/// <summary>
/// Reports which security principal a <see cref="TokenCredential"/> actually presents.
/// <para>
/// When Azure refuses a request, the useful question is not what roles were granted but which
/// principal was refused — a host can carry several identities, and configuration says only which
/// one was <em>asked</em> for. Reading the claims back off the issued token answers it directly,
/// in a form that can be compared with <c>az role assignment list</c> without guessing.
/// </para>
/// </summary>
public static class IdentityDiagnostics
{
    /// <summary>
    /// Describes the principal behind a credential as <c>oid=… appid=… tid=…</c>, or explains why
    /// it could not be determined. Never returns or logs the token itself.
    /// <para>
    /// The return value is safe to put in an HTTP response, so a failure is summarised by exception
    /// type only: <see cref="Azure.Identity.DefaultAzureCredential"/> reports failure by listing
    /// every credential it attempted, which describes the host rather than the caller's problem.
    /// Pass <paramref name="logger"/> to keep that detail where operators can still read it.
    /// </para>
    /// </summary>
    public static async Task<string> DescribeAsync(
        TokenCredential credential, string scope, ILogger? logger = null, CancellationToken ct = default)
    {
        try
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([scope]), ct).ConfigureAwait(false);
            var claims = ReadClaims(token.Token);

            string Claim(string name) => claims != null && claims.TryGetValue(name, out var v) ? v : "?";

            return $"oid={Claim("oid")} appid={Claim("appid")} tid={Claim("tid")}";
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not determine the principal behind the credential for scope {Scope}.", scope);
            return $"could not be determined ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// Pulls the identifying claims out of a JWT without validating it. The token came straight
    /// from the credential and is used only to describe it, so there is nothing to validate
    /// against: this is a diagnostic, never an authorisation decision.
    /// </summary>
    /// <remarks>
    /// Returns null for anything that is not a readable JWT rather than throwing. A token that
    /// cannot be decoded is still a token that was successfully acquired, and saying the identity
    /// "could not be determined" would point the reader at the credential — which is the one part
    /// that demonstrably worked.
    /// </remarks>
    private static Dictionary<string, string>? ReadClaims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');

            using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            var claims = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    claims[property.Name] = property.Value.GetString()!;
            }

            return claims;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }
}
