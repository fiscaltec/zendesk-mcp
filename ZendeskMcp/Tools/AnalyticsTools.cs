using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Tools;

/// <summary>
/// Support KPIs computed across many tickets in one call: ticket metrics (reply, resolution and wait
/// times, reopens, one-touch), SLA achievement over a period, and the unsolved backlog ranked by SLA
/// urgency. Every one only reads (GETs), so they sit on the read tier. The arithmetic lives in
/// <see cref="AnalyticsStats"/>, <see cref="AnalyticsMetrics"/> and <see cref="AnalyticsSla"/>; these
/// methods gather tickets and shape the result.
/// </summary>
[McpServerToolType]
public static class AnalyticsTools
{
    // Filter descriptions shared by the four analytics tools (the same as Search_tickets).
    private const string TextDescription = "Full-text search string to match against ticket subject, description, and comments. This is NOT a Zendesk Search query—just the plain text to search for. Max 500 characters. Leave empty to include all tickets.";
    private const string StatusDescription = "Ticket status filter. Must be one of: new, open, pending, hold, solved, closed. When custom ticket statuses are activated, this filters on the broader status category -- use custom_status_id to filter on one specific custom status.";
    private const string PriorityDescription = "Ticket priority filter. Can be a single value or comma-separated list (e.g., 'high,urgent'). Valid values: low, normal, high, urgent.";
    private const string TicketTypeDescription = "Ticket type filter. Can be a single value or comma-separated list (e.g., 'problem' or 'problem,incident'). Valid values: question, incident, problem, task. For example, open problem tickets are ticket_type 'problem' with status 'open'.";
    private const string AssigneeDescription = "Filter by assigned agent. Accepts: 'none' (unassigned), 'me' (current user), user name (full or partial like 'Susan Warren' or 'Warren'), email address, user ID, or phone number. Multi-word names are automatically quoted.";
    private const string RequesterDescription = "Filter by ticket requester. Accepts: 'none', 'me', user name (full or partial), email address, numeric user ID, or phone number. Use the numeric Zendesk user ID to cover all tickets from a specific user (e.g. '1234567890'). Multi-word names are automatically quoted.";
    private const string OrganizationDescription = "Filter by organisation name or organisation ID. Returns tickets from requesters who are members of the organisation. You can use the organisation name (e.g., 'Acme Corp', 'customers') or the numeric organisation ID (e.g., '22989442'). Use 'none' to return tickets from requesters not in any organisation. Multi-word organisation names are automatically quoted.";
    private const string GroupDescription = "Filter by group name or group ID. Returns tickets assigned to agents who are members of the group. You can use the group name (e.g., 'Support', 'Level 2') or the numeric group ID (e.g., '20663166'). Multi-word group names are automatically quoted.";
    private const string BrandDescription = "Filter by brand name or brand ID. You can use the brand name (e.g., 'Nordstrom', 'Banana Republic') or the numeric brand ID (e.g., '12345'). Multi-word brand names are automatically quoted.";
    private const string FormDescription = "Filter by ticket form name. Multi-word form names are automatically quoted. Example: 'Default ticket form' or 'legal'.";
    private const string IncludeTagsDescription = "Comma-separated tags that must be present, as a string (e.g., 'bug,urgent,needs-review'). Case does not matter.";
    private const string ExcludeTagsDescription = "Comma-separated tags that must NOT be present, as a string (e.g., 'spam,test_data'). Case does not matter.";
    private const string CustomFieldIdDescription = "The numeric ID of a custom ticket field to filter by. Must be used together with custom_field_value. You can get the field ID from the Ticket Fields admin page URL or via the List_ticket_fields tool.";
    private const string CustomFieldValueDescription = "The value to search for in the custom field specified by custom_field_id. Must be used together with custom_field_id. Multi-word values are automatically quoted.";
    private const string CustomStatusIdDescription = "Filter by a specific custom ticket status ID. Only applies when custom ticket statuses are activated on the account. Use this to target one exact status (e.g. 'On Hold') rather than the whole status category. Find the ID with List_ticket_fields: the Status field carries a custom_statuses array listing each status with its id and agent_label.";
    private const string HasAttachmentsDescription = "Filter by attachment presence. true = with attachments, false = without attachments, omit = ignore filter.";
    private const string StartDateDescription = "Start date filter (inclusive). ISO format: YYYY-MM-DD, YYYY-MM-DDThh:mm:ss, or YYYY-MM-DDThh:mm:ssZ. Example: '2024-01-15' or '2024-01-15T09:30:00Z'.";
    private const string EndDateDescription = "End date filter (inclusive). ISO format: YYYY-MM-DD, YYYY-MM-DDThh:mm:ss, or YYYY-MM-DDThh:mm:ssZ. Example: '2024-12-31' or '2024-12-31T23:59:59Z'.";
    private const string SolvedStartDateDescription = "Filter by solved date (inclusive, lower bound). ISO format: YYYY-MM-DD, YYYY-MM-DDThh:mm:ss, or YYYY-MM-DDThh:mm:ssZ. Example: '2024-01-15'. Maps to solved>= in the Zendesk search query.";
    private const string SolvedEndDateDescription = "Filter by solved date (inclusive, upper bound). ISO format: YYYY-MM-DD, YYYY-MM-DDThh:mm:ss, or YYYY-MM-DDThh:mm:ssZ. Example: '2024-12-31'. Maps to solved<= in the Zendesk search query.";

    private const int MaxTrendDays = 31;

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "Get_ticket_metrics_summary", Title = "Get ticket metrics summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Summarise Zendesk ticket metrics over a date range in one call: first reply time, first and full resolution time, requester wait, agent wait and on-hold time (each as n, median, avg and p90, in calendar and business minutes), reopens, replies and one-touch resolution. Use it for support KPI reports instead of fetching tickets one by one; for one ticket's values use Search_tickets with include_fields ['metrics'].\n\n" +
        "Which tickets: the same filters as Search_tickets, and a start date is required (start_date for created, or solved_start_date). For resolution, wait-time, reopen and one-touch reports filter on solved dates (solved_start_date / solved_end_date); for first reply time filter on created dates. First reply time counts every ticket an agent has replied to; every other figure counts solved tickets (status solved or closed) only. Times are in minutes. Business minutes follow the account's business-hours schedule, and equal calendar minutes when it has none. The median is the middle value (the mean of the two middle values for an even count); p90 is nearest-rank.\n\n" +
        "group_by splits every figure by group, brand, assignee, channel, week, month, status, custom_status, priority, ticket_type, form, custom_field (with group_by_field_id) or satisfaction (one value per call). For ticket counts alone, at any volume, use Count_tickets with group_by. trend: true adds a day-by-day series to the totals and to each group, as {columns, rows}, each row one comma-separated line of values in column order. Rows hold counts and sums only (tickets, solved, reopened_tickets, total_reopens, replies, one_touch, zero_touch, and <metric>_n, <metric>_sum, <metric>_business_n, <metric>_business_sum), so days add up to weeks or months: an average is sum / n, the one-touch rate is one_touch / solved, the reopen rate reopened_tickets / solved. Medians do not add up: for a weekly or monthly median call again with group_by week or month. Days, weeks and months follow the connected Zendesk user's timezone, the one Zendesk reads the date filters in (returned as timezone). A ticket falls on its solved date when the period starts with solved_start_date and no start_date, otherwise on its created date.\n\n" +
        "Limits: at most the server's analytics ticket limit (2000 by default) of matching tickets per call; above that nothing is computed and you should split the range (for example into halves or weeks). Medians from separate calls cannot be combined; averages and rates can, from n and the sums. trend covers at most 31 days per call. Make these calls one after another, not in parallel.\n\n" +
        "Returns {query, search_url, date_basis, tickets: {total, solved, unsolved}, first_reply_time, first_resolution_time, full_resolution_time, requester_wait_time, agent_wait_time, on_hold_time (each {calendar: {n, median, avg, p90}, business: {...}}), reopens: {tickets_reopened, reopen_rate_pct, total_reopens}, replies: {median, avg}, one_touch: {tickets, rate_pct, zero_touch_tickets}}, plus timezone, trend with trend, groups: [{key, name, ...the same figures}] with group_by (tickets without a value under key null, name '(none)'), and notes when something affects how to read the figures.")]
    public static async Task<string> GetTicketMetricsSummary(
        ZendeskService zendesk,
        [Description(TextDescription)] string? text = null,
        [Description(StatusDescription)] string? status = null,
        [Description(PriorityDescription)] string? priority = null,
        [Description(TicketTypeDescription)] string? ticket_type = null,
        [Description(AssigneeDescription)] string? assignee = null,
        [Description(RequesterDescription)] string? requester = null,
        [Description(OrganizationDescription)] string? organization = null,
        [Description(GroupDescription)] string? group = null,
        [Description(BrandDescription)] string? brand = null,
        [Description(FormDescription)] string? form = null,
        [Description(IncludeTagsDescription)] string? include_tags = null,
        [Description(ExcludeTagsDescription)] string? exclude_tags = null,
        [Description(CustomFieldIdDescription)] long? custom_field_id = null,
        [Description(CustomFieldValueDescription)] string? custom_field_value = null,
        [Description(CustomStatusIdDescription)] long? custom_status_id = null,
        [Description(HasAttachmentsDescription)] bool? has_attachments = null,
        [Description(StartDateDescription)] string? start_date = null,
        [Description(EndDateDescription)] string? end_date = null,
        [Description(SolvedStartDateDescription)] string? solved_start_date = null,
        [Description(SolvedEndDateDescription)] string? solved_end_date = null,
        [Description("Split every figure by this, one value per call: group, brand, assignee (agent), channel (email, web, api, native_messaging, ...), week (Monday to Sunday), month, status, custom_status, priority, ticket_type, form, custom_field (needs group_by_field_id) or satisfaction (good, good_with_comment, bad, bad_with_comment, offered = survey sent and not answered, unoffered = survey never sent). Each entry in groups carries key, name and the same figures. Tickets without a value are grouped under key null, name '(none)'.")] string? group_by = null,
        [Description("With group_by custom_field: the id of a dropdown, multiselect or checkbox ticket field (see List_ticket_fields). A ticket with several options of a multiselect field is in each of their groups.")] long? group_by_field_id = null,
        [Description("true adds a day-by-day series (counts and sums per day) to the totals and to each group. The date range can then be at most 31 days. Default false.")] bool? trend = null,
        CancellationToken cancellationToken = default)
    {
        group_by = ValidateGroupBy(group_by, AnalyticsMetrics.GroupBys);
        if (group_by == "custom_field")
        {
            if (group_by_field_id is not { } fid || fid <= 0)
            {
                throw new ArgumentException("group_by custom_field needs group_by_field_id, a positive ticket field id.");
            }
        }
        else if (group_by_field_id is not null)
        {
            throw new ArgumentException("group_by_field_id is only used with group_by custom_field.");
        }

        if (string.IsNullOrWhiteSpace(start_date) && string.IsNullOrWhiteSpace(solved_start_date))
        {
            throw new ArgumentException("A start date is required: start_date (created) or solved_start_date.");
        }

        var query = TicketSearchQuery.Build(new TicketSearchFilters
        {
            Text = text, Status = status, Priority = priority, TicketType = ticket_type, Assignee = assignee,
            Requester = requester, Organization = organization, Group = group, Brand = brand, Form = form,
            IncludeTags = include_tags, ExcludeTags = exclude_tags, CustomFieldId = custom_field_id,
            CustomFieldValue = custom_field_value, CustomStatusId = custom_status_id, HasAttachments = has_attachments,
            StartDate = start_date, EndDate = end_date, SolvedStartDate = solved_start_date, SolvedEndDate = solved_end_date,
        });
        var searchUrl = TicketSearchQuery.AgentSearchUrl(zendesk.BaseUrl, query);
        var bySolved = !string.IsNullOrWhiteSpace(solved_start_date) && string.IsNullOrWhiteSpace(start_date);

        var (zone, zoneName) = await AnalyticsSupport.GetTimeZoneAsync(zendesk, cancellationToken);
        List<DateOnly>? trendDays = null;
        if (trend == true)
        {
            var first = DayOf(bySolved ? solved_start_date! : start_date!, bySolved ? "solved_start_date" : "start_date");
            var endValue = bySolved ? solved_end_date : end_date;
            var last = string.IsNullOrWhiteSpace(endValue)
                ? AnalyticsStats.Today(zone, DateTimeOffset.UtcNow)
                : DayOf(endValue, bySolved ? "solved_end_date" : "end_date");
            if (last < first)
            {
                throw new ArgumentException("The end date is before the start date.");
            }

            if (last.DayNumber - first.DayNumber + 1 > MaxTrendDays)
            {
                throw new ArgumentException($"trend covers at most {MaxTrendDays} days per call; split the range or leave trend out.");
            }

            trendDays = AnalyticsStats.Days(first, last).ToList();
        }

        var cap = zendesk.Options.MaxAnalyticsTickets;
        var gathered = await AnalyticsSupport.GatherTicketIdsAsync(zendesk, query, cap, cancellationToken);
        if (gathered.OverCap)
        {
            return AnalyticsSupport.OverCapResult(query, searchUrl, cap, "Split the date range (for example into halves or weeks) or narrow the filters, and call again for each part.");
        }

        // show_many with include=metric_sets returns a top-level metric_sets array keyed by ticket_id
        // (confirmed against the live API).
        var (tickets, sideloads) = await AnalyticsSupport.ShowManyTicketsAsync(zendesk, gathered.Ids, "metric_sets", cancellationToken);
        var metricSets = (sideloads.GetValueOrDefault("metric_sets") ?? [])
            .Where(m => AnalyticsStats.Long(m["ticket_id"]) is not null)
            .GroupBy(m => AnalyticsStats.Long(m["ticket_id"])!.Value)
            .ToDictionary(g => g.Key, g => (JsonNode)g.First());
        var rows = tickets.Select(t => AnalyticsMetrics.ToRow(t, metricSets.GetValueOrDefault(AnalyticsStats.Long(t["id"]) ?? 0))).ToList();

        var result = new JsonObject
        {
            ["query"] = query,
            ["search_url"] = searchUrl,
            ["date_basis"] = bySolved ? "solved" : "created",
        };
        foreach (var (key, value) in AnalyticsMetrics.Figures(rows))
        {
            result[key] = value?.DeepClone();
        }

        result["timezone"] = zoneName;
        if (trendDays is not null)
        {
            result["trend"] = AnalyticsMetrics.Trend(rows, trendDays, zone, bySolved);
        }

        if (group_by is not null)
        {
            var keyed = new Dictionary<string, List<AnalyticsMetrics.Row>>(StringComparer.Ordinal);
            var none = new List<AnalyticsMetrics.Row>();
            foreach (var row in rows)
            {
                foreach (var key in AnalyticsMetrics.GroupKeys(row, group_by, group_by_field_id, zone, bySolved))
                {
                    if (key is null)
                    {
                        none.Add(row);
                    }
                    else
                    {
                        (keyed.TryGetValue(key, out var list) ? list : keyed[key] = []).Add(row);
                    }
                }
            }

            var name = await GroupNamerAsync(zendesk, group_by, group_by_field_id, keyed.Keys, cancellationToken);
            var groups = new JsonArray();
            foreach (var (key, groupRows) in keyed.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                groups.Add(MetricsGroup(key, name(key), groupRows, trendDays, zone, bySolved));
            }

            if (none.Count > 0)
            {
                groups.Add(MetricsGroup(null, "(none)", none, trendDays, zone, bySolved));
            }

            result["groups"] = groups;
        }

        var notes = new JsonArray();
        var withoutMetrics = rows.Count(r => !metricSets.ContainsKey(r.TicketId));
        if (withoutMetrics > 0)
        {
            notes.Add($"{withoutMetrics} ticket(s) had no metric set (for example archived or very new tickets); they count in tickets but not in the time figures.");
        }

        if (zoneName == "UTC")
        {
            notes.Add("Days, weeks and months are in UTC (the connected user's timezone was UTC or could not be resolved).");
        }

        if (rows.Count == 0)
        {
            notes.Add("No tickets matched these filters.");
        }

        if (notes.Count > 0)
        {
            result["notes"] = notes;
        }

        return result.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "Get_sla_summary", Title = "Get SLA summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "SLA achievement over a period, from the account's Zendesk SLA policies, in one call: for each SLA metric (first_reply_time, next_reply_time, periodic_update_time, pausable_update_time, requester_wait_time, agent_work_time, total_resolution_time) the achieved and breached targets and achieved_pct = achieved / (achieved + breached). Targets are counted one by one: a ticket with three customer follow-ups has three next_reply_time targets. Use it for SLA KPI reports and achievement rates; for one ticket's targets use Get_ticket or Search_tickets with include ['sla'], and for what to work on now use Get_sla_queue.\n\n" +
        "Period: start_date (required) and end_date, as days in the connected Zendesk user's timezone (returned as timezone). With date_basis 'outcome' (default) each target falls on the day its result was settled: its breach time if breached, its completion time if achieved; targets still running are left out. With date_basis 'created' the targets of tickets created in the period count, and active and paused counts are added. A target falls in exactly one period, so counts from separate calls add up: for a month, a quarter or 3, 6 and 12-month figures, add achieved and breached over the weeks or months and recompute the percentage (never average percentages). A ticket reopened later can still turn an achieved target into a breached one, so a past period's figures can change.\n\n" +
        "group_by splits every figure by group, assignee, brand, channel, priority, policy (the SLA policy), week or month (one value per call); ticket attributes are the ticket's current values.\n\n" +
        "Returns {query, searches, search_url, date_basis, period: {start, end}, timezone, metrics: {<metric>: {achieved, breached, achieved_pct}}, all_metrics, tickets: {read, with_sla, without_sla, archived_without_events, with_targets, with_breach, all_achieved}, groups: [{key, name, metrics, all_metrics, tickets: {with_targets, with_breach, all_achieved}}] with group_by, breached_total, breached: {columns, rows}, notes}. breached lists the breached targets in the period, most late first, each row one CSV line in column order: ticket_id, url, subject, ticket_status, metric, instance, policy, target_minutes, business_hours, breach_at, completed_at, minutes_late, assignee_id, assignee, group_id, group. breached_total counts them all, so a list cut short by breached_limit says so. with_targets counts tickets with a target in these figures; all_achieved leaves out tickets with a breached target or one still running. archived_without_events counts closed tickets Zendesk has archived (not updated for about 120 days) and no longer returns SLA events for: the figures leave them out, so for older months use the figures kept when each month closed. It covers the tickets the connected Zendesk login can see.\n\n" +
        "Limits: at most the server's analytics ticket limit (2000 by default) of tickets read per call (with date_basis outcome, the tickets updated since the start of the period plus unsolved tickets untouched since before it); above that nothing is computed, so split the period into weeks. It reads each ticket's SLA events, so it is slower than Get_ticket_metrics_summary. Run these calls one after another, not in parallel.")]
    public static async Task<string> GetSlaSummary(
        ZendeskService zendesk,
        [Description("First day of the period (inclusive), YYYY-MM-DD. Required.")] string start_date,
        [Description("Last day of the period (inclusive), YYYY-MM-DD. Default: today.")] string? end_date = null,
        [Description("outcome (default): each target counts on the day it was breached or achieved, and running targets are left out. created: the targets of tickets created in the period, with active and paused counts.")] string? date_basis = null,
        [Description("Split every figure by this, one value per call: group, assignee (agent), brand, channel, priority, policy (the SLA policy), week (Monday to Sunday) or month. With date_basis outcome a target's week or month is that of its breach or completion.")] string? group_by = null,
        [Description("How many breached targets to list, most late first: 0 to 1000, default 100. breached_total always gives the full count.")] int? breached_limit = null,
        [Description(TextDescription)] string? text = null,
        [Description(StatusDescription)] string? status = null,
        [Description(PriorityDescription)] string? priority = null,
        [Description(TicketTypeDescription)] string? ticket_type = null,
        [Description(AssigneeDescription)] string? assignee = null,
        [Description(RequesterDescription)] string? requester = null,
        [Description(OrganizationDescription)] string? organization = null,
        [Description(GroupDescription)] string? group = null,
        [Description(BrandDescription)] string? brand = null,
        [Description(FormDescription)] string? form = null,
        [Description(IncludeTagsDescription)] string? include_tags = null,
        [Description(ExcludeTagsDescription)] string? exclude_tags = null,
        [Description(CustomFieldIdDescription)] long? custom_field_id = null,
        [Description(CustomFieldValueDescription)] string? custom_field_value = null,
        [Description(CustomStatusIdDescription)] long? custom_status_id = null,
        [Description(HasAttachmentsDescription)] bool? has_attachments = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(start_date))
        {
            throw new ArgumentException("start_date is required (YYYY-MM-DD).");
        }

        var start = AnalyticsSupport.ParseDay(start_date, "start_date");
        var basis = (date_basis ?? "outcome").Trim().ToLowerInvariant();
        if (basis is not ("outcome" or "created"))
        {
            throw new ArgumentException($"date_basis must be outcome or created (got '{date_basis}').");
        }

        group_by = ValidateGroupBy(group_by, AnalyticsSla.SummaryGroupBys);
        var limit = breached_limit ?? 100;
        if (limit is < 0 or > 1000)
        {
            throw new ArgumentException($"breached_limit must be between 0 and 1000 (got {limit}).");
        }

        var filters = new TicketSearchFilters
        {
            Text = text, Status = status, Priority = priority, TicketType = ticket_type, Assignee = assignee,
            Requester = requester, Organization = organization, Group = group, Brand = brand, Form = form,
            IncludeTags = include_tags, ExcludeTags = exclude_tags, CustomFieldId = custom_field_id,
            CustomFieldValue = custom_field_value, CustomStatusId = custom_status_id, HasAttachments = has_attachments,
        };
        var baseQuery = TicketSearchQuery.Build(filters);

        var (zone, zoneName) = await AnalyticsSupport.GetTimeZoneAsync(zendesk, cancellationToken);
        var end = string.IsNullOrWhiteSpace(end_date) ? AnalyticsStats.Today(zone, DateTimeOffset.UtcNow) : AnalyticsSupport.ParseDay(end_date, "end_date");
        if (end < start)
        {
            throw new ArgumentException("end_date is before start_date.");
        }

        var startText = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endText = end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // outcome: a target settled in the period was breached or met during it, so its ticket was
        // updated since the start (Zendesk updates a ticket when it is replied to or solved) — except a
        // breach of a running target, which changes nothing on the ticket; the unsolved search catches
        // those. created: the period is the tickets' created date, as Search_tickets reads it.
        var searches = basis == "outcome"
            ? new[] { $"{baseQuery} updated>={startText}", $"{baseQuery} status<solved" }
            : new[] { $"{baseQuery} created>={startText} created<={endText}" };

        var cap = zendesk.Options.MaxAnalyticsTickets;
        var ids = new List<long>();
        var seen = new HashSet<long>();
        foreach (var search in searches)
        {
            var gathered = await AnalyticsSupport.GatherTicketIdsAsync(zendesk, search, cap, cancellationToken);
            ids.AddRange(gathered.Ids.Where(seen.Add));
            if (gathered.OverCap || ids.Count > cap)
            {
                return AnalyticsSupport.OverCapResult(baseQuery, TicketSearchQuery.AgentSearchUrl(zendesk.BaseUrl, searches[0]), cap,
                    "Split the period into weeks (or narrow the filters), call once per week, and add up achieved and breached.");
            }
        }

        var fetched = await TicketSla.FetchManyAsync(zendesk, ids, cancellationToken);
        var items = ids.Select(id => fetched[id]).ToList();

        var namer = await SlaNamerAsync(zendesk, items, group_by, cancellationToken);
        var summary = AnalyticsSla.Summarise(items, new AnalyticsSla.SummaryOptions(
            basis, start, end, zone, group_by, limit, namer, id => AnalyticsSupport.TicketUrl(zendesk, id)));

        var result = new JsonObject
        {
            ["query"] = baseQuery,
            ["searches"] = new JsonArray(searches.Select(s => (JsonNode?)s).ToArray()),
            ["search_url"] = TicketSearchQuery.AgentSearchUrl(zendesk.BaseUrl, searches[0]),
            ["date_basis"] = basis,
            ["period"] = new JsonObject { ["start"] = startText, ["end"] = endText },
            ["timezone"] = zoneName,
        };
        foreach (var (key, value) in summary)
        {
            result[key] = value?.DeepClone();
        }

        var notes = new JsonArray();
        if (basis == "outcome")
        {
            notes.Add("Targets still running (active or paused) are left out with date_basis outcome; use date_basis created to see them.");
        }

        var archived = summary["tickets"]?["archived_without_events"]?.GetValue<int>() ?? 0;
        if (archived > 0)
        {
            notes.Add($"{archived} archived ticket(s) returned no SLA events and are left out of the figures.");
        }

        var breachedTotal = summary["breached_total"]?.GetValue<int>() ?? 0;
        if (breachedTotal > limit)
        {
            notes.Add($"breached lists {limit} of {breachedTotal} breached targets; raise breached_limit (up to 1000) for more.");
        }

        if (notes.Count > 0)
        {
            result["notes"] = notes;
        }

        return result.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "Get_sla_queue", Title = "Get SLA queue", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "The work queue by SLA: the unsolved tickets that most need attention under the account's Zendesk SLA policies, across the whole backlog rather than one page. Use it for 'what should I work on next?' or 'which tickets are about to breach?'. It reads every unsolved ticket (new, open, pending, on-hold) matching the filters and returns the top `limit`: first the tickets with an SLA target overdue now (running past its breach time, longest overdue first), then the rest by their next breach, soonest first. A ticket whose late target has since been met (for example a late reply that has been sent) is ranked by its next breach. Paused targets (for example while a ticket is pending) are not due. Narrow it with assignee ('me' for the connected agent), group, brand, priority and the other search filters.\n\n" +
        "Returns {query, search_url, counts: {scanned, with_sla, without_sla, overdue, due_within_1h, due_within_4h} over the whole backlog (due_within_4h includes due_within_1h), tickets: [{id, url, subject, status, priority, assignee_id, assignee, group_id, group, urgency (overdue or due), metric (the target that ranks it), minutes_overdue or minutes_left, sla}]}, where sla is the same sla object as Get_ticket and Search_tickets return with include 'sla' ({breached, next_breach_at, next_breach_metric, targets}). It covers the tickets the connected Zendesk login can see.\n\n" +
        "Limits: at most the server's analytics ticket limit (2000 by default) of unsolved tickets per call; above that nothing is returned, so narrow by group, assignee or brand. Run these calls one after another, not in parallel.")]
    public static async Task<string> GetSlaQueue(
        ZendeskService zendesk,
        [Description("How many tickets to return, most urgent first: 1 to 100, default 25. The counts always cover the whole backlog.")] int? limit = null,
        [Description(TextDescription)] string? text = null,
        [Description("Only tickets with this status: new, open, pending or hold. Leave it out for all unsolved tickets, which is what the queue holds.")] string? status = null,
        [Description(PriorityDescription)] string? priority = null,
        [Description(TicketTypeDescription)] string? ticket_type = null,
        [Description(AssigneeDescription)] string? assignee = null,
        [Description(RequesterDescription)] string? requester = null,
        [Description(OrganizationDescription)] string? organization = null,
        [Description(GroupDescription)] string? group = null,
        [Description(BrandDescription)] string? brand = null,
        [Description(FormDescription)] string? form = null,
        [Description(IncludeTagsDescription)] string? include_tags = null,
        [Description(ExcludeTagsDescription)] string? exclude_tags = null,
        [Description(CustomFieldIdDescription)] long? custom_field_id = null,
        [Description(CustomFieldValueDescription)] string? custom_field_value = null,
        [Description(CustomStatusIdDescription)] long? custom_status_id = null,
        [Description(HasAttachmentsDescription)] bool? has_attachments = null,
        CancellationToken cancellationToken = default)
    {
        var take = limit ?? 25;
        if (take is < 1 or > 100)
        {
            throw new ArgumentException($"limit must be between 1 and 100 (got {take}).");
        }

        if (!string.IsNullOrWhiteSpace(status) && status.Trim().ToLowerInvariant() is not ("new" or "open" or "pending" or "hold"))
        {
            throw new ArgumentException($"status must be one of new, open, pending, hold: the queue holds unsolved tickets only (got '{status}').");
        }

        var query = TicketSearchQuery.Build(new TicketSearchFilters
        {
            Text = text, Status = status, Priority = priority, TicketType = ticket_type, Assignee = assignee,
            Requester = requester, Organization = organization, Group = group, Brand = brand, Form = form,
            IncludeTags = include_tags, ExcludeTags = exclude_tags, CustomFieldId = custom_field_id,
            CustomFieldValue = custom_field_value, CustomStatusId = custom_status_id, HasAttachments = has_attachments,
        });
        if (string.IsNullOrWhiteSpace(status))
        {
            query += " status<solved";
        }

        var searchUrl = TicketSearchQuery.AgentSearchUrl(zendesk.BaseUrl, query);
        var cap = zendesk.Options.MaxAnalyticsTickets;
        var gathered = await AnalyticsSupport.GatherTicketIdsAsync(zendesk, query, cap, cancellationToken);
        if (gathered.OverCap)
        {
            return AnalyticsSupport.OverCapResult(query, searchUrl, cap, "Narrow the queue by group, assignee or brand.");
        }

        // Rank from the running targets Zendesk lists on each ticket (one request per 100 tickets), and
        // read the full metric events only for the tickets returned.
        // show_many with include=slas puts slas.policy_metrics on each ticket, as the single-ticket
        // endpoint does (confirmed against the live API).
        var (tickets, _) = await AnalyticsSupport.ShowManyTicketsAsync(zendesk, gathered.Ids, "slas", cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var queue = AnalyticsSla.RankQueue(tickets, now);
        var top = queue.Ranked.Take(take).ToList();

        var topIds = top.Select(e => AnalyticsStats.Long(e.Ticket["id"]) ?? 0).Where(id => id > 0).ToList();
        var slas = await TicketSla.GetManyAsync(zendesk, topIds, cancellationToken);
        var users = await AnalyticsSupport.UserNamesAsync(zendesk, top.Select(e => AnalyticsStats.Long(e.Ticket["assignee_id"]) ?? 0), cancellationToken);
        var groups = top.Any(e => AnalyticsStats.Long(e.Ticket["group_id"]) is not null)
            ? await AnalyticsSupport.GroupNamesAsync(zendesk, cancellationToken)
            : [];

        var list = new JsonArray();
        foreach (var entry in top)
        {
            var id = AnalyticsStats.Long(entry.Ticket["id"]) ?? 0;
            var assigneeId = AnalyticsStats.Long(entry.Ticket["assignee_id"]);
            var groupId = AnalyticsStats.Long(entry.Ticket["group_id"]);
            var item = new JsonObject
            {
                ["id"] = id,
                ["url"] = AnalyticsSupport.TicketUrl(zendesk, id),
                ["subject"] = ToolJson.String(entry.Ticket["subject"]),
                ["status"] = ToolJson.String(entry.Ticket["status"]),
                ["priority"] = ToolJson.String(entry.Ticket["priority"]),
                ["assignee_id"] = assigneeId,
                ["assignee"] = assigneeId is { } a ? users.GetValueOrDefault(a) : null,
                ["group_id"] = groupId,
                ["group"] = groupId is { } g ? groups.GetValueOrDefault(g) : null,
                ["urgency"] = entry.Urgency,
                ["metric"] = entry.Metric,
            };
            item[entry.Urgency == "overdue" ? "minutes_overdue" : "minutes_left"] = entry.Minutes;
            item["sla"] = slas.GetValueOrDefault(id)?.DeepClone();
            list.Add(item);
        }

        return new JsonObject
        {
            ["query"] = query,
            ["search_url"] = searchUrl,
            ["counts"] = new JsonObject
            {
                ["scanned"] = queue.Scanned,
                ["with_sla"] = queue.WithSla,
                ["without_sla"] = queue.Scanned - queue.WithSla,
                ["overdue"] = queue.Overdue,
                ["due_within_1h"] = queue.DueWithin1h,
                ["due_within_4h"] = queue.DueWithin4h,
            },
            ["tickets"] = list,
        }.ToJsonString();
    }

    private static string? ValidateGroupBy(string? groupBy, string[] allowed)
    {
        if (string.IsNullOrWhiteSpace(groupBy))
        {
            return null;
        }

        var normalised = groupBy.Trim().ToLowerInvariant();
        return allowed.Contains(normalised)
            ? normalised
            : throw new ArgumentException($"group_by must be one of {string.Join(", ", allowed)} (got '{groupBy}').");
    }

    private static DateOnly DayOf(string value, string parameter)
    {
        TicketSearchQuery.NormaliseDate(value, parameter); // validates the format
        return AnalyticsSupport.ParseDay(value.Trim()[..10], parameter);
    }

    private static JsonObject MetricsGroup(string? key, string name, List<AnalyticsMetrics.Row> rows, List<DateOnly>? trendDays, TimeZoneInfo zone, bool bySolved)
    {
        var group = new JsonObject { ["key"] = key, ["name"] = name };
        foreach (var (field, value) in AnalyticsMetrics.Figures(rows))
        {
            group[field] = value?.DeepClone();
        }

        if (trendDays is not null)
        {
            group["trend"] = AnalyticsMetrics.Trend(rows, trendDays, zone, bySolved);
        }

        return group;
    }

    // Names for Get_ticket_metrics_summary's group keys, fetching only the lookup the dimension needs.
    private static async Task<Func<string, string>> GroupNamerAsync(ZendeskService zendesk, string groupBy, long? fieldId, IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        Dictionary<long, string>? byId = groupBy switch
        {
            "group" => await AnalyticsSupport.GroupNamesAsync(zendesk, cancellationToken),
            "brand" => await AnalyticsSupport.BrandNamesAsync(zendesk, cancellationToken),
            "form" => await AnalyticsSupport.FormNamesAsync(zendesk, cancellationToken),
            "custom_status" => await AnalyticsSupport.CustomStatusNamesAsync(zendesk, cancellationToken),
            "assignee" => await AnalyticsSupport.UserNamesAsync(zendesk, keys.Select(k => long.TryParse(k, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0), cancellationToken),
            _ => null,
        };

        if (byId is not null)
        {
            return key => long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && byId.TryGetValue(id, out var name) ? name : key;
        }

        if (groupBy == "custom_field")
        {
            var (type, options) = await AnalyticsSupport.FieldOptionsAsync(zendesk, fieldId!.Value, cancellationToken);
            return key => options.TryGetValue(key, out var name)
                ? name
                : type == "checkbox" ? (key == "true" ? "Checked" : "Unchecked") : key;
        }

        return groupBy is "week" or "month" ? key => AnalyticsStats.BucketName(key, groupBy) : key => key;
    }

    // Names for Get_sla_summary: assignees and groups for the breached table, and the group_by dimension.
    private static async Task<Func<string, string, string>> SlaNamerAsync(ZendeskService zendesk, IReadOnlyList<TicketSla.SlaTicket> items, string? groupBy, CancellationToken cancellationToken)
    {
        var tickets = items.Where(i => i.Ticket is not null).Select(i => i.Ticket!).ToList();
        var anyBreach = items.Any(i => i.Sla?["breached"] is JsonValue b && b.TryGetValue<bool>(out var x) && x);

        var users = anyBreach || groupBy == "assignee"
            ? await AnalyticsSupport.UserNamesAsync(zendesk, tickets.Select(t => AnalyticsStats.Long(t["assignee_id"]) ?? 0), cancellationToken)
            : [];
        var groups = anyBreach || groupBy == "group"
            ? await AnalyticsSupport.GroupNamesAsync(zendesk, cancellationToken)
            : [];
        var brands = groupBy == "brand" ? await AnalyticsSupport.BrandNamesAsync(zendesk, cancellationToken) : [];

        var policies = new Dictionary<long, string>();
        foreach (var target in items.SelectMany(i => i.Sla?["targets"] as JsonArray ?? []))
        {
            if (AnalyticsStats.Long(target?["policy_id"]) is { } pid && ToolJson.String(target?["policy_title"]) is { } title)
            {
                policies[pid] = title;
            }
        }

        return (dimension, key) =>
        {
            var map = dimension switch
            {
                "assignee" => users,
                "group" => groups,
                "brand" => brands,
                "policy" => policies,
                _ => null,
            };

            if (map is null)
            {
                return dimension is "week" or "month" ? AnalyticsStats.BucketName(key, dimension) : key;
            }

            return long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && map.TryGetValue(id, out var name) ? name : key;
        };
    }
}
