using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Tools;

/// <summary>
/// The fields a bulk update can change on a ticket. Each setter records that its field was given, so
/// an explicit <c>null</c> (clear the assignee, group or type) can be told apart from a field left out.
/// </summary>
/// <remarks>
/// System.Text.Json calls a property's setter for a JSON <c>null</c> as well as for a value, and not at
/// all for a property that is absent, which is what <see cref="Given"/> relies on.
/// </remarks>
public class BulkTicketChange
{
    private readonly HashSet<string> _given = new(StringComparer.Ordinal);
    private string? _subject;
    private string? _status;
    private long? _customStatusId;
    private string? _type;
    private string? _priority;
    private long? _assigneeId;
    private long? _groupId;
    private string[]? _additionalTags;
    private string[]? _removeTags;
    private TicketCustomFieldInput[]? _customFields;

    [JsonPropertyName("subject"), Description("New subject. Max 500 characters.")]
    public string? Subject { get => _subject; set => Set(ref _subject, value, "subject"); }

    [JsonPropertyName("status"), Description("New status: new, open, pending, hold or solved. Not closed: a closed ticket can never be changed again. Cannot be combined with custom_status_id.")]
    public string? Status { get => _status; set => Set(ref _status, value, "status"); }

    [JsonPropertyName("custom_status_id"), Description("New custom ticket status ID (List_ticket_fields: the Status field's custom_statuses). Cannot be combined with status.")]
    public long? CustomStatusId { get => _customStatusId; set => Set(ref _customStatusId, value, "custom_status_id"); }

    [JsonPropertyName("type"), Description("Ticket type: question, incident, problem or task; null clears it. Changing a linked incident to another type unlinks it from its problem; a problem with linked incidents cannot change type.")]
    public string? Type { get => _type; set => Set(ref _type, value, "type"); }

    [JsonPropertyName("priority"), Description("New priority: low, normal, high or urgent. Zendesk cannot clear a priority once it is set.")]
    public string? Priority { get => _priority; set => Set(ref _priority, value, "priority"); }

    [JsonPropertyName("assignee_id"), Description("Agent to assign the ticket to (Find_agent_by_name); null unassigns it.")]
    public long? AssigneeId { get => _assigneeId; set => Set(ref _assigneeId, value, "assignee_id"); }

    [JsonPropertyName("group_id"), Description("Group to assign the ticket to; null clears it.")]
    public long? GroupId { get => _groupId; set => Set(ref _groupId, value, "group_id"); }

    [JsonPropertyName("additional_tags"), Description("Tags to add, keeping the existing ones.")]
    public string[]? AdditionalTags { get => _additionalTags; set => Set(ref _additionalTags, value, "additional_tags"); }

    [JsonPropertyName("remove_tags"), Description("Tags to remove.")]
    public string[]? RemoveTags { get => _removeTags; set => Set(ref _removeTags, value, "remove_tags"); }

    [JsonPropertyName("custom_fields"), Description("Custom field values: [{id, value}]. A drop-down or multi-select value must be one of the field's options (List_ticket_fields); empty or null clears the field.")]
    public TicketCustomFieldInput[]? CustomFields { get => _customFields; set => Set(ref _customFields, value, "custom_fields"); }

    /// <summary>The fields the caller gave, by their JSON names, including those given as null.</summary>
    [JsonIgnore]
    public IReadOnlySet<string> Given => _given;

    private void Set<T>(ref T field, T value, string name)
    {
        field = value;
        _given.Add(name);
    }
}

/// <summary>One ticket's own change in a bulk update.</summary>
public sealed class BulkTicketEntry : BulkTicketChange
{
    [JsonPropertyName("id"), Description("The ticket ID.")]
    public long Id { get; set; }
}

/// <summary>
/// The confirmation a bulk update's preview hands back: proof that this server produced a preview of
/// exactly this batch, at most <see cref="Lifetime"/> ago, with every ticket as it was then.
/// </summary>
/// <remarks>
/// An HMAC under a key that exists only in this process, so it cannot be made up without a preview,
/// and a restart (or another replica) simply asks for the preview again. It is not a secret; it binds
/// the write to what the person was shown.
/// </remarks>
public static class BulkTicketConfirmation
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    public static string Issue(string material, DateTimeOffset issuedAt)
    {
        var stamp = issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes($"{stamp}\n{material}"));
        return $"{stamp}.{Convert.ToHexStringLower(mac)}";
    }

    /// <summary>Null when <paramref name="confirmation"/> is valid for <paramref name="material"/> now; otherwise why not.</summary>
    public static string? Check(string material, string confirmation, DateTimeOffset now)
    {
        var parts = confirmation.Trim().Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return "That is not a confirmation from a preview of this batch.";
        }

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var expected = Issue(material, issuedAt);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(confirmation.Trim()), Encoding.UTF8.GetBytes(expected)))
        {
            return "The batch or its tickets differ from the preview this confirmation came from (a ticket may have been updated since, or the server restarted).";
        }

        return now - issuedAt > Lifetime || issuedAt - now > TimeSpan.FromMinutes(1)
            ? $"The confirmation has expired (it is valid for {Lifetime.TotalMinutes:0} minutes)."
            : null;
    }
}

/// <summary>
/// Changing many tickets in one call, as Zendesk's bulk edit does. Kept apart from
/// <see cref="TicketsTools"/> because it works in two steps (a preview, then a background job) that a
/// single-ticket update does not have.
/// </summary>
/// <remarks>
/// The parameters and results follow Swifteq's <c>bulk_update_tickets</c>, so prompts written for
/// either server work with both (its tool definition was read on 2026-11-04).
/// </remarks>
[McpServerToolType]
public static class TicketsBulkTools
{
    /// <summary>Zendesk's limit for one <c>update_many</c> call, and for <c>show_many</c>.</summary>
    public const int MaxTickets = 100;

    private static readonly string[] FinishedJobStatuses = ["completed", "failed", "killed"];

    [Authorize(Policy = "zendesk:write")]
    [McpServerTool(Name = "Bulk_update_tickets", Title = "Bulk update tickets", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false),
     Description("Change fields on up to 100 tickets in one call, as Zendesk's bulk edit does: the same change on every ticket (ticket_ids with changes, e.g. one custom field value on 80 tickets) or a different change per ticket (tickets, e.g. a rewritten subject on each). Fields: subject, status (not closed) or custom_status_id, type, priority, assignee_id, group_id, additional_tags, remove_tags and custom_fields. For comments, CCs, files, macros, problem links and organisations use Update_ticket, one ticket at a time. TWO STEPS: call it first without confirmation to preview (nothing is written): it lists each ticket as will_change (with from and to for each field), unchanged (already as asked) or cannot_change (with the reason), and returns a confirmation. Show the person how many tickets will change and what changes, and call again with the same arguments and that confirmation only after they say yes (valid 30 minutes; a ticket updated since the preview means previewing again). Each updated ticket runs its triggers, so a status change can email requesters. Tag changes on closed tickets are allowed (Zendesk allows nothing else on them) and run no triggers. A drop-down (tagger) field adds its option's tag, and its previous option's tag is removed. Solving a problem ticket also solves its unsolved incidents: refused unless solve_incidents is true, so tell the person first. Returns each ticket's result (changed, unchanged, failed with a reason such as closed or collision, or not_confirmed when Zendesk had not finished or the ticket does not show the change) and undo: the previous values, to pass back as tickets (with a fresh preview) to put them back. Safe to repeat: tickets already as asked are left alone. Split more than 100 tickets into several calls, one after another, not in parallel. " + TicketWriteHelpers.UntrustedContentWarning)]
    public static async Task<string> BulkUpdateTickets(
        ZendeskService zendesk,
        [Description("The tickets that all get the same changes, 1 to 100. Pass this or tickets, not both.")] long[]? ticket_ids = null,
        [Description("The change for every ticket: with ticket_ids, or with tickets to add it to each entry (a field set both here and in an entry is an error).")] BulkTicketChange? changes = null,
        [Description("One entry per ticket with its own changes, 1 to 100 tickets, e.g. a different subject on each. Pass this or ticket_ids, not both.")] BulkTicketEntry[]? tickets = null,
        [Description("The confirmation from the preview of this same batch, once the person has said yes. Leave it out to preview.")] string? confirmation = null,
        [Description("Required when the batch solves a problem ticket that has unsolved linked incidents: Zendesk then also solves each of them and runs their triggers, which can email their requesters. Only set it after telling the person.")] bool? solve_incidents = null,
        CancellationToken cancellationToken = default)
    {
        var plan = Plan(ticket_ids, changes, tickets);
        var ids = plan.Select(p => p.Id).ToList();

        // Unverified against the live API: whether show_many leaves out (rather than refuses with 403)
        // tickets this agent cannot see, and whether it returns archived tickets. Either way a ticket
        // it does not return is reported as cannot_change and never written.
        var found = await ReadTicketsAsync(zendesk, ids, cancellationToken);

        var solvingStatuses = new Dictionary<long, bool>();
        var willChange = new List<Planned>();
        var unchanged = new List<long>();
        var cannotChange = new JsonArray();
        var problems = new List<string>();
        foreach (var (id, change) in plan)
        {
            if (!found.TryGetValue(id, out var ticket))
            {
                cannotChange.Add(new JsonObject { ["id"] = id, ["reason"] = "not found, or this agent cannot see it" });
                continue;
            }

            var diff = Diff(ticket, change);
            if (diff.Count == 0)
            {
                unchanged.Add(id);
                continue;
            }

            if (ToolJson.String(ticket["status"]) == "closed" && diff.Select(kv => kv.Key).Any(field => field != "tags"))
            {
                cannotChange.Add(new JsonObject { ["id"] = id, ["reason"] = "closed: Zendesk allows only tag changes on a closed ticket" });
                continue;
            }

            // Solving a problem ticket solves its unsolved linked incidents too, so those are counted
            // (as Update_ticket does) and only a problem that has some needs solve_incidents.
            var solvesIncidents = false;
            if (await SolvesProblemAsync(zendesk, ticket, change, solvingStatuses, cancellationToken))
            {
                var incidents = await zendesk.GetAllPagesAsync($"/api/v2/tickets/{id.ToString(CultureInfo.InvariantCulture)}/incidents", null, "tickets",
                    maxItems: 1000, cancellationToken: cancellationToken);
                var unsolved = incidents.Items.Count(i => ToolJson.String(i["status"]) is not ("solved" or "closed"));

                // A list cut short says nothing about the incidents past the cut, so it is never read
                // as "none unsolved".
                if (unsolved > 0 || incidents.HasMore)
                {
                    solvesIncidents = true;
                    problems.Add($"{id.ToString(CultureInfo.InvariantCulture)} ({unsolved}{(incidents.HasMore ? " or more" : string.Empty)} unsolved)");
                }
            }

            willChange.Add(new Planned(id, change, ticket, diff, solvesIncidents));
        }

        var refusals = new List<string>();
        if (problems.Count > 0 && solve_incidents != true)
        {
            refusals.Add($"Problem ticket(s) {string.Join(", ", problems)} have unsolved linked incidents; solving them also solves each of those incidents and runs their triggers, which can email their requesters. Tell the person, then call again with solve_incidents: true.");
        }

        var warnings = new List<string>();
        if (problems.Count > 0 && solve_incidents == true)
        {
            warnings.Add($"Solving problem ticket(s) {string.Join(", ", problems)} also solves their unsolved linked incidents.");
        }

        var material = Material(zendesk.BaseUrl, await TicketWriteHelpers.CallerKeyAsync(zendesk, cancellationToken), plan, found, solve_incidents == true);
        var now = DateTimeOffset.UtcNow;

        if (confirmation is null)
        {
            var canConfirm = refusals.Count == 0 && willChange.Count > 0;
            return new JsonObject
            {
                ["preview"] = true,
                ["summary"] = new JsonObject
                {
                    ["will_change"] = willChange.Count,
                    ["unchanged"] = unchanged.Count,
                    ["cannot_change"] = cannotChange.Count,
                },
                ["will_change"] = new JsonArray(willChange.Select(p => (JsonNode?)new JsonObject
                {
                    ["id"] = p.Id,
                    ["subject"] = p.Ticket["subject"]?.DeepClone(),
                    ["changes"] = p.Diff.DeepClone(),
                    ["agent_url"] = AgentUrl(zendesk.BaseUrl, p.Id),
                }).ToArray()),
                ["unchanged"] = Ids(unchanged),
                ["cannot_change"] = cannotChange,
                ["would_refuse"] = Strings(refusals),
                ["warnings"] = Strings(warnings),
                ["confirmation"] = canConfirm ? BulkTicketConfirmation.Issue(material, now) : null,
                ["confirmation_expires_at"] = canConfirm ? now.Add(BulkTicketConfirmation.Lifetime).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : null,
                ["next_step"] = refusals.Count > 0
                    ? "Nothing was changed. This batch would be refused as it stands (see would_refuse); resolve that with the person first."
                    : willChange.Count == 0
                        ? "Nothing was changed, and there is nothing to change: every ticket is already as asked or cannot be changed."
                        : "Nothing was changed. Show the person how many tickets will change and what changes, and only once they say yes call again with the same arguments and this confirmation.",
            }.ToJsonString();
        }

        if (refusals.Count > 0)
        {
            throw new ArgumentException("Nothing was changed. " + string.Join(" ", refusals));
        }

        // The preview is what the person agreed to, so the write must be that preview: the same batch,
        // and every ticket exactly as it was then (each one's updated_at is in the material).
        if (BulkTicketConfirmation.Check(material, confirmation, now) is { } invalid)
        {
            throw new ArgumentException($"Nothing was changed. {invalid} Preview the batch again (leave out confirmation) and show it to the person.");
        }

        var failed = new JsonArray();
        foreach (var refused in cannotChange)
        {
            failed.Add(refused!.DeepClone());
        }

        if (willChange.Count == 0)
        {
            return Result(null, "not_sent", "Nothing to change: every ticket is already as asked or cannot be changed.",
                [], unchanged, failed, new JsonArray(), new JsonArray(), warnings).ToJsonString();
        }

        // docs-checked, not live-tested: PUT update_many with {"tickets":[{id,…}]} applies each entry's
        // own change and answers with a job_status. safe_update with each ticket's updated_stamp makes
        // Zendesk refuse a ticket someone changed in the meantime. Unverified against the live API:
        // that update_many honours safe_update per entry. Tags are sent as the ticket's whole new list,
        // computed from the read above (the read the confirmation was checked against), because the
        // single-ticket PUT silently ignores additional_tags/remove_tags (seen live) and nothing
        // documents the per-entry form honouring them; the re-read below confirms each change landed.
        var body = new JsonArray(willChange.Select(p => (JsonNode?)WriteEntry(p)).ToArray());
        var submitted = await zendesk.PutAsync("/api/v2/tickets/update_many", new JsonObject { ["tickets"] = body }, cancellationToken: cancellationToken);
        var job = submitted["job_status"]
            ?? throw new HttpRequestException("Zendesk accepted the bulk update but returned no job_status, so its outcome is unknown. Do not call again: check the tickets with Get_ticket.");
        var jobId = ToolJson.String(job["id"]);

        // The change has been submitted from here on, so a failure reading the job must not look like a
        // failed call that is worth retrying: it is reported as not confirmed instead. For the same
        // reason the caller's cancellation no longer applies; the polls and the re-read are bounded
        // by MaxJobStatusPolls and Zendesk's own timeouts instead.
        var afterWrite = CancellationToken.None;
        var unreadable = false;
        for (var poll = 0; jobId is not null && !IsFinished(job) && poll < zendesk.Options.MaxJobStatusPolls; poll++)
        {
            try
            {
                await Task.Delay(zendesk.Options.JobStatusPollInterval, afterWrite);
                job = (await zendesk.GetAsync($"/api/v2/job_statuses/{ZendeskService.Segment(jobId)}", cancellationToken: afterWrite))["job_status"] ?? job;
            }
            catch (Exception)
            {
                unreadable = true;
                break;
            }
        }

        return (await OutcomeAsync(zendesk, willChange, job, jobId, unreadable, unchanged, failed, warnings)).ToJsonString();
    }

    private sealed record Planned(long Id, Change Change, JsonNode Ticket, JsonObject Diff, bool SolvesIncidents);

    /// <summary>One ticket's change, validated and in Zendesk's field names.</summary>
    private sealed class Change
    {
        /// <summary>Scalar fields, in Zendesk's names; a null value clears the field.</summary>
        public JsonObject Fields { get; } = [];

        public List<string> AddTags { get; } = [];

        public List<string> RemoveTags { get; } = [];

        public List<TicketCustomFieldInput> CustomFields { get; } = [];

        public bool HasAny => Fields.Count > 0 || AddTags.Count > 0 || RemoveTags.Count > 0 || CustomFields.Count > 0;

        public string Canonical() => new JsonObject
        {
            ["fields"] = new JsonObject(Fields.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone()))),
            ["additional_tags"] = StringArray(Folded(AddTags)),
            ["remove_tags"] = StringArray(Folded(RemoveTags)),
            ["custom_fields"] = TicketWriteHelpers.CustomFields([.. CustomFields.OrderBy(f => f.Id)]),
        }.ToJsonString();

        private static IEnumerable<string> Folded(IEnumerable<string> tags) =>
            tags.Select(t => t.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    }

    private static List<(long Id, Change Change)> Plan(long[]? ticketIds, BulkTicketChange? changes, BulkTicketEntry[]? tickets)
    {
        if ((ticketIds is null) == (tickets is null))
        {
            throw new ArgumentException("Pass ticket_ids (with changes) or tickets, not both and not neither.");
        }

        if (ticketIds is not null)
        {
            ValidateIds(ticketIds, "ticket_ids");
            if (changes is null)
            {
                throw new ArgumentException("ticket_ids needs changes: the change every ticket gets.");
            }

            var change = Normalise([changes], "changes");
            return ticketIds.Select(id => (id, change)).ToList();
        }

        var entries = tickets!;
        if (entries.Any(t => t is null))
        {
            throw new ArgumentException("tickets entries cannot be null.");
        }

        ValidateIds(entries.Select(t => t.Id).ToArray(), "tickets[].id");
        return entries.Select(entry => (entry.Id, Normalise(changes is null ? [entry] : [changes, entry], $"ticket {entry.Id}"))).ToList();
    }

    private static void ValidateIds(long[] ids, string parameter)
    {
        if (ids.Length == 0)
        {
            throw new ArgumentException($"{parameter} must name at least one ticket.");
        }

        if (ids.Length > MaxTickets)
        {
            throw new ArgumentException($"{parameter} takes at most {MaxTickets} tickets (got {ids.Length}); split the batch into several calls, one after another.");
        }

        foreach (var id in ids)
        {
            ToolJson.PositiveId(id, parameter);
        }

        var duplicates = ids.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
        {
            throw new ArgumentException($"{parameter} must be distinct; repeated: {Join(duplicates)}.");
        }
    }

    /// <summary>Merges the shared change and a ticket's own, refusing a field given in both.</summary>
    private static Change Normalise(BulkTicketChange[] sources, string context)
    {
        if (sources.Length == 2 && sources[0].Given.Intersect(sources[1].Given).ToList() is { Count: > 0 } both)
        {
            throw new ArgumentException($"{context}: {string.Join(", ", both)} is set both in changes and in the ticket's entry; set it in one place.");
        }

        var change = new Change();
        foreach (var source in sources)
        {
            foreach (var name in source.Given)
            {
                Apply(change, source, name, context);
            }
        }

        if (change.Fields.ContainsKey("status") && change.Fields.ContainsKey("custom_status_id"))
        {
            throw new ArgumentException($"{context}: give status or custom_status_id, not both.");
        }

        var overlap = change.AddTags.Intersect(change.RemoveTags, StringComparer.OrdinalIgnoreCase).ToList();
        if (overlap.Count > 0)
        {
            throw new ArgumentException($"{context}: a tag cannot be both added and removed: {string.Join(", ", overlap)}.");
        }

        // Drop-down, multi-select and checkbox values are stored as tags, and the tag change is sent as
        // the ticket's whole list, which would still carry each field's old value and could re-apply
        // it. Two calls keep both changes exact, the rule Update_ticket applies too.
        if ((change.AddTags.Count > 0 || change.RemoveTags.Count > 0) && change.CustomFields.Count > 0)
        {
            throw new ArgumentException($"{context}: change tags (additional_tags, remove_tags) and custom_fields in separate calls: field values are stored as tags, so one call could undo the field change.");
        }

        if (!change.HasAny)
        {
            throw new ArgumentException($"{context}: give at least one change: subject, status, custom_status_id, type, priority, assignee_id, group_id, additional_tags, remove_tags or custom_fields.");
        }

        return change;
    }

    private static void Apply(Change change, BulkTicketChange source, string name, string context)
    {
        switch (name)
        {
            case "subject":
                if (string.IsNullOrWhiteSpace(source.Subject))
                {
                    throw new ArgumentException($"{context}: subject cannot be empty or cleared.");
                }

                change.Fields["subject"] = TicketWriteHelpers.MaxLength(source.Subject, TicketWriteHelpers.MaxSubjectLength, "subject");
                break;
            case "status":
                change.Fields["status"] = TicketWriteHelpers.OneOf(source.Status, TicketWriteHelpers.Statuses, "status")
                    ?? throw new ArgumentException($"{context}: status cannot be cleared.");
                break;
            case "custom_status_id":
                change.Fields["custom_status_id"] = source.CustomStatusId is { } customStatus
                    ? ToolJson.PositiveId(customStatus, "custom_status_id")
                    : throw new ArgumentException($"{context}: custom_status_id cannot be cleared.");
                break;
            case "type":
                change.Fields["type"] = TicketWriteHelpers.OneOf(source.Type, TicketWriteHelpers.Types, "type");
                break;
            case "priority":
                change.Fields["priority"] = TicketWriteHelpers.OneOf(source.Priority, TicketWriteHelpers.Priorities, "priority")
                    ?? throw new ArgumentException($"{context}: Zendesk cannot clear a priority once it is set.");
                break;
            case "assignee_id":
                change.Fields["assignee_id"] = source.AssigneeId is { } assignee ? ToolJson.PositiveId(assignee, "assignee_id") : null;
                break;
            case "group_id":
                change.Fields["group_id"] = source.GroupId is { } group ? ToolJson.PositiveId(group, "group_id") : null;
                break;
            case "additional_tags":
                change.AddTags.AddRange((TicketWriteHelpers.Tags(source.AdditionalTags, "additional_tags") ?? []).Select(t => ToolJson.String(t)!));
                break;
            case "remove_tags":
                change.RemoveTags.AddRange((TicketWriteHelpers.Tags(source.RemoveTags, "remove_tags") ?? []).Select(t => ToolJson.String(t)!));
                break;
            case "custom_fields":
                foreach (var field in source.CustomFields ?? [])
                {
                    ToolJson.PositiveId(field.Id, "custom_fields[].id");
                    if (change.CustomFields.Any(f => f.Id == field.Id))
                    {
                        throw new ArgumentException($"{context}: custom field {field.Id} is given more than once.");
                    }

                    change.CustomFields.Add(field);
                }

                break;
        }
    }

    private static async Task<Dictionary<long, JsonNode>> ReadTicketsAsync(ZendeskService zendesk, List<long> ids, CancellationToken cancellationToken)
    {
        var read = await zendesk.GetAsync("/api/v2/tickets/show_many", ToolJson.Query(("ids", ToolJson.Ids(ids))), cancellationToken: cancellationToken);
        var found = new Dictionary<long, JsonNode>();
        foreach (var (id, ticket) in WithIds(read["tickets"]))
        {
            found[id] = ticket;
        }

        return found;
    }

    /// <summary>
    /// The fields this change would alter on this ticket, each as <c>{from, to}</c>. Empty when the
    /// ticket is already as asked. When a value cannot be compared with certainty (a multi-select's
    /// list), it counts as a change: an unnecessary write is harmless, a skipped one is not.
    /// </summary>
    private static JsonObject Diff(JsonNode ticket, Change change)
    {
        var diff = new JsonObject();
        foreach (var (field, to) in change.Fields)
        {
            var from = ticket[field];
            if (Text(from) != Text(to))
            {
                diff[field] = new JsonObject { ["from"] = from?.DeepClone(), ["to"] = to?.DeepClone() };
            }
        }

        if (change.AddTags.Count > 0 || change.RemoveTags.Count > 0)
        {
            var current = CurrentTags(ticket);
            var after = NewTags(current, change);
            if (!current.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(after))
            {
                diff["tags"] = new JsonObject { ["from"] = StringArray(current), ["to"] = StringArray(after) };
            }
        }

        if (change.CustomFields.Count > 0)
        {
            var current = CurrentCustomFields(ticket);
            var differing = new JsonArray();
            foreach (var field in change.CustomFields)
            {
                var from = current.GetValueOrDefault(field.Id);
                if (!SameFieldValue(from, field.Value))
                {
                    differing.Add(new JsonObject { ["id"] = field.Id, ["from"] = from?.DeepClone(), ["to"] = field.Value });
                }
            }

            if (differing.Count > 0)
            {
                diff["custom_fields"] = differing;
            }
        }

        return diff;
    }

    private static async Task<bool> SolvesProblemAsync(ZendeskService zendesk, JsonNode ticket, Change change, Dictionary<long, bool> cache, CancellationToken cancellationToken)
    {
        bool solving;
        if (ToolJson.Long(change.Fields["custom_status_id"]) is { } customStatusId)
        {
            if (!cache.TryGetValue(customStatusId, out solving))
            {
                solving = await TicketWriteHelpers.IsSolvingAsync(zendesk, null, customStatusId, cancellationToken);
                cache[customStatusId] = solving;
            }
        }
        else
        {
            solving = await TicketWriteHelpers.IsSolvingAsync(zendesk, ToolJson.String(change.Fields["status"]), null, cancellationToken);
        }

        var resultingType = ToolJson.String(change.Fields.TryGetPropertyValue("type", out var newType) ? newType : ticket["type"]);
        return solving && resultingType == "problem" && ToolJson.String(ticket["status"]) is not ("solved" or "closed");
    }

    /// <summary>
    /// What the confirmation is bound to: the account, the person (so one person's preview cannot
    /// confirm another's write), the batch as normalised, the solve_incidents confirmation, and every
    /// ticket's <c>updated_at</c> (or its absence). Order-free throughout (tickets by id, fields by
    /// name, tags case-folded): a model re-sending the same batch with its keys in another order is
    /// confirming the same thing.
    /// </summary>
    private static string Material(string baseUrl, string caller, List<(long Id, Change Change)> plan, Dictionary<long, JsonNode> found, bool solveIncidents)
    {
        var material = new StringBuilder().Append(baseUrl).Append('\n').Append(caller).Append('\n').Append(solveIncidents ? "solve_incidents" : "-").Append('\n');
        foreach (var (id, change) in plan.OrderBy(p => p.Id))
        {
            material.Append(id.ToString(CultureInfo.InvariantCulture)).Append('@')
                .Append(found.TryGetValue(id, out var ticket) ? ToolJson.String(ticket["updated_at"]) ?? "?" : "missing")
                .Append(' ').Append(change.Canonical()).Append('\n');
        }

        return material.ToString();
    }

    /// <summary>The entry sent for one ticket: only what differs, guarded by safe_update.</summary>
    private static JsonObject WriteEntry(Planned planned)
    {
        var entry = new JsonObject
        {
            ["id"] = planned.Id,
            ["safe_update"] = true,
            ["updated_stamp"] = planned.Ticket["updated_at"]?.DeepClone(),
        };
        foreach (var (field, value) in planned.Diff)
        {
            entry[field] = field == "custom_fields"
                ? new JsonArray(value!.AsArray().Select(f => (JsonNode?)new JsonObject { ["id"] = f!["id"]!.DeepClone(), ["value"] = FieldValue(f["from"], f["to"]) }).ToArray())
                : value!["to"]?.DeepClone();
        }

        return entry;
    }

    /// <summary>
    /// The value to send for a custom field: a multi-select (a field that holds a list) gets its option
    /// tags as a list, everything else the text as given. Unverified against the live API: the list
    /// form for a multi-select through update_many.
    /// </summary>
    private static JsonNode? FieldValue(JsonNode? from, JsonNode? to) =>
        from is JsonArray && ToolJson.String(to) is { } text
            ? StringArray(text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : to?.DeepClone();

    private static bool IsFinished(JsonNode job) => FinishedJobStatuses.Contains(ToolJson.String(job["status"]));

    /// <summary>
    /// Each ticket's outcome. A ticket is reported changed only when Zendesk's job calls it a success
    /// <b>and</b> a fresh read shows the change; anything less is failed or not_confirmed, never assumed.
    /// </summary>
    /// <remarks>
    /// Unverified against the live API: the shape of <c>results</c> entries (<c>id</c>, <c>success</c>,
    /// <c>status</c>, <c>error</c>, <c>details</c>) is taken from Zendesk's job status documentation.
    /// </remarks>
    private static async Task<JsonObject> OutcomeAsync(ZendeskService zendesk, List<Planned> sent, JsonNode job, string? jobId, bool unreadable,
        List<long> unchanged, JsonArray failed, List<string> warnings)
    {
        var jobStatus = unreadable ? "unknown" : IsFinished(job) ? ToolJson.String(job["status"])! : "running";
        var notConfirmed = new JsonArray();
        var reportedSuccess = new List<Planned>();

        if (jobStatus is "unknown" or "running")
        {
            var reason = unreadable ? "the job's progress could not be read" : "Zendesk had not finished";
            foreach (var p in sent)
            {
                notConfirmed.Add(new JsonObject { ["id"] = p.Id, ["reason"] = reason });
            }
        }
        else
        {
            var results = new Dictionary<long, JsonNode>();
            foreach (var (id, result) in WithIds(job["results"]))
            {
                results.TryAdd(id, result);
            }

            foreach (var p in sent)
            {
                if (!results.TryGetValue(p.Id, out var result))
                {
                    notConfirmed.Add(new JsonObject { ["id"] = p.Id, ["reason"] = "Zendesk's job did not report on this ticket" });
                    continue;
                }

                var success = result["success"] is JsonValue s && s.TryGetValue<bool>(out var b) ? b : (bool?)null;
                if (result["error"] is not null || success == false)
                {
                    failed.Add(new JsonObject
                    {
                        ["id"] = p.Id,
                        ["reason"] = ToolJson.String(result["details"]) ?? ToolJson.String(result["error"]) ?? "Zendesk reported a failure",
                        ["error"] = result["error"]?.DeepClone(),
                    });
                }
                else if (success == true || string.Equals(ToolJson.String(result["status"]), "Updated", StringComparison.OrdinalIgnoreCase))
                {
                    reportedSuccess.Add(p);
                }
                else
                {
                    notConfirmed.Add(new JsonObject { ["id"] = p.Id, ["reason"] = "Zendesk's job did not say whether this ticket was updated" });
                }
            }
        }

        // A success Zendesk reports is checked against the ticket itself: that is what would have caught
        // the 2026-10-28 class of bug, a write accepted and silently not applied.
        var changed = new List<long>();
        if (reportedSuccess.Count > 0)
        {
            Dictionary<long, JsonNode>? reread = null;
            try
            {
                reread = await ReadTicketsAsync(zendesk, reportedSuccess.Select(p => p.Id).ToList(), CancellationToken.None);
            }
            catch (Exception)
            {
                // Reported below as not confirmed; as far as Zendesk said, the write itself succeeded.
            }

            foreach (var p in reportedSuccess)
            {
                if (reread is null || !reread.TryGetValue(p.Id, out var current))
                {
                    notConfirmed.Add(new JsonObject { ["id"] = p.Id, ["reason"] = "Zendesk reported it updated, but it could not be read back to confirm" });
                }
                else if (Diff(current, p.Change) is { Count: > 0 } still)
                {
                    notConfirmed.Add(new JsonObject
                    {
                        ["id"] = p.Id,
                        ["reason"] = $"Zendesk reported it updated, but it does not show the change now ({string.Join(", ", still.Select(kv => kv.Key))}); a trigger may have changed it again",
                    });
                }
                else
                {
                    changed.Add(p.Id);
                }
            }
        }

        var notConfirmedIds = notConfirmed.Select(n => ToolJson.Long(n?["id"])).OfType<long>().ToHashSet();
        // An entry with nothing to restore is left out: passed back, a bare {"id"} is refused, and with
        // it the whole batch.
        var undo = new JsonArray(sent.Where(p => changed.Contains(p.Id) || notConfirmedIds.Contains(p.Id))
            .Select(p => Undo(p, warnings)).Where(e => e.Count > 1).Select(e => (JsonNode?)e).ToArray());

        var summary = jobStatus switch
        {
            "unknown" => $"The change was submitted (job {jobId}), but its progress could not be read. Do not call again: check the tickets with Get_ticket or Search_tickets.",
            "running" => $"Zendesk is still applying the change (job {jobId}). Do not call again: check the tickets with Get_ticket or Search_tickets in a minute.",
            _ => $"Changed {changed.Count} of {sent.Count} ticket(s) sent; {unchanged.Count} already as asked, {failed.Count} failed, {notConfirmed.Count} not confirmed.",
        };

        return Result(jobId, jobStatus, summary, changed, unchanged, failed, notConfirmed, undo, warnings);
    }

    /// <summary>The previous values of the fields this change altered, in the <c>tickets</c> entry shape.</summary>
    private static JsonObject Undo(Planned planned, List<string> warnings)
    {
        var entry = new JsonObject { ["id"] = planned.Id };
        foreach (var (field, value) in planned.Diff)
        {
            switch (field)
            {
                case "tags":
                    var before = value!["from"]!.AsArray().Select(ToolJson.String).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var after = value["to"]!.AsArray().Select(ToolJson.String).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var restore = before.Where(t => !after.Contains(t)).ToList();
                    var drop = after.Where(t => !before.Contains(t)).ToList();
                    if (restore.Count > 0)
                    {
                        entry["additional_tags"] = StringArray(restore);
                    }

                    if (drop.Count > 0)
                    {
                        entry["remove_tags"] = StringArray(drop);
                    }

                    break;
                case "custom_fields":
                    var fields = new JsonArray();
                    foreach (var f in value!.AsArray())
                    {
                        if (f!["from"] is JsonArray or JsonObject)
                        {
                            warnings.Add($"Ticket {planned.Id}: custom field {f["id"]} held a list, which undo cannot express; restore it with Update_ticket.");
                            continue;
                        }

                        fields.Add(new JsonObject { ["id"] = f["id"]!.DeepClone(), ["value"] = ScalarText(f["from"]) });
                    }

                    if (fields.Count > 0)
                    {
                        entry["custom_fields"] = fields;
                    }

                    break;
                case "priority" when value!["from"] is null:
                    warnings.Add($"Ticket {planned.Id} had no priority before; Zendesk cannot clear one, so undo leaves it.");
                    break;
                case "status":
                    // A status change also resets the custom status to its category's default, so the
                    // custom status the ticket had is what puts it back exactly.
                    if (!planned.Diff.ContainsKey("custom_status_id") && ToolJson.Long(planned.Ticket["custom_status_id"]) is { } previousCustom)
                    {
                        entry["custom_status_id"] = previousCustom;
                    }
                    else if (ToolJson.String(value!["from"]) == "new")
                    {
                        warnings.Add($"Ticket {planned.Id} was New; a ticket cannot return to New, so undo leaves its status.");
                    }
                    else
                    {
                        entry["status"] = value!["from"]?.DeepClone();
                    }

                    if (planned.SolvesIncidents)
                    {
                        warnings.Add($"Undoing problem ticket {planned.Id} does not reopen the incidents solving it solved.");
                    }

                    break;
                case "custom_status_id" when value!["from"] is null:
                    entry["status"] = planned.Ticket["status"]?.DeepClone();
                    break;
                case "type" when ToolJson.Long(planned.Ticket["problem_id"]) is { } problemId && ToolJson.String(value!["from"]) == "incident":
                    entry["type"] = "incident";
                    warnings.Add($"Ticket {planned.Id} was an incident of problem {problemId}; changing its type unlinked it, and undo does not relink it (use Update_ticket with problem_id).");
                    break;
                default:
                    entry[field] = value!["from"]?.DeepClone();
                    break;
            }
        }

        return entry;
    }

    private static JsonObject Result(string? jobId, string jobStatus, string summary, List<long> changed, List<long> unchanged,
        JsonArray failed, JsonArray notConfirmed, JsonArray undo, List<string> warnings) => new()
    {
        ["preview"] = false,
        ["job"] = new JsonObject { ["id"] = jobId, ["status"] = jobStatus },
        ["summary"] = summary,
        ["changed"] = Ids(changed),
        ["unchanged"] = Ids(unchanged),
        ["failed"] = failed,
        ["not_confirmed"] = notConfirmed,
        ["undo"] = undo,
        ["warnings"] = Strings(warnings),
    };

    private static List<string> CurrentTags(JsonNode ticket) =>
        (ticket["tags"] as JsonArray ?? []).Select(ToolJson.String).OfType<string>().ToList();

    private static List<string> NewTags(List<string> current, Change change)
    {
        var remove = change.RemoveTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return current.Concat(change.AddTags).Where(t => !remove.Contains(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Dictionary<long, JsonNode?> CurrentCustomFields(JsonNode ticket)
    {
        var values = new Dictionary<long, JsonNode?>();
        foreach (var (id, field) in WithIds(ticket["custom_fields"]))
        {
            values.TryAdd(id, field["value"]);
        }

        return values;
    }

    /// <summary>
    /// Whether a custom field already holds the requested value. Empty, null and a cleared checkbox's
    /// <c>false</c> all mean "no value". A multi-select holds a list of option tags, compared as a set
    /// with the requested value's comma- or space-separated tags.
    /// </summary>
    private static bool SameFieldValue(JsonNode? current, string? requested)
    {
        if (current is JsonObject)
        {
            return false;
        }

        if (current is JsonArray list)
        {
            var have = list.Select(ScalarText).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var want = (requested ?? string.Empty).Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return have.SetEquals(want);
        }

        var currentText = ScalarText(current);
        static bool None(string? text) => string.IsNullOrEmpty(text) || text == "false";
        return (None(currentText) && None(requested)) || string.Equals(currentText, requested, StringComparison.Ordinal);
    }

    /// <summary>A scalar's text: a string's own value, a number or boolean as JSON writes it.</summary>
    private static string? ScalarText(JsonNode? node) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    /// <summary>The objects in a JSON array that carry a whole-number <c>id</c>, with it.</summary>
    private static IEnumerable<(long Id, JsonNode Node)> WithIds(JsonNode? array) =>
        (array as JsonArray ?? [])
            .OfType<JsonNode>()
            .Select(node => (Id: ToolJson.Long(node["id"]), Node: node))
            .Where(x => x.Id is not null)
            .Select(x => (x.Id!.Value, x.Node));

    private static string Text(JsonNode? node) => node?.ToJsonString() ?? "null";

    private static string AgentUrl(string baseUrl, long id) => $"{baseUrl}/agent/tickets/{id.ToString(CultureInfo.InvariantCulture)}";

    private static string Join(IEnumerable<long> ids) => string.Join(", ", ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));

    private static JsonArray Ids(IEnumerable<long> ids) => new(ids.Select(id => (JsonNode?)id).ToArray());

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonArray StringArray(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)v).ToArray());
}
