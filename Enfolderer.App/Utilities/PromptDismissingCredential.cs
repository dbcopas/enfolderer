using Azure.Core;

namespace Enfolderer.App.Utilities;

/// <summary>
/// Wraps a credential so a sign-in prompt is dismissed once the token request finishes, whether it
/// succeeded, failed or was cancelled. Without this the device code prompt would have to be modal
/// to know when to close, and a modal prompt blocks the polling it is telling the user to wait for.
/// </summary>
internal sealed class PromptDismissingCredential : TokenCredential
{
    private readonly TokenCredential _inner;
    private readonly Action _dismiss;

    public PromptDismissingCredential(TokenCredential inner, Action dismiss)
    {
        _inner = inner;
        _dismiss = dismiss;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        try { return _inner.GetToken(requestContext, cancellationToken); }
        finally { _dismiss(); }
    }

    public override async ValueTask<AccessToken> GetTokenAsync(
        TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        try { return await _inner.GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false); }
        finally { _dismiss(); }
    }
}
