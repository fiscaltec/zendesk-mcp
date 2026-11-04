using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

namespace ZendeskMcp.Tools;

/// <summary>
/// A ticket's SLA targets under the account's Zendesk SLA policies, as the <c>sla</c> object that
/// Get_ticket, Search_tickets (include 'sla'), Get_sla_queue and Get_sla_summary share:
/// <c>{breached, next_breach_at, next_breach_metric, targets: [...]}</c>, null when no policy ever
/// applied, and <c>{events_missing: true}</c> for an archived ticket.
/// </summary>
/// <remarks>
/// <para>
/// The source is the ticket's metric events (<c>GET /api/v2/tickets/{id}?include=metric_events,slas</c>):
/// for each metric instance, <c>apply_sla</c> names the target and policy, <c>activate</c>/<c>pause</c>/
/// <c>fulfill</c> say whether the clock is running, and <c>breach</c> events carry the moment the
/// target is (or was) breached. Zendesk writes a breach event ahead of time, when the clock starts, and
/// marks it <c>deleted</c> when the target is met or re-planned — so a breach event counts only when it
/// is not deleted and its time has passed before the target was met. <c>slas.policy_metrics</c> on the
/// ticket (the current running targets with their breach_at) fills in a breach time the events lack,
/// and stands in for the events entirely when Zendesk returns none.
/// </para>
/// <para>
/// Confirmed against the live API: the <c>include=metric_events</c> sideload is keyed by metric
/// (<c>reply_time</c>, <c>resolution_time</c>, <c>agent_work_time</c>, <c>pausable_update_time</c>,
/// <c>periodic_update_time</c>, <c>requester_wait_time</c>) and arrives <i>inside</i> the ticket, beside
/// <c>slas</c> (one beside <c>ticket</c> or a flat array is still accepted); <c>reply_time</c> instance 1
/// carries the first-reply target; <c>apply_sla.sla</c> is <c>{target (minutes), target_in_seconds,
/// business_hours, policy {id, title, description}}</c>; and <c>policy_metrics</c> entries are
/// <c>{breach_at, stage, metric}</c> plus one of <c>minutes</c>/<c>hours</c>/<c>days</c> (no
/// <c>business_hours</c>). Note that a breached target that was later met reads <c>stage: achieved</c>
/// there, which is why the events, not the stage, decide <c>breached</c>.
/// </para>
/// <para>
/// Unverified against the live API: that later <c>reply_time</c> instances are next replies, and that
/// an <c>apply_sla</c> on <c>resolution_time</c> is the total_resolution_time target — the validation
/// account's policy sets neither target, so no such event was available.
/// </para>
/// </remarks>
public static class TicketSla
{
    /// <summary>The SLA metrics, in the order targets are listed.</summary>
    public static readonly string[] Metrics =
    [
        "first_reply_time", "next_reply_time", "periodic_update_time", "pausable_update_time",
        "requester_wait_time", "agent_work_time", "total_resolution_time",
    ];

    /// <summary>A ticket fetched with its SLA: the ticket object (null when it no longer exists) and its sla object.</summary>
    public sealed record SlaTicket(long Id, JsonObject? Ticket, JsonNode? Sla);

    /// <summary>The sla object for one ticket.</summary>
    public static async Task<JsonNode?> GetAsync(ZendeskService zendesk, long ticketId, CancellationToken cancellationToken = default) =>
        (await FetchAsync(zendesk, ticketId, missingIsNull: false, cancellationToken)).Sla;

    /// <summary>The sla object for each of several tickets, keyed by ticket id.</summary>
    public static async Task<IReadOnlyDictionary<long, JsonNode?>> GetManyAsync(ZendeskService zendesk, IReadOnlyList<long> ticketIds, CancellationToken cancellationToken = default)
    {
        var fetched = await FetchManyAsync(zendesk, ticketIds, cancellationToken);
        return fetched.ToDictionary(kv => kv.Key, kv => kv.Value.Sla);
    }

    /// <summary>
    /// Each ticket with its sla object, a few requests at a time (<see cref="AnalyticsSupport.MaxConcurrency"/>).
    /// A ticket deleted since it was found comes back with a null ticket and sla rather than failing the batch.
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, SlaTicket>> FetchManyAsync(ZendeskService zendesk, IReadOnlyList<long> ticketIds, CancellationToken cancellationToken = default)
    {
        var distinct = ticketIds.Distinct().ToList();
        var results = await AnalyticsSupport.ForEachBoundedAsync(
            distinct,
            (id, ct) => FetchAsync(zendesk, id, missingIsNull: true, ct),
            cancellationToken);
        return results.ToDictionary(r => r.Id);
    }

    private static async Task<SlaTicket> FetchAsync(ZendeskService zendesk, long ticketId, bool missingIsNull, CancellationToken cancellationToken)
    {
        ToolJson.PositiveId(ticketId, "ticket_id");
        JsonNode response;
        try
        {
            response = await zendesk.GetAsync(
                $"/api/v2/tickets/{ticketId.ToString(CultureInfo.InvariantCulture)}",
                ToolJson.Query(("include", "metric_events,slas")),
                cancellationToken: cancellationToken);
        }
        catch (HttpRequestException ex) when (missingIsNull && ex.StatusCode == HttpStatusCode.NotFound)
        {
            return new SlaTicket(ticketId, null, null);
        }

        var ticket = response["ticket"] as JsonObject;
        var events = response["metric_events"] ?? ticket?["metric_events"];
        return new SlaTicket(ticketId, ticket, Evaluate(ticket, events, DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// The pure evaluator: the sla object from a ticket (for its status and <c>slas.policy_metrics</c>)
    /// and its metric events, as of <paramref name="now"/>.
    /// </summary>
    /// <returns>
    /// Null when no SLA policy ever applied; <c>{events_missing: true}</c> when the ticket is closed and
    /// Zendesk returned no events (an archived ticket); otherwise the sla object.
    /// </returns>
    public static JsonNode? Evaluate(JsonNode? ticket, JsonNode? metricEvents, DateTimeOffset now)
    {
        var instances = GroupEvents(metricEvents);
        var policyMetrics = ticket?["slas"]?["policy_metrics"] as JsonArray;

        if (instances.Count == 0)
        {
            // Archived tickets (closed and untouched for about 120 days) come back without events, and
            // a closed ticket has no running targets to read from policy_metrics either, so the honest
            // answer is "unknown", not "no SLA".
            if (string.Equals(ToolJson.String(ticket?["status"]), "closed", StringComparison.OrdinalIgnoreCase))
            {
                return new JsonObject { ["events_missing"] = true };
            }

            return FromPolicyMetrics(policyMetrics, now);
        }

        var targets = new List<Target>();
        foreach (var ((metric, instance), events) in instances)
        {
            if (EvaluateInstance(metric, instance, events, now) is { } target)
            {
                targets.Add(target);
            }
        }

        if (targets.Count == 0)
        {
            // Events, but none of them applied a policy: the ticket never had an SLA. Running targets in
            // policy_metrics without an apply_sla event would be a contradiction, but trusting them is
            // the safer reading for a queue.
            return FromPolicyMetrics(policyMetrics, now);
        }

        // Cross-check: an active target whose breach event is missing takes its breach time from
        // policy_metrics for the same metric, where Zendesk keeps the running deadline.
        foreach (var target in targets.Where(t => t.Status == "active" && t.BreachAt is null))
        {
            target.BreachAt = PolicyBreachAt(policyMetrics, target.Metric);
            if (target.BreachAt is { } due && due <= now)
            {
                target.Status = "breached";
                target.MinutesLate = WholeMinutes(now - due);
            }
        }

        return Shape(targets);
    }

    /// <summary>
    /// The sla object from <c>slas.policy_metrics</c> alone (the running targets Zendesk lists on a
    /// ticket). Used when there are no metric events and by the queue's fast ranking. Null when empty.
    /// </summary>
    /// <remarks>
    /// Live, an entry's <c>minutes</c>/<c>hours</c>/<c>days</c> is the time left until <c>breach_at</c>
    /// (negative once overdue), not the target, so no target length is reported from here. A met target
    /// reads <c>stage: achieved</c> whether or not it was met in time; <c>breach_at</c> stays set only
    /// when it was breached.
    /// </remarks>
    public static JsonNode? FromPolicyMetrics(JsonArray? policyMetrics, DateTimeOffset now)
    {
        if (policyMetrics is null || policyMetrics.Count == 0)
        {
            return null;
        }

        var targets = new List<Target>();
        foreach (var entry in policyMetrics)
        {
            var metric = ToolJson.String(entry?["metric"]);
            if (metric is null || !Metrics.Contains(metric))
            {
                continue;
            }

            var breachAt = AnalyticsStats.Moment(entry?["breach_at"]);
            var stage = ToolJson.String(entry?["stage"])?.ToLowerInvariant();
            var target = new Target(metric, null)
            {
                BusinessHours = entry?["business_hours"] is JsonValue bh && bh.TryGetValue<bool>(out var b) ? b : null,
                BreachAt = breachAt,
            };

            switch (stage)
            {
                case "paused":
                    target.Status = "paused";
                    break;
                case "achieved":
                    target.Status = breachAt is null ? "achieved" : "breached";
                    break;
                default:
                    if (breachAt is { } due && due <= now)
                    {
                        target.Status = "breached";
                        target.MinutesLate = WholeMinutes(now - due);
                    }
                    else
                    {
                        target.Status = "active";
                    }

                    break;
            }

            targets.Add(target);
        }

        return targets.Count == 0 ? null : Shape(targets);
    }

    private static Target? EvaluateInstance(string metric, long? instance, List<JsonNode> events, DateTimeOffset now)
    {
        JsonNode? sla = null;
        DateTimeOffset? fulfilledAt = null;
        var paused = false;
        var breaches = new List<DateTimeOffset>();

        foreach (var ev in events)
        {
            var time = AnalyticsStats.Moment(ev["time"]);
            switch (ToolJson.String(ev["type"]))
            {
                case "apply_sla":
                case "apply_group_sla":
                    // A re-applied policy (a priority change, say) replaces the target; Zendesk deletes the
                    // old breach event and writes a new one, which the breach handling below follows.
                    sla = ev["sla"];
                    break;
                case "activate":
                    paused = false;
                    fulfilledAt = null; // the clock restarts, e.g. resolution time after a reopen
                    break;
                case "pause":
                    paused = true;
                    break;
                case "fulfill":
                    paused = false;
                    fulfilledAt = time;
                    break;
                case "breach":
                    if (time is { } at && !(ev["deleted"] is JsonValue d && d.TryGetValue<bool>(out var deleted) && deleted))
                    {
                        breaches.Add(at);
                    }

                    break;
            }
        }

        if (sla is null)
        {
            return null;
        }

        var target = new Target(metric, instance)
        {
            TargetMinutes = AnalyticsStats.Number(sla["target_in_seconds"]) is { } seconds ? seconds / 60.0 : AnalyticsStats.Number(sla["target"]),
            BusinessHours = sla["business_hours"] is JsonValue bh && bh.TryGetValue<bool>(out var b) ? b : null,
            PolicyId = AnalyticsStats.Long(sla["policy"]?["id"]),
            PolicyTitle = ToolJson.String(sla["policy"]?["title"]),
            CompletedAt = fulfilledAt,
        };

        var settledBy = fulfilledAt ?? now;
        var breachedAt = breaches.Where(t => t <= settledBy).Order().Cast<DateTimeOffset?>().FirstOrDefault();
        if (breachedAt is { } breach)
        {
            target.Status = "breached";
            target.BreachAt = breach;
            target.MinutesLate = WholeMinutes(settledBy - breach);
        }
        else if (fulfilledAt is not null)
        {
            target.Status = "achieved";
        }
        else if (paused)
        {
            target.Status = "paused";
        }
        else
        {
            target.Status = "active";
            target.BreachAt = breaches.Where(t => t > now).Order().Cast<DateTimeOffset?>().FirstOrDefault();
        }

        return target;
    }

    // Events grouped per (SLA metric, instance), each group in time order (ties by event id).
    private static List<KeyValuePair<(string Metric, long? Instance), List<JsonNode>>> GroupEvents(JsonNode? metricEvents)
    {
        var flat = new List<(string Key, JsonNode Event)>();
        switch (metricEvents)
        {
            case JsonObject byMetric:
                foreach (var (key, value) in byMetric)
                {
                    if (value is JsonArray array)
                    {
                        flat.AddRange(array.Where(e => e is not null).Select(e => (ToolJson.String(e!["metric"]) ?? key, e!)));
                    }
                }

                break;
            case JsonArray array:
                flat.AddRange(array.Where(e => e is not null && ToolJson.String(e["metric"]) is not null).Select(e => (ToolJson.String(e!["metric"])!, e!)));
                break;
        }

        var groups = new Dictionary<(string, long?), List<JsonNode>>();
        foreach (var (key, ev) in flat)
        {
            var instanceId = AnalyticsStats.Long(ev["instance_id"]);
            var (metric, instance) = MapMetric(key, instanceId);
            if (metric is null)
            {
                continue;
            }

            if (!groups.TryGetValue((metric, instance), out var list))
            {
                list = [];
                groups[(metric, instance)] = list;
            }

            list.Add(ev);
        }

        return groups
            .Select(g => new KeyValuePair<(string, long?), List<JsonNode>>(
                g.Key,
                g.Value.OrderBy(e => AnalyticsStats.Moment(e["time"]) ?? DateTimeOffset.MinValue)
                    .ThenBy(e => AnalyticsStats.Long(e["id"]) ?? 0)
                    .ToList()))
            .OrderBy(g => Array.IndexOf(Metrics, g.Key.Item1))
            .ThenBy(g => g.Key.Item2 ?? 0)
            .ToList();
    }

    // Zendesk's metric-event names to the SLA policy metric names. reply_time instance 1 is the first
    // reply; instance n (n ≥ 2) is the (n-1)th next reply, so the next_reply_time instances number from 1.
    private static (string? Metric, long? Instance) MapMetric(string key, long? instanceId) => key switch
    {
        "reply_time" when instanceId is null or <= 1 => ("first_reply_time", 1),
        "reply_time" => ("next_reply_time", instanceId - 1),
        "resolution_time" => ("total_resolution_time", instanceId),
        _ when Metrics.Contains(key) => (key, instanceId),
        _ => (null, null),
    };

    private static DateTimeOffset? PolicyBreachAt(JsonArray? policyMetrics, string metric) =>
        policyMetrics?
            .Where(m => ToolJson.String(m?["metric"]) == metric && ToolJson.String(m?["stage"]) is null or "active")
            .Select(m => AnalyticsStats.Moment(m?["breach_at"]))
            .Where(t => t is not null)
            .Min();

    private static long WholeMinutes(TimeSpan span) => (long)Math.Floor(Math.Max(0, span.TotalMinutes));

    private static JsonObject Shape(List<Target> targets)
    {
        var next = targets
            .Where(t => t.Status == "active" && t.BreachAt is not null)
            .OrderBy(t => t.BreachAt)
            .FirstOrDefault();

        return new JsonObject
        {
            ["breached"] = targets.Any(t => t.Status == "breached"),
            ["next_breach_at"] = next?.BreachAt is { } at ? AnalyticsStats.Iso(at) : null,
            ["next_breach_metric"] = next?.Metric,
            ["targets"] = new JsonArray(targets.Select(t => (JsonNode?)t.ToJson()).ToArray()),
        };
    }

    private sealed class Target(string metric, long? instance)
    {
        public string Metric { get; } = metric;
        public long? Instance { get; } = instance;
        public string Status { get; set; } = "active";
        public double? TargetMinutes { get; init; }
        public bool? BusinessHours { get; init; }
        public long? PolicyId { get; init; }
        public string? PolicyTitle { get; init; }
        public DateTimeOffset? BreachAt { get; set; }
        public DateTimeOffset? CompletedAt { get; init; }
        public long? MinutesLate { get; set; }

        public JsonObject ToJson() => new()
        {
            ["metric"] = Metric,
            ["instance"] = Instance,
            ["status"] = Status,
            ["target_minutes"] = TargetMinutes is { } m && m == Math.Floor(m) ? JsonValue.Create((long)m) : JsonValue.Create(TargetMinutes),
            ["business_hours"] = BusinessHours,
            ["policy_id"] = PolicyId,
            ["policy_title"] = PolicyTitle,
            ["breach_at"] = BreachAt is { } b ? AnalyticsStats.Iso(b) : null,
            ["completed_at"] = CompletedAt is { } c ? AnalyticsStats.Iso(c) : null,
            ["minutes_late"] = MinutesLate,
        };
    }
}
