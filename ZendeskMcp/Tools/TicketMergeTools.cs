using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Tools;

/// <summary>
/// Merging duplicate tickets into one. Kept apart from <see cref="TicketsTools"/> because a merge is
/// a different shape of write: it closes the source tickets for good, it runs as a Zendesk background
/// job rather than answering with the ticket, and it can email two sets of requesters at once.
/// </summary>
[McpServerToolType]
public static class TicketMergeTools
{
    [Authorize(Policy = "zendesk:write")]
    [McpServerTool(Name = "Merge_tickets", Title = "Merge tickets", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Merge one to five duplicate tickets (source_ticket_ids) into a target ticket. Zendesk closes each source ticket for good, tags it closed_by_merge and adds a comment to it and to the target; a merge cannot be undone. Before calling, show the person the tickets (subject, requester, status) and get their agreement: to find duplicates, use Search_tickets with exclude_tags ['closed_by_merge'] and read candidates with Get_ticket. Closed tickets, tickets already merged and tickets the agent cannot see are refused before anything changes. Comments are private notes unless target_comment_is_public or source_comment_is_public is true; a public merge comment emails that ticket's requester and CCs. Returns the tickets as read before the merge, the job's final status, and each source ticket's state afterwards; a job still running when the tool stops waiting is reported as still running, not as merged. " + TicketWriteHelpers.UntrustedContentWarning)]
    public static Task<string> MergeTickets(
        ZendeskService zendesk,
        [Description("Id of the ticket to keep; the sources are merged into it.")] long target_ticket_id,
        [Description("Ids of 1 to 5 tickets to merge into the target and close. Distinct, and not the target.")] long[] source_ticket_ids,
        [Description("Comment added to the target ticket, at most 10,000 characters. Default: a note naming the merged tickets.")] string? target_comment = null,
        [Description("Comment added to each source ticket, at most 10,000 characters. Default: a note naming the target ticket.")] string? source_comment = null,
        [Description("true to make the target comment public, emailing the target's requester and CCs. Default false (private note).")] bool? target_comment_is_public = null,
        [Description("true to make the source comment public, emailing each source ticket's requester and CCs. Default false (private note).")] bool? source_comment_is_public = null,
        CancellationToken cancellationToken = default) =>
        TicketMerge.MergeAsync(zendesk, target_ticket_id, source_ticket_ids, target_comment, source_comment,
            target_comment_is_public, source_comment_is_public, TicketMerge.DefaultPollDelays, Task.Delay, cancellationToken);
}

/// <summary>
/// The work behind <c>Merge_tickets</c>, with the job-polling wait passed in so tests can run the
/// "still running" path without sleeping through the real schedule.
/// </summary>
public static class TicketMerge
{
    public const int MaxSources = 5;

    /// <summary>
    /// The wait before each poll of the merge job: ten polls over roughly twenty-five seconds. A merge
    /// of a handful of tickets normally completes within the first few seconds; the bound keeps a
    /// stuck job from holding the tool call (and the single replica's request) open indefinitely.
    /// </summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultPollDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4),
    ];

    private static readonly string[] TerminalJobStatuses = ["completed", "failed", "killed"];

    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);

    public static async Task<string> MergeAsync(
        ZendeskService zendesk,
        long targetTicketId,
        long[]? sourceTicketIds,
        string? targetComment,
        string? sourceComment,
        bool? targetCommentIsPublic,
        bool? sourceCommentIsPublic,
        IReadOnlyList<TimeSpan> pollDelays,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        ToolJson.PositiveId(targetTicketId, "target_ticket_id");
        var sources = sourceTicketIds ?? [];
        if (sources.Length is 0 or > MaxSources)
        {
            throw new ArgumentException($"source_ticket_ids takes 1 to {MaxSources} ticket ids (got {sources.Length}).");
        }

        foreach (var id in sources)
        {
            ToolJson.PositiveId(id, "source_ticket_ids");
        }

        if (sources.Distinct().Count() != sources.Length)
        {
            throw new ArgumentException("source_ticket_ids contains the same ticket more than once.");
        }

        if (sources.Contains(targetTicketId))
        {
            throw new ArgumentException($"Ticket {targetTicketId} is the target; a ticket cannot be merged into itself, so leave it out of source_ticket_ids.");
        }

        TicketWriteHelpers.MaxLength(targetComment, TicketWriteHelpers.MaxCommentLength, "target_comment");
        TicketWriteHelpers.MaxLength(sourceComment, TicketWriteHelpers.MaxCommentLength, "source_comment");
        if (targetComment is not null && string.IsNullOrWhiteSpace(targetComment))
        {
            throw new ArgumentException("target_comment cannot be empty; leave it out for the default note.");
        }

        if (sourceComment is not null && string.IsNullOrWhiteSpace(sourceComment))
        {
            throw new ArgumentException("source_comment cannot be empty; leave it out for the default note.");
        }

        // A public merge comment is a reply: it emails the requester and CCs of that ticket.
        var targetPublic = targetCommentIsPublic ?? false;
        var sourcePublic = sourceCommentIsPublic ?? false;
        if (targetPublic)
        {
            ToolJson.RequireFeature(zendesk.Options.Features.AllowPublicReplies, "AllowPublicReplies", "A public merge comment on the target ticket");
        }

        if (sourcePublic)
        {
            ToolJson.RequireFeature(zendesk.Options.Features.AllowPublicReplies, "AllowPublicReplies", "A public merge comment on the source tickets");
        }

        // Read every ticket first: Zendesk's own refusals arrive only as a failed background job, after
        // the call has returned, so the checks it would make are made here where they can name the
        // ticket. The read also puts every ticket id into this call's audit record — the merge's own
        // response is a job status, which names no ticket.
        var allIds = new[] { targetTicketId }.Concat(sources).ToArray();
        var before = await ReadTicketsAsync(zendesk, allIds, cancellationToken);

        var problems = new List<string>();
        foreach (var id in allIds)
        {
            var role = id == targetTicketId ? "target" : "source";
            if (!before.Tickets.TryGetValue(id, out var ticket))
            {
                problems.Add($"Ticket {id} ({role}) was not found, or this agent cannot see it.");
                continue;
            }

            if (ToolJson.String(ticket["status"]) == "closed")
            {
                problems.Add($"Ticket {id} ({role}) is closed, and closed tickets cannot be merged.");
            }

            if (HasTag(ticket, "closed_by_merge"))
            {
                problems.Add($"Ticket {id} ({role}) has already been merged into another ticket (tagged closed_by_merge).");
            }
        }

        if (problems.Count > 0)
        {
            throw new ArgumentException("Nothing was merged. " + string.Join(" ", problems));
        }

        // Zendesk's own merge dialogue adds a note to each side; without one the merge leaves no trace
        // in either conversation. These follow its wording in substance, naming tickets by id only.
        // Unverified against the live API: whether Zendesk adds its own note when these are omitted.
        var body = new JsonObject
        {
            ["ids"] = new JsonArray(sources.Select(s => (JsonNode)s).ToArray()),
            ["target_comment"] = targetComment ?? DefaultTargetComment(sources),
            ["source_comment"] = sourceComment ?? $"This request was closed and merged into request #{Id(targetTicketId)}.",
            ["target_comment_is_public"] = targetPublic,
            ["source_comment_is_public"] = sourcePublic,
        };

        // docs-checked, not live-tested: POST /api/v2/tickets/{id}/merge answers {"job_status": {...}}.
        var accepted = await zendesk.PostAsync($"/api/v2/tickets/{Id(targetTicketId)}/merge", body, cancellationToken: cancellationToken);
        var job = accepted["job_status"] as JsonObject
            ?? throw new HttpRequestException($"Zendesk accepted the merge into ticket {targetTicketId} but returned no job status, so its outcome is unknown. Check the tickets with Get_ticket before retrying.");
        var jobId = ToolJson.String(job["id"]);

        var polls = 0;
        while (!IsTerminal(job) && polls < pollDelays.Count)
        {
            if (string.IsNullOrEmpty(jobId))
            {
                break;
            }

            await delay(pollDelays[polls], cancellationToken);
            polls++;
            var polled = await zendesk.GetAsync($"/api/v2/job_statuses/{ZendeskService.Segment(jobId)}", cancellationToken: cancellationToken);
            job = polled["job_status"] as JsonObject ?? job;
        }

        var jobStatus = ToolJson.String(job["status"]) ?? "unknown";
        var jobSummary = ToolJson.Pick(job, "id", "status", "total", "progress", "message", "results");

        if (jobStatus is "failed" or "killed")
        {
            throw new HttpRequestException(
                $"Zendesk's merge job {jobId} {jobStatus}; the tickets may be unchanged. Job: {Truncate(jobSummary.ToJsonString(), 1024)}",
                inner: null, HttpStatusCode.UnprocessableEntity);
        }

        var result = new JsonObject
        {
            ["target_ticket_id"] = targetTicketId,
            ["source_ticket_ids"] = new JsonArray(sources.Select(s => (JsonNode)s).ToArray()),
            ["job"] = jobSummary,
            ["polls"] = polls,
            ["tickets_before"] = new JsonArray(allIds.Select(id => (JsonNode)Describe(before, id, id == targetTicketId ? "target" : "source")).ToArray()),
        };

        if (jobStatus != "completed")
        {
            // Never reported as merged: the job may still finish, or fail, after this call returns.
            result["merged"] = false;
            result["outcome"] = "still_running";
            result["next_step"] = $"Zendesk had not finished the merge when this tool stopped waiting (job status '{jobStatus}'). Check the source tickets with Get_ticket in a minute: a merged source is closed and tagged closed_by_merge. Do not call Merge_tickets again until that shows the merge did not happen.";
            return result.ToJsonString();
        }

        // The job's results shape is not documented for merges, so the outcome is read back from the
        // tickets themselves: a merged source is closed and tagged closed_by_merge.
        var after = await ReadTicketsAsync(zendesk, sources, cancellationToken);
        var sourcesAfter = new JsonArray();
        var unconfirmed = new List<long>();
        foreach (var id in sources)
        {
            after.Tickets.TryGetValue(id, out var ticket);
            var closedByMerge = ticket is not null && ToolJson.String(ticket["status"]) == "closed" && HasTag(ticket, "closed_by_merge");
            if (!closedByMerge)
            {
                unconfirmed.Add(id);
            }

            sourcesAfter.Add(new JsonObject
            {
                ["id"] = id,
                ["status"] = ticket is null ? null : ToolJson.String(ticket["status"]),
                ["closed_by_merge"] = closedByMerge,
            });
        }

        result["merged"] = unconfirmed.Count == 0;
        result["outcome"] = unconfirmed.Count == 0 ? "completed" : "completed_unconfirmed";
        result["sources_after"] = sourcesAfter;
        result["target_agent_url"] = $"{zendesk.BaseUrl}/agent/tickets/{Id(targetTicketId)}";
        if (unconfirmed.Count > 0)
        {
            result["warning"] = $"Zendesk reported the job completed, but ticket(s) {string.Join(", ", unconfirmed.Select(Id))} do not yet read as closed and tagged closed_by_merge. Check them with Get_ticket before doing anything else with them.";
        }

        return result.ToJsonString();
    }

    private static string DefaultTargetComment(long[] sources) =>
        sources.Length == 1
            ? $"Request #{Id(sources[0])} was closed and merged into this request."
            : $"Requests {string.Join(", ", sources.Select(s => "#" + Id(s)))} were closed and merged into this request.";

    private static bool IsTerminal(JsonNode job) => TerminalJobStatuses.Contains(ToolJson.String(job["status"]));

    private static bool HasTag(JsonNode ticket, string tag) =>
        (ticket["tags"] as JsonArray ?? []).Any(t => string.Equals(ToolJson.String(t), tag, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    private sealed record TicketRead(Dictionary<long, JsonNode> Tickets, Dictionary<long, JsonNode> Users);

    // show_many leaves out ids it cannot find or the agent cannot see, rather than failing, so a
    // missing entry is how a missing or hidden ticket shows.
    private static async Task<TicketRead> ReadTicketsAsync(ZendeskService zendesk, IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        var page = await zendesk.GetAsync("/api/v2/tickets/show_many",
            ToolJson.Query(("ids", ToolJson.Ids(ids)), ("include", "users")), cancellationToken: cancellationToken);
        var tickets = new Dictionary<long, JsonNode>();
        foreach (var ticket in page["tickets"] as JsonArray ?? [])
        {
            if (ticket is not null && ToolJson.Long(ticket["id"]) is { } id)
            {
                tickets[id] = ticket;
            }
        }

        var users = new Dictionary<long, JsonNode>();
        foreach (var user in page["users"] as JsonArray ?? [])
        {
            if (user is not null && ToolJson.Long(user["id"]) is { } id)
            {
                users[id] = user;
            }
        }

        return new TicketRead(tickets, users);
    }

    private static JsonObject Describe(TicketRead read, long id, string role)
    {
        var ticket = read.Tickets[id];
        var described = ToolJson.Pick(ticket, "id", "subject", "status", "requester_id", "organization_id", "created_at", "updated_at");
        described["role"] = role;
        if (ToolJson.Long(ticket["requester_id"]) is { } requesterId && read.Users.TryGetValue(requesterId, out var requester))
        {
            described["requester"] = ToolJson.Pick(requester, "id", "name", "email");
        }

        return described;
    }
}
