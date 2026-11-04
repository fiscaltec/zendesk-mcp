namespace ZendeskMcp.Tests;

/// <summary>
/// A token store holding a value that is neither a grant nor a tombstone, as a damaged Key Vault secret
/// would be, until it is removed or replaced.
/// </summary>
public sealed class UnreadableTokenStore(ZendeskStoredTokenUnreadableException unreadable) : IZendeskTokenStore
{
    private readonly InMemoryZendeskTokenStore _inner = new();
    private bool _unreadable = true;

    public int Removed { get; private set; }

    public Task<ZendeskUserToken?> GetAsync(string objectId, CancellationToken cancellationToken = default) =>
        _unreadable ? throw unreadable : _inner.GetAsync(objectId, cancellationToken);

    public Task SaveAsync(string objectId, ZendeskUserToken token, CancellationToken cancellationToken = default)
    {
        _unreadable = false;
        return _inner.SaveAsync(objectId, token, cancellationToken);
    }

    public Task RemoveAsync(string objectId, CancellationToken cancellationToken = default)
    {
        _unreadable = false;
        Removed++;
        return _inner.RemoveAsync(objectId, cancellationToken);
    }
}
