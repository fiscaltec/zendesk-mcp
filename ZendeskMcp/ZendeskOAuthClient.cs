using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ZendeskMcp;

/// <summary>
/// The Zendesk side of the per-user OAuth flow: the authorisation URL, the code and refresh
/// exchanges, the identity check on a new token, and revocation.
/// </summary>
/// <remarks>
/// This is a different OAuth relationship from the one <c>/oauth/*</c> in Program.cs proxies. That one
/// signs the person in to <i>this server</i> with Entra; this one has the person grant this server a
/// Zendesk token to act as them. The two never share a token, a client or a callback.
/// </remarks>
public sealed class ZendeskOAuthClient(
    IHttpClientFactory httpClientFactory,
    IOptions<ZendeskServerOptions> options,
    IMemoryCache cache,
    ILogger<ZendeskOAuthClient> logger,
    SecretClient? secretClient = null,
    TimeProvider? timeProvider = null)
{
    public const string HttpClientName = "zendesk-oauth";

    private readonly ZendeskServerOptions _options = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>The Zendesk authorisation URL for one connect attempt.</summary>
    public string BuildAuthorizeUrl(string redirectUri, string state, string codeVerifier)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.OAuthClientId ?? string.Empty,
            ["redirect_uri"] = redirectUri,
            ["scope"] = _options.OAuthScopes,
            ["state"] = state,
            ["code_challenge"] = CodeChallenge(codeVerifier),
            ["code_challenge_method"] = "S256",
        };

        return $"{_options.BaseUrl}/oauth/authorizations/new?"
            + string.Join("&", query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>A PKCE verifier: 32 random bytes, base64url (43 characters).</summary>
    public static string NewCodeVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string CodeChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Exchanges an authorisation code for a token. Throws <see cref="ZendeskOAuthException"/> on refusal.</summary>
    public async Task<TokenResponse> ExchangeCodeAsync(string code, string redirectUri, string codeVerifier, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = _options.OAuthClientId,
            ["client_secret"] = await GetClientSecretAsync(cancellationToken),
            ["redirect_uri"] = redirectUri,
            ["scope"] = _options.OAuthScopes,
            ["code_verifier"] = codeVerifier,
        };
        AddLifetimes(body);
        return await PostTokenAsync(body, cancellationToken);
    }

    /// <summary>Exchanges a refresh token for a new access token (and, from Zendesk, a new refresh token).</summary>
    public async Task<TokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _options.OAuthClientId,
            ["client_secret"] = await GetClientSecretAsync(cancellationToken),
            ["scope"] = _options.OAuthScopes,
        };
        AddLifetimes(body);
        return await PostTokenAsync(body, cancellationToken);
    }

    /// <summary>
    /// The Zendesk user a token belongs to, from <c>/api/v2/users/me</c>. Called with the new token
    /// before it is stored, which is what lets the connect flow refuse a token for the wrong person.
    /// </summary>
    public async Task<ZendeskIdentity> GetIdentityAsync(string accessToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.BaseUrl}/api/v2/users/me.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await Client().SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new ZendeskOAuthException($"Zendesk did not accept the new token ({(int)response.StatusCode}).");
        }

        var user = JsonNode.Parse(json)?["user"]
            ?? throw new ZendeskOAuthException("Zendesk returned no user for the new token.");
        return new ZendeskIdentity(
            Id: user["id"]?.GetValue<long>() ?? 0,
            Name: user["name"]?.GetValue<string>() ?? string.Empty,
            Email: user["email"]?.GetValue<string>() ?? string.Empty,
            Role: user["role"]?.GetValue<string>() ?? string.Empty);
    }

    /// <summary>
    /// Revokes a token at Zendesk. Best effort: a token Zendesk already considers invalid is the
    /// outcome wanted, so a 401 counts as success, and any other failure is logged and swallowed so
    /// that disconnecting locally still happens.
    /// </summary>
    public async Task RevokeAsync(string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{_options.BaseUrl}/api/v2/oauth/tokens/current.json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await Client().SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode && (int)response.StatusCode != 401)
            {
                logger.LogWarning("Zendesk token revocation returned {StatusCode}", (int)response.StatusCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Zendesk token revocation failed: {FailureType}", ex.GetType().Name);
        }
    }

    /// <summary>Builds the stored record from a token response and the identity it was checked against.</summary>
    public ZendeskUserToken ToUserToken(TokenResponse token, ZendeskIdentity identity, DateTimeOffset? connectedAt = null)
    {
        var now = _time.GetUtcNow();
        return new ZendeskUserToken(
            AccessToken: token.AccessToken,
            RefreshToken: token.RefreshToken,
            ExpiresAt: token.ExpiresIn is { } expiresIn ? now.AddSeconds(expiresIn) : null,
            RefreshExpiresAt: token.RefreshTokenExpiresIn is { } refreshIn ? now.AddSeconds(refreshIn) : null,
            ZendeskUserId: identity.Id,
            ZendeskEmail: identity.Email,
            ZendeskRole: identity.Role,
            ConnectedAt: connectedAt ?? now);
    }

    private void AddLifetimes(JsonObject body)
    {
        if (_options.AccessTokenExpiresInSeconds is { } expiresIn)
        {
            body["expires_in"] = expiresIn;
            if (_options.RefreshTokenExpiresInSeconds is { } refreshIn)
            {
                body["refresh_token_expires_in"] = refreshIn;
            }
        }
    }

    private async Task<TokenResponse> PostTokenAsync(JsonObject body, CancellationToken cancellationToken)
    {
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await Client().PostAsync($"{_options.BaseUrl}/oauth/tokens", content, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // The error CODE only: Zendesk's error_description can echo the request, and the request
            // carried the client secret and a code or refresh token.
            var error = TryReadString(json, "error") ?? "unknown_error";
            logger.LogWarning("Zendesk token endpoint refused the request: {StatusCode} {OAuthError}",
                (int)response.StatusCode, error);
            throw new ZendeskOAuthException($"Zendesk refused the token request ({error}).", error);
        }

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String
            ? at.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ZendeskOAuthException("Zendesk's token response carried no access token.");
        }

        return new TokenResponse(
            AccessToken: accessToken,
            RefreshToken: root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null,
            ExpiresIn: TryReadLong(root, "expires_in"),
            RefreshTokenExpiresIn: TryReadLong(root, "refresh_token_expires_in"),
            Scope: root.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String ? sc.GetString() : null);
    }

    private async Task<string> GetClientSecretAsync(CancellationToken cancellationToken)
    {
        if (secretClient is null)
        {
            return _options.DevelopmentOAuthClientSecret
                ?? throw new InvalidOperationException(
                    "The Zendesk OAuth client secret cannot be resolved: no Key Vault client registered and no DevelopmentOAuthClientSecret configured.");
        }

        var cacheKey = $"zendesk-oauth-client-secret::{_options.OAuthClientSecretRef}";
        if (cache.TryGetValue<string>(cacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        var secret = await secretClient.GetSecretAsync(_options.OAuthClientSecretRef, cancellationToken: cancellationToken);
        var value = secret.Value.Value
            ?? throw new InvalidOperationException($"Key Vault secret '{_options.OAuthClientSecretRef}' has no value.");
        cache.Set(cacheKey, value, _options.TokenCacheDuration);
        return value;
    }

    private HttpClient Client() => httpClientFactory.CreateClient(HttpClientName);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static long? TryReadLong(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)
            ? n
            : null;

    private static string? TryReadString(string json, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public sealed record TokenResponse(string AccessToken, string? RefreshToken, long? ExpiresIn, long? RefreshTokenExpiresIn, string? Scope);
}

/// <summary>The Zendesk user behind a token.</summary>
public sealed record ZendeskIdentity(long Id, string Name, string Email, string Role)
{
    /// <summary>Agents and admins can use the Zendesk agent APIs this server calls; end users cannot.</summary>
    public bool IsStaff => Role is "agent" or "admin";
}

/// <summary>A refusal from Zendesk's OAuth endpoints. <see cref="Error"/> is the OAuth error code when there is one.</summary>
public sealed class ZendeskOAuthException(string message, string? error = null) : Exception(message)
{
    public string? Error { get; } = error;
}
