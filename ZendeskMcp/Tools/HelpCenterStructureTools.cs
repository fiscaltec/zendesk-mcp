using System.ComponentModel;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Tools;

/// <summary>
/// The Help Center's structure — categories and sections — and the two access lists articles refer
/// to: user segments (who can view) and permission groups (who can manage). Every <c>brand_url</c> is
/// passed to <see cref="ZendeskService"/> as <c>brandUrl</c>, which accepts only the account's brands.
/// </summary>
[McpServerToolType]
public static class HelpCenterStructureTools
{
    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "List_help_center_categories", Title = "List Help Center categories", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "List Help Center categories, optionally in one locale and sorted. Returns each category's id, name, description, locale, position and html_url. " +
        "Results are paginated: when pagination_info.has_more is true, call again with after_cursor set to pagination_info.next_cursor. " +
        "For multi-brand accounts, pass brand_url to list a non-default brand's categories.")]
    public static async Task<string> ListHelpCenterCategories(
        ZendeskService zendesk,
        [Description("Locale code (e.g. 'en-us', 'fr', 'de-de'). If omitted, the Help Center's default locale is used.")] string? locale = null,
        [Description("Field to sort by: 'position' (default, the manual order), 'created_at' or 'updated_at'.")] string? sort_by = null,
        [Description("Sort order: 'asc' (default) or 'desc'.")] string? sort_order = null,
        [Description("Results per page (1-100, default 100).")] int? page_size = null,
        [Description("Cursor for the next page: pagination_info.next_cursor from a previous List_help_center_categories response.")] string? after_cursor = null,
        [Description(HelpCenterJson.BrandUrlDescription)] string? brand_url = null,
        CancellationToken cancellationToken = default)
    {
        var validLocale = HelpCenterJson.Locale(locale);
        var pageSize = ToolJson.PageSize(page_size);
        var query = HelpCenterJson.PageQuery(pageSize, after_cursor,
            ("sort_by", HelpCenterJson.SortBy(sort_by)),
            ("sort_order", HelpCenterJson.SortOrder(sort_order)));

        var page = await zendesk.GetAsync($"{HelpCenterJson.Root(validLocale)}/categories", query, brand_url, cancellationToken);
        return Listing(page, "categories", isSection: false, pageSize);
    }

    [Authorize(Policy = "zendesk:write")]
    [McpServerTool(Name = "Create_help_center_category", Title = "Create Help Center category", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Create a Help Center category. Categories have no draft state: they exist in the Help Center as soon as they are created, although Help Center themes usually hide categories that contain no published articles. " +
        "Creating categories requires the connected Zendesk user to be a Help Center manager. Use List_help_center_categories first to see the existing categories and their positions. " +
        "For multi-brand accounts, pass brand_url to create the category in a non-default brand's Help Center. " + HelpCenterJson.UntrustedContentWarning)]
    public static async Task<string> CreateHelpCenterCategory(
        ZendeskService zendesk,
        [Description("Name of the category. Max 255 characters.")] string name,
        [Description("Locale code the category is created in (e.g. 'en-us', 'fr').")] string locale,
        [Description("Optional description. Max 10,000 characters.")] string? description = null,
        [Description("Position among categories; lower numbers appear first. Only affects ordering when the Help Center uses manual sorting.")] int? position = null,
        [Description(HelpCenterJson.BrandUrlDescription)] string? brand_url = null,
        CancellationToken cancellationToken = default)
    {
        var category = new JsonObject
        {
            ["name"] = HelpCenterJson.RequiredText(name, "name", HelpCenterJson.MaxNameLength),
            ["locale"] = HelpCenterJson.RequiredLocale(locale),
        };
        AddOptional(category, description, position);

        var response = await zendesk.PostAsync("/api/v2/help_center/categories", new JsonObject { ["category"] = category }, brandUrl: brand_url, cancellationToken: cancellationToken);
        return HelpCenterJson.Container(response["category"], isSection: false).ToJsonString();
    }

    [Authorize(Policy = "zendesk:write")]
    [McpServerTool(Name = "Update_help_center_category", Title = "Update Help Center category", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description(
        "Update a Help Center category: change its position (to reorder categories) and/or its name or description in one locale. Provide at least one of position, name or description; locale is required with name or description. " +
        "To reorder several categories, call this once per category with its new position. Name and description changes update only that locale's translation. " +
        "Requires the connected Zendesk user to be a Help Center manager. For multi-brand accounts, pass brand_url for a non-default brand's category. " + HelpCenterJson.UntrustedContentWarning)]
    public static async Task<string> UpdateHelpCenterCategory(
        ZendeskService zendesk,
        [Description("The id of the category to update. Use List_help_center_categories to find it.")] long category_id,
        [Description("New position among categories; lower numbers appear first. Only affects ordering when the Help Center uses manual sorting.")] int? position = null,
        [Description("New name in the given locale. Max 255 characters.")] string? name = null,
        [Description("New description in the given locale. Max 10,000 characters.")] string? description = null,
        [Description("Locale of the translation to change (e.g. 'en-us'). Required when name or description is given.")] string? locale = null,
        [Description(HelpCenterJson.BrandUrlDescription)] string? brand_url = null,
        CancellationToken cancellationToken = default)
    {
        ToolJson.PositiveId(category_id, "category_id");
        var translation = TranslationChange(name, description, locale, out var validLocale);
        HelpCenterJson.Position(position);
        if (position is null && translation is null)
        {
            throw new ArgumentException("Provide at least one of position, name or description.");
        }

        var path = $"/api/v2/help_center/categories/{HelpCenterJson.Id(category_id)}";
        var result = new JsonObject { ["category_id"] = category_id };
        if (position is not null)
        {
            var response = await zendesk.PutAsync(path, new JsonObject { ["category"] = new JsonObject { ["position"] = position } }, brandUrl: brand_url, cancellationToken: cancellationToken);
            result["category"] = HelpCenterJson.Container(response["category"], isSection: false);
        }

        if (translation is not null)
        {
            var response = await zendesk.PutAsync($"{path}/translations/{ZendeskService.Segment(validLocale!)}", new JsonObject { ["translation"] = translation }, brandUrl: brand_url, cancellationToken: cancellationToken);
            result["translation"] = ToolJson.Pick(response["translation"], "id", "locale", "title", "body", "updated_at");
        }

        return result.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "List_help_center_sections", Title = "List Help Center sections", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "List Help Center sections, optionally in one locale, within one category, and sorted. Returns each section's id, name, description, locale, category_id, position and html_url. " +
        "Results are paginated: when pagination_info.has_more is true, call again with after_cursor set to pagination_info.next_cursor. " +
        "For multi-brand accounts, pass brand_url to list a non-default brand's sections.")]
    public static async Task<string> ListHelpCenterSections(
        ZendeskService zendesk,
        [Description("Locale code (e.g. 'en-us', 'fr', 'de-de'). If omitted, the Help Center's default locale is used. Required with category_id.")] string? locale = null,
        [Description("Only list the sections in this category. Requires locale.")] long? category_id = null,
        [Description("Field to sort by: 'position' (default, the manual order), 'created_at' or 'updated_at'.")] string? sort_by = null,
        [Description("Sort order: 'asc' (default) or 'desc'.")] string? sort_order = null,
        [Description("Results per page (1-100, default 100).")] int? page_size = null,
        [Description("Cursor for the next page: pagination_info.next_cursor from a previous List_help_center_sections response.")] string? after_cursor = null,
        [Description(HelpCenterJson.BrandUrlDescription)] string? brand_url = null,
        CancellationToken cancellationToken = default)
    {
        var validLocale = HelpCenterJson.Locale(locale);
        var pageSize = ToolJson.PageSize(page_size);
        var query = HelpCenterJson.PageQuery(pageSize, after_cursor,
            ("sort_by", HelpCenterJson.SortBy(sort_by)),
            ("sort_order", HelpCenterJson.SortOrder(sort_order)));

        string path;
        if (category_id is not null)
        {
            ToolJson.PositiveId(category_id.Value, "category_id");
            if (validLocale is null)
            {
                throw new ArgumentException("locale is required when category_id is given (e.g. 'en-us').");
            }

            path = $"{HelpCenterJson.Root(validLocale)}/categories/{HelpCenterJson.Id(category_id.Value)}/sections";
        }
        else
        {
            path = $"{HelpCenterJson.Root(validLocale)}/sections";
        }

        var page = await zendesk.GetAsync(path, query, brand_url, cancellationToken);
        return Listing(page, "sections", isSection: true, pageSize);
    }

    [Authorize(Policy = "zendesk:write")]
    [McpServerTool(Name = "Create_help_center_section", Title = "Create Help Center section", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Create a section inside a Help Center category. Sections have no draft state: they exist as soon as they are created, although Help Center themes usually hide sections that contain no published articles. " +
        "Use List_help_center_categories to find category_id and List_help_center_sections to see the existing sections and their positions. " +
        "For multi-brand accounts, pass brand_url for a non-default brand's category. " + HelpCenterJson.UntrustedContentWarning)]
    public static async Task<string> CreateHelpCenterSection(
        ZendeskService zendesk,
        [Description("The id of the category to create the section in.")] long category_id,
        [Description("Name of the section. Max 255 characters.")] string name,
        [Description("Locale code the section is created in (e.g. 'en-us', 'fr').")] string locale,
        [Description("Optional description. Max 10,000 characters.")] string? description = null,
        [Description("Position among the category's sections; lower numbers appear first. Only affects ordering when the Help Center uses manual sorting.")] int? position = null,
        [Description(HelpCenterJson.BrandUrlDescription)] string? brand_url = null,
        CancellationToken cancellationToken = default)
    {
        ToolJson.PositiveId(category_id, "category_id");
        var section = new JsonObject
        {
            ["name"] = HelpCenterJson.RequiredText(name, "name", HelpCenterJson.MaxNameLength),
            ["locale"] = HelpCenterJson.RequiredLocale(locale),
        };
        AddOptional(section, description, position);

        var response = await zendesk.PostAsync($"/api/v2/help_center/categories/{HelpCenterJson.Id(category_id)}/sections", new JsonObject { ["section"] = section }, brandUrl: brand_url, cancellationToken: cancellationToken);
        return HelpCenterJson.Container(response["section"], isSection: true).ToJsonString();
    }

    [Authorize(Policy = "zendesk:write")]
    [McpServerTool(Name = "Update_help_center_section", Title = "Update Help Center section", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description(
        "Update a Help Center section: change its position (to reorder sections), move it to another category, and/or change its name or description in one locale. Provide at least one of position, category_id, name or description; locale is required with name or description. " +
        "To reorder several sections, call this once per section with its new position. Name and description changes update only that locale's translation. " +
        "For multi-brand accounts, pass brand_url for a non-default brand's section. " + HelpCenterJson.UntrustedContentWarning)]
    public static async Task<string> UpdateHelpCenterSection(
        ZendeskService zendesk,
        [Description("The id of the section to update. Use List_help_center_sections to find it.")] long section_id,
        [Description("New position among the sections of its category; lower numbers appear first.")] int? position = null,
        [Description("Move the section to this category.")] long? category_id = null,
        [Description("New name in the given locale. Max 255 characters.")] string? name = null,
        [Description("New description in the given locale. Max 10,000 characters.")] string? description = null,
        [Description("Locale of the translation to change (e.g. 'en-us'). Required when name or description is given.")] string? locale = null,
        [Description(HelpCenterJson.BrandUrlDescription)] string? brand_url = null,
        CancellationToken cancellationToken = default)
    {
        ToolJson.PositiveId(section_id, "section_id");
        var translation = TranslationChange(name, description, locale, out var validLocale);
        HelpCenterJson.Position(position);
        if (category_id is not null)
        {
            ToolJson.PositiveId(category_id.Value, "category_id");
        }

        if (position is null && category_id is null && translation is null)
        {
            throw new ArgumentException("Provide at least one of position, category_id, name or description.");
        }

        var path = $"/api/v2/help_center/sections/{HelpCenterJson.Id(section_id)}";
        var result = new JsonObject { ["section_id"] = section_id };
        if (position is not null || category_id is not null)
        {
            var section = new JsonObject();
            if (position is not null)
            {
                section["position"] = position;
            }

            if (category_id is not null)
            {
                section["category_id"] = category_id;
            }

            var response = await zendesk.PutAsync(path, new JsonObject { ["section"] = section }, brandUrl: brand_url, cancellationToken: cancellationToken);
            result["section"] = HelpCenterJson.Container(response["section"], isSection: true);
        }

        if (translation is not null)
        {
            var response = await zendesk.PutAsync($"{path}/translations/{ZendeskService.Segment(validLocale!)}", new JsonObject { ["translation"] = translation }, brandUrl: brand_url, cancellationToken: cancellationToken);
            result["translation"] = ToolJson.Pick(response["translation"], "id", "locale", "title", "body", "updated_at");
        }

        return result.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "List_user_segments", Title = "List user segments", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "List Help Center user segments. User segments define who can view article content, by user type, tags, groups or organisations. Returns each segment's id, name, user_type and criteria. " +
        "Use this to find user_segment_id values for Create_help_center_article, and to resolve the user_segment_ids returned on articles.")]
    public static async Task<string> ListUserSegments(ZendeskService zendesk, CancellationToken cancellationToken = default)
    {
        // user_segments follows cursor pagination (confirmed against the live API).
        var pages = await zendesk.GetAllPagesAsync("/api/v2/help_center/user_segments", null, "user_segments", maxItems: 1000, cancellationToken: cancellationToken);
        var segments = new JsonArray();
        foreach (var segment in pages.Items)
        {
            segments.Add(ToolJson.Pick(segment, "id", "name", "user_type", "group_ids", "organization_ids", "tags", "or_tags", "built_in", "added_user_ids", "created_at", "updated_at"));
        }

        return new JsonObject { ["user_segments"] = segments, ["truncated"] = pages.HasMore }.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "List_permission_groups", Title = "List permission groups", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "List Help Center management permission groups. Permission groups define which agents can create, update and publish articles. Returns each group's id, name, and the user segment ids with edit and publish rights. " +
        "Use this to find permission_group_id values for Create_help_center_article.")]
    public static async Task<string> ListPermissionGroups(ZendeskService zendesk, CancellationToken cancellationToken = default)
    {
        var pages = await zendesk.GetAllPagesAsync("/api/v2/guide/permission_groups", null, "permission_groups", maxItems: 1000, cancellationToken: cancellationToken);
        var groups = new JsonArray();
        foreach (var group in pages.Items)
        {
            groups.Add(ToolJson.Pick(group, "id", "name", "edit", "publish", "built_in", "created_at", "updated_at"));
        }

        return new JsonObject { ["permission_groups"] = groups, ["truncated"] = pages.HasMore }.ToJsonString();
    }

    private static string Listing(JsonNode page, string property, bool isSection, int pageSize)
    {
        var items = new JsonArray();
        foreach (var item in page[property] as JsonArray ?? [])
        {
            items.Add(HelpCenterJson.Container(item, isSection));
        }

        return new JsonObject
        {
            [property] = items,
            ["pagination_info"] = ToolJson.PaginationInfo(page, pageSize),
        }.ToJsonString();
    }

    private static void AddOptional(JsonObject target, string? description, int? position)
    {
        if (HelpCenterJson.Text(description, "description", HelpCenterJson.MaxDescriptionLength) is { } text)
        {
            target["description"] = text;
        }

        if (HelpCenterJson.Position(position) is { } value)
        {
            target["position"] = value;
        }
    }

    // A category or section translation keeps the name in "title" and the description in "body".
    private static JsonObject? TranslationChange(string? name, string? description, string? locale, out string? validLocale)
    {
        validLocale = HelpCenterJson.Locale(locale);
        if (name is null && description is null)
        {
            return null;
        }

        if (validLocale is null)
        {
            throw new ArgumentException("locale is required when name or description is given (e.g. 'en-us').");
        }

        var translation = new JsonObject();
        if (name is not null)
        {
            translation["title"] = HelpCenterJson.RequiredText(name, "name", HelpCenterJson.MaxNameLength);
        }

        if (description is not null)
        {
            translation["body"] = HelpCenterJson.Text(description, "description", HelpCenterJson.MaxDescriptionLength);
        }

        return translation;
    }
}
