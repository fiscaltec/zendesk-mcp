using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZendeskMcp.Tools;

/// <summary>
/// Parameter descriptions shared by the ticket tools that take the common search filters. MCP
/// parameters cannot be shared between methods, but their descriptions can, which keeps
/// Search_tickets and Count_tickets describing the same filter the same way.
/// </summary>
public static class TicketFilterDescriptions
{
    public const string Text = "Plain words to find in the ticket subject, description and comments (at most 500 characters). Not a query language: operators such as 'status:open' are searched for as words. Use the other filters instead.";
    public const string Status = "Ticket status: new, open, pending, hold, solved or closed.";
    public const string Priority = "Comma-separated priorities, any of low, normal, high, urgent.";
    public const string TicketType = "Comma-separated ticket types, any of question, incident, problem, task.";
    public const string Assignee = "Assignee: 'none' (unassigned), 'me' (the connected agent), a name, an email, a phone number or a numeric user id.";
    public const string Requester = "Requester: 'none', 'me', a name, an email, a phone number or a numeric user id.";
    public const string Organization = "Organisation name or numeric id, or 'none' for tickets with no organisation.";
    public const string Group = "Group name or numeric id.";
    public const string Brand = "Brand name or numeric id.";
    public const string Form = "Ticket form name.";
    public const string IncludeTags = "Comma-separated tags the tickets must have.";
    public const string ExcludeTags = "Comma-separated tags the tickets must not have.";
    public const string CustomFieldId = "Numeric id of a ticket field to filter on. Give custom_field_value with it. Use List_ticket_fields to find ids.";
    public const string CustomFieldValue = "Value the custom field must have (for a drop-down, the option's tag). Requires custom_field_id.";
    public const string CustomStatusId = "Numeric id of a custom ticket status.";
    public const string HasAttachments = "true for tickets with attachments, false for tickets without.";
    public const string StartDate = "Created on or after this date (YYYY-MM-DD, or an ISO date-time).";
    public const string EndDate = "Created on or before this date (YYYY-MM-DD, or an ISO date-time).";
    public const string SolvedStartDate = "Solved on or after this date (YYYY-MM-DD, or an ISO date-time).";
    public const string SolvedEndDate = "Solved on or before this date (YYYY-MM-DD, or an ISO date-time).";
}

/// <summary>Everything looked up once per call to enrich a page of search results.</summary>
public sealed class TicketEnrichment
{
    public Dictionary<long, string?> BrandNames { get; } = [];
    public Dictionary<long, JsonObject> Organizations { get; } = [];
    public Dictionary<long, JsonObject> Users { get; } = [];
    public Dictionary<long, JsonObject> Metrics { get; } = [];
    public Dictionary<long, JsonNode?> CommentCounts { get; } = [];
    public IReadOnlyDictionary<long, JsonNode?> Sla { get; set; } = new Dictionary<long, JsonNode?>();

    /// <summary>ticket id → custom object key → the record whose external_id is the ticket id.</summary>
    public Dictionary<long, Dictionary<string, JsonObject>> CustomObjects { get; } = [];
}

/// <summary>
/// The lookups, shaping and CSV writing behind Search_tickets. Kept apart from the tool so the
/// tool method reads as the request it makes.
/// </summary>
public static class TicketSearchHelpers
{
    /// <summary>Fields a caller may add to each result with include_fields.</summary>
    public static readonly string[] IncludeFieldValues =
    [
        "subject", "requester_id", "tags", "updated_at", "via", "comment_count", "url", "brand_name",
        "organization_name", "custom_fields", "description", "custom_status_id", "metrics", "type", "problem_id",
    ];

    public static readonly string[] SearchIncludeValues = ["organizations", "users", "sla"];

    /// <summary>The CSV export reads at most this many tickets, whatever the page size.</summary>
    public const int MaxCsvRows = 1000;

    /// <summary>Most custom object keys one search may join, each costing a lookup per 100 tickets.</summary>
    public const int MaxCustomObjectKeys = 5;

    private static readonly Regex CustomObjectKey = new("^[a-z0-9_]{1,64}$", RegexOptions.CultureInvariant);

    // The metric-set fields Search_tickets reports, under the names it reports them by. Zendesk calls
    // the first reply time reply_time_in_minutes.
    private static readonly (string Name, string Source)[] DurationMetrics =
    [
        ("first_reply_time", "reply_time_in_minutes"),
        ("first_resolution_time", "first_resolution_time_in_minutes"),
        ("full_resolution_time", "full_resolution_time_in_minutes"),
        ("requester_wait_time", "requester_wait_time_in_minutes"),
        ("agent_wait_time", "agent_wait_time_in_minutes"),
        ("on_hold_time", "on_hold_time_in_minutes"),
    ];

    /// <summary>
    /// Validates an array parameter against its allowed values, returning them lower-cased, in the
    /// allowed list's order and without duplicates (so the CSV column order does not depend on how
    /// the caller happened to list them).
    /// </summary>
    public static IReadOnlyList<string> ParseEnumList(string[]? values, string[] allowed, string parameter)
    {
        if (values is null || values.Length == 0)
        {
            return [];
        }

        var requested = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var normalised = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (!allowed.Contains(normalised))
            {
                throw new ArgumentException($"{parameter} values must be from: {string.Join(", ", allowed)} (got '{value}').");
            }

            requested.Add(normalised);
        }

        return allowed.Where(requested.Contains).ToList();
    }

    /// <summary>Validates custom object keys: lower-case API keys, a handful, never a ticket field name.</summary>
    public static IReadOnlyList<string> ParseCustomObjectKeys(string[]? keys)
    {
        if (keys is null || keys.Length == 0)
        {
            return [];
        }

        var distinct = keys.Select(k => (k ?? string.Empty).Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count > MaxCustomObjectKeys)
        {
            throw new ArgumentException($"include_custom_objects takes at most {MaxCustomObjectKeys} keys (got {distinct.Count}).");
        }

        foreach (var key in distinct)
        {
            if (!CustomObjectKey.IsMatch(key))
            {
                throw new ArgumentException($"include_custom_objects keys are custom object API keys: lower-case letters, digits and underscores (got '{key}').");
            }

            // The record is attached under its key, so a key named like a ticket field would overwrite it.
            if (IncludeFieldValues.Contains(key) || key is "id" or "status" or "created_at" or "sla")
            {
                throw new ArgumentException($"include_custom_objects key '{key}' clashes with a ticket field name.");
            }
        }

        return distinct;
    }

    /// <summary>Splits ids into Zendesk's 100-per-call show_many batches.</summary>
    public static IEnumerable<long[]> Batches(IEnumerable<long> ids, int size = 100) =>
        ids.Distinct().Chunk(size);

    /// <summary>Looks up everything the requested fields and includes need, a batch at a time.</summary>
    public static async Task<TicketEnrichment> EnrichAsync(
        ZendeskService zendesk,
        IReadOnlyList<JsonObject> tickets,
        IReadOnlyCollection<string> fields,
        IReadOnlyCollection<string> includes,
        IReadOnlyList<string> customObjectKeys,
        CancellationToken cancellationToken)
    {
        var enrichment = new TicketEnrichment();
        if (tickets.Count == 0)
        {
            return enrichment;
        }

        var ticketIds = tickets.Select(t => ToolJson.Long(t["id"])).OfType<long>().ToList();

        if (fields.Contains("brand_name"))
        {
            var brands = await zendesk.GetAllPagesAsync("/api/v2/brands", null, "brands", maxItems: 1000, cancellationToken: cancellationToken);
            foreach (var brand in brands.Items)
            {
                if (ToolJson.Long(brand["id"]) is { } brandId)
                {
                    enrichment.BrandNames[brandId] = ToolJson.String(brand["name"]);
                }
            }
        }

        if (fields.Contains("organization_name") || includes.Contains("organizations"))
        {
            var orgIds = tickets.Select(t => ToolJson.Long(t["organization_id"])).OfType<long>();
            foreach (var batch in Batches(orgIds))
            {
                var page = await zendesk.GetAsync("/api/v2/organizations/show_many",
                    ToolJson.Query(("ids", ToolJson.Ids(batch))), cancellationToken: cancellationToken);
                foreach (var org in page["organizations"]?.AsArray() ?? [])
                {
                    if (org is JsonObject orgObject && ToolJson.Long(orgObject["id"]) is { } orgId)
                    {
                        enrichment.Organizations[orgId] = ToolJson.Pick(orgObject,
                            "id", "name", "external_id", "domain_names", "tags", "group_id", "shared_tickets",
                            "details", "notes", "organization_fields", "created_at", "updated_at", "url");
                    }
                }
            }
        }

        if (includes.Contains("users"))
        {
            var requesterIds = tickets.Select(t => ToolJson.Long(t["requester_id"])).OfType<long>();
            foreach (var batch in Batches(requesterIds))
            {
                var page = await zendesk.GetAsync("/api/v2/users/show_many",
                    ToolJson.Query(("ids", ToolJson.Ids(batch))), cancellationToken: cancellationToken);
                foreach (var user in page["users"]?.AsArray() ?? [])
                {
                    if (user is JsonObject userObject && ToolJson.Long(userObject["id"]) is { } userId)
                    {
                        enrichment.Users[userId] = ToolJson.Pick(userObject,
                            "id", "name", "email", "external_id", "role", "organization_id", "phone", "locale",
                            "time_zone", "tags", "user_fields", "created_at", "updated_at");
                    }
                }
            }
        }

        var wantMetrics = fields.Contains("metrics");
        var wantCommentCount = fields.Contains("comment_count");
        if (wantMetrics || wantCommentCount)
        {
            // Search results carry neither, so both come from show_many with sideloads, which takes
            // up to 100 ids a call. show_many accepts comment_count alongside metric_sets (confirmed
            // against the live API): comment_count lands on each ticket, metric_sets beside them.
            var sideloads = string.Join(",", new[] { wantMetrics ? "metric_sets" : null, wantCommentCount ? "comment_count" : null }.OfType<string>());
            foreach (var batch in Batches(ticketIds))
            {
                var page = await zendesk.GetAsync("/api/v2/tickets/show_many",
                    ToolJson.Query(("ids", ToolJson.Ids(batch)), ("include", sideloads)), cancellationToken: cancellationToken);

                if (wantCommentCount)
                {
                    foreach (var ticket in page["tickets"]?.AsArray() ?? [])
                    {
                        if (ToolJson.Long(ticket?["id"]) is { } id && ticket?["comment_count"] is { } count)
                        {
                            enrichment.CommentCounts[id] = count.DeepClone();
                        }
                    }
                }

                if (wantMetrics)
                {
                    foreach (var metricSet in page["metric_sets"]?.AsArray() ?? [])
                    {
                        if (ToolJson.Long(metricSet?["ticket_id"]) is { } id)
                        {
                            enrichment.Metrics[id] = ShapeMetrics(metricSet);
                        }
                    }
                }
            }
        }

        if (includes.Contains("sla"))
        {
            enrichment.Sla = await TicketSla.GetManyAsync(zendesk, ticketIds, cancellationToken);
        }

        foreach (var key in customObjectKeys)
        {
            // Unverified against the live API: the filter[external_ids] filter on List Custom Object
            // Records, and the custom_object_records / custom_object_fields response shape.
            foreach (var batch in Batches(ticketIds))
            {
                var page = await zendesk.GetAsync($"/api/v2/custom_objects/{ZendeskService.Segment(key)}/records",
                    ToolJson.Query(("filter[external_ids]", ToolJson.Ids(batch)), ("page[size]", 100)),
                    cancellationToken: cancellationToken);
                foreach (var record in page["custom_object_records"]?.AsArray() ?? [])
                {
                    if (record is not JsonObject recordObject
                        || !long.TryParse(ToolJson.String(recordObject["external_id"]), NumberStyles.None, CultureInfo.InvariantCulture, out var ticketId))
                    {
                        continue;
                    }

                    var shaped = ToolJson.Pick(recordObject, "id", "name", "external_id");
                    if (recordObject["custom_object_fields"] is JsonObject objectFields)
                    {
                        foreach (var (name, value) in objectFields)
                        {
                            shaped[name] = value?.DeepClone();
                        }
                    }

                    if (!enrichment.CustomObjects.TryGetValue(ticketId, out var byKey))
                    {
                        byKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                        enrichment.CustomObjects[ticketId] = byKey;
                    }

                    byKey[key] = shaped;
                }
            }
        }

        return enrichment;
    }

    /// <summary>A Zendesk ticket metric set as Search_tickets reports it: minutes, calendar and business.</summary>
    public static JsonObject ShapeMetrics(JsonNode? metricSet)
    {
        var shaped = new JsonObject();
        foreach (var (name, source) in DurationMetrics)
        {
            var value = metricSet?[source];
            shaped[name] = new JsonObject
            {
                ["calendar"] = value?["calendar"]?.DeepClone(),
                ["business"] = value?["business"]?.DeepClone(),
            };
        }

        shaped["reopens"] = metricSet?["reopens"]?.DeepClone();
        shaped["replies"] = metricSet?["replies"]?.DeepClone();
        shaped["solved_at"] = metricSet?["solved_at"]?.DeepClone();
        return shaped;
    }

    /// <summary>One search result as Search_tickets returns it: id, status and created_at, plus what was asked for.</summary>
    public static JsonObject ShapeTicket(JsonObject raw, IReadOnlyCollection<string> fields, TicketEnrichment enrichment,
        IReadOnlyList<string> customObjectKeys, bool includeSla)
    {
        var id = ToolJson.Long(raw["id"]) ?? 0;
        var ticket = new JsonObject
        {
            ["id"] = raw["id"]?.DeepClone(),
            ["status"] = raw["status"]?.DeepClone(),
            ["created_at"] = raw["created_at"]?.DeepClone(),
        };

        foreach (var field in fields)
        {
            ticket[field] = field switch
            {
                "brand_name" => ToolJson.Long(raw["brand_id"]) is { } brandId && enrichment.BrandNames.TryGetValue(brandId, out var brandName)
                    ? brandName
                    : null,
                "organization_name" => ToolJson.Long(raw["organization_id"]) is { } orgId && enrichment.Organizations.TryGetValue(orgId, out var org)
                    ? org["name"]?.DeepClone()
                    : null,
                "comment_count" => enrichment.CommentCounts.TryGetValue(id, out var count) ? count?.DeepClone() : raw["comment_count"]?.DeepClone(),
                "metrics" => enrichment.Metrics.TryGetValue(id, out var metrics) ? metrics.DeepClone() : null,
                _ => raw[field]?.DeepClone(),
            };
        }

        foreach (var key in customObjectKeys)
        {
            ticket[key] = enrichment.CustomObjects.TryGetValue(id, out var byKey) && byKey.TryGetValue(key, out var record)
                ? record.DeepClone()
                : null;
        }

        if (includeSla)
        {
            ticket["sla"] = enrichment.Sla.TryGetValue(id, out var sla) ? sla?.DeepClone() : null;
        }

        return ticket;
    }

    /// <summary>
    /// Builds the CSV export: one row per ticket, custom fields one column each (titled by the field
    /// title), and the organisation, requester, SLA, metrics and custom object columns that were asked for.
    /// </summary>
    public static string BuildCsv(
        IReadOnlyList<JsonObject> rawTickets,
        IReadOnlyList<string> fields,
        IReadOnlyCollection<string> includes,
        IReadOnlyList<string> customObjectKeys,
        TicketEnrichment enrichment,
        IReadOnlyDictionary<long, string> fieldTitles)
    {
        var columns = new List<(string Header, Func<JsonObject, long, JsonNode?> Value)>
        {
            ("id", (t, _) => t["id"]),
            ("status", (t, _) => t["status"]),
            ("created_at", (t, _) => t["created_at"]),
        };

        foreach (var field in fields)
        {
            switch (field)
            {
                case "via":
                    columns.Add(("via", (t, _) => t["via"]?["channel"]));
                    break;
                case "brand_name":
                    columns.Add(("brand_name", (t, _) => ToolJson.Long(t["brand_id"]) is { } b && enrichment.BrandNames.TryGetValue(b, out var n) ? n : null));
                    break;
                case "organization_name":
                    columns.Add(("organization_name", (t, _) => ToolJson.Long(t["organization_id"]) is { } o && enrichment.Organizations.TryGetValue(o, out var org) ? org["name"] : null));
                    break;
                case "comment_count":
                    columns.Add(("comment_count", (t, id) => enrichment.CommentCounts.TryGetValue(id, out var c) ? c : t["comment_count"]));
                    break;
                case "custom_fields":
                    AddCustomFieldColumns(columns, rawTickets, fieldTitles);
                    break;
                case "metrics":
                    foreach (var (name, _) in DurationMetrics)
                    {
                        columns.Add(($"metrics.{name}.calendar", (_, id) => enrichment.Metrics.TryGetValue(id, out var m) ? m[name]?["calendar"] : null));
                        columns.Add(($"metrics.{name}.business", (_, id) => enrichment.Metrics.TryGetValue(id, out var m) ? m[name]?["business"] : null));
                    }

                    foreach (var name in new[] { "reopens", "replies", "solved_at" })
                    {
                        columns.Add(($"metrics.{name}", (_, id) => enrichment.Metrics.TryGetValue(id, out var m) ? m[name] : null));
                    }

                    break;
                default:
                    var captured = field;
                    columns.Add((field, (t, _) => t[captured]));
                    break;
            }
        }

        foreach (var key in customObjectKeys)
        {
            var recordFields = enrichment.CustomObjects.Values
                .Where(byKey => byKey.ContainsKey(key))
                .SelectMany(byKey => byKey[key].Select(p => p.Key))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name switch { "id" => 0, "name" => 1, "external_id" => 2, _ => 3 })
                .ThenBy(name => name, StringComparer.Ordinal)
                .ToList();
            foreach (var name in recordFields)
            {
                columns.Add(($"{key}.{name}", (_, id) =>
                    enrichment.CustomObjects.TryGetValue(id, out var byKey) && byKey.TryGetValue(key, out var record) ? record[name] : null));
            }
        }

        if (includes.Contains("organizations"))
        {
            JsonObject? Org(JsonObject t) =>
                ToolJson.Long(t["organization_id"]) is { } o && enrichment.Organizations.TryGetValue(o, out var org) ? org : null;
            foreach (var name in new[] { "id", "name", "external_id", "domain_names", "tags" })
            {
                columns.Add(($"organization.{name}", (t, _) => Org(t)?[name]));
            }

            var orgFieldKeys = enrichment.Organizations.Values
                .SelectMany(o => (o["organization_fields"] as JsonObject)?.Select(p => p.Key) ?? [])
                .Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal);
            foreach (var key in orgFieldKeys)
            {
                columns.Add(($"organization.{key}", (t, _) => Org(t)?["organization_fields"]?[key]));
            }
        }

        if (includes.Contains("users"))
        {
            foreach (var name in new[] { "id", "name", "email", "external_id", "role" })
            {
                columns.Add(($"requester.{name}", (t, _) =>
                    ToolJson.Long(t["requester_id"]) is { } r && enrichment.Users.TryGetValue(r, out var user) ? user[name] : null));
            }
        }

        if (includes.Contains("sla"))
        {
            JsonNode? Sla(long id) => enrichment.Sla.TryGetValue(id, out var sla) ? sla : null;
            columns.Add(("sla.breached", (_, id) => Sla(id)?["breached"]));
            columns.Add(("sla.breached_metrics", (_, id) => BreachedMetrics(Sla(id))));
            columns.Add(("sla.next_breach_at", (_, id) => Sla(id)?["next_breach_at"]));
            columns.Add(("sla.next_breach_metric", (_, id) => Sla(id)?["next_breach_metric"]));
            columns.Add(("sla.policy", (_, id) => Sla(id)?["policy"] is JsonObject policy ? policy["title"] : Sla(id)?["policy"]));
            columns.Add(("sla.events_missing", (_, id) => Sla(id)?["events_missing"]));
        }

        var builder = new StringBuilder();
        AppendRow(builder, columns.Select(c => c.Header));
        foreach (var ticket in rawTickets)
        {
            var id = ToolJson.Long(ticket["id"]) ?? 0;
            AppendRow(builder, columns.Select(c => Cell(c.Value(ticket, id))));
        }

        return builder.ToString();
    }

    // The SLA object may list its breached metrics itself or only per target; read either.
    private static JsonNode? BreachedMetrics(JsonNode? sla)
    {
        if (sla is null)
        {
            return null;
        }

        if (sla["breached_metrics"] is { } listed)
        {
            return listed;
        }

        if (sla["targets"] is JsonArray targets)
        {
            var breached = targets
                .Where(t => t?["breached"] is JsonValue v && v.TryGetValue<bool>(out var b) && b)
                .Select(t => ToolJson.String(t?["metric"]))
                .OfType<string>()
                .ToList();
            return string.Join(", ", breached);
        }

        return null;
    }

    private static void AddCustomFieldColumns(List<(string Header, Func<JsonObject, long, JsonNode?> Value)> columns,
        IReadOnlyList<JsonObject> rawTickets, IReadOnlyDictionary<long, string> fieldTitles)
    {
        var fieldIds = rawTickets
            .SelectMany(t => t["custom_fields"]?.AsArray() ?? [])
            .Select(f => ToolJson.Long(f?["id"]))
            .OfType<long>()
            .Distinct()
            .Order()
            .ToList();

        // Two fields may share a title; the id keeps their columns apart.
        var titleCounts = fieldIds
            .GroupBy(id => fieldTitles.GetValueOrDefault(id) ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        foreach (var fieldId in fieldIds)
        {
            var title = fieldTitles.GetValueOrDefault(fieldId);
            var header = string.IsNullOrWhiteSpace(title)
                ? $"custom_field_{fieldId.ToString(CultureInfo.InvariantCulture)}"
                : titleCounts[title] > 1 ? $"{title} ({fieldId.ToString(CultureInfo.InvariantCulture)})" : title;
            columns.Add((header, (t, _) => (t["custom_fields"]?.AsArray() ?? [])
                .FirstOrDefault(f => ToolJson.Long(f?["id"]) == fieldId)?["value"]));
        }
    }

    private static void AppendRow(StringBuilder builder, IEnumerable<string> cells)
    {
        builder.Append(string.Join(",", cells.Select(Escape)));
        builder.Append("\r\n");
    }

    /// <summary>
    /// One value as CSV cell text. Text that a spreadsheet would read as a formula gets a leading
    /// apostrophe: ticket fields are written by customers, and a cell beginning <c>=</c> would
    /// otherwise run when the export is opened.
    /// </summary>
    public static string Cell(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return string.Empty;
            case JsonArray array:
                return string.Join(", ", array.Select(item => item is JsonValue ? Cell(item) : item?.ToJsonString() ?? string.Empty));
            case JsonObject obj:
                return obj.ToJsonString();
            case JsonValue value when value.TryGetValue<string>(out var text):
                return NeutraliseFormula(text);
            case JsonValue value when value.TryGetValue<bool>(out var flag):
                return flag ? "true" : "false";
            default:
                return node.ToJsonString();
        }
    }

    private static string NeutraliseFormula(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var first = text[0];
        var risky = first is '=' or '+' or '@' or '\t' or '\r'
            || (first == '-' && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        return risky ? "'" + text : text;
    }

    /// <summary>RFC 4180 quoting: quote a cell holding a comma, quote or line break, doubling inner quotes.</summary>
    public static string Escape(string cell) =>
        cell.IndexOfAny([',', '"', '\r', '\n']) >= 0 || (cell.Length > 0 && (char.IsWhiteSpace(cell[0]) || char.IsWhiteSpace(cell[^1])))
            ? $"\"{cell.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : cell;

    /// <summary>The account's ticket field titles by id, for the CSV custom field columns.</summary>
    public static async Task<Dictionary<long, string>> GetFieldTitlesAsync(ZendeskService zendesk, CancellationToken cancellationToken)
    {
        var fields = await zendesk.GetAllPagesAsync("/api/v2/ticket_fields", null, "ticket_fields", maxItems: 2000, cancellationToken: cancellationToken);
        var titles = new Dictionary<long, string>();
        foreach (var field in fields.Items)
        {
            if (ToolJson.Long(field["id"]) is { } id && ToolJson.String(field["title"]) is { } title)
            {
                titles[id] = title;
            }
        }

        return titles;
    }

    /// <summary>The number of tickets a search query matches.</summary>
    public static async Task<long> CountAsync(ZendeskService zendesk, string query, CancellationToken cancellationToken)
    {
        var result = await zendesk.GetAsync("/api/v2/search/count", ToolJson.Query(("query", query)), cancellationToken: cancellationToken);
        return ToolJson.Long(result["count"]) ?? 0;
    }

    /// <summary>
    /// Counts several queries, four at a time: enough to keep a group-by quick without tripping
    /// Zendesk's search rate limit for the person whose token is used.
    /// </summary>
    public static async Task<long[]> CountManyAsync(ZendeskService zendesk, IReadOnlyList<string> queries, CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(4);
        var tasks = queries.Select(async query =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await CountAsync(zendesk, query, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        return await Task.WhenAll(tasks);
    }
}
