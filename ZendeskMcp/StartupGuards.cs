namespace ZendeskMcp;

/// <summary>
/// Fail-fast configuration guards checked at startup, before the app serves traffic.
/// </summary>
public static class StartupGuards
{
    /// <summary>
    /// Refuses a configuration that disables authentication while a Key Vault is configured — that
    /// combination looks like a production deployment accidentally running unauthenticated. NoAuth is
    /// for local development only (which has no Key Vault and uses a development API token instead).
    /// </summary>
    public static void EnsureSafeAuthConfig(bool noAuth, string? keyVaultUri)
    {
        if (noAuth && !string.IsNullOrWhiteSpace(keyVaultUri))
        {
            throw new InvalidOperationException(
                "OAuth:NoAuth=true together with a Zendesk:KeyVaultUri is refused — this looks like a " +
                "production deployment running unauthenticated. NoAuth is for local development only " +
                "(no Key Vault). Remove NoAuth in any environment that uses Key Vault.");
        }
    }

    /// <summary>
    /// Refuses a development credential (<c>Zendesk:DevelopmentAccessToken</c> or the legacy
    /// <c>Zendesk:DevelopmentApiToken</c>) anywhere but unauthenticated local development. The
    /// token makes every Zendesk call as one person; behind real sign-in that would quietly turn a
    /// per-user server back into a shared service account, attributing every agent's changes to
    /// whoever owns the token — the exact outcome per-user OAuth exists to prevent.
    /// </summary>
    /// <remarks>
    /// Also refused outside the Development environment: NoAuth with no Key Vault is otherwise a valid
    /// shape for a mis-deployed container, and that container would be an unauthenticated public proxy
    /// for one person's Zendesk token.
    /// </remarks>
    public static void EnsureDevelopmentTokenIsLocal(string? developmentApiToken, string? keyVaultUri, bool noAuth, bool isDevelopment)
    {
        if (string.IsNullOrWhiteSpace(developmentApiToken))
        {
            return;
        }

        if (!noAuth || !string.IsNullOrWhiteSpace(keyVaultUri) || !isDevelopment)
        {
            throw new InvalidOperationException(
                "A Zendesk development credential (DevelopmentAccessToken / DevelopmentApiToken) is for unauthenticated local development only (OAuth:NoAuth=true, no " +
                "Zendesk:KeyVaultUri, the Development environment). With sign-in enabled it would make every person's Zendesk calls as the " +
                "token's owner. Remove it, and let each person connect their own Zendesk account.");
        }
    }

    /// <summary>
    /// Refuses <c>Zendesk:RequireEmailMatch=false</c> behind real sign-in. The email match is what binds
    /// a Zendesk login to the Entra identity it is stored under: without it, anyone who opens a person's
    /// connect link — forwarded, leaked, or coaxed out of a prompt-injected model — attaches their own
    /// Zendesk account to that person's identity. It may only be relaxed for unauthenticated local work.
    /// </summary>
    public static void EnsureIdentityBindingIsOn(string? requireEmailMatch, bool noAuth)
    {
        if (!noAuth && bool.TryParse(requireEmailMatch, out var required) && !required)
        {
            throw new InvalidOperationException(
                "Zendesk:RequireEmailMatch=false is refused with sign-in enabled: it would let a forwarded or leaked " +
                "connect link attach someone else's Zendesk account to a person's identity.");
        }
    }

    /// <summary>
    /// Resolves the upstream OIDC discovery document once at startup so a provider that is
    /// unreachable — or publishing a document missing an endpoint the proxy needs — refuses to
    /// start. Two of those endpoints are republished to MCP clients in our RFC 8414 document, so
    /// booting without having verified them means advertising unverified endpoints as fact; a
    /// refusal to start is the lesser failure, and a loud one. No-op when the OAuth proxy is off,
    /// because nothing then reads the document.
    /// </summary>
    /// <param name="metadata">Resolver whose cache this warms.</param>
    /// <param name="proxyEnabled">Whether <c>OAuth:SharedClientId</c> is set.</param>
    /// <param name="timeout">Bound on the fetch, so an unresponsive provider fails rather than hangs.</param>
    public static async Task EnsureUpstreamOidcEndpointsAsync(
        UpstreamOidcMetadata metadata,
        bool proxyEnabled,
        TimeSpan timeout)
    {
        if (!proxyEnabled)
        {
            return;
        }

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            _ = await metadata.GetAsync(cts.Token);
        }
        // Two specific clauses rather than one catch-all: UpstreamOidcMetadata funnels every failure
        // mode — transport errors included — into InvalidOperationException, and the only other way
        // out is the timeout above. Anything else would be a genuine bug, and letting it propagate
        // uncaught still refuses the start, just without the added context.
        catch (InvalidOperationException ex)
        {
            throw Unresolvable(ex);
        }
        catch (OperationCanceledException ex)
        {
            throw Unresolvable(ex);
        }
    }

    private static InvalidOperationException Unresolvable(Exception cause) =>
        new("The upstream OIDC discovery document could not be resolved, so the OAuth proxy cannot " +
            "determine the provider's authorize, token, jwks and userinfo endpoints. Refusing to start " +
            "rather than advertise endpoints that were never verified. Check that OAuth:Authority is the " +
            "provider's issuer URL and that its /.well-known/openid-configuration is reachable from " +
            $"this host. Underlying failure: {cause.Message}",
            cause);
}
