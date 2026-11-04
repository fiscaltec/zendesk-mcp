using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <exception cref="ZendeskStoredTokenUnreadableException">
    /// Something is stored but it is neither a grant nor a tombstone. That is not "not connected": the
    /// grant inside may still be live at Zendesk.
    /// </exception>
    Task<ZendeskUserToken?> GetAsync(string objectId, CancellationToken cancellationToken = default);

    Task SaveAsync(string objectId, ZendeskUserToken token, CancellationToken cancellationToken = default);

    /// <summary>Forgets the caller's grant. Does not revoke it at Zendesk; see <see cref="ZendeskOAuthClient.RevokeAsync"/>.</summary>
    Task RemoveAsync(string objectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores one Key Vault secret per person, named <c>{TokenSecretPrefix}{objectId}</c>
/// (<c>zendesk-user-{objectId}</c> in production).
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

    public string SecretName(string objectId) => $"{_options.TokenSecretPrefix}{objectId}";

    private static string CacheKey(string objectId) => $"zendesk-token::{objectId}";

    public async Task<ZendeskUserToken?> GetAsync(string objectId, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue<CachedToken>(CacheKey(objectId), out var cached) && cached is not null)
        {
            metrics?.CacheLookup("user_token", hit: true);
            return cached.Token;
        }

        metrics?.CacheLookup("user_token", hit: false);

        KeyVaultSecret? secret;
        try
        {
            secret = (await secrets.GetSecretAsync(SecretName(objectId), cancellationToken: cancellationToken)).Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            secret = null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The object id is not personal data in the sense the audit rules guard (it resolves only
            // with directory access), and it is what an operator needs to find the secret.
            LogBestEffort(() => logger.LogError(ex,
                "Failed to read the Zendesk token for {UserObjectId} from Key Vault", objectId));
            throw;
        }

        ZendeskUserToken? token;
        try
        {
            token = Parse(secret?.Value);
        }
        catch (ZendeskStoredTokenUnreadableException)
        {
            // Not cached: caching it as "not connected" is what let a reconnect replace the grant
            // without revoking it (#12). The version is what an operator needs to look at it; the
            // value is never logged, since it may hold a live token.
            LogBestEffort(() => logger.LogError(
                "The stored Zendesk token for {UserObjectId} (secret version {SecretVersion}) is neither a grant nor a tombstone",
                objectId, secret?.Properties.Version));
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

        // Cached BEFORE the write: after a refresh, Zendesk has already spent the old refresh token, so
        // if the vault write fails this replica must still hold the new one rather than the spent one.
        cache.Set(CacheKey(objectId), new CachedToken(token), _options.TokenCacheDuration);
        await secrets.SetSecretAsync(secret, cancellationToken);
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

    /// <summary>
    /// Reads a stored value: a grant, or null for nothing stored or a tombstone. Anything else throws
    /// <see cref="ZendeskStoredTokenUnreadableException"/>, carrying whatever tokens could be read
    /// from it so that the grant can still be revoked.
    /// </summary>
    public static ZendeskUserToken? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        JsonObject? stored;
        try
        {
            stored = JsonNode.Parse(value) as JsonObject;

            // A JsonObject builds its members lazily, so a duplicate key surfaces as ArgumentException
            // on first access. Forced here, so it is refused as unreadable like any other bad JSON
            // rather than escaping as an exception nothing logs or tombstones.
            _ = stored?.Count;
        }
        catch (JsonException)
        {
            throw new ZendeskStoredTokenUnreadableException(salvaged: null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Duplicate keys: valid JSON, so the tokens may still be readable. Never a usable grant —
            // which value a duplicated member means is ambiguous — but a token named exactly once is
            // salvaged for revocation (Copilot on #48).
            throw new ZendeskStoredTokenUnreadableException(SalvageDuplicateKeyed(value));
        }

        if (stored is null)
        {
            throw new ZendeskStoredTokenUnreadableException(salvaged: null);
        }

        if (IsTombstone(stored))
        {
            return null;
        }

        ZendeskUserToken? token = null;
        try
        {
            token = stored.Deserialize<ZendeskUserToken>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // A member of the wrong type: fall through and salvage what can be read.
        }

        if (token is not null && !string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return token;
        }

        throw new ZendeskStoredTokenUnreadableException(Salvage(stored));
    }

    private static bool IsTombstone(JsonObject stored) =>
        stored.TryGetPropertyValue("revoked", out var revoked)
        && revoked is JsonValue flag && flag.TryGetValue<bool>(out var isRevoked) && isRevoked
        && !stored.ContainsKey("access_token") && !stored.ContainsKey("refresh_token");

    // Only the two tokens matter to a revocation. With a refresh token the expiry is set in the past (the
    // Unix epoch: DateTimeOffset.MinValue would overflow when a refresh skew is subtracted from it),
    // because the stored one cannot be trusted: ZendeskOAuthClient.RevokeGrantAsync then refreshes
    // first, so an expired access token's 401 cannot read as a revocation.
    private static ZendeskUserToken? Salvage(JsonObject stored)
    {
        var access = StringMember(stored, "access_token");
        var refresh = StringMember(stored, "refresh_token");
        if (access is null && refresh is null)
        {
            return null;
        }

        return new ZendeskUserToken(
            AccessToken: access ?? string.Empty,
            RefreshToken: refresh,
            ExpiresAt: refresh is null ? null : DateTimeOffset.UnixEpoch,
            RefreshExpiresAt: null,
            ZendeskUserId: 0,
            ZendeskEmail: string.Empty,
            ZendeskRole: string.Empty,
            ConnectedAt: DateTimeOffset.MinValue);
    }

    private static ZendeskUserToken? SalvageDuplicateKeyed(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var stored = new JsonObject();
            foreach (var name in new[] { "access_token", "refresh_token" })
            {
                var values = document.RootElement.EnumerateObject().Where(p => p.Name == name).ToList();
                if (values.Count == 1 && values[0].Value.ValueKind == JsonValueKind.String)
                {
                    stored[name] = values[0].Value.GetString();
                }
            }

            return Salvage(stored);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringMember(JsonObject stored, string name) =>
        stored.TryGetPropertyValue(name, out var node) && node is JsonValue value
            && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

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

/// <summary>
/// Thrown by <see cref="IZendeskTokenStore.GetAsync"/> when a person's stored value is neither a grant
/// nor a tombstone. Its message names no token. <see cref="Salvaged"/> holds the access and refresh
/// tokens if either could be read, for revocation only; everything else in it is blank.
/// </summary>
public sealed class ZendeskStoredTokenUnreadableException(ZendeskUserToken? salvaged)
    : Exception("The stored Zendesk connection could not be read.")
{
    public ZendeskUserToken? Salvaged { get; } = salvaged;
}
