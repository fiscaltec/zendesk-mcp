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
    internal const string StateCookie = "zendesk_connect_state";

    public static void MapZendeskConnect(this WebApplication app)
    {
        app.MapGet(ConnectPath, async (HttpContext ctx, ZendeskConnectLinks links, ZendeskOAuthClient oauthClient,
            IOptions<OAuthOptions> oauth, IOptions<ZendeskServerOptions> zendesk) =>
        {
            if (!string.IsNullOrWhiteSpace(zendesk.Value.DevelopmentApiToken))
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

            ZendeskOAuthClient.TokenResponse token;
            ZendeskIdentity identity;
            try
            {
                token = await oauthClient.ExchangeCodeAsync(code, CallbackUrl(ctx, oauth.Value), attempt.CodeVerifier, ctx.RequestAborted);
                identity = await oauthClient.GetIdentityAsync(token.AccessToken, ctx.RequestAborted);
            }
            catch (ZendeskOAuthException ex)
            {
                return Page(StatusCodes.Status502BadGateway, "Connection not completed", ex.Message);
            }

            var refusal = Refusal(identity, attempt.SignInName, zendesk.Value.RequireEmailMatch);
            if (refusal is not null)
            {
                // Revoked straight away: a token for the wrong person must not outlive the refusal.
                await oauthClient.RevokeAsync(token.AccessToken, ctx.RequestAborted);
                logger.LogWarning(
                    "Refused a Zendesk connection for {UserObjectId}: {RefusalReason} (Zendesk user {ZendeskUserId})",
                    attempt.ObjectId, refusal.Value.LogReason, identity.Id);
                return Page(StatusCodes.Status403Forbidden, "Connection refused", refusal.Value.Message);
            }

            await store.SaveAsync(attempt.ObjectId, oauthClient.ToUserToken(token, identity), ctx.RequestAborted);
            await links.RedeemAsync(attempt.Ticket, ctx.RequestAborted);
            logger.LogInformation("Connected Zendesk user {ZendeskUserId} for {UserObjectId}", identity.Id, attempt.ObjectId);

            return Page(StatusCodes.Status200OK, "Zendesk connected",
                $"Connected as {identity.Name} ({identity.Email}). You can close this window and go back to Claude.");
        });
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

    private static string CallbackUrl(HttpContext ctx, OAuthOptions oauth)
    {
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
