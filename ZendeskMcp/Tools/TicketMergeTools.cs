using System.ComponentModel;
using System.Globalization;
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
     Description("Merge one to five duplicate tickets (source_ticket_ids) into a target ticket. Zendesk closes each source ticket for good, tags it closed_by_merge and adds a comment to it and to the target; a merge cannot be undone. Before calling, show the person the tickets (subject, requester, status) and get their agreement: to find duplicates, use Search_tickets with exclude_tags ['closed_by_merge'] and read candidates with Get_ticket. Every source must have the same requester as the target. Closed tickets, solved sources, tickets merged into a different ticket, tickets the agent cannot see and sources with a different requester are refused, naming each, before anything changes. Safe to repeat: a source already merged into this target is reported as already_merged and not merged again. Comments are private notes unless target_comment_public or source_comment_public is true; a public merge comment emails that ticket's requester and CCs. Returns result ('merged', 'partially_merged', 'already_merged', or 'not_confirmed' when Zendesk had not finished or its answer was lost: do not merge again; check the sources with Get_ticket and, if any is still open, ask the person to check the tickets in Zendesk), the tickets as read before the merge, the job's final status and, once the job has completed, each source with its own result; a failed job is returned as an error that also says to check the tickets first, as some may have been merged. " + TicketWriteHelpers.UntrustedContentWarning)]
    public static Task<string> MergeTickets(
        ZendeskService zendesk,
        [Description("Id of the ticket to keep; the sources are merged into it.")] long target_ticket_id,
        [Description("Ids of 1 to 5 tickets to merge into the target and close. Distinct, and not the target.")] long[] source_ticket_ids,
        [Description("Comment added to the target ticket, at most 10,000 characters. Default: a note naming the merged tickets.")] string? target_comment = null,
        [Description("Comment added to each source ticket, at most 10,000 characters. Default: a note naming the target ticket.")] string? source_comment = null,
        [Description("true to make the target comment public, emailing the target's requester and CCs. Default false (private note).")] bool? target_comment_public = null,
        [Description("true to make the source comment public, emailing each source ticket's requester and CCs. Default false (private note).")] bool? source_comment_public = null,
        CancellationToken cancellationToken = default) =>
        TicketMerge.MergeAsync(zendesk, target_ticket_id, source_ticket_ids, target_comment, source_comment,
            target_comment_public, source_comment_public, TicketMerge.DefaultPollDelays, Task.Delay, cancellationToken);
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
    /// stuck job from holding the tool call (and the single replica's request) open indefinitely. A
    /// poll answered 429 is retried by <c>ZendeskRateLimitHandler</c> on <c>Retry-After</c>, so under
    /// rate limiting the whole wait can run longer than this.
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

        // Read every ticket first: some of Zendesk's refusals arrive only as a failed background job,
        // after the merge request has returned, so the checks it would make are made here where they can
        // name the ticket. The read also puts every ticket id into this call's audit record — the merge's own
        // response is a job status, which names no ticket.
        var allIds = new[] { targetTicketId }.Concat(sources).ToArray();
        var before = await ReadTicketsAsync(zendesk, allIds, cancellationToken);

        var problems = new List<string>();
        var mergedSources = new List<long>();
        foreach (var id in allIds)
        {
            var role = id == targetTicketId ? "target" : "source";
            if (!before.Tickets.TryGetValue(id, out var ticket))
            {
                problems.Add($"Ticket {id} ({role}) was not found, or this agent cannot see it.");
                continue;
            }

            // A source already merged is closed, so it is set aside here and judged below by where it went.
            if (role == "source" && HasTag(ticket, "closed_by_merge"))
            {
                mergedSources.Add(id);
                continue;
            }

            var status = ToolJson.String(ticket["status"]);
            if (status == "closed")
            {
                problems.Add($"Ticket {id} ({role}) is closed, and closed tickets cannot be merged.");
            }
            else if (status == "solved" && role == "source")
            {
                problems.Add($"Ticket {id} (source) is solved, and solved tickets cannot be merged into another.");
            }

            // Merging tickets from different people would put one requester's conversation in front of
            // another, so a requester mismatch is refused outright, as Swifteq's merge_tickets does.
            if (role == "source" && before.Tickets.TryGetValue(targetTicketId, out var target)
                && ToolJson.Long(ticket["requester_id"]) != ToolJson.Long(target["requester_id"]))
            {
                problems.Add($"Ticket {id} (source) has a different requester from target ticket {targetTicketId}; only one person's tickets can be merged together.");
            }

            if (HasTag(ticket, "closed_by_merge"))
            {
                problems.Add($"Ticket {id} ({role}) has already been merged into another ticket (tagged closed_by_merge).");
            }
        }

        // Safe to repeat, as Swifteq's merge_tickets is: a source already merged into this target is
        // reported as already_merged and left out of the request. Only a merge Zendesk records against
        // this target counts; one into any other ticket, or one whose record cannot be found, is refused.
        var alreadyMerged = new List<long>();
        if (problems.Count == 0)
        {
            foreach (var id in mergedSources)
            {
                var mergedHere = await WasMergedIntoAsync(zendesk, id, targetTicketId, cancellationToken);
                if (mergedHere == true)
                {
                    alreadyMerged.Add(id);
                }
                else if (mergedHere is null)
                {
                    problems.Add($"Ticket {id} (source) has already been merged (tagged closed_by_merge), and its history could not be read to tell whether that was into ticket {targetTicketId}.");
                }
                else
                {
                    problems.Add($"Ticket {id} (source) has already been merged into another ticket (tagged closed_by_merge), and Zendesk's record does not show it going into ticket {targetTicketId}.");
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new ArgumentException("Nothing was merged. " + string.Join(" ", problems));
        }

        var toMerge = sources.Except(alreadyMerged).ToArray();
        var alreadyMergedJson = new JsonArray(alreadyMerged.Select(s => (JsonNode)s).ToArray());
        if (toMerge.Length == 0)
        {
            return new JsonObject
            {
                ["target_ticket_id"] = targetTicketId,
                ["source_ticket_ids"] = new JsonArray(sources.Select(s => (JsonNode)s).ToArray()),
                ["merged"] = true,
                ["result"] = "already_merged",
                ["already_merged"] = alreadyMergedJson,
                ["sources_after"] = new JsonArray(sources.Select(id => (JsonNode)AlreadyMergedEntry(before, id)).ToArray()),
                ["target_agent_url"] = $"{zendesk.BaseUrl}/agent/tickets/{Id(targetTicketId)}",
                ["note"] = "Every source was already merged into this ticket; nothing was sent to Zendesk.",
            }.ToJsonString();
        }

        // Zendesk's own merge dialogue adds a note to each side; without one the merge leaves no trace
        // in either conversation. These follow its wording in substance, naming tickets by id only, and
        // are always sent, so Zendesk's behaviour when the comments are omitted does not arise.
        var body = new JsonObject
        {
            ["ids"] = new JsonArray(toMerge.Select(s => (JsonNode)s).ToArray()),
            ["target_comment"] = targetComment ?? DefaultTargetComment(toMerge),
            ["source_comment"] = sourceComment ?? $"This request was closed and merged into request #{Id(targetTicketId)}.",
            ["target_comment_is_public"] = targetPublic,
            ["source_comment_is_public"] = sourcePublic,
        };

        var result = new JsonObject
        {
            ["target_ticket_id"] = targetTicketId,
            ["source_ticket_ids"] = new JsonArray(sources.Select(s => (JsonNode)s).ToArray()),
            ["already_merged"] = alreadyMergedJson,
            ["tickets_before"] = new JsonArray(allIds.Select(id => (JsonNode)Describe(before, id, id == targetTicketId ? "target" : "source")).ToArray()),
        };

        // docs-checked, not live-tested: POST /api/v2/tickets/{id}/merge answers {"job_status": {...}}.
        // Only a definite refusal is an error here: Zendesk answered 4xx (after the rate-limit handler's
        // retries), or the request was refused before it was sent (tier, connection). A timeout, a lost
        // connection, a 5xx from a gateway or a body that cannot be parsed can each follow a merge Zendesk
        // has already queued, so they are reported as unconfirmed rather than as "nothing happened".
        JsonNode accepted;
        try
        {
            accepted = await zendesk.PostAsync($"/api/v2/tickets/{Id(targetTicketId)}/merge", body, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && !IsDefiniteRefusal(ex))
        {
            return NotConfirmed(result, null, 0, Failure("The merge request's answer was lost or unreadable", ex));
        }

        // From here on Zendesk has accepted the merge, so it may happen whatever goes wrong next. A
        // failure is therefore never surfaced as a plain error, which a model would read as "nothing
        // changed" and answer with a second merge: the pre-check above cannot refuse that retry while
        // the first job is still queued, because the sources are not yet tagged closed_by_merge. That
        // covers every exception, not only HTTP ones: a dropped connection (whose connect link says
        // "then try again"), a denial from a stale entitlement, a timeout. Only the caller's own
        // cancellation escapes, since nobody is left to read the answer.
        // Indexed only through an object: a 200 with an array or scalar root would otherwise throw here,
        // after the merge may already be queued, instead of reaching the not_confirmed answer below.
        var job = (accepted as JsonObject)?["job_status"] as JsonObject;
        if (job is null)
        {
            return NotConfirmed(result, null, 0, "Zendesk accepted the merge request but returned no job status.");
        }

        var jobId = JobId(job);
        var polls = 0;
        try
        {
            while (!IsTerminal(job) && polls < pollDelays.Count && !string.IsNullOrEmpty(jobId))
            {
                await delay(pollDelays[polls], cancellationToken);
                polls++;
                var polled = await zendesk.GetAsync($"/api/v2/job_statuses/{ZendeskService.Segment(jobId)}", cancellationToken: cancellationToken);
                job = (polled as JsonObject)?["job_status"] as JsonObject ?? job;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return NotConfirmed(result, job, polls, Failure("Checking the merge job failed", ex));
        }

        var jobStatus = ToolJson.String(job["status"]) ?? "unknown";
        if (jobStatus is "failed" or "killed")
        {
            // No status code: Zendesk answered every request with success; it is the job that failed.
            // A multi-ticket job may have merged some sources before failing, so this is not "unchanged".
            throw new HttpRequestException(
                $"Zendesk's merge job{(string.IsNullOrEmpty(jobId) ? string.Empty : " " + jobId)} {jobStatus}. Some source tickets may still have been merged. Check each with Get_ticket (a merged source is closed and tagged closed_by_merge) before calling Merge_tickets again. Job: {Truncate(ToolJson.Pick(job, "id", "status", "total", "progress", "message", "results").ToJsonString(), 1024)}");
        }

        if (jobStatus != "completed")
        {
            return NotConfirmed(result, job, polls, string.IsNullOrEmpty(jobId)
                ? $"Zendesk accepted the merge but its job status carried no id, so the job could not be followed (job status '{jobStatus}')."
                : $"Zendesk had not finished the merge when this tool stopped waiting (job status '{jobStatus}').");
        }

        result["job"] = JobSummary(job);
        result["polls"] = polls;

        // The job's results shape is not documented for merges, so the outcome is read back from the
        // tickets themselves: a merged source is closed and tagged closed_by_merge.
        TicketRead after;
        try
        {
            after = await ReadTicketsAsync(zendesk, toMerge, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return NotConfirmed(result, job, polls, Failure("Zendesk reported the merge job completed, but reading the source tickets back failed", ex));
        }

        var sourcesAfter = new JsonArray();
        var unconfirmed = new List<long>();
        var confirmed = 0;
        foreach (var id in sources)
        {
            if (alreadyMerged.Contains(id))
            {
                sourcesAfter.Add(AlreadyMergedEntry(before, id));
                continue;
            }

            after.Tickets.TryGetValue(id, out var ticket);
            var closedByMerge = ticket is not null && ToolJson.String(ticket["status"]) == "closed" && HasTag(ticket, "closed_by_merge");
            if (closedByMerge)
            {
                confirmed++;
            }
            else
            {
                unconfirmed.Add(id);
            }

            sourcesAfter.Add(new JsonObject
            {
                ["id"] = id,
                ["status"] = ticket is null ? null : ToolJson.String(ticket["status"]),
                ["closed_by_merge"] = closedByMerge,
                ["result"] = closedByMerge ? "merged" : "not_confirmed",
            });
        }

        result["merged"] = unconfirmed.Count == 0;
        result["result"] = unconfirmed.Count == 0 ? "merged" : confirmed > 0 ? "partially_merged" : "not_confirmed";
        result["sources_after"] = sourcesAfter;
        result["target_agent_url"] = $"{zendesk.BaseUrl}/agent/tickets/{Id(targetTicketId)}";
        if (unconfirmed.Count > 0)
        {
            result["warning"] = $"Zendesk reported the job completed, but ticket(s) {string.Join(", ", unconfirmed.Select(Id))} do not yet read as closed and tagged closed_by_merge.";
            result["next_step"] = "Check those tickets with Get_ticket in a few minutes. Do not call Merge_tickets again for them: the job completed, so they are most likely merged and only reading back late.";
        }

        return result.ToJsonString();
    }

    /// <summary>
    /// The answer when the merge was accepted but its result is not known: never reported as merged,
    /// and never as nothing having happened, because the job may still finish after this call returns.
    /// </summary>
    private static string NotConfirmed(JsonObject result, JsonObject? job, int polls, string reason)
    {
        result["job"] = job is null ? null : JobSummary(job);
        result["polls"] = polls;
        result["merged"] = false;
        result["result"] = "not_confirmed";
        // No retry is ever suggested from here: a source that is still open and untagged looks the same
        // whether the job failed or is still queued, and no tool here can read a job's status, so only
        // a person looking in Zendesk can tell the two apart.
        var jobId = job is null ? null : JobId(job);
        result["next_step"] = $"{reason} The merge may still happen or may already have happened. Do not call Merge_tickets again for these tickets. Check the source tickets with Get_ticket in a few minutes: a merged source is closed and tagged closed_by_merge. If any is still open, tell the person the merge's outcome is unknown{(string.IsNullOrEmpty(jobId) ? string.Empty : $" (Zendesk job {jobId})")} and ask them to check the tickets in Zendesk before deciding whether to merge again.";
        return result.ToJsonString();
    }

    // A refusal Zendesk or this server made before any merge could be queued: a 4xx answer, or a request
    // refused before sending (tier, read-only, no connection). Anything else is ambiguous.
    private static bool IsDefiniteRefusal(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => (int)status is >= 400 and < 500,
        UnauthorizedAccessException or ZendeskConnectionRequiredException or ArgumentException => true,
        _ => false,
    };

    // A dropped Zendesk connection's message is the connect link the person needs, so it is kept.
    private static string Failure(string what, Exception ex) =>
        ex is ZendeskConnectionRequiredException ? $"{what}: {ex.Message}" : $"{what} ({ex.GetType().Name}).";

    // Zendesk's job ids are strings; a number is read as its text rather than lost.
    private static string? JobId(JsonObject job) => job["id"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        JsonValue v when ToolJson.Long(v) is { } number => number.ToString(CultureInfo.InvariantCulture),
        _ => null,
    };

    private static JsonObject JobSummary(JsonObject job) => ToolJson.Pick(job, "id", "status", "total", "progress", "message", "results");

    private static JsonObject AlreadyMergedEntry(TicketRead before, long id) => new()
    {
        ["id"] = id,
        ["status"] = ToolJson.String(before.Tickets[id]["status"]),
        ["closed_by_merge"] = true,
        ["result"] = "already_merged",
    };

    /// <summary>
    /// Whether Zendesk's audit trail for <paramref name="sourceId"/> records a merge naming
    /// <paramref name="targetId"/>. Zendesk marks a merge in an audit's or event's <c>via</c> as
    /// <c>source.rel = "merge"</c>, naming the other ticket under <c>source.to</c> or <c>source.from</c>
    /// as <c>ticket_id</c> or <c>ticket_ids</c>. Direction need not be read: the target has passed the
    /// checks above (open, not merged anywhere), so the only merge between the two that leaves the source
    /// closed is source into target.
    /// </summary>
    /// <remarks>
    /// Null when the audits cannot be read. Unverified against the live API: the via shape of merge audits. A shape this does not recognise
    /// only means the source is refused as merged elsewhere, which is what happened before this check.
    /// </remarks>
    private static async Task<bool?> WasMergedIntoAsync(ZendeskService zendesk, long sourceId, long targetId, CancellationToken cancellationToken)
    {
        // A merge is among the last things to happen to a ticket, so the newest page is enough. Nothing
        // has been changed yet, so a failed read is a refusal (null), never a guess either way.
        JsonNode page;
        try
        {
            page = await zendesk.GetAsync($"/api/v2/tickets/{Id(sourceId)}/audits",
                ToolJson.Query(("sort_order", "desc"), ("page[size]", 100)), cancellationToken: cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }

        var vias = (page["audits"] as JsonArray ?? [])
            .Where(audit => audit is not null)
            .SelectMany(audit => new[] { audit!["via"] }.Concat((audit["events"] as JsonArray ?? []).Select(e => e?["via"])));
        return vias.Any(via => ToolJson.String(via?["source"]?["rel"]) == "merge"
            && (NamesTicket(via!["source"]!["to"], targetId) || NamesTicket(via["source"]!["from"], targetId)));
    }

    private static bool NamesTicket(JsonNode? end, long ticketId) =>
        ToolJson.Long(end?["ticket_id"]) == ticketId
        || (end?["ticket_ids"] as JsonArray ?? []).Any(t => ToolJson.Long(t) == ticketId);

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
        var tickets = ById(page["tickets"]);
        var users = ById(page["users"]);
        return new TicketRead(tickets, users);
    }

    private static Dictionary<long, JsonNode> ById(JsonNode? array) =>
        (array as JsonArray ?? [])
            .Where(node => node is not null && ToolJson.Long(node["id"]) is not null)
            .Select(node => node!)
            .DistinctBy(node => ToolJson.Long(node["id"])!.Value)
            .ToDictionary(node => ToolJson.Long(node["id"])!.Value);

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
