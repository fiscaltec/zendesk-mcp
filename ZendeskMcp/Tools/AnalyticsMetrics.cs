using System.Globalization;
using System.Text.Json.Nodes;

namespace ZendeskMcp.Tools;

/// <summary>
/// The pure part of Get_ticket_metrics_summary: one row per ticket from the ticket and its metric set,
/// and the figures, groups and day-by-day series computed from those rows.
/// </summary>
public static class AnalyticsMetrics
{
    /// <summary>Output metric name → the Zendesk ticket_metrics field holding it (<c>{calendar, business}</c> minutes).</summary>
    public static readonly (string Name, string Field)[] TimeMetrics =
    [
        ("first_reply_time", "reply_time_in_minutes"),
        ("first_resolution_time", "first_resolution_time_in_minutes"),
        ("full_resolution_time", "full_resolution_time_in_minutes"),
        ("requester_wait_time", "requester_wait_time_in_minutes"),
        ("agent_wait_time", "agent_wait_time_in_minutes"),
        ("on_hold_time", "on_hold_time_in_minutes"),
    ];

    /// <summary>The group_by values Get_ticket_metrics_summary accepts.</summary>
    public static readonly string[] GroupBys =
    [
        "group", "brand", "assignee", "channel", "week", "month", "status", "custom_status", "priority",
        "ticket_type", "form", "custom_field", "satisfaction",
    ];

    /// <summary>One ticket's figures.</summary>
    public sealed record Row(
        long TicketId,
        JsonObject Ticket,
        bool Solved,
        DateTimeOffset? CreatedAt,
        DateTimeOffset? SolvedAt,
        IReadOnlyDictionary<string, (double? Calendar, double? Business)> Times,
        long? Reopens,
        long? Replies);

    /// <summary>Builds a row from a ticket and its metric set (null when Zendesk returned none).</summary>
    public static Row ToRow(JsonObject ticket, JsonNode? metricSet)
    {
        var status = ToolJson.String(ticket["status"])?.ToLowerInvariant();
        var times = new Dictionary<string, (double?, double?)>(StringComparer.Ordinal);
        foreach (var (name, field) in TimeMetrics)
        {
            var value = metricSet?[field];
            times[name] = (AnalyticsStats.Number(value?["calendar"]), AnalyticsStats.Number(value?["business"]));
        }

        return new Row(
            AnalyticsStats.Long(ticket["id"]) ?? 0,
            ticket,
            status is "solved" or "closed",
            AnalyticsStats.Moment(ticket["created_at"]),
            AnalyticsStats.Moment(metricSet?["solved_at"]),
            times,
            AnalyticsStats.Long(metricSet?["reopens"]),
            AnalyticsStats.Long(metricSet?["replies"]));
    }

    /// <summary>
    /// The figures for a set of rows. First reply time counts every ticket with a reply; every other
    /// figure counts solved tickets (solved or closed) only, because an unsolved ticket's resolution,
    /// wait and reopen counts are still moving.
    /// </summary>
    public static JsonObject Figures(IReadOnlyCollection<Row> rows)
    {
        var solved = rows.Where(r => r.Solved).ToList();
        var figures = new JsonObject
        {
            ["tickets"] = new JsonObject
            {
                ["total"] = rows.Count,
                ["solved"] = solved.Count,
                ["unsolved"] = rows.Count - solved.Count,
            },
        };

        foreach (var (name, _) in TimeMetrics)
        {
            var source = name == "first_reply_time" ? rows : (IReadOnlyCollection<Row>)solved;
            figures[name] = new JsonObject
            {
                ["calendar"] = AnalyticsStats.Summary(source.Select(r => r.Times[name].Calendar).OfType<double>().ToList()),
                ["business"] = AnalyticsStats.Summary(source.Select(r => r.Times[name].Business).OfType<double>().ToList()),
            };
        }

        var reopened = solved.Count(r => r.Reopens > 0);
        figures["reopens"] = new JsonObject
        {
            ["tickets_reopened"] = reopened,
            ["reopen_rate_pct"] = AnalyticsStats.RatePct(reopened, solved.Count),
            ["total_reopens"] = solved.Sum(r => r.Reopens ?? 0),
        };

        var replies = solved.Select(r => r.Replies).OfType<long>().Select(v => (double)v).ToList();
        figures["replies"] = new JsonObject
        {
            ["median"] = AnalyticsStats.Median(replies) is { } median ? AnalyticsStats.Round(median) : null,
            ["avg"] = replies.Count > 0 ? AnalyticsStats.Round(replies.Average()) : null,
        };

        // One-touch as Zendesk defines it: solved with exactly one agent reply. Zero-touch: solved
        // with none (closed by a trigger, merged, or answered by automation).
        var oneTouch = solved.Count(r => r.Replies == 1);
        figures["one_touch"] = new JsonObject
        {
            ["tickets"] = oneTouch,
            ["rate_pct"] = AnalyticsStats.RatePct(oneTouch, solved.Count),
            ["zero_touch_tickets"] = solved.Count(r => r.Replies == 0),
        };

        return figures;
    }

    /// <summary>The trend columns: date, then counts and sums that add up across days.</summary>
    public static IReadOnlyList<string> TrendColumns { get; } = BuildTrendColumns();

    /// <summary>
    /// A day-by-day series over <paramref name="days"/>: a ticket falls on its solved date when
    /// <paramref name="bySolved"/>, otherwise its created date, in <paramref name="zone"/>.
    /// </summary>
    public static JsonObject Trend(IReadOnlyCollection<Row> rows, IReadOnlyList<DateOnly> days, TimeZoneInfo zone, bool bySolved)
    {
        var byDay = rows
            .Select(r => (Row: r, Moment: bySolved ? r.SolvedAt : r.CreatedAt))
            .Where(x => x.Moment is not null)
            .GroupBy(x => AnalyticsStats.LocalDay(x.Moment!.Value, zone))
            .ToDictionary(g => g.Key, g => g.Select(x => x.Row).ToList());

        var lines = new List<IEnumerable<object?>>();
        foreach (var day in days)
        {
            var dayRows = byDay.TryGetValue(day, out var list) ? list : [];
            var solved = dayRows.Where(r => r.Solved).ToList();
            var values = new List<object?>
            {
                day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                dayRows.Count,
                solved.Count,
                solved.Count(r => r.Reopens > 0),
                solved.Sum(r => r.Reopens ?? 0),
                solved.Sum(r => r.Replies ?? 0),
                solved.Count(r => r.Replies == 1),
                solved.Count(r => r.Replies == 0),
            };

            foreach (var (name, _) in TimeMetrics)
            {
                var source = name == "first_reply_time" ? dayRows : solved;
                var calendar = source.Select(r => r.Times[name].Calendar).OfType<double>().ToList();
                var business = source.Select(r => r.Times[name].Business).OfType<double>().ToList();
                values.Add(calendar.Count);
                values.Add(calendar.Sum());
                values.Add(business.Count);
                values.Add(business.Sum());
            }

            lines.Add(values);
        }

        return AnalyticsStats.Table(TrendColumns, lines);
    }

    /// <summary>
    /// The group keys a ticket belongs to for <paramref name="groupBy"/> (usually one; several for a
    /// multiselect field; a single null for a ticket without a value).
    /// </summary>
    public static IReadOnlyList<string?> GroupKeys(Row row, string groupBy, long? fieldId, TimeZoneInfo zone, bool bySolved)
    {
        var ticket = row.Ticket;
        switch (groupBy)
        {
            case "group": return [IdKey(ticket["group_id"])];
            case "brand": return [IdKey(ticket["brand_id"])];
            case "assignee": return [IdKey(ticket["assignee_id"])];
            case "form": return [IdKey(ticket["ticket_form_id"])];
            case "custom_status": return [IdKey(ticket["custom_status_id"])];
            case "channel": return [ToolJson.String(ticket["via"]?["channel"])];
            case "status": return [ToolJson.String(ticket["status"])];
            case "priority": return [ToolJson.String(ticket["priority"])];
            case "ticket_type": return [ToolJson.String(ticket["type"])];
            case "satisfaction": return [SatisfactionKey(ticket["satisfaction_rating"])];
            case "week":
            case "month":
                var moment = bySolved ? row.SolvedAt : row.CreatedAt;
                return [moment is { } m ? AnalyticsStats.BucketKey(m, zone, groupBy) : null];
            case "custom_field":
                return FieldKeys(ticket, fieldId);
            default:
                throw new ArgumentException($"Unknown group_by '{groupBy}'.");
        }
    }

    /// <summary>
    /// The satisfaction bucket: good / good_with_comment / bad / bad_with_comment from the rating and
    /// whether it has a comment; offered (sent, unanswered); unoffered (never sent, or no rating).
    /// </summary>
    public static string SatisfactionKey(JsonNode? rating)
    {
        var score = ToolJson.String(rating?["score"])?.ToLowerInvariant();
        var hasComment = !string.IsNullOrWhiteSpace(ToolJson.String(rating?["comment"]));
        return score switch
        {
            "good" => hasComment ? "good_with_comment" : "good",
            "bad" => hasComment ? "bad_with_comment" : "bad",
            "offered" => "offered",
            _ => "unoffered",
        };
    }

    private static IReadOnlyList<string?> FieldKeys(JsonObject ticket, long? fieldId)
    {
        var field = (ticket["custom_fields"] as JsonArray ?? [])
            .FirstOrDefault(f => AnalyticsStats.Long(f?["id"]) == fieldId);
        var value = field?["value"];
        switch (value)
        {
            case JsonArray many:
                var keys = many.Select(v => ToolJson.String(v)).Where(v => !string.IsNullOrEmpty(v)).Distinct().Cast<string?>().ToList();
                return keys.Count == 0 ? [null] : keys;
            case JsonValue single when single.TryGetValue<bool>(out var flag):
                return [flag ? "true" : "false"];
            case JsonValue single when single.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text):
                return [text];
            case JsonValue single when single.TryGetValue<long>(out var number):
                return [number.ToString(CultureInfo.InvariantCulture)];
            default:
                return [null];
        }
    }

    private static string? IdKey(JsonNode? node) =>
        AnalyticsStats.Long(node) is { } id ? id.ToString(CultureInfo.InvariantCulture) : null;

    private static List<string> BuildTrendColumns()
    {
        var columns = new List<string> { "date", "tickets", "solved", "reopened_tickets", "total_reopens", "replies", "one_touch", "zero_touch" };
        foreach (var (name, _) in TimeMetrics)
        {
            columns.Add($"{name}_n");
            columns.Add($"{name}_sum");
            columns.Add($"{name}_business_n");
            columns.Add($"{name}_business_sum");
        }

        return columns;
    }
}
