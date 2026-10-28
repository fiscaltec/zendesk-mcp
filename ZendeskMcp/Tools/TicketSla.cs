using System.Text.Json.Nodes;

namespace ZendeskMcp.Tools;

/// <summary>
/// A ticket's SLA targets under the account's Zendesk SLA policies, as the <c>sla</c> object that
/// Get_ticket, Search_tickets (include 'sla'), Get_sla_queue and Get_sla_summary share:
/// <c>{breached, next_breach_at, next_breach_metric, targets: [...]}</c>, null when no policy ever
/// applied, and <c>{events_missing: true}</c> for an archived ticket.
/// </summary>
public static class TicketSla
{
    /// <summary>The sla object for one ticket.</summary>
    public static Task<JsonNode?> GetAsync(ZendeskService zendesk, long ticketId, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    /// <summary>The sla object for each of several tickets, keyed by ticket id.</summary>
    public static Task<IReadOnlyDictionary<long, JsonNode?>> GetManyAsync(ZendeskService zendesk, IReadOnlyList<long> ticketIds, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
}
