using System.Globalization;
using System.Text.Json.Nodes;

namespace ZendeskMcp.Tools;

/// <summary>
/// Compares a ticket with the ticket a macro would produce (the <c>result.ticket</c> of
/// <c>…/macros/{id}/apply</c>), for Preview_macro's change list and Apply_macro's update body.
/// </summary>
/// <remarks>
/// Unverified against the live API: exactly which keys the apply result's ticket carries. It is read
/// tolerantly — scalar fields, <c>tags</c> (the full new list) or <c>additional_tags</c>/<c>remove_tags</c>,
/// <c>custom_fields</c> or <c>fields</c> as <c>[{id, value}]</c>, and <c>email_cc_ids</c>,
/// <c>follower_ids</c> and <c>collaborator_ids</c> — and only fields that differ from the current
/// ticket are written back, so a full ticket echoed back changes nothing it does not mean to.
/// </remarks>
public static class MacroTicketDiff
{
    private static readonly string[] ScalarFields =
        ["status", "custom_status_id", "priority", "type", "subject", "group_id", "assignee_id", "ticket_form_id", "brand_id", "problem_id", "due_at"];

    /// <summary>The changes (for the person) and the update (for Zendesk).</summary>
    public sealed record Result(JsonArray Changes, JsonObject Update, bool AddsCcs);

    public static Result Compute(JsonNode current, JsonNode? applied)
    {
        var changes = new JsonArray();
        var update = new JsonObject();
        var addsCcs = false;
        if (applied is not JsonObject after)
        {
            return new Result(changes, update, false);
        }

        foreach (var field in ScalarFields)
        {
            if (after.TryGetPropertyValue(field, out var value)
                && MacroSupport.ScalarText(value) != MacroSupport.ScalarText(current[field]))
            {
                changes.Add(new JsonObject { ["field"] = field, ["before"] = current[field]?.DeepClone(), ["after"] = value?.DeepClone() });
                update[field] = value?.DeepClone();
            }
        }

        var currentTags = Strings(current["tags"]);
        var newTags = new HashSet<string>(currentTags, StringComparer.Ordinal);
        if (after["tags"] is JsonArray tags)
        {
            newTags = Strings(tags);
        }

        newTags.UnionWith(Strings(after["additional_tags"] ?? after["current_tags"]));
        newTags.ExceptWith(Strings(after["remove_tags"]));
        if (!newTags.SetEquals(currentTags))
        {
            var added = newTags.Except(currentTags).Order(StringComparer.Ordinal).ToList();
            var removed = currentTags.Except(newTags).Order(StringComparer.Ordinal).ToList();
            changes.Add(new JsonObject
            {
                ["field"] = "tags",
                ["before"] = Array(currentTags.Order(StringComparer.Ordinal)),
                ["after"] = Array(newTags.Order(StringComparer.Ordinal)),
                ["added"] = Array(added),
                ["removed"] = Array(removed),
            });
            update["tags"] = Array(newTags.Order(StringComparer.Ordinal));
        }

        var currentFields = new Dictionary<long, JsonNode?>();
        foreach (var field in current["custom_fields"] as JsonArray ?? [])
        {
            if (ToolJson.Long(field?["id"]) is { } id)
            {
                currentFields[id] = field?["value"];
            }
        }

        var fieldUpdates = new JsonArray();
        foreach (var field in after["custom_fields"] as JsonArray ?? after["fields"] as JsonArray ?? [])
        {
            if (ToolJson.Long(field?["id"]) is not { } id)
            {
                continue;
            }

            var before = currentFields.GetValueOrDefault(id);
            var value = field?["value"];
            if (Text(before) != Text(value))
            {
                changes.Add(new JsonObject
                {
                    ["field"] = "custom_field_" + id.ToString(CultureInfo.InvariantCulture),
                    ["before"] = before?.DeepClone(),
                    ["after"] = value?.DeepClone(),
                });
                fieldUpdates.Add(new JsonObject { ["id"] = id, ["value"] = value?.DeepClone() });
            }
        }

        if (fieldUpdates.Count > 0)
        {
            update["custom_fields"] = fieldUpdates;
        }

        addsCcs |= People(current, after, "email_cc_ids", "email_ccs", changes, update, list => list.Select(id => (JsonNode)new JsonObject { ["user_id"] = id, ["action"] = "put" }));
        People(current, after, "follower_ids", "followers", changes, update, list => list.Select(id => (JsonNode)new JsonObject { ["user_id"] = id, ["action"] = "put" }));
        addsCcs |= People(current, after, "collaborator_ids", "additional_collaborators", changes, update, list => list.Select(id => (JsonNode)id));

        return new Result(changes, update, addsCcs);
    }

    /// <summary>Adds group and assignee names to their changes (one read each at most); names are a nicety.</summary>
    public static async Task NameAsync(ZendeskService zendesk, JsonArray changes, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var change in changes.OfType<JsonObject>().Where(c => ToolJson.String(c["field"]) == "group_id"))
            {
                foreach (var side in new[] { "before", "after" })
                {
                    if (ToolJson.Long(change[side]) is { } groupId)
                    {
                        var group = await zendesk.GetAsync($"/api/v2/groups/{groupId.ToString(CultureInfo.InvariantCulture)}", cancellationToken: cancellationToken);
                        change[side + "_name"] = group["group"]?["name"]?.DeepClone();
                    }
                }
            }

            var assignees = changes.OfType<JsonObject>().Where(c => ToolJson.String(c["field"]) == "assignee_id").ToList();
            var ids = assignees.SelectMany(c => new[] { ToolJson.Long(c["before"]), ToolJson.Long(c["after"]) }).OfType<long>().Distinct().ToList();
            if (ids.Count > 0)
            {
                var users = await zendesk.GetAsync("/api/v2/users/show_many", ToolJson.Query(("ids", ToolJson.Ids(ids))), cancellationToken: cancellationToken);
                var names = (users["users"] as JsonArray ?? []).Where(u => ToolJson.Long(u?["id"]) is not null)
                    .ToDictionary(u => ToolJson.Long(u!["id"])!.Value, u => ToolJson.String(u!["name"]));
                foreach (var change in assignees)
                {
                    foreach (var side in new[] { "before", "after" })
                    {
                        if (ToolJson.Long(change[side]) is { } userId && names.TryGetValue(userId, out var name))
                        {
                            change[side + "_name"] = name;
                        }
                    }
                }
            }
        }
        catch (HttpRequestException)
        {
            // The ids are already in the changes.
        }
    }

    private static bool People(JsonNode current, JsonObject after, string key, string updateKey, JsonArray changes, JsonObject update, Func<IEnumerable<long>, IEnumerable<JsonNode>> toUpdate)
    {
        if (after[key] is not JsonArray list)
        {
            return false;
        }

        var before = (current[key] as JsonArray ?? []).Select(ToolJson.Long).OfType<long>().ToHashSet();
        var added = list.Select(ToolJson.Long).OfType<long>().Where(id => !before.Contains(id)).Distinct().ToList();
        if (added.Count == 0)
        {
            return false;
        }

        changes.Add(new JsonObject
        {
            ["field"] = key,
            ["before"] = Array(before.Order().Select(id => id.ToString(CultureInfo.InvariantCulture))),
            ["after"] = Array(before.Concat(added).Order().Select(id => id.ToString(CultureInfo.InvariantCulture))),
            ["added"] = new JsonArray(added.Select(id => (JsonNode)id).ToArray()),
        });
        update[updateKey] = new JsonArray(toUpdate(added).ToArray());
        return true;
    }

    private static HashSet<string> Strings(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(ToolJson.String).OfType<string>().ToHashSet(StringComparer.Ordinal),
        JsonValue v when v.TryGetValue<string>(out var s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal),
        _ => new HashSet<string>(StringComparer.Ordinal),
    };

    private static string? Text(JsonNode? node) => node is JsonArray array
        ? string.Join(",", array.Select(MacroSupport.ScalarText).Order(StringComparer.Ordinal))
        : MacroSupport.ScalarText(node);

    private static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());
}
