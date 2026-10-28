using System.Globalization;
using System.Text.Json.Nodes;

namespace ZendeskMcp.Tools;

/// <summary>
/// How Count_tickets breaks a count down: each group-by value is one extra search term, counted
/// with Zendesk's search count endpoint. Zendesk has no group-by of its own, so a breakdown is one
/// count per value.
/// </summary>
public static class TicketCountHelpers
{
    public static readonly string[] GroupByValues =
    [
        "status", "custom_status", "priority", "ticket_type", "brand", "group", "form", "channel",
        "custom_field", "satisfaction", "day", "week", "month",
    ];

    /// <summary>Most counts one call will run; each is a search request made as the caller.</summary>
    public const int MaxGroupValues = 200;

    // Unverified against the live API: the exact set of values the `via:` search keyword accepts.
    // These are the channel names Zendesk reports in via.channel.
    public static readonly string[] Channels =
    [
        "email", "web", "api", "voice", "native_messaging", "chat", "mobile", "mobile_sdk", "sms",
        "facebook", "twitter", "whatsapp", "web_widget",
    ];

    /// <summary>One value of the grouped dimension.</summary>
    /// <param name="Key">What the group is keyed by (an id, a tag, a status, a date).</param>
    /// <param name="Name">A readable label.</param>
    /// <param name="Term">The search term that selects it.</param>
    /// <param name="InSum">Whether it counts towards the total when working out the remainder; false for subsets of another value.</param>
    public sealed record GroupValue(JsonNode? Key, string Name, string Term, bool InSum = true);

    /// <summary>The values to count, and how to present the result.</summary>
    public sealed record GroupPlan(IReadOnlyList<GroupValue> Values, bool DropEmpty, string RemainderName, JsonObject Extras);

    /// <summary>
    /// Works out the values for a group-by. Throws <see cref="ArgumentException"/> when the filters
    /// already pin the grouped dimension (the breakdown would be one value), or a needed argument is
    /// missing.
    /// </summary>
    public static async Task<GroupPlan> PlanAsync(ZendeskService zendesk, string groupBy, long? groupByFieldId,
        TicketSearchFilters filters, CancellationToken cancellationToken)
    {
        void RefuseIf(bool filtered, string filter)
        {
            if (filtered)
            {
                throw new ArgumentException($"group_by {groupBy} cannot be combined with the {filter} filter, which already narrows tickets to particular {groupBy} values. Drop the filter, or count without group_by.");
            }
        }

        if (groupByFieldId is not null && groupBy != "custom_field")
        {
            throw new ArgumentException("group_by_field_id only applies with group_by custom_field.");
        }

        switch (groupBy)
        {
            case "status":
                RefuseIf(!string.IsNullOrWhiteSpace(filters.Status), "status");
                return new GroupPlan(Fixed("status", ["new", "open", "pending", "hold", "solved", "closed"]), false, "(other)", []);

            case "priority":
                RefuseIf(!string.IsNullOrWhiteSpace(filters.Priority), "priority");
                return new GroupPlan(Fixed("priority", ["low", "normal", "high", "urgent"]), false, "(none)", []);

            case "ticket_type":
                RefuseIf(!string.IsNullOrWhiteSpace(filters.TicketType), "ticket_type");
                return new GroupPlan(Fixed("ticket_type", ["question", "incident", "problem", "task"]), false, "(none)", []);

            case "channel":
                return new GroupPlan(Fixed("via", Channels), true, "(other)", []);

            case "satisfaction":
            {
                // Unverified against the live API: the with-comment keywords (goodwithcomment,
                // badwithcomment), and that satisfaction:good includes the good ratings with a
                // comment. The with-comment values are treated as subsets so the remainder is not
                // double-counted.
                var values = new List<GroupValue>
                {
                    new(JsonValue.Create("good"), "good", "satisfaction:good"),
                    new(JsonValue.Create("good_with_comment"), "good_with_comment", "satisfaction:goodwithcomment", InSum: false),
                    new(JsonValue.Create("bad"), "bad", "satisfaction:bad"),
                    new(JsonValue.Create("bad_with_comment"), "bad_with_comment", "satisfaction:badwithcomment", InSum: false),
                    new(JsonValue.Create("offered"), "offered", "satisfaction:offered"),
                    new(JsonValue.Create("unoffered"), "unoffered", "satisfaction:unoffered"),
                };
                return new GroupPlan(values, false, "(other)", []);
            }

            case "custom_status":
            {
                RefuseIf(filters.CustomStatusId is not null, "custom_status_id");
                var statuses = await zendesk.GetAsync("/api/v2/custom_statuses", cancellationToken: cancellationToken);
                var values = (statuses["custom_statuses"]?.AsArray() ?? [])
                    .Select(s => (Id: ToolJson.Long(s?["id"]), Label: ToolJson.String(s?["agent_label"])))
                    .Where(s => s.Id is not null)
                    .Select(s => new GroupValue(JsonValue.Create(s.Id!.Value), s.Label ?? Id(s.Id.Value), $"custom_status_id:{Id(s.Id.Value)}"))
                    .ToList();
                return new GroupPlan(values, true, "(none)", []);
            }

            case "brand":
            {
                RefuseIf(!string.IsNullOrWhiteSpace(filters.Brand), "brand");
                var brands = await zendesk.GetAllPagesAsync("/api/v2/brands", null, "brands", maxItems: 1000, cancellationToken: cancellationToken);
                return new GroupPlan(ById(brands.Items, "brand_id", "name"), true, "(other)", []);
            }

            case "group":
            {
                RefuseIf(!string.IsNullOrWhiteSpace(filters.Group), "group");
                var groups = await zendesk.GetAllPagesAsync("/api/v2/groups", null, "groups", maxItems: MaxGroupValues + 1, cancellationToken: cancellationToken);
                return new GroupPlan(ById(groups.Items.Where(g => g["deleted"] is not JsonValue d || !d.TryGetValue<bool>(out var deleted) || !deleted), "group_id", "name"), true, "(none)", []);
            }

            case "form":
            {
                RefuseIf(!string.IsNullOrWhiteSpace(filters.Form), "form");
                var forms = await zendesk.GetAsync("/api/v2/ticket_forms", cancellationToken: cancellationToken);
                var values = (forms["ticket_forms"]?.AsArray() ?? [])
                    .Where(f => ToolJson.Long(f?["id"]) is not null && !string.IsNullOrWhiteSpace(ToolJson.String(f?["name"])))
                    .Select(f => new GroupValue(JsonValue.Create(ToolJson.Long(f!["id"])!.Value), ToolJson.String(f["name"])!,
                        $"form:{TicketSearchQuery.Quote(ToolJson.String(f["name"])!)}"))
                    .ToList();
                return new GroupPlan(values, true, "(none)", []);
            }

            case "custom_field":
                return await PlanCustomFieldAsync(zendesk, groupByFieldId, filters, cancellationToken);

            case "day":
            case "week":
            case "month":
                return await PlanDatesAsync(zendesk, groupBy, filters, cancellationToken);

            default:
                throw new ArgumentException($"group_by must be one of {string.Join(", ", GroupByValues)} (got '{groupBy}').");
        }
    }

    /// <summary>Shapes the counts into groups, dropping empty values where asked and adding the remainder.</summary>
    public static JsonArray BuildGroups(GroupPlan plan, IReadOnlyList<long> counts, long total)
    {
        var groups = new JsonArray();
        long sum = 0;
        for (var i = 0; i < plan.Values.Count; i++)
        {
            var value = plan.Values[i];
            if (value.InSum)
            {
                sum += counts[i];
            }

            if (plan.DropEmpty && counts[i] == 0)
            {
                continue;
            }

            groups.Add(new JsonObject
            {
                ["key"] = value.Key?.DeepClone(),
                ["name"] = value.Name,
                ["count"] = counts[i],
            });
        }

        // Tickets matching none of the values (no priority, no group, a deleted brand). A multi-select
        // field can put one ticket in several groups, so the sum can exceed the total; then there is
        // nothing meaningful to report.
        if (total - sum > 0)
        {
            groups.Add(new JsonObject { ["key"] = null, ["name"] = plan.RemainderName, ["count"] = total - sum });
        }

        return groups;
    }

    /// <summary>The CSAT summary that accompanies a satisfaction breakdown.</summary>
    public static JsonObject Csat(GroupPlan plan, IReadOnlyList<long> counts)
    {
        long CountOf(string key) =>
            plan.Values.Select((v, i) => (v, i)).Where(p => ToolJson.String(p.v.Key) == key).Select(p => counts[p.i]).FirstOrDefault();

        var good = CountOf("good");
        var bad = CountOf("bad");
        return new JsonObject
        {
            ["rated"] = good + bad,
            ["good"] = good,
            ["bad"] = bad,
            ["score_pct"] = good + bad == 0 ? null : Math.Round(100.0 * good / (good + bad), 1),
        };
    }

    private static List<GroupValue> Fixed(string keyword, string[] values) =>
        values.Select(v => new GroupValue(JsonValue.Create(v), v, $"{keyword}:{v}")).ToList();

    private static List<GroupValue> ById(IEnumerable<JsonNode> records, string keyword, string nameField) =>
        records
            .Select(r => (Id: ToolJson.Long(r["id"]), Name: ToolJson.String(r[nameField])))
            .Where(r => r.Id is not null)
            .Select(r => new GroupValue(JsonValue.Create(r.Id!.Value), r.Name ?? Id(r.Id.Value), $"{keyword}:{Id(r.Id.Value)}"))
            .ToList();

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static async Task<GroupPlan> PlanCustomFieldAsync(ZendeskService zendesk, long? fieldId, TicketSearchFilters filters, CancellationToken cancellationToken)
    {
        if (fieldId is not { } id || id <= 0)
        {
            throw new ArgumentException("group_by custom_field needs group_by_field_id: the id of a drop-down, multi-select or checkbox ticket field (see List_ticket_fields).");
        }

        if (filters.CustomFieldId == id)
        {
            throw new ArgumentException("group_by custom_field cannot be combined with a custom_field_id filter on the same field.");
        }

        var response = await zendesk.GetAsync($"/api/v2/ticket_fields/{Id(id)}", cancellationToken: cancellationToken);
        var field = response["ticket_field"];
        var type = ToolJson.String(field?["type"]);
        var extras = new JsonObject
        {
            ["field"] = new JsonObject { ["id"] = id, ["title"] = field?["title"]?.DeepClone(), ["type"] = type },
        };
        var term = $"custom_field_{Id(id)}";

        switch (type)
        {
            case "tagger":
            case "multiselect":
            {
                // Drop-down and multi-select options are searched by their tag (the option's value).
                var values = (field?["custom_field_options"]?.AsArray() ?? [])
                    .Select(o => (Name: ToolJson.String(o?["name"]), Tag: ToolJson.String(o?["value"])))
                    .Where(o => !string.IsNullOrWhiteSpace(o.Tag) && !o.Tag.Any(char.IsWhiteSpace))
                    .Select(o => new GroupValue(JsonValue.Create(o.Tag), o.Name ?? o.Tag!, $"{term}:{o.Tag}"))
                    .ToList();
                return new GroupPlan(values, true, "(none)", extras);
            }

            case "checkbox":
                // Unverified against the live API: searching a checkbox field as custom_field_<id>:true / :false.
                return new GroupPlan(
                [
                    new GroupValue(JsonValue.Create("true"), "checked", $"{term}:true"),
                    new GroupValue(JsonValue.Create("false"), "unchecked", $"{term}:false"),
                ], true, "(none)", extras);

            default:
                throw new ArgumentException($"Ticket field {id} is a '{type}' field; group_by custom_field works with drop-down, multi-select and checkbox fields only.");
        }
    }

    private static async Task<GroupPlan> PlanDatesAsync(ZendeskService zendesk, string groupBy, TicketSearchFilters filters, CancellationToken cancellationToken)
    {
        string basis;
        string start;
        string? end;
        if (!string.IsNullOrWhiteSpace(filters.StartDate))
        {
            (basis, start, end) = ("created", filters.StartDate, filters.EndDate);
        }
        else if (!string.IsNullOrWhiteSpace(filters.SolvedStartDate))
        {
            (basis, start, end) = ("solved", filters.SolvedStartDate, filters.SolvedEndDate);
        }
        else
        {
            throw new ArgumentException($"group_by {groupBy} needs start_date (buckets by created date) or solved_start_date (buckets by solved date).");
        }

        // Date-only search terms are read in a time zone, so the buckets are reported in the connected
        // person's own Zendesk time zone, which is what they see in the agent interface.
        // Unverified against the live API: that search reads date-only terms in the user's time zone
        // rather than the account's.
        var me = await zendesk.GetAsync("/api/v2/users/me", cancellationToken: cancellationToken);
        var timeZone = ToolJson.String(me["user"]?["time_zone"]);
        var ianaTimeZone = ToolJson.String(me["user"]?["iana_time_zone"]);

        var first = DateOnly.ParseExact(TicketSearchQuery.NormaliseDate(start, basis == "created" ? "start_date" : "solved_start_date")[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var last = string.IsNullOrWhiteSpace(end)
            ? Today(ianaTimeZone)
            : DateOnly.ParseExact(TicketSearchQuery.NormaliseDate(end, basis == "created" ? "end_date" : "solved_end_date")[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (last < first)
        {
            throw new ArgumentException("The end date is before the start date.");
        }

        var cursor = groupBy switch
        {
            "week" => first.AddDays(-(((int)first.DayOfWeek + 6) % 7)),
            "month" => new DateOnly(first.Year, first.Month, 1),
            _ => first,
        };

        var values = new List<GroupValue>();
        while (cursor <= last)
        {
            var next = groupBy switch
            {
                "week" => cursor.AddDays(7),
                "month" => cursor.AddMonths(1),
                _ => cursor.AddDays(1),
            };
            var key = cursor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var name = groupBy switch
            {
                "week" => $"{key} to {next.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
                "month" => cursor.ToString("yyyy-MM", CultureInfo.InvariantCulture),
                _ => key,
            };
            values.Add(new GroupValue(JsonValue.Create(key), name,
                $"{basis}>={key} {basis}<{next.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"));
            cursor = next;

            if (values.Count > MaxGroupValues)
            {
                break;
            }
        }

        var extras = new JsonObject
        {
            ["date_basis"] = basis,
            ["timezone"] = new JsonObject { ["time_zone"] = timeZone, ["iana_time_zone"] = ianaTimeZone },
        };
        return new GroupPlan(values, false, "(other)", extras);
    }

    private static DateOnly Today(string? ianaTimeZone)
    {
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(ianaTimeZone))
        {
            try
            {
                now = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZone));
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // An unknown zone falls back to UTC; the reported timezone still says what Zendesk has.
            }
        }

        return DateOnly.FromDateTime(now.DateTime);
    }
}
