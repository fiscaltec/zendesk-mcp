using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace ZendeskMcp;

/// <summary>
/// Issues and redeems the short-lived links that start a Zendesk connection, and the per-attempt
/// state that carries a connection from <c>/zendesk/connect</c> to <c>/zendesk/callback</c>.
/// </summary>
/// <remarks>
/// <para>
/// A link is a 256-bit random capability naming the Entra identity it was issued to. It is handed
/// only to an authenticated tool caller, inside their own tool result, and it expires after
/// <see cref="ZendeskServerOptions.ConnectLinkLifetime"/>. What makes a leaked link harmless is not the
/// secrecy, though, but the identity check at the callback: the Zendesk login that completes it must
/// have the same email as the link's owner (see <see cref="ZendeskServerOptions.RequireEmailMatch"/>),
/// so someone else opening it cannot attach their Zendesk account to the owner's identity.
/// </para>
/// <para>
/// Backed by <see cref="IDistributedCache"/>, registered in-memory today. That is correct only while
/// the app runs one replica, because the link may be opened against any replica; moving to a shared
/// cache (Redis) is a registration change here, not a code change.
/// </para>
/// </remarks>
public sealed class ZendeskConnectLinks(IDistributedCache cache, IOptions<ZendeskServerOptions> options)
{
    private const string TicketPrefix = "zendesk-connect:ticket:";
    private const string StatePrefix = "zendesk-connect:state:";

    /// <summary>The lifetime of one round trip to Zendesk, from redirect to callback.</summary>
    public static readonly TimeSpan AttemptLifetime = TimeSpan.FromMinutes(10);

    private readonly ZendeskServerOptions _options = options.Value;

    /// <summary>Issues a link for <paramref name="objectId"/> and returns its absolute URL.</summary>
    public async Task<string> CreateAsync(string objectId, string? signInName, string publicBaseUrl, CancellationToken cancellationToken = default)
    {
        var ticket = NewToken();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new ConnectTicket(objectId, signInName));
        await cache.SetAsync(TicketPrefix + ticket, payload,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _options.ConnectLinkLifetime },
            cancellationToken);
        return $"{publicBaseUrl.TrimEnd('/')}/zendesk/connect?ticket={Uri.EscapeDataString(ticket)}";
    }

    /// <summary>Reads a link without spending it, so that an abandoned Zendesk sign-in can be retried from the same link.</summary>
    public async Task<ConnectTicket?> PeekAsync(string ticket, CancellationToken cancellationToken = default) =>
        Deserialize<ConnectTicket>(await cache.GetAsync(TicketPrefix + ticket, cancellationToken));

    /// <summary>Spends a link. Called only once a connection has been stored.</summary>
    public Task RedeemAsync(string ticket, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync(TicketPrefix + ticket, cancellationToken);

    /// <summary>Starts one attempt: returns the OAuth <c>state</c> value naming it.</summary>
    public async Task<string> BeginAttemptAsync(ConnectAttempt attempt, CancellationToken cancellationToken = default)
    {
        var state = NewToken();
        await cache.SetAsync(StatePrefix + state, JsonSerializer.SerializeToUtf8Bytes(attempt),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = AttemptLifetime },
            cancellationToken);
        return state;
    }

    /// <summary>Ends an attempt and returns it, or null for an unknown, expired or already used state.</summary>
    public async Task<ConnectAttempt?> CompleteAttemptAsync(string state, CancellationToken cancellationToken = default)
    {
        var key = StatePrefix + state;
        var attempt = Deserialize<ConnectAttempt>(await cache.GetAsync(key, cancellationToken));
        if (attempt is not null)
        {
            await cache.RemoveAsync(key, cancellationToken);
        }

        return attempt;
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static T? Deserialize<T>(byte[]? payload) where T : class
    {
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payload);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What a link carries: whose connection it makes, and the sign-in name the Zendesk email must match.</summary>
    public sealed record ConnectTicket(string ObjectId, string? SignInName);

    /// <summary>One redirect to Zendesk: the link it came from, and the PKCE verifier for its code.</summary>
    public sealed record ConnectAttempt(string Ticket, string ObjectId, string? SignInName, string CodeVerifier);
}
