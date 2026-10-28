using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ZendeskMcp;

/// <summary>
/// Resolves the Zendesk credential for the current request: the signed-in person's own OAuth token,
/// refreshed when it is about to expire, or — in local development only — the configured API token.
/// </summary>
/// <remarks>
/// <para>
/// There is <b>no fallback to a shared credential</b>. A person who has not connected gets a
/// <see cref="ZendeskConnectionRequiredException"/> carrying their connect link, never a call made as
/// somebody else. That is the property per-user OAuth was chosen for: every Zendesk change is
/// attributed to the agent who asked for it, and Zendesk applies that agent's own role and group
/// restrictions.
/// </para>
/// <para>
/// Scoped, like the rest of the request pipeline, so it reads the caller from this request's
/// <see cref="HttpContext"/> and never another's.
/// </para>
/// </remarks>
public sealed class ZendeskCredentialProvider(
    IOptions<ZendeskServerOptions> options,
    IOptions<OAuthOptions> oauth,
    IZendeskTokenStore store,
    ZendeskOAuthClient oauthClient,
    ZendeskConnectLinks connectLinks,
    ILogger<ZendeskCredentialProvider> logger,
    IHttpContextAccessor? httpContextAccessor = null,
    TimeProvider? timeProvider = null)
{
    // Refresh this long before expiry, so a token never expires between being read and being used.
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    // One refresh per person at a time. Zendesk rotates the refresh token on use, so two concurrent
    // refreshes would have the loser present a spent token, fail with invalid_grant, and disconnect a
    // person who did nothing wrong.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshLocks = new(StringComparer.Ordinal);

    private readonly ZendeskServerOptions _options = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>True when every call is made with the development API token rather than per-user OAuth.</summary>
    public bool UsesDevelopmentToken => !string.IsNullOrWhiteSpace(_options.DevelopmentApiToken);

    /// <summary>The Authorization header for a Zendesk API call made by the current caller.</summary>
    public async Task<AuthenticationHeaderValue> GetAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        if (UsesDevelopmentToken)
        {
            var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.DevelopmentEmail}/token:{_options.DevelopmentApiToken}"));
            return new AuthenticationHeaderValue("Basic", basic);
        }

        var token = await GetUserTokenAsync(cancellationToken);
        return new AuthenticationHeaderValue("Bearer", token.AccessToken);
    }

    /// <summary>
    /// The caller's stored grant, refreshed if needed. Throws <see cref="ZendeskConnectionRequiredException"/>
    /// when there is none or it can no longer be refreshed.
    /// </summary>
    public async Task<ZendeskUserToken> GetUserTokenAsync(CancellationToken cancellationToken = default)
    {
        var objectId = RequireObjectId();
        var token = await store.GetAsync(objectId, cancellationToken);
        if (token is null)
        {
            throw await ConnectionRequiredAsync(objectId, "Your Zendesk account is not connected to this server yet.", cancellationToken);
        }

        if (!token.NeedsRefresh(_time.GetUtcNow(), RefreshSkew))
        {
            return token;
        }

        var gate = RefreshLocks.GetOrAdd(objectId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Re-read under the lock: a concurrent call may already have refreshed it.
            token = await store.GetAsync(objectId, cancellationToken);
            if (token is null)
            {
                throw await ConnectionRequiredAsync(objectId, "Your Zendesk account is not connected to this server yet.", cancellationToken);
            }

            if (!token.NeedsRefresh(_time.GetUtcNow(), RefreshSkew))
            {
                return token;
            }

            if (string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                await store.RemoveAsync(objectId, cancellationToken);
                throw await ConnectionRequiredAsync(objectId, "Your Zendesk connection has expired.", cancellationToken);
            }

            ZendeskOAuthClient.TokenResponse refreshed;
            try
            {
                refreshed = await oauthClient.RefreshAsync(token.RefreshToken, cancellationToken);
            }
            catch (ZendeskOAuthException ex) when (ex.Error is "invalid_grant" or "invalid_token" or "unauthorized_client")
            {
                // Expired, revoked in Zendesk, or the agent was downgraded: only a new sign-in helps.
                await store.RemoveAsync(objectId, cancellationToken);
                throw await ConnectionRequiredAsync(objectId, "Your Zendesk connection has expired or was revoked.", cancellationToken);
            }

            var renewed = token with
            {
                AccessToken = refreshed.AccessToken,
                RefreshToken = refreshed.RefreshToken ?? token.RefreshToken,
                ExpiresAt = refreshed.ExpiresIn is { } expiresIn ? _time.GetUtcNow().AddSeconds(expiresIn) : null,
                RefreshExpiresAt = refreshed.RefreshTokenExpiresIn is { } refreshIn
                    ? _time.GetUtcNow().AddSeconds(refreshIn)
                    : token.RefreshExpiresAt,
            };
            await store.SaveAsync(objectId, renewed, cancellationToken);
            return renewed;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Called when Zendesk answers 401 to the caller's token: forgets the grant and returns the
    /// exception for the caller to throw, which asks them to reconnect.
    /// </summary>
    public async Task<Exception> OnTokenRejectedAsync(CancellationToken cancellationToken = default)
    {
        if (UsesDevelopmentToken)
        {
            return new UnauthorizedAccessException(
                "Zendesk rejected Zendesk:DevelopmentApiToken. Check the token and Zendesk:DevelopmentEmail.");
        }

        var objectId = RequireObjectId();
        logger.LogWarning("Zendesk rejected the stored token for {UserObjectId}; asking them to reconnect", objectId);
        await store.RemoveAsync(objectId, cancellationToken);
        return await ConnectionRequiredAsync(objectId, "Zendesk no longer accepts your connection (it may have been revoked or your Zendesk role changed).", cancellationToken);
    }

    /// <summary>A connect link for the current caller, for the connection tools.</summary>
    public async Task<string> CreateConnectLinkAsync(CancellationToken cancellationToken = default)
    {
        var objectId = RequireObjectId();
        return await connectLinks.CreateAsync(objectId, CallerIdentity.TryGetSignInName(User), PublicBaseUrl(), cancellationToken);
    }

    /// <summary>Revokes the caller's grant at Zendesk and forgets it. Returns false when there was nothing to remove.</summary>
    public async Task<bool> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var objectId = RequireObjectId();
        var token = await store.GetAsync(objectId, cancellationToken);
        if (token is null)
        {
            return false;
        }

        await oauthClient.RevokeAsync(token.AccessToken, cancellationToken);
        await store.RemoveAsync(objectId, cancellationToken);
        return true;
    }

    /// <summary>The caller's stored grant without refreshing it, or null.</summary>
    public Task<ZendeskUserToken?> PeekAsync(CancellationToken cancellationToken = default) =>
        store.GetAsync(RequireObjectId(), cancellationToken);

    private ClaimsPrincipal? User => httpContextAccessor?.HttpContext?.User;

    private string RequireObjectId() =>
        CallerIdentity.TryGetObjectId(User)
        ?? throw new UnauthorizedAccessException(
            "Your sign-in carries no Entra object id, so there is no Zendesk connection to use. Sign in to this server again.");

    private async Task<ZendeskConnectionRequiredException> ConnectionRequiredAsync(string objectId, string reason, CancellationToken cancellationToken)
    {
        var url = await connectLinks.CreateAsync(objectId, CallerIdentity.TryGetSignInName(User), PublicBaseUrl(), cancellationToken);
        return new ZendeskConnectionRequiredException(url, reason);
    }

    // Prefer the configured origin, for the same Host-header reason the OAuth metadata does.
    private string PublicBaseUrl()
    {
        var configured = oauth.Value.PublicBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var request = httpContextAccessor?.HttpContext?.Request;
        return request is null ? string.Empty : $"{request.Scheme}://{request.Host}";
    }
}
