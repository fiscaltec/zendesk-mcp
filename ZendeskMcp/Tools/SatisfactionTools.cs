using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Tools;

/// <summary>
/// CSAT rating records. This version reads Zendesk's legacy satisfaction ratings
/// (<c>GET /api/v2/satisfaction_ratings</c>, good/bad with an optional comment and reason).
/// </summary>
/// <remarks>
/// Zendesk's newer CSAT surveys (1–5 scales, closed- and open-ended questions) keep their responses in
/// a separate survey-response store. There is no endpoint for it this server can rely on without the
/// live API to check against, so rather than guess, the tool says plainly that survey responses are
/// not read here, and points to Count_tickets group_by satisfaction, which reads the good/bad rating
/// Zendesk records on each ticket under either system.
/// </remarks>
[McpServerToolType]
public static class SatisfactionTools
{
    /// <summary>The legacy <c>score</c> filter values Zendesk accepts.</summary>
    public static readonly string[] Scores =
    [
        "good", "bad", "offered", "unoffered", "received", "received_with_comment", "received_without_comment",
        "good_with_comment", "good_without_comment", "bad_with_comment", "bad_without_comment",
    ];

    private const int MaxCsvRows = 1000;

    private static readonly string[] Fields =
    [
        "id", "url", "score", "comment", "reason", "reason_id", "reason_code", "ticket_id", "requester_id",
        "assignee_id", "group_id", "created_at", "updated_at",
    ];

    private const string SurveyNote =
        "These are legacy CSAT ratings. If the account uses Zendesk's newer CSAT surveys, their responses (1-5 ratings and survey answers) are not read by this version of the server and the list may be empty. For CSAT scores and counts under either system, use Count_tickets with group_by satisfaction.";

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "Get_satisfaction_ratings", Title = "Get satisfaction ratings", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Retrieve CSAT / satisfaction ratings as raw rating records: Zendesk's legacy good/bad ratings, each with its score, optional comment and reason, ticket, requester, assignee and group. The response's csat_system is 'legacy'. Responses to Zendesk's newer customisable CSAT surveys are not read by this version; the notes say so. " +
        "For CSAT scores and counts (how many good and bad ratings, the CSAT %), use Count_tickets with group_by satisfaction instead of downloading ratings: one call, any number of tickets, with the ticket filters.\n\n" +
        "Paging: output_format 'json' (default) returns one page with pagination_info {has_more, next_cursor, page_size}; call again with after_cursor = next_cursor while has_more is true. output_format 'csv' returns all matching ratings (up to 1000 rows) as one CSV string, without paging.")]
    public static async Task<string> GetSatisfactionRatings(
        ZendeskService zendesk,
        [Description("Filter by score: 'good' or 'bad'. Zendesk's other legacy filters are accepted too: offered, unoffered, received, received_with_comment, received_without_comment, good_with_comment, good_without_comment, bad_with_comment, bad_without_comment.")] string? score = null,
        [Description("Start date filter (inclusive), in UTC. ISO format: YYYY-MM-DD or YYYY-MM-DDThh:mm:ssZ. A date alone starts at 00:00:00 UTC that day.")] string? start_date = null,
        [Description("End date filter (inclusive), in UTC. ISO format: YYYY-MM-DD or YYYY-MM-DDThh:mm:ssZ. A date alone covers that whole day, up to 23:59:59 UTC.")] string? end_date = null,
        [Description("Only the rating on this ticket. Looks the rating up from the ticket, so score and date filters still apply and there is no paging.")] long? ticket_id = null,
        [Description("Number of results per page (1-100, default: 100). Only used when output_format is NOT 'csv'. When has_more is true in the response, call again with the returned after_cursor to get the next page.")] int? page_size = null,
        [Description("Cursor for pagination. Pass the next_cursor value from a previous response to fetch the next page.")] string? after_cursor = null,
        [Description("Output format. 'json' (default) returns paginated JSON. 'csv' returns all results as a single CSV string (up to 1000 rows, no pagination).")] string? output_format = null,
        CancellationToken cancellationToken = default)
    {
        var format = (output_format ?? "json").Trim().ToLowerInvariant();
        if (format is not ("json" or "csv"))
        {
            throw new ArgumentException($"output_format must be json or csv (got '{output_format}').");
        }

        var normalisedScore = string.IsNullOrWhiteSpace(score) ? null : score.Trim().ToLowerInvariant();
        if (normalisedScore is not null && !Scores.Contains(normalisedScore))
        {
            throw new ArgumentException($"score must be one of {string.Join(", ", Scores)} (got '{score}').");
        }

        var startTime = start_date is null ? (long?)null : UnixSeconds(start_date, "start_date", endOfDay: false);
        var endTime = end_date is null ? (long?)null : UnixSeconds(end_date, "end_date", endOfDay: true);
        if (startTime > endTime)
        {
            throw new ArgumentException("end_date is before start_date.");
        }

        var size = ToolJson.PageSize(page_size);
        if (format == "csv" && (page_size is not null || after_cursor is not null))
        {
            throw new ArgumentException("page_size and after_cursor apply to output_format json only; csv returns every row up to 1000.");
        }

        if (ticket_id is { } tid)
        {
            var one = await RatingForTicketAsync(zendesk, ToolJson.PositiveId(tid, "ticket_id"), cancellationToken);
            var matches = one is not null
                && MatchesScore(one, normalisedScore)
                && InRange(one, startTime, endTime)
                ? new List<JsonNode> { one }
                : [];
            return Shape(matches, format, pagination: null, truncated: false);
        }

        var query = ToolJson.Query(("score", normalisedScore), ("start_time", startTime), ("end_time", endTime));
        if (format == "csv")
        {
            var all = await zendesk.GetAllPagesAsync("/api/v2/satisfaction_ratings", query, "satisfaction_ratings", MaxCsvRows, cancellationToken: cancellationToken);
            return Shape(all.Items, format, pagination: null, truncated: all.HasMore);
        }

        query.Add(new("page[size]", size.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(after_cursor))
        {
            query.Add(new("page[after]", after_cursor));
        }

        var page = await zendesk.GetAsync("/api/v2/satisfaction_ratings", query, cancellationToken: cancellationToken);
        var items = (page["satisfaction_ratings"] as JsonArray ?? []).Where(r => r is not null).Select(r => r!).ToList();
        return Shape(items, format, ToolJson.PaginationInfo(page, size), truncated: false);
    }

    /// <summary>
    /// Converts a date filter to Unix seconds in UTC: a date alone is the start of that day, or its last
    /// second for an end date; a date and time is taken as given (UTC unless it carries an offset).
    /// </summary>
    public static long UnixSeconds(string value, string parameter, bool endOfDay)
    {
        var trimmed = value.Trim();
        if (DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            var moment = new DateTimeOffset(day.ToDateTime(endOfDay ? new TimeOnly(23, 59, 59) : TimeOnly.MinValue), TimeSpan.Zero);
            return moment.ToUnixTimeSeconds();
        }

        if (trimmed.Contains('T')
            && DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
        {
            return at.ToUnixTimeSeconds();
        }

        throw new ArgumentException($"{parameter} must be YYYY-MM-DD or YYYY-MM-DDThh:mm:ssZ (got '{value}').");
    }

    // A legacy rating hangs off its ticket as satisfaction_rating {id, score, comment}; the rating
    // record itself carries the reason, assignee and dates.
    private static async Task<JsonNode?> RatingForTicketAsync(ZendeskService zendesk, long ticketId, CancellationToken cancellationToken)
    {
        var ticket = await zendesk.GetAsync($"/api/v2/tickets/{ticketId.ToString(CultureInfo.InvariantCulture)}", cancellationToken: cancellationToken);
        if (AnalyticsStats.Long(ticket["ticket"]?["satisfaction_rating"]?["id"]) is not { } ratingId)
        {
            return null;
        }

        try
        {
            var rating = await zendesk.GetAsync($"/api/v2/satisfaction_ratings/{ratingId.ToString(CultureInfo.InvariantCulture)}", cancellationToken: cancellationToken);
            return rating["satisfaction_rating"];
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static bool MatchesScore(JsonNode rating, string? filter)
    {
        if (filter is null)
        {
            return true;
        }

        var score = ToolJson.String(rating["score"])?.ToLowerInvariant();
        var hasComment = !string.IsNullOrWhiteSpace(ToolJson.String(rating["comment"]));
        var received = score is "good" or "bad";
        return filter switch
        {
            "received" => received,
            "received_with_comment" => received && hasComment,
            "received_without_comment" => received && !hasComment,
            "good_with_comment" => score == "good" && hasComment,
            "good_without_comment" => score == "good" && !hasComment,
            "bad_with_comment" => score == "bad" && hasComment,
            "bad_without_comment" => score == "bad" && !hasComment,
            _ => score == filter,
        };
    }

    private static bool InRange(JsonNode rating, long? start, long? end)
    {
        if (start is null && end is null)
        {
            return true;
        }

        if (AnalyticsStats.Moment(rating["created_at"]) is not { } created)
        {
            return false;
        }

        var seconds = created.ToUnixTimeSeconds();
        return (start is null || seconds >= start) && (end is null || seconds <= end);
    }

    private static string Shape(IReadOnlyList<JsonNode> ratings, string format, JsonObject? pagination, bool truncated)
    {
        var result = new JsonObject { ["csat_system"] = "legacy" };
        if (format == "csv")
        {
            var rows = ratings.Select(r => Fields.Select(f => (object?)r[f]));
            result["output_format"] = "csv";
            result["row_count"] = ratings.Count;
            result["truncated"] = truncated;
            result["csv"] = AnalyticsStats.Csv(Fields, rows);
        }
        else
        {
            result["satisfaction_ratings"] = new JsonArray(ratings.Select(r => (JsonNode?)ToolJson.Pick(r, Fields)).ToArray());
            if (pagination is not null)
            {
                result["pagination_info"] = pagination;
            }
        }

        var notes = new JsonArray { SurveyNote };
        if (truncated)
        {
            notes.Add($"The CSV holds the first {ratings.Count} ratings; narrow the dates for the rest.");
        }

        result["notes"] = notes;
        return result.ToJsonString();
    }
}
