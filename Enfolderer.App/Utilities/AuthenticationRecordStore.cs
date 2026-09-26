using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;

namespace Enfolderer.App.Utilities;

/// <summary>
/// Persists the <see cref="AuthenticationRecord"/> from an interactive sign-in so later runs can
/// reuse the cached token instead of prompting again.
/// <para>
/// A persisted token cache alone is not enough: it can hold entries for several accounts, and the
/// credential needs this record as the key to find the right one. Without it every scan prompts,
/// even though the tokens were cached all along.
/// </para>
/// <para>
/// The record holds no secret and no token — only the account identifiers (username, home account
/// id, tenant, client id, authority) needed to look one up. The tokens themselves stay in the
/// SDK's cache, which is encrypted per user by DPAPI on Windows.
/// </para>
/// </summary>
public sealed class AuthenticationRecordStore
{
    /// <summary>File holding the record, written beside <c>aiconfig.txt</c>.</summary>
    public const string FileName = "aiauth.json";

    private readonly string _path;

    public AuthenticationRecordStore(string path) => _path = path;

    /// <summary>Creates a store for the record beside the running executable.</summary>
    public static AuthenticationRecordStore BesideExecutable() =>
        new(Path.Combine(AppContext.BaseDirectory, FileName));

    /// <summary>
    /// Reads the saved record, or returns <see langword="null"/> if there is none, it cannot be
    /// read, or it belongs to a different tenant or application than the configuration now asks
    /// for. A mismatched record would never match a cache entry, so discarding it here turns a
    /// confusing silent-auth failure into an ordinary first-time sign-in.
    /// </summary>
    public AuthenticationRecord? Load(string tenantId, string clientId)
    {
        if (!File.Exists(_path)) return null;

        try
        {
            using var stream = File.OpenRead(_path);
            var record = AuthenticationRecord.Deserialize(stream);

            var matches =
                string.Equals(record.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(record.ClientId, clientId, StringComparison.OrdinalIgnoreCase);

            return matches ? record : null;
        }
        catch (Exception)
        {
            // A corrupt or unreadable record is not worth failing a scan over: sign in again and
            // overwrite it.
            return null;
        }
    }

    /// <summary>
    /// Saves the record, replacing any previous one. Failures are swallowed: not being able to
    /// write it costs an extra sign-in next time, which is far better than losing the scan.
    /// </summary>
    public async Task SaveAsync(AuthenticationRecord record, CancellationToken ct = default)
    {
        try
        {
            using var stream = File.Create(_path);
            await record.SerializeAsync(stream, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Removes the saved record, so the next run signs in from scratch.</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception)
        {
        }
    }
}
