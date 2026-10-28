using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Tools;

/// <summary>Finding Zendesk users — customers and agents — and reading their records.</summary>
[McpServerToolType]
public static class UsersTools
{
    private const int MaxWithOrganizations = 25;
    private static readonly string[] Roles = ["end-user", "agent", "admin"];

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "Search_users", Title = "Search users", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Find Zendesk users (customers and agents) and read their records: id, name, email, role, external_id, default organization_id, tags, active, suspended, verified, phone, time_zone, locale, user_fields (user custom fields), timestamps and an agent URL. Filter by user_ids (up to 100) or external_ids (up to 100); or by email, name, role and/or tags together (e.g. role='end-user', tags='vip' for end users tagged vip, or tags='6281234567890' for a user whose mobile number is stored as a tag); or by organization_id to list an organisation's members. It takes no other arguments (there is no free-text query). Set include_fields=['organizations'] to add every organisation each user belongs to, with its id, name, external_id and whether it is the user's default (page_size is then capped at 25). Results are paginated: when has_more is true, call again with the returned next_cursor. To get the requesters of a set of tickets, prefer Search_tickets or Get_ticket with include=['users'].")]
    public static async Task<string> SearchUsers(
        ZendeskService zendesk,
        [Description("User IDs to fetch, up to 100. Returns not_found for IDs that matched nothing.")] long[]? user_ids = null,
        [Description("User external IDs to fetch (exact match), up to 100.")] string[]? external_ids = null,
        [Description("Email address to match. Can be combined with name, role and tags.")] string? email = null,
        [Description("User name to match (Zendesk search on the name). Can be combined with email, role and tags.")] string? name = null,
        [Description("Only users with this role: end-user, agent or admin. Can be combined with email, name and tags.")] string? role = null,
        [Description("Comma-separated user tags, as a string; returns users that have any of them (e.g. 'vip' or '6281234567890'). A phone number goes in as digits only, without spaces, dashes or brackets: '+62 812-3456-7890' is '6281234567890'. When it is not clear how the number is stored (country code, leading 0, a prefix), '*' and its last digits finds it, e.g. '*81234567890'. Send a number as a string: as a JSON number it loses a leading zero. Case does not matter, and characters Zendesk drops from tags (+ # @ ? & ' and similar) are ignored. One '*' at the start or end matches part of a tag, with at least 2 other characters. Only the user's own tags count, not their organisation's. Can be combined with email, name and role.")] string? tags = null,
        [Description("List the members of this organisation.")] long? organization_id = null,
        [Description("'organizations' adds each user's organisation memberships. Costs one Zendesk request per user, so page_size is capped at 25.")] string[]? include_fields = null,
        [Description("Number of results per page (1-100, default: 100; at most 25 with include_fields=['organizations']). Ignored for user_ids and external_ids, which return every match.")] int? page_size = null,
        [Description("Cursor for pagination. Pass the next_cursor value from a previous Search_users response, with the same filters.")] string? after_cursor = null,
        CancellationToken cancellationToken = default)
    {
        var withOrganizations = false;
        foreach (var field in include_fields ?? [])
        {
            withOrganizations |= field?.Trim().ToLowerInvariant() == "organizations"
                ? true
                : throw new ArgumentException($"include_fields accepts only 'organizations' (got '{field}').");
        }

        var size = ToolJson.PageSize(page_size, 100);
        if (withOrganizations)
        {
            size = Math.Min(size, MaxWithOrganizations);
        }

        if (email is { Length: > 254 } || (email is not null && email.Any(c => char.IsWhiteSpace(c) || c == '"')))
        {
            throw new ArgumentException("email must be at most 254 characters without spaces or quotes.");
        }

        if (name is { Length: > 200 })
        {
            throw new ArgumentException("name is limited to 200 characters.");
        }

        var roleValue = role is null ? null : Roles.Contains(role.Trim().ToLowerInvariant())
            ? role.Trim().ToLowerInvariant()
            : throw new ArgumentException($"role must be one of {string.Join(", ", Roles)} (got '{role}').");
        var tagTerms = PeopleSupport.TagTerms(tags);
        var searching = !string.IsNullOrWhiteSpace(email) || !string.IsNullOrWhiteSpace(name) || roleValue is not null || tagTerms.Count > 0;

        var modes = new[] { user_ids is { Length: > 0 }, external_ids is { Length: > 0 }, searching, organization_id is not null }.Count(m => m);
        if (modes == 0)
        {
            throw new ArgumentException("Pass a filter: user_ids, external_ids, organization_id, or any of email, name, role and tags.");
        }

        if (modes > 1)
        {
            throw new ArgumentException("Pass one kind of filter: user_ids, external_ids, organization_id, or email/name/role/tags (these four combine).");
        }

        var output = new JsonObject();
        List<JsonNode> users;
        if (user_ids is { Length: > 0 })
        {
            var ids = PeopleSupport.IdList(user_ids, "user_ids", withOrganizations ? MaxWithOrganizations : 100);
            var page = await zendesk.GetAsync("/api/v2/users/show_many", ToolJson.Query(("ids", ToolJson.Ids(ids))), cancellationToken: cancellationToken);
            users = (page["users"] as JsonArray ?? []).OfType<JsonNode>().ToList();
            var returned = users.Select(u => ToolJson.Long(u["id"])).OfType<long>().ToHashSet();
            output["not_found"] = new JsonArray(ids.Where(id => !returned.Contains(id)).Select(id => (JsonNode)id).ToArray());
            output["pagination_info"] = PeopleSupport.NoMorePages(users.Count);
        }
        else if (external_ids is { Length: > 0 })
        {
            var ids = PeopleSupport.ExternalIdList(external_ids, "external_ids");
            if (withOrganizations && ids.Count > MaxWithOrganizations)
            {
                throw new ArgumentException($"With include_fields=['organizations'], external_ids is limited to {MaxWithOrganizations} values.");
            }

            var page = await zendesk.GetAsync("/api/v2/users/show_many", ToolJson.Query(("external_ids", string.Join(",", ids))), cancellationToken: cancellationToken);
            users = (page["users"] as JsonArray ?? []).OfType<JsonNode>().ToList();
            var returned = users.Select(u => ToolJson.String(u["external_id"])).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            output["not_found"] = new JsonArray(ids.Where(id => !returned.Contains(id)).Select(id => (JsonNode)id).ToArray());
            output["pagination_info"] = PeopleSupport.NoMorePages(users.Count);
        }
        else if (organization_id is { } orgId)
        {
            ToolJson.PositiveId(orgId, "organization_id");
            var page = await zendesk.GetAsync($"/api/v2/organizations/{orgId.ToString(CultureInfo.InvariantCulture)}/users",
                ToolJson.Query(("page[size]", size), ("page[after]", after_cursor)), cancellationToken: cancellationToken);
            users = (page["users"] as JsonArray ?? []).OfType<JsonNode>().ToList();
            output["pagination_info"] = ToolJson.PaginationInfo(page, size);
        }
        else
        {
            var terms = new List<string> { "type:user" };
            if (!string.IsNullOrWhiteSpace(email))
            {
                terms.Add("email:" + PeopleSupport.Quoted(email));
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                terms.Add("name:" + PeopleSupport.Quoted(name));
            }

            if (roleValue is not null)
            {
                terms.Add("role:" + roleValue);
            }

            // Unverified against the live API: repeated tags: terms match users with ANY of the tags
            // (Zendesk ORs repeated values of one keyword), and a leading '*' wildcard is honoured.
            terms.AddRange(tagTerms);
            var query = string.Join(' ', terms);
            var page = await zendesk.GetAsync("/api/v2/search/export",
                ToolJson.Query(("query", query), ("filter[type]", "user"), ("page[size]", size), ("page[after]", after_cursor)), cancellationToken: cancellationToken);
            users = (page["results"] as JsonArray ?? []).OfType<JsonNode>().ToList();
            output["query"] = query;
            output["pagination_info"] = ToolJson.PaginationInfo(page, size);
        }

        var shaped = new JsonArray();
        var organizations = withOrganizations ? await MembershipsAsync(zendesk, users, cancellationToken) : null;
        foreach (var user in users)
        {
            var item = PeopleSupport.ShapeUser(user, zendesk.BaseUrl);
            if (organizations is not null && ToolJson.Long(user["id"]) is { } id)
            {
                item["organizations"] = organizations.TryGetValue(id, out var list) ? list : new JsonArray();
            }

            shaped.Add(item);
        }

        var result = new JsonObject { ["users"] = shaped };
        foreach (var (key, value) in output.ToList())
        {
            output.Remove(key);
            result[key] = value;
        }

        return result.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "Find_agent_by_name", Title = "Find agent by name", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("Find a Zendesk agent (or admin) by their full name, using Zendesk search with role filtering. Returns each matching agent's id, name, email, role, active and suspended, exact name matches first. To look up any user, customers included, by email, name, external ID, ID or organisation, use Search_users.")]
    public static async Task<string> FindAgentByName(
        ZendeskService zendesk,
        [Description("The full name of the agent to search for. Required, max 200 characters (e.g. 'John Doe').")] string full_name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(full_name) || full_name.Length > 200)
        {
            throw new ArgumentException("full_name is required and limited to 200 characters.");
        }

        var wanted = full_name.Trim();
        var agents = new Dictionary<long, JsonObject>();

        // Two searches rather than one with -role:end-user: a role: term is the documented way to
        // narrow a user search, and both agent roles must be covered.
        foreach (var role in new[] { "agent", "admin" })
        {
            var query = $"type:user role:{role} name:{PeopleSupport.Quoted(wanted)}";
            var page = await zendesk.GetAsync("/api/v2/search", ToolJson.Query(("query", query)), cancellationToken: cancellationToken);
            foreach (var user in page["results"] as JsonArray ?? [])
            {
                if (user is not null && ToolJson.Long(user["id"]) is { } id && ToolJson.String(user["role"]) is "agent" or "admin")
                {
                    agents.TryAdd(id, ToolJson.Pick(user, "id", "name", "email", "role", "active", "suspended"));
                }
            }
        }

        var ordered = agents.Values
            .OrderByDescending(a => string.Equals(ToolJson.String(a["name"])?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
            .ThenBy(a => ToolJson.String(a["name"]), StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new JsonObject
        {
            ["agents"] = new JsonArray(ordered.Select(a => (JsonNode)a).ToArray()),
            ["count"] = ordered.Count,
            ["exact_match"] = ordered.Any(a => string.Equals(ToolJson.String(a["name"])?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)),
        }.ToJsonString();
    }

    // Each user's organisation memberships, with each organisation's name and external id (one
    // memberships read per user, then one organisations read for all of them).
    private static async Task<Dictionary<long, JsonArray>> MembershipsAsync(ZendeskService zendesk, List<JsonNode> users, CancellationToken cancellationToken)
    {
        var memberships = new Dictionary<long, List<JsonNode>>();
        foreach (var user in users)
        {
            if (ToolJson.Long(user["id"]) is not { } userId)
            {
                continue;
            }

            var page = await zendesk.GetAllPagesAsync($"/api/v2/users/{userId.ToString(CultureInfo.InvariantCulture)}/organization_memberships",
                null, "organization_memberships", maxItems: 300, cancellationToken: cancellationToken);
            memberships[userId] = page.Items.ToList();
        }

        var orgIds = memberships.Values.SelectMany(m => m).Select(m => ToolJson.Long(m["organization_id"])).OfType<long>().Distinct().ToList();
        var organizations = new Dictionary<long, JsonNode>();
        foreach (var chunk in orgIds.Chunk(100))
        {
            var page = await zendesk.GetAsync("/api/v2/organizations/show_many", ToolJson.Query(("ids", ToolJson.Ids(chunk))), cancellationToken: cancellationToken);
            foreach (var org in page["organizations"] as JsonArray ?? [])
            {
                if (org is not null && ToolJson.Long(org["id"]) is { } id)
                {
                    organizations[id] = org;
                }
            }
        }

        var result = new Dictionary<long, JsonArray>();
        foreach (var (userId, list) in memberships)
        {
            var array = new JsonArray();
            foreach (var membership in list)
            {
                var orgId = ToolJson.Long(membership["organization_id"]);
                var org = orgId is { } o ? organizations.GetValueOrDefault(o) : null;
                array.Add(new JsonObject
                {
                    ["id"] = orgId,
                    ["name"] = org?["name"]?.DeepClone() ?? membership["organization_name"]?.DeepClone(),
                    ["external_id"] = org?["external_id"]?.DeepClone(),
                    ["is_default"] = MacroSupport.ParseBool(membership["default"]) == true,
                    ["membership_id"] = membership["id"]?.DeepClone(),
                });
            }

            result[userId] = array;
        }

        return result;
    }
}
