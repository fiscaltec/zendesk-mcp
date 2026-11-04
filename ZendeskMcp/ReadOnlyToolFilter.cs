using ModelContextProtocol.Protocol;

namespace ZendeskMcp;

/// <summary>
/// Filters a tools/list result for read-only deployments: keeps only tools whose annotation marks
/// them read-only (<c>ReadOnlyHint == true</c>) — i.e. drops every create/update/delete tool — so a
/// read-only server never advertises destructive operations. A pass-through when not read-only.
/// The one exception is <see cref="OwnConnectionTools"/>.
/// </summary>
public static class ReadOnlyToolFilter
{
    /// <summary>
    /// Tools kept in read-only mode although they are not annotated read-only. <c>Disconnect_zendesk</c>
    /// revokes and forgets the caller's <i>own</i> Zendesk connection and touches no Zendesk record, so
    /// read-only mode has nothing to protect from it — and read-only is the emergency lock-down, the
    /// moment someone most needs to cut their own connection. Its revoke goes through
    /// <see cref="ZendeskOAuthClient"/>, not <see cref="ZendeskService"/>, so the read-only verb check
    /// in <see cref="ToolAuthorizer"/> never stood in its way; hiding it only removed the way to call it.
    /// </summary>
    public static readonly IReadOnlySet<string> OwnConnectionTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "Disconnect_zendesk",
    };

    public static IList<Tool> FilterTools(IEnumerable<Tool> tools, bool readOnly)
    {
        if (!readOnly)
        {
            return tools.ToList();
        }
        return tools.Where(t => t.Annotations?.ReadOnlyHint == true || OwnConnectionTools.Contains(t.Name)).ToList();
    }
}
