using System.Globalization;
using System.Text.Json.Nodes;

namespace ZendeskMcp.Tools;

/// <summary>
/// The Zendesk plumbing the analytics tools share: gathering the ticket ids a search matches, fetching
/// tickets in batches a few requests at a time, the connected user's timezone, and turning ids into
/// names. Every call here is a GET, so the analytics tools stay on the read tier.
/// </summary>
public static class AnalyticsSupport
{
    /// <summary>
    /// Requests in flight at once for one tool call. Zendesk limits concurrent requests per account as
    /// well as per minute, and the analytics tools share that budget with everything else the agent's
    /// colleagues are doing, so this stays small.
    /// </summary>
    public const int MaxConcurrency = 4;

    /// <summary>Zendesk's limit for <c>show_many</c> ids per request.</summary>
    public const int ShowManyBatch = 100;

    /// <summary>The ids a search matched, and whether there were more than the cap.</summary>
    public sealed record GatheredIds(IReadOnlyList<long> Ids, bool OverCap);

    /// <summary>
    /// Runs <paramref name="work"/> over <paramref name="items"/> with at most
    /// <see cref="MaxConcurrency"/> in flight, returning results in input order.
    /// </summary>
    public static async Task<IReadOnlyList<TResult>> ForEachBoundedAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        Func<TItem, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken,
        int maxConcurrency = MaxConcurrency)
    {
        var results = new TResult[items.Count];
        using var gate = new SemaphoreSlim(maxConcurrency);
        var tasks = items.Select(async (item, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                results[index] = await work(item, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        return results;
    }

    /// <summary>
    /// Collects the ids of the tickets a search matches with Export Search (cursor-paged, no 1000-result
    /// ceiling), stopping as soon as there are more than <paramref name="cap"/>.
    /// </summary>
    /// <remarks>
    /// Paged here rather than through <see cref="ZendeskService.GetAllPagesAsync"/> because that pager
    /// stops at <c>MaxAutoPageFetches</c> pages, below the analytics cap; the cap is the bound here.
    /// </remarks>
    public static async Task<GatheredIds> GatherTicketIdsAsync(ZendeskService zendesk, string query, int cap, CancellationToken cancellationToken)
    {
        var ids = new List<long>();
        var seen = new HashSet<long>();
        string? after = null;
        while (true)
        {
            var page = await zendesk.GetAsync(
                "/api/v2/search/export",
                ToolJson.Query(("query", query), ("filter[type]", "ticket"), ("page[size]", 100), ("page[after]", after)),
                cancellationToken: cancellationToken);

            foreach (var result in page["results"] as JsonArray ?? [])
            {
                if (AnalyticsStats.Long(result?["id"]) is { } id && seen.Add(id))
                {
                    ids.Add(id);
                }
            }

            if (ids.Count > cap)
            {
                return new GatheredIds(ids.Take(cap).ToList(), OverCap: true);
            }

            var hasMore = page["meta"]?["has_more"] is JsonValue more && more.TryGetValue<bool>(out var m) && m;
            after = ToolJson.String(page["meta"]?["after_cursor"]);
            if (!hasMore || string.IsNullOrEmpty(after))
            {
                return new GatheredIds(ids, OverCap: false);
            }
        }
    }

    /// <summary>
    /// Fetches tickets by id with <c>GET /api/v2/tickets/show_many</c>, 100 per request, a few requests
    /// at a time, merging the sideloads (<c>metric_sets</c>, …) of every batch.
    /// </summary>
    public static async Task<(List<JsonObject> Tickets, Dictionary<string, List<JsonObject>> Sideloads)> ShowManyTicketsAsync(
        ZendeskService zendesk, IReadOnlyList<long> ids, string? include, CancellationToken cancellationToken)
    {
        var batches = ids.Distinct().Chunk(ShowManyBatch).ToList();
        var pages = await ForEachBoundedAsync(
            batches,
            (batch, ct) => zendesk.GetAsync("/api/v2/tickets/show_many", ToolJson.Query(("ids", ToolJson.Ids(batch)), ("include", include)), cancellationToken: ct),
            cancellationToken);

        var tickets = new List<JsonObject>();
        var sideloads = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            if (page is not JsonObject pageObject)
            {
                continue;
            }

            foreach (var (name, value) in pageObject)
            {
                if (value is not JsonArray array)
                {
                    continue;
                }

                var target = name == "tickets" ? tickets : sideloads.TryGetValue(name, out var list) ? list : sideloads[name] = [];
                target.AddRange(array.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()));
            }
        }

        return (tickets, sideloads);
    }

    /// <summary>
    /// The connected Zendesk user's timezone (<c>GET /api/v2/users/me</c> → <c>iana_time_zone</c>), the
    /// zone Zendesk reads date filters in. Falls back to UTC, and says so in the returned name.
    /// </summary>
    public static async Task<(TimeZoneInfo Zone, string Name)> GetTimeZoneAsync(ZendeskService zendesk, CancellationToken cancellationToken)
    {
        var me = await zendesk.GetAsync("/api/v2/users/me", cancellationToken: cancellationToken);
        var iana = ToolJson.String(me["user"]?["iana_time_zone"]);
        var zone = AnalyticsStats.ResolveTimeZone(iana);
        return zone == TimeZoneInfo.Utc && !string.Equals(iana, "UTC", StringComparison.OrdinalIgnoreCase) && !string.Equals(iana, "Etc/UTC", StringComparison.OrdinalIgnoreCase)
            ? (zone, "UTC")
            : (zone, iana ?? "UTC");
    }

    /// <summary>Names of the account's groups, by id.</summary>
    public static Task<Dictionary<long, string>> GroupNamesAsync(ZendeskService zendesk, CancellationToken cancellationToken) =>
        NamesFromListAsync(zendesk, "/api/v2/groups", "groups", "name", cancellationToken);

    /// <summary>Names of the account's brands, by id.</summary>
    public static Task<Dictionary<long, string>> BrandNamesAsync(ZendeskService zendesk, CancellationToken cancellationToken) =>
        NamesFromListAsync(zendesk, "/api/v2/brands", "brands", "name", cancellationToken);

    /// <summary>Names of the account's ticket forms, by id.</summary>
    public static Task<Dictionary<long, string>> FormNamesAsync(ZendeskService zendesk, CancellationToken cancellationToken) =>
        NamesFromListAsync(zendesk, "/api/v2/ticket_forms", "ticket_forms", "name", cancellationToken);

    /// <summary>Agent labels of the account's custom ticket statuses, by id.</summary>
    public static Task<Dictionary<long, string>> CustomStatusNamesAsync(ZendeskService zendesk, CancellationToken cancellationToken) =>
        NamesFromListAsync(zendesk, "/api/v2/custom_statuses", "custom_statuses", "agent_label", cancellationToken);

    /// <summary>Names of the given users (<c>/api/v2/users/show_many</c>, 100 per request).</summary>
    public static async Task<Dictionary<long, string>> UserNamesAsync(ZendeskService zendesk, IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var batches = ids.Where(id => id > 0).Distinct().Chunk(ShowManyBatch).ToList();
        var pages = await ForEachBoundedAsync(
            batches,
            (batch, ct) => zendesk.GetAsync("/api/v2/users/show_many", ToolJson.Query(("ids", ToolJson.Ids(batch))), cancellationToken: ct),
            cancellationToken);

        var names = new Dictionary<long, string>();
        foreach (var user in pages.SelectMany(p => p["users"] as JsonArray ?? []))
        {
            if (AnalyticsStats.Long(user?["id"]) is { } id && ToolJson.String(user?["name"]) is { } name)
            {
                names[id] = name;
            }
        }

        return names;
    }

    /// <summary>
    /// A ticket field's option names by option value (tag), and the field's type
    /// (<c>GET /api/v2/ticket_fields/{id}</c>).
    /// </summary>
    public static async Task<(string? Type, Dictionary<string, string> Options)> FieldOptionsAsync(ZendeskService zendesk, long fieldId, CancellationToken cancellationToken)
    {
        var response = await zendesk.GetAsync($"/api/v2/ticket_fields/{fieldId.ToString(CultureInfo.InvariantCulture)}", cancellationToken: cancellationToken);
        var field = response["ticket_field"];
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var option in field?["custom_field_options"] as JsonArray ?? [])
        {
            if (ToolJson.String(option?["value"]) is { } value)
            {
                options[value] = ToolJson.String(option?["name"]) ?? value;
            }
        }

        return (ToolJson.String(field?["type"]), options);
    }

    /// <summary>The result when a search matches more tickets than one call may read: nothing computed, and how to narrow it.</summary>
    public static string OverCapResult(string query, string searchUrl, int cap, string advice) => new JsonObject
    {
        ["query"] = query,
        ["search_url"] = searchUrl,
        ["too_many_tickets"] = true,
        ["limit"] = cap,
        ["note"] = $"More than {cap} tickets match, so nothing was computed. {advice}",
    }.ToJsonString();

    /// <summary>Validates a YYYY-MM-DD day parameter.</summary>
    public static DateOnly ParseDay(string value, string parameter) =>
        DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
            ? day
            : throw new ArgumentException($"{parameter} must be a date, YYYY-MM-DD (got '{value}').");

    /// <summary>The ticket's link in the agent interface.</summary>
    public static string TicketUrl(ZendeskService zendesk, long id) =>
        $"{zendesk.BaseUrl}/agent/tickets/{id.ToString(CultureInfo.InvariantCulture)}";

    private static async Task<Dictionary<long, string>> NamesFromListAsync(ZendeskService zendesk, string path, string property, string nameField, CancellationToken cancellationToken)
    {
        var items = await zendesk.GetAllPagesAsync(path, null, property, maxItems: 5000, cancellationToken: cancellationToken);
        var names = new Dictionary<long, string>();
        foreach (var item in items.Items)
        {
            if (AnalyticsStats.Long(item["id"]) is { } id && ToolJson.String(item[nameField]) is { } name)
            {
                names[id] = name;
            }
        }

        return names;
    }
}
