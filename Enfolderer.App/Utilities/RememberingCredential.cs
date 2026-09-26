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
    private readonly Func<TokenRequestContext, CancellationToken, AuthenticationRecord> _authenticate;
    private readonly Func<TokenRequestContext, CancellationToken, Task<AuthenticationRecord>> _authenticateAsync;
    private readonly AuthenticationRecordStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _hasRecord;

    public RememberingCredential(
        TokenCredential inner,
        Func<TokenRequestContext, CancellationToken, AuthenticationRecord> authenticate,
        Func<TokenRequestContext, CancellationToken, Task<AuthenticationRecord>> authenticateAsync,
        AuthenticationRecordStore store,
        bool hasRecord)
    {
        _inner = inner;
        _authenticate = authenticate;
        _authenticateAsync = authenticateAsync;
        _store = store;
        _hasRecord = hasRecord;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        // Implemented without blocking on the async path: a caller on the UI thread would deadlock
        // if the interactive sign-in it is waiting for needed that same thread.
        _gate.Wait(cancellationToken);
        try
        {
            if (!_hasRecord)
            {
                SignIn(requestContext, cancellationToken);
                return _inner.GetToken(requestContext, cancellationToken);
            }

            try
            {
                return _inner.GetToken(requestContext, cancellationToken);
            }
            catch (AuthenticationRequiredException)
            {
                Forget();
                SignIn(requestContext, cancellationToken);
                return _inner.GetToken(requestContext, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

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
            catch (AuthenticationRequiredException)
            {
                // The record is still valid as a name, but the token behind it has expired or been
                // revoked, so silent authentication cannot succeed. Sign in again rather than
                // failing the scan, and replace the record with the new one.
                // Only this exception, which DisableAutomaticAuthentication raises for exactly that
                // case, is treated this way: a network error or service outage is an
                // AuthenticationFailedException too, and discarding a good record over one would be
                // the very thing this class exists to avoid.
                Forget();
                await SignInAsync(requestContext, cancellationToken).ConfigureAwait(false);
                return await _inner.GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Forget()
    {
        _store.Clear();
        _hasRecord = false;
    }

    private void SignIn(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var record = _authenticate(requestContext, cancellationToken);
        _store.Save(record);
        _hasRecord = true;
    }

    private async Task SignInAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        var record = await _authenticateAsync(requestContext, cancellationToken).ConfigureAwait(false);
        await _store.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        _hasRecord = true;
    }
}
