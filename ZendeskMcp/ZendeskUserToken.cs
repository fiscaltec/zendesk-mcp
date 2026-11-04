using System.Text.Json.Serialization;

namespace ZendeskMcp;

/// <summary>
/// One person's Zendesk OAuth grant, stored against their Entra object id. Serialised as the value
/// of their Key Vault secret, so every field here is something that may sit in a vault.
/// </summary>
/// <param name="AccessToken">The Zendesk bearer token.</param>
/// <param name="RefreshToken">Present when Zendesk issued an expiring token.</param>
/// <param name="ExpiresAt">When <paramref name="AccessToken"/> stops working, or null for a non-expiring token.</param>
/// <param name="RefreshExpiresAt">When <paramref name="RefreshToken"/> stops working, if known.</param>
/// <param name="ZendeskUserId">The Zendesk user the grant belongs to, confirmed at connect time.</param>
/// <param name="ZendeskEmail">That user's Zendesk email, confirmed at connect time.</param>
/// <param name="ZendeskRole">That user's Zendesk role at connect time (agent or admin).</param>
/// <param name="ConnectedAt">When the person connected.</param>
public sealed record ZendeskUserToken(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt,
    [property: JsonPropertyName("refresh_expires_at")] DateTimeOffset? RefreshExpiresAt,
    [property: JsonPropertyName("zendesk_user_id")] long ZendeskUserId,
    [property: JsonPropertyName("zendesk_email")] string ZendeskEmail,
    [property: JsonPropertyName("zendesk_role")] string ZendeskRole,
    [property: JsonPropertyName("connected_at")] DateTimeOffset ConnectedAt)
{
    /// <summary>
    /// True when the access token has expired or will within <paramref name="skew"/>. A token with no
    /// expiry never needs refreshing.
    /// </summary>
    public bool NeedsRefresh(DateTimeOffset now, TimeSpan skew) =>
        ExpiresAt is { } expires && expires - skew <= now;
}

/// <summary>
/// Thrown when the caller has no usable Zendesk connection. Its message carries the link that makes
/// one, and it is surfaced to the client verbatim (see <see cref="ToolErrorResult"/>), so the model can
/// hand the person the link instead of reporting a bare failure.
/// </summary>
public sealed class ZendeskConnectionRequiredException(string connectUrl, string reason)
    : Exception($"{reason} Ask the person to open this link to connect their Zendesk account, then try again: {connectUrl} (the link works once and expires shortly).")
{
    public string ConnectUrl { get; } = connectUrl;
}
