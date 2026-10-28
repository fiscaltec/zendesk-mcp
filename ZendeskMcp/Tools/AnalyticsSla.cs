using System.Globalization;
using System.Text.Json.Nodes;

namespace ZendeskMcp.Tools;

/// <summary>
/// The pure part of Get_sla_summary and Get_sla_queue: counting SLA targets into a period and groups,
/// listing breaches, and ranking a backlog by urgency. Works on the <c>sla</c> objects
/// <see cref="TicketSla"/> produces, so the tools and Get_ticket can never disagree about a target.
/// </summary>
public static class AnalyticsSla
{
    /// <summary>The group_by values Get_sla_summary accepts.</summary>
    public static readonly string[] SummaryGroupBys = ["group", "assignee", "brand", "channel", "priority", "policy", "week", "month"];

    /// <summary>The columns of the breached table, in order.</summary>
    public static readonly string[] BreachedColumns =
    [
        "ticket_id", "url", "subject", "ticket_status", "metric", "instance", "policy", "target_minutes",
        "business_hours", "breach_at", "completed_at", "minutes_late", "assignee_id", "assignee", "group_id", "group",
    ];

    /// <summary>What Get_sla_summary needs besides the tickets.</summary>
    public sealed record SummaryOptions(
        string DateBasis,
        DateOnly Start,
        DateOnly End,
        TimeZoneInfo Zone,
        string? GroupBy,
        int BreachedLimit,
        Func<string, string, string> Name,
        Func<long, string> TicketUrl);

    /// <summary>
    /// Counts the targets of <paramref name="tickets"/> into the period. With date basis
    /// <c>outcome</c> a target counts on the day it settled (its breach time if breached, its completion
    /// if achieved) and running targets are left out; with <c>created</c> every target of the tickets
    /// (already selected by created date) counts, active and paused included.
    /// </summary>
    public static JsonObject Summarise(IReadOnlyList<TicketSla.SlaTicket> tickets, SummaryOptions options)
    {
        var created = options.DateBasis == "created";
        var total = new Tally(created);
        var groups = new Dictionary<string, Tally>(StringComparer.Ordinal);
        var nullGroup = new Tally(created);
        var breached = new List<(double MinutesLate, object?[] Row)>();
        int read = 0, withSla = 0, withoutSla = 0, archived = 0;

        foreach (var item in tickets)
        {
            if (item.Ticket is not { } ticket)
            {
                continue;
            }

            read++;
            if (item.Sla is null)
            {
                withoutSla++;
                continue;
            }

            if (item.Sla["events_missing"] is JsonValue missing && missing.TryGetValue<bool>(out var m) && m)
            {
                archived++;
                continue;
            }

            withSla++;
            foreach (var target in item.Sla["targets"] as JsonArray ?? [])
            {
                if (target is null)
                {
                    continue;
                }

                var status = ToolJson.String(target["status"]) ?? "active";
                DateTimeOffset? settled = status switch
                {
                    "breached" => AnalyticsStats.Moment(target["breach_at"]),
                    "achieved" => AnalyticsStats.Moment(target["completed_at"]),
                    _ => null,
                };

                if (!created)
                {
                    if (settled is not { } at)
                    {
                        continue;
                    }

                    var day = AnalyticsStats.LocalDay(at, options.Zone);
                    if (day < options.Start || day > options.End)
                    {
                        continue;
                    }
                }

                var metric = ToolJson.String(target["metric"]) ?? "unknown";
                total.Add(item.Id, metric, status);

                if (options.GroupBy is { } groupBy)
                {
                    var key = GroupKey(ticket, target, groupBy, created ? AnalyticsStats.Moment(ticket["created_at"]) : settled, options.Zone);
                    var tally = key is null ? nullGroup : groups.TryGetValue(key, out var existing) ? existing : groups[key] = new Tally(created);
                    tally.Add(item.Id, metric, status);
                }

                if (status == "breached")
                {
                    var assigneeId = AnalyticsStats.Long(ticket["assignee_id"]);
                    var groupId = AnalyticsStats.Long(ticket["group_id"]);
                    var minutesLate = AnalyticsStats.Number(target["minutes_late"]) ?? 0;
                    breached.Add((minutesLate, new object?[]
                    {
                        item.Id,
                        options.TicketUrl(item.Id),
                        ToolJson.String(ticket["subject"]),
                        ToolJson.String(ticket["status"]),
                        metric,
                        AnalyticsStats.Long(target["instance"]),
                        ToolJson.String(target["policy_title"]) ?? AnalyticsStats.Long(target["policy_id"])?.ToString(CultureInfo.InvariantCulture),
                        AnalyticsStats.Number(target["target_minutes"]),
                        target["business_hours"] is JsonValue bh && bh.TryGetValue<bool>(out var b) ? b : null,
                        ToolJson.String(target["breach_at"]),
                        ToolJson.String(target["completed_at"]),
                        (long)minutesLate,
                        assigneeId,
                        assigneeId is { } a ? options.Name("assignee", a.ToString(CultureInfo.InvariantCulture)) : null,
                        groupId,
                        groupId is { } g ? options.Name("group", g.ToString(CultureInfo.InvariantCulture)) : null,
                    }));
                }
            }
        }

        var result = new JsonObject
        {
            ["metrics"] = total.Metrics(),
            ["all_metrics"] = total.AllMetrics(),
            ["tickets"] = new JsonObject
            {
                ["read"] = read,
                ["with_sla"] = withSla,
                ["without_sla"] = withoutSla,
                ["archived_without_events"] = archived,
                ["with_targets"] = total.WithTargets,
                ["with_breach"] = total.WithBreach,
                ["all_achieved"] = total.AllAchieved,
            },
        };

        if (options.GroupBy is { } by)
        {
            var groupArray = new JsonArray();
            foreach (var (key, tally) in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                groupArray.Add(tally.ToGroup(key, options.Name(by, key)));
            }

            if (nullGroup.WithTargets > 0)
            {
                groupArray.Add(nullGroup.ToGroup(null, "(none)"));
            }

            result["groups"] = groupArray;
        }

        result["breached_total"] = breached.Count;
        result["breached"] = AnalyticsStats.Table(
            BreachedColumns,
            breached.OrderByDescending(b => b.MinutesLate).Take(options.BreachedLimit).Select(b => (IEnumerable<object?>)b.Row));
        return result;
    }

    /// <summary>One ticket's place in the SLA queue.</summary>
    public sealed record QueueEntry(JsonObject Ticket, string Urgency, string Metric, long Minutes);

    /// <summary>The queue: ranked entries and counts over the whole backlog.</summary>
    public sealed record Queue(IReadOnlyList<QueueEntry> Ranked, int Scanned, int WithSla, int Overdue, int DueWithin1h, int DueWithin4h);

    /// <summary>
    /// Ranks unsolved tickets from <c>slas.policy_metrics</c>: overdue first (a running target past its
    /// breach, longest overdue first), then by next breach, soonest first. Paused targets are not due;
    /// a ticket with only paused targets is counted but not ranked. due_within_4h includes due_within_1h.
    /// </summary>
    public static Queue RankQueue(IReadOnlyList<JsonObject> tickets, DateTimeOffset now)
    {
        var overdue = new List<QueueEntry>();
        var due = new List<QueueEntry>();
        var withSla = 0;

        foreach (var ticket in tickets)
        {
            var sla = TicketSla.FromPolicyMetrics(ticket["slas"]?["policy_metrics"] as JsonArray, now);
            if (sla is null)
            {
                continue;
            }

            withSla++;
            var targets = (sla["targets"] as JsonArray ?? []).OfType<JsonObject>().ToList();
            // A target met late reads breached with no minutes_late (policy_metrics has no completion
            // time); only one still running past its breach is overdue.
            var late = targets
                .Where(t => ToolJson.String(t["status"]) == "breached" && t["completed_at"] is null && t["minutes_late"] is not null)
                .OrderByDescending(t => AnalyticsStats.Long(t["minutes_late"]) ?? 0)
                .FirstOrDefault();
            if (late is not null)
            {
                overdue.Add(new QueueEntry(ticket, "overdue", ToolJson.String(late["metric"])!, AnalyticsStats.Long(late["minutes_late"]) ?? 0));
                continue;
            }

            var next = targets
                .Where(t => ToolJson.String(t["status"]) == "active" && AnalyticsStats.Moment(t["breach_at"]) is not null)
                .OrderBy(t => AnalyticsStats.Moment(t["breach_at"]))
                .FirstOrDefault();
            if (next is not null)
            {
                var minutesLeft = (long)Math.Floor((AnalyticsStats.Moment(next["breach_at"])!.Value - now).TotalMinutes);
                due.Add(new QueueEntry(ticket, "due", ToolJson.String(next["metric"])!, Math.Max(0, minutesLeft)));
            }
        }

        var ranked = overdue.OrderByDescending(e => e.Minutes)
            .Concat(due.OrderBy(e => e.Minutes))
            .ToList();
        return new Queue(ranked, tickets.Count, withSla, overdue.Count, due.Count(e => e.Minutes <= 60), due.Count(e => e.Minutes <= 240));
    }

    private static string? GroupKey(JsonObject ticket, JsonNode target, string groupBy, DateTimeOffset? moment, TimeZoneInfo zone) => groupBy switch
    {
        "group" => IdKey(ticket["group_id"]),
        "assignee" => IdKey(ticket["assignee_id"]),
        "brand" => IdKey(ticket["brand_id"]),
        "channel" => ToolJson.String(ticket["via"]?["channel"]),
        "priority" => ToolJson.String(ticket["priority"]),
        "policy" => IdKey(target["policy_id"]),
        "week" or "month" => moment is { } at ? AnalyticsStats.BucketKey(at, zone, groupBy) : null,
        _ => throw new ArgumentException($"Unknown group_by '{groupBy}'."),
    };

    private static string? IdKey(JsonNode? node) =>
        AnalyticsStats.Long(node) is { } id ? id.ToString(CultureInfo.InvariantCulture) : null;

    // Counts per metric and per ticket for one set of targets (the total, or one group).
    private sealed class Tally(bool withRunning)
    {
        private readonly Dictionary<string, Dictionary<string, int>> _byMetric = new(StringComparer.Ordinal);
        private readonly Dictionary<long, (bool Breached, bool Running)> _tickets = [];

        public int WithTargets => _tickets.Count;
        public int WithBreach => _tickets.Values.Count(t => t.Breached);
        public int AllAchieved => _tickets.Values.Count(t => !t.Breached && !t.Running);

        public void Add(long ticketId, string metric, string status)
        {
            if (!_byMetric.TryGetValue(metric, out var counts))
            {
                counts = new Dictionary<string, int>(StringComparer.Ordinal);
                _byMetric[metric] = counts;
            }

            counts[status] = counts.GetValueOrDefault(status) + 1;
            var state = _tickets.GetValueOrDefault(ticketId);
            _tickets[ticketId] = (state.Breached || status == "breached", state.Running || status is "active" or "paused");
        }

        public JsonObject Metrics()
        {
            var metrics = new JsonObject();
            foreach (var metric in TicketSla.Metrics.Concat(_byMetric.Keys.Where(k => !TicketSla.Metrics.Contains(k))))
            {
                metrics[metric] = Counts(_byMetric.GetValueOrDefault(metric));
            }

            return metrics;
        }

        public JsonObject AllMetrics()
        {
            var all = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var counts in _byMetric.Values)
            {
                foreach (var (status, count) in counts)
                {
                    all[status] = all.GetValueOrDefault(status) + count;
                }
            }

            return Counts(all);
        }

        public JsonObject ToGroup(string? key, string name) => new()
        {
            ["key"] = key,
            ["name"] = name,
            ["metrics"] = Metrics(),
            ["all_metrics"] = AllMetrics(),
            ["tickets"] = new JsonObject
            {
                ["with_targets"] = WithTargets,
                ["with_breach"] = WithBreach,
                ["all_achieved"] = AllAchieved,
            },
        };

        // achieved_pct = achieved / (achieved + breached): a rate from counts, never an average of rates.
        private JsonObject Counts(Dictionary<string, int>? counts)
        {
            var achieved = counts?.GetValueOrDefault("achieved") ?? 0;
            var breached = counts?.GetValueOrDefault("breached") ?? 0;
            var result = new JsonObject
            {
                ["achieved"] = achieved,
                ["breached"] = breached,
                ["achieved_pct"] = AnalyticsStats.RatePct(achieved, achieved + breached),
            };

            if (withRunning)
            {
                result["active"] = counts?.GetValueOrDefault("active") ?? 0;
                result["paused"] = counts?.GetValueOrDefault("paused") ?? 0;
            }

            return result;
        }
    }
}
