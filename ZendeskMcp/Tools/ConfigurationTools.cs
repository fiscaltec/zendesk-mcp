using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Tools;

/// <summary>
/// The account's configuration, read-only: ticket fields (with custom statuses), ticket forms,
/// triggers and brands. Other tools point the model here to resolve ids.
/// </summary>
[McpServerToolType]
public static class ConfigurationTools
{
    private static readonly string[] TicketFieldFields =
    [
        "id", "type", "title", "raw_title", "description", "agent_description", "active", "required", "required_in_portal",
        "visible_in_portal", "editable_in_portal", "title_in_portal", "position", "removable", "tag", "key",
        "custom_field_options", "system_field_options", "sub_type_id", "regexp_for_validation", "relationship_target_type",
        "created_at", "updated_at",
    ];

    private static readonly string[] TriggerSorts = ["alphabetical", "created_at", "updated_at", "position"];

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "List_ticket_fields", Title = "List ticket fields", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("List ticket fields for the Zendesk account: both system and custom ticket fields with their configuration (type, title, whether required, the options of drop-down fields with their tags). Useful for understanding ticket structure and finding custom field ids for Search_tickets and Update_ticket. The Status field carries custom_statuses: each status's id, agent_label, status_category and active, which is where custom_status_id values for other tools come from. Results are paginated: when has_more is true, call again with the returned next_cursor to get subsequent pages.")]
    public static async Task<string> ListTicketFields(
        ZendeskService zendesk,
        [Description("Number of results per page (1-100, default: 100). When has_more is true in the response, call again with the returned next_cursor to get the next page.")] int? page_size = null,
        [Description("Cursor for pagination. Pass the next_cursor value from a previous List_ticket_fields response to fetch the next page of results.")] string? after_cursor = null,
        CancellationToken cancellationToken = default)
    {
        var size = ToolJson.PageSize(page_size, 100);
        var page = await zendesk.GetAsync("/api/v2/ticket_fields", ToolJson.Query(("page[size]", size), ("page[after]", after_cursor)), cancellationToken: cancellationToken);
        var fields = new JsonArray();
        JsonArray? statuses = null;
        foreach (var field in page["ticket_fields"] as JsonArray ?? [])
        {
            if (field is null)
            {
                continue;
            }

            var shaped = ToolJson.Pick(field, TicketFieldFields);
            if (ToolJson.String(field["type"]) is "status" or "custom_status")
            {
                statuses ??= await CustomStatusesAsync(zendesk, cancellationToken);
                shaped["custom_statuses"] = statuses.DeepClone();
            }

            fields.Add(shaped);
        }

        return new JsonObject
        {
            ["ticket_fields"] = fields,
            ["pagination_info"] = ToolJson.PaginationInfo(page, size),
        }.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "List_ticket_forms", Title = "List ticket forms", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("List ticket forms for the Zendesk account. Ticket forms let admins define a subset of ticket fields for display to agents and end users. Returns each form's id, name, display_name, active, default, end_user_visible, position, ticket_field_ids, the brands it is restricted to and its field conditions.")]
    public static async Task<string> ListTicketForms(
        ZendeskService zendesk,
        [Description("If true, returns only active ticket forms; if false, returns only inactive forms. Default: true.")] bool? active = null,
        CancellationToken cancellationToken = default)
    {
        var page = await zendesk.GetAsync("/api/v2/ticket_forms", ToolJson.Query(("active", active ?? true)), cancellationToken: cancellationToken);
        var forms = new JsonArray();
        foreach (var form in page["ticket_forms"] as JsonArray ?? [])
        {
            forms.Add(ToolJson.Pick(form, "id", "name", "display_name", "raw_name", "active", "default", "end_user_visible", "position",
                "ticket_field_ids", "in_all_brands", "restricted_brand_ids", "agent_conditions", "end_user_conditions", "created_at", "updated_at"));
        }

        return new JsonObject { ["ticket_forms"] = forms, ["count"] = forms.Count }.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "List_triggers", Title = "List triggers", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("List ticket triggers for the Zendesk account with usage statistics. Returns each trigger's id, title, description, active, position, category_id, conditions (all/any), actions, usage counts for the last 1h/24h/7d/30d (null when the Zendesk plan has no usage data), timestamps and an admin url. Results are paginated: when has_more is true, call again with the returned next_cursor to get subsequent pages.")]
    public static async Task<string> ListTriggers(
        ZendeskService zendesk,
        [Description("Filter by active triggers (true) or inactive triggers (false). If omitted, returns all triggers.")] bool? active = null,
        [Description("Filter triggers by category ID.")] string? category_id = null,
        [Description("Field to sort results by: alphabetical, created_at, updated_at or position. Default is 'position'.")] string? sort = null,
        [Description("Sort order: asc or desc. Default is 'asc' for alphabetical and position, 'desc' for others.")] string? sort_order = null,
        [Description("Number of results per page (1-100, default: 100). When has_more is true in the response, call again with the returned next_cursor to get the next page.")] int? page_size = null,
        [Description("Cursor for pagination. Pass the next_cursor value from a previous List_triggers response to fetch the next page of results.")] string? after_cursor = null,
        CancellationToken cancellationToken = default)
    {
        var size = ToolJson.PageSize(page_size, 100);
        var sortBy = sort?.Trim().ToLowerInvariant();
        if (sortBy is not null && !TriggerSorts.Contains(sortBy))
        {
            throw new ArgumentException($"sort must be one of {string.Join(", ", TriggerSorts)} (got '{sort}').");
        }

        var order = sort_order?.Trim().ToLowerInvariant();
        if (order is not null and not ("asc" or "desc"))
        {
            throw new ArgumentException($"sort_order must be asc or desc (got '{sort_order}').");
        }

        var category = category_id?.Trim();
        if (category is not null && (category.Length is 0 or > 64 || !category.All(char.IsAsciiLetterOrDigit)))
        {
            throw new ArgumentException($"category_id must be a trigger category id (letters and digits, got '{category_id}').");
        }

        // Zendesk names the sort parameter sort_by; this tool's parameter keeps the name 'sort'.
        var page = await zendesk.GetAsync("/api/v2/triggers", ToolJson.Query(
            ("active", active),
            ("category_id", category),
            ("sort_by", sortBy),
            ("sort_order", order),
            ("include", MacroSupport.UsageInclude),
            ("page[size]", size),
            ("page[after]", after_cursor)), cancellationToken: cancellationToken);

        var triggers = new JsonArray();
        foreach (var trigger in page["triggers"] as JsonArray ?? [])
        {
            if (trigger is null)
            {
                continue;
            }

            var shaped = ToolJson.Pick(trigger, "id", "title", "description", "active", "position", "category_id", "default", "conditions", "actions", "created_at", "updated_at");
            shaped["usage"] = MacroSupport.Usage(trigger);
            if (ToolJson.Long(trigger["id"]) is { } id)
            {
                shaped["url"] = $"{zendesk.BaseUrl}/admin/objects-rules/rules/triggers/{id.ToString(CultureInfo.InvariantCulture)}";
            }

            triggers.Add(shaped);
        }

        return new JsonObject
        {
            ["triggers"] = triggers,
            ["pagination_info"] = ToolJson.PaginationInfo(page, size),
        }.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "List_brands", Title = "List brands", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false),
     Description("List brands for the Zendesk account. Brands are customer-facing identities that might represent multiple products, services, or literal brands. Returns each brand's id, name, subdomain, brand_url (the value Help Center tools take as brand_url), host_mapping, active, default, has_help_center and help_center_state. Results are paginated: when has_more is true, call again with the returned next_cursor to get subsequent pages.")]
    public static async Task<string> ListBrands(
        ZendeskService zendesk,
        [Description("Number of results per page (1-100, default: 100). When has_more is true in the response, call again with the returned next_cursor to get the next page.")] int? page_size = null,
        [Description("Cursor for pagination. Pass the next_cursor value from a previous List_brands response to fetch the next page of results.")] string? after_cursor = null,
        CancellationToken cancellationToken = default)
    {
        var size = ToolJson.PageSize(page_size, 100);
        var page = await zendesk.GetAsync("/api/v2/brands", ToolJson.Query(("page[size]", size), ("page[after]", after_cursor)), cancellationToken: cancellationToken);
        var brands = new JsonArray();
        foreach (var brand in page["brands"] as JsonArray ?? [])
        {
            brands.Add(ToolJson.Pick(brand, "id", "name", "subdomain", "brand_url", "host_mapping", "active", "default", "has_help_center", "help_center_state"));
        }

        return new JsonObject
        {
            ["brands"] = brands,
            ["pagination_info"] = ToolJson.PaginationInfo(page, size),
        }.ToJsonString();
    }

    // The account's custom statuses, which the Status field's values are on accounts that use them.
    private static async Task<JsonArray> CustomStatusesAsync(ZendeskService zendesk, CancellationToken cancellationToken)
    {
        var response = await zendesk.GetAsync("/api/v2/custom_statuses", cancellationToken: cancellationToken);
        var statuses = new JsonArray();
        foreach (var status in response["custom_statuses"] as JsonArray ?? [])
        {
            statuses.Add(ToolJson.Pick(status, "id", "agent_label", "end_user_label", "status_category", "active", "default"));
        }

        return statuses;
    }
}
