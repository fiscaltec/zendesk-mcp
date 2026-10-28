using System.Collections.Concurrent;
using System.Text.Json;
using Azure;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ZendeskMcp;

/// <summary>Where each person's Zendesk OAuth grant is kept, keyed by their Entra object id.</summary>
public interface IZendeskTokenStore
{
    /// <summary>The caller's grant, or null when they have not connected (or have disconnected).</summary>
    Task<ZendeskUserToken?> GetAsync(string objectId, CancellationToken cancellationToken = default);

    Task SaveAsync(string objectId, ZendeskUserToken token, CancellationToken cancellationToken = default);

    /// <summary>Forgets the caller's grant. Does not revoke it at Zendesk; see <see cref="ZendeskOAuthClient.RevokeAsync"/>.</summary>
    Task RemoveAsync(string objectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores one Key Vault secret per person, named <c>zendesk-user-{objectId}</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Removal writes a tombstone version rather than deleting the secret.</b> The vault has
/// soft-delete on (it cannot be turned off), and a soft-deleted secret's name cannot be set again
/// until it is purged — so a delete would make disconnect-then-reconnect fail with a 409 for the
/// whole retention period, and purging would need a far broader role than this identity holds. A
/// tombstone is a new current version that reads as "not connected"; earlier versions keep the old
/// token, which is why <see cref="ZendeskOAuthClient.RevokeAsync"/> revokes it at Zendesk first.
/// </para>
/// <para>
/// Reads are cached for <see cref="ZendeskServerOptions.TokenCacheDuration"/>, and a removal or save
/// replaces the cached entry, so this replica never serves a token it has just revoked. Another
/// replica could for up to the cache duration, which is one reason the app runs a single replica.
/// </para>
/// </remarks>
public sealed class KeyVaultZendeskTokenStore(
    SecretClient secrets,
    IMemoryCache cache,
    IOptions<ZendeskServerOptions> options,
    ILogger<KeyVaultZendeskTokenStore> logger,
    ZendeskMetrics? metrics = null) : IZendeskTokenStore
{
    private const string TombstoneValue = "{\"revoked\":true}";
    private readonly ZendeskServerOptions _options = options.Value;

    public static string SecretName(string objectId) => $"zendesk-user-{objectId}";

    private static string CacheKey(string objectId) => $"zendesk-token::{objectId}";

    public async Task<ZendeskUserToken?> GetAsync(string objectId, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue<CachedToken>(CacheKey(objectId), out var cached) && cached is not null)
        {
            metrics?.CacheLookup("user_token", hit: true);
            return cached.Token;
        }

        metrics?.CacheLookup("user_token", hit: false);

        ZendeskUserToken? token;
        try
        {
            var response = await secrets.GetSecretAsync(SecretName(objectId), cancellationToken: cancellationToken);
            token = Parse(response.Value.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            token = null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The object id is not personal data in the sense the audit rules guard (it resolves only
            // with directory access), and it is what an operator needs to find the secret.
            LogBestEffort(() => logger.LogError(ex,
                "Failed to read the Zendesk token for {UserObjectId} from Key Vault", objectId));
            throw;
        }

        cache.Set(CacheKey(objectId), new CachedToken(token), _options.TokenCacheDuration);
        return token;
    }

    public async Task SaveAsync(string objectId, ZendeskUserToken token, CancellationToken cancellationToken = default)
    {
        var secret = new KeyVaultSecret(SecretName(objectId), JsonSerializer.Serialize(token));
        secret.Properties.ContentType = "application/json";
        secret.Properties.Tags["kind"] = "zendesk-user-token";
        secret.Properties.Tags["zendesk-user-id"] = token.ZendeskUserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (token.RefreshExpiresAt is { } refreshExpiry)
        {
            secret.Properties.ExpiresOn = refreshExpiry;
        }

        await secrets.SetSecretAsync(secret, cancellationToken);
        cache.Set(CacheKey(objectId), new CachedToken(token), _options.TokenCacheDuration);
    }

    public async Task RemoveAsync(string objectId, CancellationToken cancellationToken = default)
    {
        var secret = new KeyVaultSecret(SecretName(objectId), TombstoneValue);
        secret.Properties.ContentType = "application/json";
        secret.Properties.Tags["kind"] = "zendesk-user-token";
        secret.Properties.Tags["revoked"] = "true";
        await secrets.SetSecretAsync(secret, cancellationToken);
        cache.Set(CacheKey(objectId), new CachedToken(null), _options.TokenCacheDuration);
    }

    internal static ZendeskUserToken? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            var token = JsonSerializer.Deserialize<ZendeskUserToken>(value);
            return token is null || string.IsNullOrWhiteSpace(token.AccessToken) ? null : token;
        }
        catch (JsonException)
        {
            // A tombstone has no access_token and fails the record's required members; anything else
            // unreadable is treated the same way — as no connection — so the person is asked to
            // reconnect rather than every call failing.
            return null;
        }
    }

    // Wrapped so "nothing stored" can be cached as well as a token: without it every call from a person
    // who has not connected would reach Key Vault.
    private sealed record CachedToken(ZendeskUserToken? Token);

    private static void LogBestEffort(Action write)
    {
        try
        {
            write();
        }
        catch (Exception)
        {
            // Deliberately ignored: a failing log sink must not replace Key Vault's exception.
        }
    }
}

/// <summary>
/// Process-local store for local development and tests. Tokens are lost on restart, which is the
/// point: nothing written here should outlive a development session.
/// </summary>
public sealed class InMemoryZendeskTokenStore : IZendeskTokenStore
{
    private readonly ConcurrentDictionary<string, ZendeskUserToken> _tokens = new(StringComparer.Ordinal);

    public Task<ZendeskUserToken?> GetAsync(string objectId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tokens.TryGetValue(objectId, out var token) ? token : null);

    public Task SaveAsync(string objectId, ZendeskUserToken token, CancellationToken cancellationToken = default)
    {
        _tokens[objectId] = token;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string objectId, CancellationToken cancellationToken = default)
    {
        _tokens.TryRemove(objectId, out _);
        return Task.CompletedTask;
    }
}
