using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;

namespace Enfolderer.App.Utilities;

/// <summary>
/// Wraps an interactive credential so the account it signs in is remembered, and so a remembered
/// account that no longer works falls back to prompting instead of failing the scan.
/// </summary>
/// <remarks>
/// Both interactive credentials cache their tokens on disk, but a cache can hold several accounts
/// and needs an <see cref="AuthenticationRecord"/> to identify which entry to reuse. This type owns
/// that record's lifecycle: obtain one on the first sign-in, save it, and discard it when the
/// cached token behind it has expired or been revoked.
/// </remarks>
internal sealed class RememberingCredential : TokenCredential
{
    private readonly TokenCredential _inner;
    private readonly Func<TokenRequestContext, CancellationToken, Task<AuthenticationRecord>> _authenticate;
    private readonly AuthenticationRecordStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _hasRecord;

    public RememberingCredential(
        TokenCredential inner,
        Func<TokenRequestContext, CancellationToken, Task<AuthenticationRecord>> authenticate,
        AuthenticationRecordStore store,
        bool hasRecord)
    {
        _inner = inner;
        _authenticate = authenticate;
        _store = store;
        _hasRecord = hasRecord;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        // Serialised so two concurrent requests cannot both start an interactive sign-in and show
        // the user two prompts for the same scan.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_hasRecord)
            {
                await SignInAsync(requestContext, cancellationToken).ConfigureAwait(false);
                return await _inner.GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return await _inner.GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false);
            }
            catch (AuthenticationFailedException)
            {
                // The record is still valid as a name, but the token behind it has expired or been
                // revoked, so silent authentication cannot succeed. Sign in again rather than
                // failing the scan, and replace the record with the new one.
                _store.Clear();
                _hasRecord = false;
                await SignInAsync(requestContext, cancellationToken).ConfigureAwait(false);
                return await _inner.GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SignInAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var record = await _authenticate(requestContext, cancellationToken).ConfigureAwait(false);
        await _store.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        _hasRecord = true;
    }
}
