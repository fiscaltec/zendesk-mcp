using System.Net;
using Microsoft.Extensions.Options;

namespace ZendeskMcp;

/// <summary>
/// The browser half of the per-user Zendesk connection: <c>GET /zendesk/connect</c> sends the person
/// to Zendesk to grant access, and <c>GET /zendesk/callback</c> receives the grant, checks whose it is
/// and stores it.
/// </summary>
/// <remarks>
/// <para>
/// Neither endpoint is behind the Entra bearer requirement — a browser has no such token. Identity
/// comes from the link instead (issued only to an authenticated tool caller; see
/// <see cref="ZendeskConnectLinks"/>), and is <b>confirmed</b> at the callback by matching the
/// Zendesk login's email to the sign-in name the link was issued for.
/// </para>
/// <para>
/// The <c>state</c> parameter is also set as an HttpOnly cookie and compared at the callback, so a
/// code obtained in one browser cannot be completed in another (OAuth login CSRF). SameSite=Lax is
/// what lets the cookie ride the top-level redirect back from Zendesk.
/// </para>
/// </remarks>
public static class ZendeskConnectEndpoints
{
    public const string ConnectPath = "/zendesk/connect";
    public const string CallbackPath = "/zendesk/callback";

    /// <summary>How long the callback's check, store and spend of the link may take once begun, whatever the browser does.</summary>
    internal static readonly TimeSpan StoreTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long the code exchange may take once begun, whatever the browser does.</summary>
    internal static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(30);
    internal const string StateCookie = "zendesk_connect_state";

    public static void MapZendeskConnect(this WebApplication app)
    {
        app.MapGet(ConnectPath, async (HttpContext ctx, ZendeskConnectLinks links, ZendeskOAuthClient oauthClient,
            IOptions<OAuthOptions> oauth, IOptions<ZendeskServerOptions> zendesk) =>
        {
            if (zendesk.Value.HasDevelopmentCredential)
            {
                return Page(StatusCodes.Status404NotFound, "Not available",
                    "This server is running with a development API token, so there is nothing to connect.");
            }

            var ticketId = ctx.Request.Query["ticket"].ToString();
            var ticket = string.IsNullOrWhiteSpace(ticketId) ? null : await links.PeekAsync(ticketId, ctx.RequestAborted);
            if (ticket is null)
            {
                return Page(StatusCodes.Status400BadRequest, "This link has expired",
                    "Connect links work once and expire after a few minutes. Ask Claude for a new one — any Zendesk tool will offer it.");
            }

            var verifier = ZendeskOAuthClient.NewCodeVerifier();
            var state = await links.BeginAttemptAsync(
                new ZendeskConnectLinks.ConnectAttempt(ticketId, ticket.ObjectId, ticket.SignInName, verifier),
                ctx.RequestAborted);

            ctx.Response.Cookies.Append(StateCookie, state, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/zendesk",
                MaxAge = ZendeskConnectLinks.AttemptLifetime,
            });

            return Results.Redirect(oauthClient.BuildAuthorizeUrl(CallbackUrl(ctx, oauth.Value), state, verifier));
        });

        app.MapGet(CallbackPath, async (HttpContext ctx, ZendeskConnectLinks links, ZendeskOAuthClient oauthClient,
            IZendeskTokenStore store, IOptions<OAuthOptions> oauth, IOptions<ZendeskServerOptions> zendesk,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("ZendeskMcp.ZendeskConnect");
            var query = ctx.Request.Query;
            var state = query["state"].ToString();
            var cookieState = ctx.Request.Cookies[StateCookie];
            ctx.Response.Cookies.Delete(StateCookie, new CookieOptions { Path = "/zendesk", Secure = true, SameSite = SameSiteMode.Lax });

            if (string.IsNullOrWhiteSpace(state)
                || string.IsNullOrWhiteSpace(cookieState)
                || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(state), System.Text.Encoding.UTF8.GetBytes(cookieState)))
            {
                return Page(StatusCodes.Status400BadRequest, "Connection not completed",
                    "This sign-in did not start in this browser, or took too long. Open your connect link again.");
            }

            var attempt = await links.CompleteAttemptAsync(state, ctx.RequestAborted);
            if (attempt is null)
            {
                return Page(StatusCodes.Status400BadRequest, "Connection not completed",
                    "This sign-in took too long or was already used. Open your connect link again.");
            }

            var error = query["error"].ToString();
            if (!string.IsNullOrWhiteSpace(error))
            {
                return Page(StatusCodes.Status400BadRequest, "Access was not granted",
                    "Zendesk reported that access was not granted. Open your connect link again if that was not intended.");
            }

            var code = query["code"].ToString();
            if (string.IsNullOrWhiteSpace(code))
            {
                return Page(StatusCodes.Status400BadRequest, "Connection not completed", "Zendesk returned no authorisation code.");
            }

            // On a bounded token of its own, not the browser's: once the code is sent, Zendesk may issue a
            // grant whatever the browser does, and an exchange cancelled at that point would leave it live
            // with nobody to revoke it. Finishing the exchange hands any issued grant to UnkeptGrant.
            ZendeskOAuthClient.TokenResponse token;
            using (var exchange = new CancellationTokenSource(ExchangeTimeout))
            {
                try
                {
                    token = await oauthClient.ExchangeCodeAsync(code, CallbackUrl(ctx, oauth.Value), attempt.CodeVerifier, exchange.Token);
                }
                catch (ZendeskOAuthException ex)
                {
                    return Page(StatusCodes.Status502BadGateway, "Connection not completed", ex.Message);
                }
            }

            // From here — once the exchange has returned a grant — a live grant exists at Zendesk.
            // Every exit that does not keep it revokes it:
            // the refusals below explicitly, and anything else — the browser giving up, a timeout, an
            // unexpected exception — when `unkept` is disposed. Revocation runs on a bounded token of
            // its own, because RequestAborted may be the very thing that has just been cancelled.
            await using var unkept = new UnkeptGrant(oauthClient, logger, token.AccessToken);

            ZendeskIdentity identity;
            try
            {
                identity = await oauthClient.GetIdentityAsync(token.AccessToken, ctx.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ctx.RequestAborted.IsCancellationRequested)
            {
                // A token whose owner could not be checked is never stored, and is revoked straight
                // away like a refused one, rather than left live at Zendesk. An HTTP timeout lands here
                // too: it is a TaskCanceledException, but not the browser's cancellation.
                await unkept.RevokeAsync();
                return Page(StatusCodes.Status502BadGateway, "Connection not completed",
                    ex is ZendeskOAuthException ? ex.Message : "Zendesk could not confirm who this sign-in belongs to. Open your connect link again.");
            }

            var refusal = Refusal(identity, attempt.SignInName, zendesk.Value.RequireEmailMatch);
            if (refusal is not null)
            {
                // Revoked straight away: a token for the wrong person must not outlive the refusal.
                await unkept.RevokeAsync();
                logger.LogWarning(
                    "Refused a Zendesk connection for {UserObjectId}: {RefusalReason} (Zendesk user {ZendeskUserId})",
                    attempt.ObjectId, refusal.Value.LogReason, identity.Id);
                return Page(StatusCodes.Status403Forbidden, "Connection refused", refusal.Value.Message);
            }

            // Under the person's lock, so a refresh of their previous grant that is already in flight
            // cannot land after this and overwrite the new connection with the old one renewed.
            //
            // The link is checked and spent under the same lock. One link opened in two browsers
            // starts two attempts; without this, the second would store a grant after the first had
            // spent the link, and would reconnect someone who had disconnected in between (#13).
            //
            // Once the lock is held the section runs to the end on a bounded token of its own, not the
            // browser's: a browser that gave up after the store would otherwise leave the grant stored
            // and the link unspent, open to exactly that second attempt.
            var gate = ZendeskCredentialProvider.LockFor(attempt.ObjectId);
            await gate.WaitAsync(ctx.RequestAborted);
            using var commit = new CancellationTokenSource(StoreTimeout);
            ZendeskUserToken? replaced = null;
            var replacedUnreadable = false;
            var linkSpent = false;
            var saveEntered = false;
            try
            {
                if (await links.PeekAsync(attempt.Ticket, commit.Token) is null)
                {
                    linkSpent = true;
                }
                else
                {
                    try
                    {
                        replaced = await store.GetAsync(attempt.ObjectId, commit.Token);
                    }
                    catch (ZendeskStoredTokenUnreadableException ex)
                    {
                        // Replaced all the same — the person is reconnecting precisely because the old
                        // grant does not work — but revoked below as far as it can be read.
                        replaced = ex.Salvaged;
                        replacedUnreadable = true;
                    }

                    saveEntered = true;
                    await store.SaveAsync(attempt.ObjectId, oauthClient.ToUserToken(token, identity), commit.Token);
                    await links.RedeemAsync(attempt.Ticket, commit.Token);
                    // Kept only once stored and the link spent; any exit before this revokes it.
                    unkept.Keep();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || commit.IsCancellationRequested)
            {
                // Not persisted, so not kept: revoke it rather than leave a live grant nobody holds.
                // (The store cached it before the write failed; revoked, a call with it meets a 401
                // and is handed a fresh connect link.)
                await unkept.RevokeAsync();

                // Once SaveAsync was entered the previous grant may already be overwritten: the write
                // can land and still fail, or RedeemAsync or the commit timeout can fail after it. Left
                // like that, the previous grant would be neither stored nor revoked, and the store would
                // hold the grant just revoked. So the previous grant is revoked too and the connection
                // forgotten (a tombstone), both on cleanup tokens and still under the person's lock;
                // the link is unspent, so the person can simply open it again.
                if (saveEntered)
                {
                    if (replaced is not null && replaced.AccessToken != token.AccessToken
                        && !await UnkeptGrant.RevokeGrantForCleanupAsync(oauthClient, logger, replaced))
                    {
                        logger.LogWarning("Could not confirm the revocation of the Zendesk grant replaced for {UserObjectId}; it may still be live at Zendesk",
                            attempt.ObjectId);
                    }

                    await UnkeptGrant.RemoveForCleanupAsync(store, logger, attempt.ObjectId);
                }

                logger.LogError("Could not store the Zendesk connection for {UserObjectId} ({FailureType}); the new grant was revoked",
                    attempt.ObjectId, ex.GetType().Name);
                return Page(StatusCodes.Status503ServiceUnavailable, "Connection not completed",
                    "Your Zendesk account could not be saved. Open your connect link again in a few minutes.");
            }
            finally
            {
                gate.Release();
            }

            if (linkSpent)
            {
                // Revoked straight away, like a refused one: nothing holds it, so it must not stay live.
                var revoked = await unkept.RevokeAsync();
                logger.LogWarning("Refused a Zendesk connection for {UserObjectId}: its connect link was already used or has expired; revoking the new grant {RevocationOutcome}",
                    attempt.ObjectId, revoked ? "succeeded" : "was not confirmed");
                return Page(StatusCodes.Status400BadRequest, "Connection not completed",
                    "This connect link has already been used, or has expired. If you are not connected, ask Claude for a new link — any Zendesk tool will offer it.");
            }

            // A reconnect replaces the previous grant; revoke it so it does not stay live at Zendesk.
            // The whole grant, not just its access token: the usual reason to reconnect is an expired
            // one, and revoking an expired access token answers 401 — read as revoked — while its
            // refresh token stays live (#12).
            if (replaced is not null && replaced.AccessToken != token.AccessToken)
            {
                // On a cleanup token, never the browser's: this runs after Keep(), so a browser that
                // left here would otherwise cancel it and leave the replaced grant live. The whole
                // grant, through RevokeGrantAsync, not just its access token (#12).
                if (!await UnkeptGrant.RevokeGrantForCleanupAsync(oauthClient, logger, replaced))
                {
                    logger.LogWarning("Could not confirm the revocation of the Zendesk grant replaced for {UserObjectId}; it may still be live at Zendesk",
                        attempt.ObjectId);
                }
            }
            else if (replacedUnreadable)
            {
                logger.LogError("The Zendesk grant replaced for {UserObjectId} could not be read, so it was not revoked; it may still be live at Zendesk",
                    attempt.ObjectId);
            }

            logger.LogInformation("Connected Zendesk user {ZendeskUserId} for {UserObjectId}", identity.Id, attempt.ObjectId);

            return Page(StatusCodes.Status200OK, "Zendesk connected",
                $"Connected as {identity.Name} ({identity.Email}). You can close this window and go back to Claude.");
        });
    }

    /// <summary>
    /// A grant just issued by the code exchange, revoked on every exit from the callback that does not
    /// keep it. <see cref="RevokeAsync"/> revokes it on purpose (a refusal); <see cref="Keep"/> marks
    /// it stored; disposal revokes it if neither happened — the browser gave up, a call timed out, or
    /// something threw.
    /// </summary>
    private sealed class UnkeptGrant(ZendeskOAuthClient oauthClient, ILogger logger, string accessToken) : IAsyncDisposable
    {
        // Revocation is one request; this bounds it without tying it to a request that has gone.
        internal static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

        private bool _settled;

        public void Keep() => _settled = true;

        public Task<bool> RevokeAsync()
        {
            _settled = true;
            return RevokeForCleanupAsync(oauthClient, logger, accessToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_settled)
            {
                return;
            }

            _settled = true;
            var revoked = await RevokeForCleanupAsync(oauthClient, logger, accessToken);
            logger.LogWarning("A Zendesk connection was abandoned before it was stored; revoking the new grant {RevocationOutcome}",
                revoked ? "succeeded" : "was not confirmed");
        }

        /// <summary>
        /// Forgets the person's connection (a tombstone) on a bounded token of its own. Never throws:
        /// it runs on a path that is already reporting a failure, and only the exception type is logged.
        /// </summary>
        public static async Task RemoveForCleanupAsync(IZendeskTokenStore store, ILogger logger, string objectId)
        {
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            try
            {
                await store.RemoveAsync(objectId, cleanup.Token);
            }
            catch (Exception ex)
            {
                logger.LogError("Could not forget the Zendesk connection for {UserObjectId} after a failed store ({FailureType})",
                    objectId, ex.GetType().Name);
            }
        }

        /// <summary>
        /// Revokes on a bounded token of its own, never the request's: cleanup must still happen when
        /// the browser has gone. Never throws; false means Zendesk did not confirm.
        /// </summary>
        public static async Task<bool> RevokeForCleanupAsync(ZendeskOAuthClient oauthClient, ILogger logger, string accessToken)
        {
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            try
            {
                return await oauthClient.RevokeAsync(accessToken, cleanup.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Zendesk token revocation did not complete within {CleanupSeconds} s", CleanupTimeout.TotalSeconds);
                return false;
            }
        }

        /// <summary>
        /// Revokes a whole grant being discarded (through <see cref="ZendeskOAuthClient.RevokeGrantAsync"/>,
        /// which refreshes an expired grant first) on a bounded token of its own, never the request's.
        /// Never throws; false means Zendesk did not confirm.
        /// </summary>
        public static async Task<bool> RevokeGrantForCleanupAsync(ZendeskOAuthClient oauthClient, ILogger logger, ZendeskUserToken grant)
        {
            using var cleanup = new CancellationTokenSource(CleanupTimeout);
            try
            {
                return await oauthClient.RevokeGrantAsync(grant, cleanup.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Zendesk grant revocation did not complete within {CleanupSeconds} s", CleanupTimeout.TotalSeconds);
                return false;
            }
        }
    }

    /// <summary>
    /// Why a Zendesk identity must not be stored for this link, or null when it may. Public for tests.
    /// </summary>
    public static (string Message, string LogReason)? Refusal(ZendeskIdentity identity, string? signInName, bool requireEmailMatch)
    {
        if (!identity.IsStaff)
        {
            return ("Only Zendesk agents and admins can connect. Sign in to Zendesk with your agent account and open the link again.",
                "not an agent or admin");
        }

        if (!requireEmailMatch)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(signInName))
        {
            return ("Your Microsoft sign-in did not include an email address, so this connection cannot be checked. Contact the Infrastructure team.",
                "no sign-in name on the link");
        }

        return string.Equals(identity.Email.Trim(), signInName.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : ("You are signed in to Zendesk as a different person from your Microsoft account. Sign out of Zendesk, sign in as yourself, and open the link again.",
                "Zendesk email does not match the sign-in name");
    }

    // The configured origin; the request's own only for unauthenticated local work (see
    // ZendeskCredentialProvider.PublicBaseUrl for why behind sign-in it must be configured).
    private static string CallbackUrl(HttpContext ctx, OAuthOptions oauth)
    {
        if (string.IsNullOrWhiteSpace(oauth.PublicBaseUrl) && !oauth.NoAuth)
        {
            throw new InvalidOperationException("OAuth:PublicBaseUrl must be set for the Zendesk connect flow.");
        }

        var baseUrl = string.IsNullOrWhiteSpace(oauth.PublicBaseUrl)
            ? $"{ctx.Request.Scheme}://{ctx.Request.Host}"
            : oauth.PublicBaseUrl;
        return $"{baseUrl.TrimEnd('/')}{CallbackPath}";
    }

    private static IResult Page(int statusCode, string title, string message)
    {
        var html = $$"""
            <!doctype html>
            <html lang="en-GB">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>
              body { font-family: system-ui, sans-serif; max-width: 36rem; margin: 4rem auto; padding: 0 1rem; line-height: 1.5; color: #1f2328; background: #fff; }
              h1 { font-size: 1.4rem; }
              @media (prefers-color-scheme: dark) { body { color: #e6edf3; background: #0d1117; } }
            </style>
            </head>
            <body>
            <h1>{{WebUtility.HtmlEncode(title)}}</h1>
            <p>{{WebUtility.HtmlEncode(message)}}</p>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", System.Text.Encoding.UTF8, statusCode);
    }
}
