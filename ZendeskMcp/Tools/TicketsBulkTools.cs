using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Tools;

/// <summary>
/// Applying one change to many tickets in a call. Kept apart from <see cref="TicketsTools"/> because it
/// works in two steps (a preview, then a background job) that a single-ticket update does not have.
/// </summary>
[McpServerToolType]
public static class TicketsBulkTools
{
    /// <summary>Zendesk's limit for one <c>update_many</c> call, and for <c>show_many</c>.</summary>
    public const int MaxTickets = 100;

    private static readonly string[] FinishedJobStatuses = ["completed", "failed", "killed"];

    [Authorize(Policy = "zendesk:write")]
    [McpServerTool(Name = "Bulk_update_tickets", Title = "Bulk update tickets", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false),
     Description("Apply the same change to up to 100 tickets: status or custom_status_id (not both), type, priority, assignee, group, custom fields, tags (additional_tags adds, remove_tags removes, others are kept) and an optional comment added to every ticket (a private internal note unless public is true; a public comment emails each ticket's requester and CCs and needs public replies switched on in this server's settings, an administrator switch). dry_run defaults to true: nothing is changed, and the result shows each ticket's current values beside the proposed ones. ALWAYS show that preview to the person and call again with dry_run: false and the same arguments only once they have agreed to it. Tickets that do not exist, that this agent cannot see, or that are closed are refused; remove them and retry. Solving problem tickets also solves their linked incidents, so it is refused unless solve_incidents is true. Zendesk applies the change as a background job: the result lists each ticket as updated, failed (with Zendesk's reason) or unconfirmed, or says the job is still running — then check the tickets rather than calling again, as a comment would be added twice. For per-ticket changes, subjects, organisations, CCs, attachments or incident linking use Update_ticket. " + TicketWriteHelpers.UntrustedContentWarning)]
    public static async Task<string> BulkUpdateTickets(
        ZendeskService zendesk,
        [Description("The tickets to change: 1 to 100 distinct ticket ids.")] long[] ticket_ids,
        [Description("new, open, pending, hold or solved. Not with custom_status_id.")] string? status = null,
        [Description("Custom status id. Not with status.")] long? custom_status_id = null,
        [Description("question, incident, problem or task.")] string? type = null,
        [Description("low, normal, high or urgent.")] string? priority = null,
        [Description("Agent to assign.")] long? assignee_id = null,
        [Description("Group to assign.")] long? group_id = null,
        [Description("A comment to add to every ticket, at most 10,000 characters.")] string? comment = null,
        [Description("true for a public comment that emails each requester and their CCs; false (default) for a private internal note.")] bool? @public = null,
        [Description("Tags to add.")] string[]? additional_tags = null,
        [Description("Tags to remove.")] string[]? remove_tags = null,
        [Description("Custom field values: [{id, value}].")] TicketCustomFieldInput[]? custom_fields = null,
        [Description("Must be true to solve problem tickets, which also solves their unsolved linked incidents.")] bool? solve_incidents = null,
        [Description("true (default) to preview the change without making it; false to apply it, once the person has agreed to the preview.")] bool dry_run = true,
        CancellationToken cancellationToken = default)
    {
        var ids = TicketIds(ticket_ids);
        TicketWriteHelpers.MaxLength(comment, TicketWriteHelpers.MaxCommentLength, "comment");
        var normalisedStatus = TicketWriteHelpers.OneOf(status, TicketWriteHelpers.Statuses, "status");
        var normalisedType = TicketWriteHelpers.OneOf(type, TicketWriteHelpers.Types, "type");
        var normalisedPriority = TicketWriteHelpers.OneOf(priority, TicketWriteHelpers.Priorities, "priority");
        var hasComment = !string.IsNullOrWhiteSpace(comment);
        var isPublic = @public ?? false;

        foreach (var (value, name) in new[] { (custom_status_id, "custom_status_id"), (assignee_id, "assignee_id"), (group_id, "group_id") })
        {
            if (value is { } id)
            {
                ToolJson.PositiveId(id, name);
            }
        }

        var addTags = TicketWriteHelpers.Tags(additional_tags, "additional_tags");
        var dropTags = TicketWriteHelpers.Tags(remove_tags, "remove_tags");
        var fields = custom_fields is { Length: > 0 } ? TicketWriteHelpers.CustomFields(custom_fields) : null;

        var hasChange = normalisedStatus is not null || custom_status_id is not null || normalisedType is not null
            || normalisedPriority is not null || assignee_id is not null || group_id is not null || hasComment
            || addTags is not null || dropTags is not null || fields is not null;
        if (!hasChange)
        {
            throw new ArgumentException("Give at least one change: status, custom_status_id, type, priority, assignee_id, group_id, comment, additional_tags, remove_tags or custom_fields.");
        }

        if (normalisedStatus is not null && custom_status_id is not null)
        {
            throw new ArgumentException("Give status or custom_status_id, not both.");
        }

        if (!hasComment && @public is not null)
        {
            throw new ArgumentException("public applies to a comment; give comment as well.");
        }

        // Drop-down, multi-select and checkbox values are stored as tags, so a tag change in the same
        // call could remove the value the field change sets. Two calls keep both changes exact, and
        // match the rule Update_ticket applies.
        if ((addTags is not null || dropTags is not null) && fields is not null)
        {
            throw new ArgumentException("Change tags (additional_tags, remove_tags) and custom_fields in separate calls: field values are stored as tags, so one call could undo the field change.");
        }

        var overlap = (addTags ?? []).Select(t => ToolJson.String(t)!)
            .Intersect((dropTags ?? []).Select(t => ToolJson.String(t)!), StringComparer.OrdinalIgnoreCase).ToList();
        if (overlap.Count > 0)
        {
            throw new ArgumentException($"A tag cannot be both added and removed: {string.Join(", ", overlap)}.");
        }

        // Checked before anything is read, so a preview never shows a change that would be refused.
        if (hasComment && isPublic)
        {
            ToolJson.RequireFeature(zendesk.Options.Features.AllowPublicReplies, "AllowPublicReplies", "Adding a public comment");
        }

        // Read every ticket first, in both modes: the preview is built from it, and a write refuses
        // tickets that are missing, hidden from this agent or closed, rather than letting the job fail
        // on them one by one.
        var read = await zendesk.GetAsync("/api/v2/tickets/show_many", ToolJson.Query(("ids", ToolJson.Ids(ids))), cancellationToken: cancellationToken);
        var found = new Dictionary<long, JsonNode>();
        foreach (var ticket in read["tickets"]?.AsArray() ?? [])
        {
            if (ToolJson.Long(ticket?["id"]) is { } id && ticket is not null)
            {
                found[id] = ticket;
            }
        }

        var notFound = ids.Where(id => !found.ContainsKey(id)).ToList();
        var closed = ids.Where(id => found.TryGetValue(id, out var t) && ToolJson.String(t["status"]) == "closed").ToList();

        var solving = await TicketWriteHelpers.IsSolvingAsync(zendesk, normalisedStatus, custom_status_id, cancellationToken);

        // Solving a problem ticket solves every linked incident too. Update_ticket counts them first;
        // for up to a hundred tickets that would be a hundred more reads, so any open problem ticket
        // being solved needs the confirmation, whether or not it has incidents.
        var problems = solving
            ? ids.Where(id => found.TryGetValue(id, out var t)
                && (normalisedType ?? ToolJson.String(t["type"])) == "problem"
                && ToolJson.String(t["status"]) is not ("solved" or "closed")).ToList()
            : [];

        var refusals = new List<string>();
        if (notFound.Count > 0)
        {
            refusals.Add($"Ticket(s) {Join(notFound)} do not exist or this agent cannot see them.");
        }

        if (closed.Count > 0)
        {
            refusals.Add($"Ticket(s) {Join(closed)} are closed, and Zendesk does not allow closed tickets to be changed.");
        }

        if (problems.Count > 0 && solve_incidents != true)
        {
            refusals.Add($"Ticket(s) {Join(problems)} are problem tickets; solving them also solves all of their unsolved linked incidents and copies the solving comment to each of them (publicly, emailing their requesters, if the comment is public). Confirm with the person, then call again with solve_incidents: true.");
        }

        var warnings = new List<string>();
        if (problems.Count > 0 && solve_incidents == true)
        {
            warnings.Add($"Solving problem ticket(s) {Join(problems)} also solves their unsolved linked incidents.");
        }

        var change = new JsonObject();
        SetIfPresent(change, "status", normalisedStatus);
        SetIfPresent(change, "custom_status_id", custom_status_id);
        SetIfPresent(change, "type", normalisedType);
        SetIfPresent(change, "priority", normalisedPriority);
        SetIfPresent(change, "assignee_id", assignee_id);
        SetIfPresent(change, "group_id", group_id);
        if (addTags is not null)
        {
            change["additional_tags"] = addTags.DeepClone();
        }

        if (dropTags is not null)
        {
            change["remove_tags"] = dropTags.DeepClone();
        }

        if (fields is not null)
        {
            change["custom_fields"] = fields.DeepClone();
        }

        if (dry_run)
        {
            if (hasComment)
            {
                change["comment"] = new JsonObject { ["public"] = isPublic, ["body"] = comment };
            }

            return new JsonObject
            {
                ["dry_run"] = true,
                ["changed"] = false,
                ["change"] = change,
                ["tickets"] = new JsonArray(ids.Select(id => (JsonNode?)Preview(id, found.GetValueOrDefault(id), change, addTags, dropTags, zendesk.BaseUrl)).ToArray()),
                ["would_refuse"] = Strings(refusals),
                ["warnings"] = Strings(warnings),
                ["next_step"] = refusals.Count > 0
                    ? "Nothing was changed. This change would be refused as it stands (see would_refuse); resolve that with the person first."
                    : "Nothing was changed. Show this preview to the person, and call again with dry_run: false and the same arguments only once they agree.",
            }.ToJsonString();
        }

        if (refusals.Count > 0)
        {
            throw new ArgumentException("Nothing was changed. " + string.Join(" ", refusals));
        }

        var body = (JsonObject)change.DeepClone();
        if (hasComment)
        {
            body["comment"] = await TicketWriteHelpers.CommentAsync(zendesk, comment!, isPublic, null, null, warnings, cancellationToken);
        }

        // docs-checked, not live-tested: PUT update_many?ids= with {"ticket":{…}} applies one change to
        // every listed ticket and answers with a job_status; additional_tags/remove_tags are its
        // native tag format (unlike the single-ticket PUT, which ignores them).
        var submitted = await zendesk.PutAsync("/api/v2/tickets/update_many", new JsonObject { ["ticket"] = body },
            ToolJson.Query(("ids", ToolJson.Ids(ids))), cancellationToken: cancellationToken);
        var job = submitted["job_status"]
            ?? throw new HttpRequestException("Zendesk accepted the bulk update but returned no job_status, so its outcome is unknown. Check the tickets with Get_ticket before trying again.");
        var jobId = ToolJson.String(job["id"]);

        for (var poll = 0; jobId is not null && !IsFinished(job) && poll < zendesk.Options.MaxJobStatusPolls; poll++)
        {
            await Task.Delay(zendesk.Options.JobStatusPollInterval, cancellationToken);
            job = (await zendesk.GetAsync($"/api/v2/job_statuses/{ZendeskService.Segment(jobId)}", cancellationToken: cancellationToken))["job_status"] ?? job;
        }

        return Outcome(ids, job, jobId, warnings).ToJsonString();
    }

    private static List<long> TicketIds(long[]? ticketIds)
    {
        if (ticketIds is null || ticketIds.Length == 0)
        {
            throw new ArgumentException("ticket_ids must name at least one ticket.");
        }

        if (ticketIds.Length > MaxTickets)
        {
            throw new ArgumentException($"ticket_ids takes at most {MaxTickets} tickets (got {ticketIds.Length}); split the change into several calls.");
        }

        foreach (var id in ticketIds)
        {
            ToolJson.PositiveId(id, "ticket_ids");
        }

        var duplicates = ticketIds.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new ArgumentException($"ticket_ids must be distinct; repeated: {Join(duplicates)}.");
        }

        return [.. ticketIds];
    }

    /// <summary>One ticket's current values beside the proposed ones, for the fields the change touches.</summary>
    private static JsonObject Preview(long id, JsonNode? ticket, JsonObject change, JsonArray? addTags, JsonArray? dropTags, string baseUrl)
    {
        if (ticket is null)
        {
            return new JsonObject { ["id"] = id, ["outcome"] = "not_found" };
        }

        var current = new JsonObject();
        var proposed = new JsonObject();
        foreach (var field in new[] { "status", "custom_status_id", "type", "priority", "assignee_id", "group_id" })
        {
            if (change[field] is { } value)
            {
                current[field] = ticket[field]?.DeepClone();
                proposed[field] = value.DeepClone();
            }
        }

        if (addTags is not null || dropTags is not null)
        {
            var currentTags = (ticket["tags"] as JsonArray ?? []).Select(ToolJson.String).OfType<string>().ToList();
            var remove = (dropTags ?? []).Select(t => ToolJson.String(t)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var after = currentTags.Concat((addTags ?? []).Select(t => ToolJson.String(t)!))
                .Where(t => !remove.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            current["tags"] = new JsonArray(currentTags.Select(t => (JsonNode?)t).ToArray());
            proposed["tags"] = new JsonArray(after.Select(t => (JsonNode?)t).ToArray());
        }

        if (change["custom_fields"] is JsonArray fields)
        {
            var currentValues = (ticket["custom_fields"] as JsonArray ?? [])
                .Where(f => ToolJson.Long(f?["id"]) is not null)
                .GroupBy(f => ToolJson.Long(f!["id"])!.Value)
                .ToDictionary(g => g.Key, g => g.First()!["value"]);
            current["custom_fields"] = new JsonArray(fields.Select(f =>
            {
                var fieldId = ToolJson.Long(f?["id"])!.Value;
                return (JsonNode?)new JsonObject { ["id"] = fieldId, ["value"] = currentValues.GetValueOrDefault(fieldId)?.DeepClone() };
            }).ToArray());
            proposed["custom_fields"] = fields.DeepClone();
        }

        var status = ToolJson.String(ticket["status"]);
        return new JsonObject
        {
            ["id"] = id,
            ["subject"] = ticket["subject"]?.DeepClone(),
            ["outcome"] = status == "closed" ? "closed" : "would_update",
            ["current"] = current,
            ["proposed"] = proposed,
            ["agent_url"] = $"{baseUrl}/agent/tickets/{id.ToString(CultureInfo.InvariantCulture)}",
        };
    }

    private static bool IsFinished(JsonNode job) => FinishedJobStatuses.Contains(ToolJson.String(job["status"]));

    /// <summary>
    /// What the job reports, ticket by ticket. Only a ticket Zendesk's results name as a success is
    /// reported as updated; anything else is failed or unconfirmed, never assumed to have worked.
    /// </summary>
    /// <remarks>
    /// Unverified against the live API: the shape of <c>results</c> entries (<c>id</c>, <c>success</c>,
    /// <c>status</c>, <c>error</c>, <c>details</c>) is taken from Zendesk's job status documentation.
    /// </remarks>
    private static JsonObject Outcome(List<long> ids, JsonNode job, string? jobId, List<string> warnings)
    {
        var jobStatus = ToolJson.String(job["status"]) ?? "unknown";
        var updated = new List<long>();
        var failed = new JsonArray();
        var reported = new HashSet<long>();

        foreach (var result in job["results"] as JsonArray ?? [])
        {
            if (ToolJson.Long(result?["id"]) is not { } id || !ids.Contains(id) || !reported.Add(id))
            {
                continue;
            }

            var error = result!["error"];
            var success = result["success"] is JsonValue s && s.TryGetValue<bool>(out var b) ? b : (bool?)null;
            if (error is not null || success == false)
            {
                failed.Add(new JsonObject
                {
                    ["id"] = id,
                    ["error"] = error?.DeepClone(),
                    ["details"] = result["details"]?.DeepClone(),
                });
            }
            else if (success == true || string.Equals(ToolJson.String(result["status"]), "Updated", StringComparison.OrdinalIgnoreCase))
            {
                updated.Add(id);
            }
            else
            {
                reported.Remove(id);
            }
        }

        var unconfirmed = ids.Where(id => !reported.Contains(id)).ToList();
        var finished = FinishedJobStatuses.Contains(jobStatus);

        string summary;
        if (!finished)
        {
            summary = $"Zendesk is still applying the change (job {jobStatus}). Do not call again: check the tickets with Get_ticket or Search_tickets in a minute. Tickets not listed as updated may still change.";
        }
        else if (jobStatus != "completed")
        {
            summary = $"The Zendesk job {jobStatus}. Tickets not listed as updated may not have changed; check them with Get_ticket before trying again.";
        }
        else if (failed.Count > 0 || unconfirmed.Count > 0)
        {
            summary = $"Updated {updated.Count} of {ids.Count} ticket(s); see failed and unconfirmed for the rest.";
        }
        else
        {
            summary = $"Updated all {ids.Count} ticket(s).";
        }

        return new JsonObject
        {
            ["dry_run"] = false,
            ["job"] = new JsonObject { ["id"] = jobId, ["status"] = finished ? jobStatus : "running" },
            ["summary"] = summary,
            ["updated"] = new JsonArray(updated.Select(id => (JsonNode?)id).ToArray()),
            ["failed"] = failed,
            ["unconfirmed"] = new JsonArray(unconfirmed.Select(id => (JsonNode?)id).ToArray()),
            ["warnings"] = Strings(warnings),
        };
    }

    private static string Join(IEnumerable<long> ids) => string.Join(", ", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static void SetIfPresent(JsonObject target, string name, string? value)
    {
        if (value is not null)
        {
            target[name] = value;
        }
    }

    private static void SetIfPresent(JsonObject target, string name, long? value)
    {
        if (value is not null)
        {
            target[name] = value;
        }
    }
}
