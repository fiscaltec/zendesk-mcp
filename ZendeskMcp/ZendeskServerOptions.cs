namespace ZendeskMcp;

/// <summary>
/// The Zendesk side of the configuration: which Zendesk account to call, how each user's Zendesk
/// OAuth connection is made and stored, and the account-level safety switches that sit on top of
/// the Entra permission tiers.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every Zendesk call is made as the signed-in person</b>, with the Zendesk OAuth token they
/// granted through <c>/zendesk/connect</c>. There is no shared service account in production, so a
/// ticket update is attributed to the agent who asked for it, and Zendesk's own role and group
/// permissions apply on top of this server's tiers. See <see cref="ZendeskCredentialProvider"/>.
/// </para>
/// <para>
/// <see cref="DevelopmentApiToken"/> is the exception, for local development only: with no Key Vault
/// configured it authenticates every call as <see cref="DevelopmentEmail"/> using a Zendesk API
/// token. <see cref="StartupGuards"/> refuses it alongside a Key Vault.
/// </para>
/// </remarks>
public class ZendeskServerOptions
{
    public const string SectionName = "Zendesk";

    /// <summary>The account subdomain: <c>fiscaltec</c> for <c>https://fiscaltec.zendesk.com</c>.</summary>
    public string? Subdomain { get; set; }

    /// <summary>
    /// The vault holding the Zendesk OAuth client secret and one secret per connected user. Separate
    /// from any vault holding other services' secrets, because this server's identity needs
    /// <i>write</i> access here to store tokens.
    /// </summary>
    public string? KeyVaultUri { get; set; }

    /// <summary>The Zendesk OAuth client's unique identifier (Admin Centre → Apps and integrations → APIs → OAuth clients).</summary>
    public string? OAuthClientId { get; set; }

    /// <summary>Key Vault secret holding the Zendesk OAuth client secret.</summary>
    public string OAuthClientSecretRef { get; set; } = "zendesk-oauth-client-secret";

    /// <summary>Local development only: the Zendesk OAuth client secret, when no Key Vault is configured.</summary>
    public string? DevelopmentOAuthClientSecret { get; set; }

    /// <summary>
    /// Scopes requested from Zendesk. <c>read write</c> covers tickets, users, organisations, macros
    /// and the Help Center; the person's Zendesk role still limits what either can reach.
    /// </summary>
    public string OAuthScopes { get; set; } = "read write";

    /// <summary>
    /// Lifetime requested for Zendesk access tokens, in seconds. When set, Zendesk issues an
    /// expiring access token plus a refresh token, and <see cref="ZendeskCredentialProvider"/>
    /// refreshes it. Null requests Zendesk's legacy non-expiring token.
    /// </summary>
    public int? AccessTokenExpiresInSeconds { get; set; } = 7200;

    /// <summary>Lifetime requested for refresh tokens, in seconds. Only sent with <see cref="AccessTokenExpiresInSeconds"/>.</summary>
    public int? RefreshTokenExpiresInSeconds { get; set; } = 7_776_000;

    /// <summary>How long a decrypted token is held in memory before Key Vault is read again.</summary>
    public TimeSpan TokenCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a <c>/zendesk/connect</c> link handed to a tool caller stays usable.</summary>
    public TimeSpan ConnectLinkLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Refuse a connection whose Zendesk email differs from the caller's Entra sign-in name. This is
    /// what binds a Zendesk login to the Entra identity it is stored under: without it, a connect link
    /// opened by someone else would store <i>their</i> Zendesk token against the link's owner.
    /// </summary>
    public bool RequireEmailMatch { get; set; } = true;

    /// <summary>Local development only: the Zendesk user an API token belongs to.</summary>
    public string? DevelopmentEmail { get; set; }

    /// <summary>Local development only: a Zendesk API token, used for every call when no Key Vault is configured.</summary>
    public string? DevelopmentApiToken { get; set; }

    /// <summary>Upper bound on pages the server will fetch for one tool call when it pages on the caller's behalf.</summary>
    public int MaxAutoPageFetches { get; set; } = 10;

    /// <summary>Upper bound on tickets read by one analytics call (metrics summary, SLA summary and queue).</summary>
    public int MaxAnalyticsTickets { get; set; } = 2000;

    /// <summary>Largest attachment the server will download or upload, in bytes.</summary>
    public long MaxAttachmentBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>
    /// Account-level switches for the actions with the widest reach. These apply to everyone, on
    /// top of each person's Entra tier and Zendesk role, and mirror the settings a hosted Zendesk
    /// MCP exposes. All default to off.
    /// </summary>
    public ZendeskFeatureOptions Features { get; set; } = new();

    public string BaseUrl => $"https://{Subdomain}.zendesk.com";

    public void Validate()
    {
        Subdomain = Subdomain?.Trim();
        if (string.IsNullOrWhiteSpace(Subdomain)
            || !Subdomain.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            throw new InvalidOperationException(
                $"Zendesk:Subdomain must be set to the account subdomain, letters, digits and hyphens only (got '{Subdomain}').");
        }

        KeyVaultUri = KeyVaultUri?.Trim();
        OAuthClientId = OAuthClientId?.Trim();
        OAuthClientSecretRef = OAuthClientSecretRef?.Trim() ?? string.Empty;
        DevelopmentOAuthClientSecret = DevelopmentOAuthClientSecret?.Trim();
        DevelopmentEmail = DevelopmentEmail?.Trim();
        DevelopmentApiToken = DevelopmentApiToken?.Trim();
        OAuthScopes = string.IsNullOrWhiteSpace(OAuthScopes) ? "read write" : OAuthScopes.Trim();

        if (!string.IsNullOrWhiteSpace(KeyVaultUri)
            && (!Uri.TryCreate(KeyVaultUri, UriKind.Absolute, out var vaultUri) || vaultUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Zendesk:KeyVaultUri must be an absolute https URI (got '{KeyVaultUri}').");
        }

        var hasDevToken = !string.IsNullOrWhiteSpace(DevelopmentApiToken);
        if (hasDevToken && string.IsNullOrWhiteSpace(DevelopmentEmail))
        {
            throw new InvalidOperationException(
                "Zendesk:DevelopmentEmail is required with Zendesk:DevelopmentApiToken (Zendesk API tokens authenticate as '{email}/token').");
        }

        if (!hasDevToken)
        {
            // Per-user OAuth is the only other way to reach Zendesk, so its client must be complete.
            if (string.IsNullOrWhiteSpace(OAuthClientId))
            {
                throw new InvalidOperationException(
                    "Zendesk:OAuthClientId is required unless Zendesk:DevelopmentApiToken is set (local development).");
            }

            if (string.IsNullOrWhiteSpace(KeyVaultUri) && string.IsNullOrWhiteSpace(DevelopmentOAuthClientSecret))
            {
                throw new InvalidOperationException(
                    "Either Zendesk:KeyVaultUri must be set (production) or Zendesk:DevelopmentOAuthClientSecret (local development).");
            }

            if (!string.IsNullOrWhiteSpace(KeyVaultUri) && string.IsNullOrWhiteSpace(OAuthClientSecretRef))
            {
                throw new InvalidOperationException(
                    "Zendesk:OAuthClientSecretRef cannot be empty when Zendesk:KeyVaultUri is set.");
            }
        }

        if (AccessTokenExpiresInSeconds is { } access && (access < 300 || access > 172_800))
        {
            throw new InvalidOperationException(
                $"Zendesk:AccessTokenExpiresInSeconds must be between 300 and 172800 (got {access}).");
        }

        if (RefreshTokenExpiresInSeconds is { } refresh && (refresh < 604_800 || refresh > 7_776_000))
        {
            throw new InvalidOperationException(
                $"Zendesk:RefreshTokenExpiresInSeconds must be between 604800 and 7776000 (got {refresh}).");
        }

        if (MaxAutoPageFetches < 1)
        {
            throw new InvalidOperationException(
                $"Zendesk:MaxAutoPageFetches must be >= 1 (got {MaxAutoPageFetches}).");
        }

        if (MaxAnalyticsTickets < 1)
        {
            throw new InvalidOperationException(
                $"Zendesk:MaxAnalyticsTickets must be >= 1 (got {MaxAnalyticsTickets}).");
        }

        if (ConnectLinkLifetime <= TimeSpan.Zero || ConnectLinkLifetime > TimeSpan.FromHours(1))
        {
            throw new InvalidOperationException(
                $"Zendesk:ConnectLinkLifetime must be positive and at most one hour (got {ConnectLinkLifetime}).");
        }
    }
}

/// <summary>
/// Account-wide safety switches. Each guards an action whose effect reaches beyond the agent making
/// it — a customer's inbox, the public Help Center, every agent's macro list, or who can see an
/// organisation's tickets — and is off until an administrator turns it on.
/// </summary>
public class ZendeskFeatureOptions
{
    /// <summary>Allow public ticket comments, CCs and public macro replies (they email customers).</summary>
    public bool AllowPublicReplies { get; set; }

    /// <summary>Allow publishing Help Center articles and translations rather than saving drafts.</summary>
    public bool AllowArticlePublishing { get; set; }

    /// <summary>Allow creating, editing, activating and deactivating macros.</summary>
    public bool AllowMacroChanges { get; set; }

    /// <summary>Allow changes to users and organisations: new end users from an email, and organisation memberships.</summary>
    public bool AllowUserAndOrganizationChanges { get; set; }
}
