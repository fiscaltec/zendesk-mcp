using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace ZendeskMcp.Tools;

/// <summary>
/// Help Center (Guide) articles: search, read, export, create, and edit translations. Every call that
/// takes <c>brand_url</c> passes it to <see cref="ZendeskService"/> as <c>brandUrl</c>, which accepts only
/// this account's own brands — the base URL decides where the agent's Zendesk token is sent.
/// </summary>
[McpServerToolType]
public static partial class HelpCenterArticlesTools
{
    private const int MaxSearchPageSize = 50;

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "Search_help_center_articles", Title = "Search Help Center articles", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Search Help Center articles using Zendesk's Unified Search API. Returns matching articles with id, title, locale, html_url, brand_url and updated_at. Supports filtering by locale, brand, category and section. " +
        "This tool does NOT return the article body, nor who can view each article: for an article's audience (user_segment_ids) use Get_help_center_article, or Export_help_center_articles for a whole section. " +
        "For multi-brand accounts, pass each result's brand_url to Get_help_center_article / Export_help_center_articles to fetch articles from non-default brands. " +
        "Results are paginated: when pagination_info.has_more is true, call again with after_cursor set to pagination_info.next_cursor.")]
    public static async Task<string> SearchHelpCenterArticles(
        ZendeskService zendesk,
        [Description("REQUIRED. Comma-separated locale codes to search within (e.g. 'en-gb,en-us'). Must be locales enabled for the account's Help Centers.")] string locales,
        [Description("Search text to match against article content. Max 500 characters. Optional: if omitted, articles are returned in Zendesk's internal order.")] string? query = null,
        [Description("Comma-separated brand ids to limit the search to (e.g. '123,456'). If omitted, all brands are searched.")] string? brand_ids = null,
        [Description("Comma-separated category ids to limit the search to (e.g. '789,012').")] string? category_ids = null,
        [Description("Comma-separated section ids to limit the search to (e.g. '345,678').")] string? section_ids = null,
        [Description("Results per page (1-50, default 50).")] int? page_size = null,
        [Description("Cursor for the next page: pagination_info.next_cursor from a previous Search_help_center_articles response.")] string? after_cursor = null,
        CancellationToken cancellationToken = default)
    {
        var localeList = HelpCenterJson.LocaleList(locales, "locales");
        HelpCenterJson.Text(query, "query", 500);
        // Unified Search refuses a page size above 50 ("expected integer less than or equal to 50").
        var pageSize = ToolJson.PageSize(page_size, MaxSearchPageSize);
        if (pageSize > MaxSearchPageSize)
        {
            throw new ArgumentException($"page_size must be between 1 and {MaxSearchPageSize} (got {pageSize}).");
        }

        // Confirmed against the live API: filter[locales] is required, and it and the brand, category
        // and section filters take comma-separated lists. Each result carries only title, type
        // ('ARTICLE'), updated_at and url (the article's Help Center page), so id, locale, html_url and
        // brand_url are read from that url.
        var request = HelpCenterJson.PageQuery(pageSize, after_cursor,
            ("filter[locales]", localeList),
            ("query", string.IsNullOrWhiteSpace(query) ? null : query.Trim()),
            ("filter[brand_ids]", HelpCenterJson.IdList(brand_ids, "brand_ids")),
            ("filter[category_ids]", HelpCenterJson.IdList(category_ids, "category_ids")),
            ("filter[section_ids]", HelpCenterJson.IdList(section_ids, "section_ids")));

        var page = await zendesk.GetAsync("/api/v2/guide/search", request, cancellationToken: cancellationToken);

        var results = new JsonArray();
        foreach (var item in page["results"] as JsonArray ?? [])
        {
            // Unified Search can return other content types (posts, external records); only articles
            // are addressable by the other article tools.
            var type = ToolJson.String(item?["type"]);
            if (type is not null && !string.Equals(type, "article", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var htmlUrl = ToolJson.String(item?["html_url"]) ?? ToolJson.String(item?["url"]);
            var (id, locale) = ArticleUrlParts(htmlUrl);
            var shaped = new JsonObject
            {
                ["id"] = item?["id"]?.DeepClone() ?? (id is { } articleId ? JsonValue.Create(articleId) : null),
                ["title"] = item?["title"]?.DeepClone(),
                ["locale"] = item?["locale"]?.DeepClone() ?? (locale is null ? null : JsonValue.Create(locale)),
                ["html_url"] = htmlUrl,
                ["updated_at"] = item?["updated_at"]?.DeepClone(),
                ["brand_url"] = BrandUrlOf(item, htmlUrl),
            };
            results.Add(shaped);
        }

        return new JsonObject
        {
            ["articles"] = results,
            ["pagination_info"] = ToolJson.PaginationInfo(page, pageSize),
        }.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "Get_help_center_article", Title = "Get Help Center article", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Fetch one Help Center article by id with its full HTML body, plus section_name, category_name and author_name. " +
        "user_segment_ids lists every user segment that can view the article (an empty list means everyone can view it); user_segment_id is kept for compatibility and shows at most one segment, so use user_segment_ids to decide who can see an article. Resolve the ids with List_user_segments. " +
        "Set locale to 'all' to fetch every translation. With embed_images, images hosted on this Zendesk account are inlined in the body as base64 data URIs; images on any other host are skipped and listed in images_skipped. " +
        "For multi-brand accounts, pass the article's brand_url (from Search_help_center_articles results or List_brands) to fetch articles from non-default brands.")]
    public static async Task<string> GetHelpCenterArticle(
        ZendeskService zendesk,
        [Description("The id of the article to fetch.")] long article_id,
        [Description("Locale code (e.g. 'en-us', 'fr', 'de-de'), or 'all' to fetch every translation. If omitted, the Help Center's default locale is used.")] string? locale = null,
        [Description("If true, inline the article's images in the body as base64 data URIs. Only images hosted on this Zendesk account are fetched. Default false.")] bool embed_images = false,
        [Description(HelpCenterJson.BrandUrlDescription)] string? brand_url = null,
        CancellationToken cancellationToken = default)
    {
        ToolJson.PositiveId(article_id, "article_id");
        var all = string.Equals(locale?.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        var validLocale = all ? null : HelpCenterJson.Locale(locale);
        var id = HelpCenterJson.Id(article_id);

        // Unverified against the live API: that the locale-less path answers in the Help Center's
        // default locale. The validation account's Help Center has one locale, so it could not tell.
        var response = await zendesk.GetAsync($"{HelpCenterJson.Root(validLocale)}/articles/{id}", brandUrl: brand_url, cancellationToken: cancellationToken);
        var article = response["article"] ?? throw new HttpRequestException($"Zendesk returned no article for id {article_id}.");
        var result = HelpCenterJson.Article(article, includeBody: true);

        var sectionId = ToolJson.Long(article["section_id"]);
        JsonNode? section = null;
        if (sectionId is not null)
        {
            section = await TryGetAsync(zendesk, $"{HelpCenterJson.Root(validLocale)}/sections/{HelpCenterJson.Id(sectionId.Value)}", "section", brand_url, cancellationToken);
        }

        result["section_name"] = section?["name"]?.DeepClone();
        var categoryId = ToolJson.Long(section?["category_id"]);
        result["category_id"] = categoryId;
        JsonNode? category = null;
        if (categoryId is not null)
        {
            category = await TryGetAsync(zendesk, $"{HelpCenterJson.Root(validLocale)}/categories/{HelpCenterJson.Id(categoryId.Value)}", "category", brand_url, cancellationToken);
        }

        result["category_name"] = category?["name"]?.DeepClone();

        // Users are account-wide Support records, so the author is looked up on the default host.
        var authorId = ToolJson.Long(article["author_id"]);
        JsonNode? author = null;
        if (authorId is not null)
        {
            author = await TryGetAsync(zendesk, $"/api/v2/users/{HelpCenterJson.Id(authorId.Value)}", "user", brandUrl: null, cancellationToken);
        }

        result["author_name"] = author?["name"]?.DeepClone();

        JsonArray? translations = null;
        if (all)
        {
            // The translations list follows cursor pagination (confirmed against the live API).
            var pages = await zendesk.GetAllPagesAsync($"/api/v2/help_center/articles/{id}/translations", null, "translations", maxItems: 500, brandUrl: brand_url, cancellationToken: cancellationToken);
            translations = [];
            foreach (var translation in pages.Items)
            {
                translations.Add(ToolJson.Pick(translation, "id", "locale", "title", "body", "draft", "outdated", "html_url", "created_at", "updated_at", "updated_by_id"));
            }

            result["translations"] = translations;
        }

        if (embed_images)
        {
            var baseUrl = await zendesk.ResolveBrandBaseUrlAsync(brand_url, cancellationToken) ?? zendesk.BaseUrl;
            var session = new HelpCenterImages.Session(zendesk, baseUrl);
            result["body"] = await HelpCenterImages.EmbedAsync(session, ToolJson.String(result["body"]), cancellationToken);

            // Only the main body. Inlining into every translation would put a base64 copy of each image
            // into the result once per locale — up to hundreds of copies for locale 'all'.
            if (translations is { Count: > 0 })
            {
                result["images_note"] = "Images are inlined in the main body only; translations keep their image URLs.";
            }

            result["images_embedded"] = session.EmbeddedCount;
            result["images_skipped"] = session.SkippedImages;
        }

        return result.ToJsonString();
    }

    [Authorize(Policy = "zendesk:write")]
    [McpServerTool(Name = "Create_help_center_article", Title = "Create Help Center article", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description(
        "Create a new Help Center article in a section. Articles are created as drafts by default; publishing (draft=false) must be allowed in this server's settings (an administrator switch). " +
        "Use List_help_center_sections to find section_id, List_permission_groups for permission_group_id (if omitted and the Help Center has exactly one permission group, that one is used) and List_user_segments for user_segment_id. " +
        "If user_segment_id is omitted the article is visible to everyone once it is published; while it is a draft only agents who can manage it see it. " +
        "For multi-brand accounts, pass brand_url when the section belongs to a non-default brand. " + HelpCenterJson.UntrustedContentWarning)]
    public static async Task<string> CreateHelpCenterArticle(
        ZendeskService zendesk,
        [Description("The id of the section to create the article in. Use List_help_center_sections to find it.")] long section_id,
        [Description("Locale code for the article (e.g. 'en-us', 'fr').")] string locale,
        [Description("Title of the article. Max 500 characters.")] string title,
        [Description("HTML body of the article. Max 100,000 characters.")] string? body = null,
        [Description("If true (default), the article is created as a draft. If false, it is published immediately, which must be allowed in this server's settings.")] bool draft = true,
        [Description("Labels to apply to the article. Max 20 labels, each max 100 characters.")] string[]? label_names = null,
        [Description("The permission group that controls which agents can manage the article. Use List_permission_groups. If omitted and exactly one group exists, that one is used.")] long? permission_group_id = null,
        [Description("The user segment that controls who can view the article once published. Use List_user_segments. If omitted, everyone can view it once published.")] long? user_segment_id = null,
        [Description("Position of the article within the section. Lower numbers appear first.")] int? position = null,
        [Description(HelpCenterJson.BrandUrlDescription)] string? brand_url = null,
        CancellationToken cancellationToken = default)
    {
        ToolJson.PositiveId(section_id, "section_id");
        var validLocale = HelpCenterJson.RequiredLocale(locale);
        var validTitle = HelpCenterJson.RequiredText(title, "title", HelpCenterJson.MaxTitleLength);
        HelpCenterJson.Text(body, "body", HelpCenterJson.MaxBodyLength);
        HelpCenterJson.Position(position);
        var labels = Labels(label_names);
        if (permission_group_id is not null)
        {
            ToolJson.PositiveId(permission_group_id.Value, "permission_group_id");
        }

        if (user_segment_id is not null)
        {
            ToolJson.PositiveId(user_segment_id.Value, "user_segment_id");
        }

        HelpCenterJson.RequirePublishingAllowed(zendesk, draft);

        var groupId = permission_group_id ?? await SinglePermissionGroupAsync(zendesk, brand_url, cancellationToken);

        // Zendesk requires user_segment_id on create and reads null as "everyone". Sent explicitly as
        // null when omitted (rather than refusing): the article is a draft by default, so nobody outside
        // its managers sees it until someone publishes it, and publishing is itself an administrator switch.
        var article = new JsonObject
        {
            ["title"] = validTitle,
            ["locale"] = validLocale,
            ["draft"] = draft,
            ["permission_group_id"] = groupId,
            ["user_segment_id"] = user_segment_id,
        };
        if (body is not null)
        {
            article["body"] = body;
        }

        if (labels is not null)
        {
            article["label_names"] = labels;
        }

        if (position is not null)
        {
            article["position"] = position;
        }

        var response = await zendesk.PostAsync(
            $"{HelpCenterJson.Root(validLocale)}/sections/{HelpCenterJson.Id(section_id)}/articles",
            new JsonObject { ["article"] = article },
            brandUrl: brand_url,
            cancellationToken: cancellationToken);

        var created = HelpCenterJson.Article(response["article"], includeBody: false);
        created["visible_to"] = user_segment_id is null ? "everyone once published" : "members of user_segment_id once published";
        return created.ToJsonString();
    }

    [Authorize(Policy = "zendesk:write")]
    [McpServerTool(Name = "Update_article_translation", Title = "Update article translation", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description(
        "Update the title and/or body of a Help Center article translation. At least one of title or body must be given. " +
        "If the translation does not exist for the locale and upsert is true (the default), it is created. " +
        "Translations are saved as drafts by default — note that saving a currently published translation with draft=true takes it off the live Help Center until it is published again. Publishing (draft=false) must be allowed in this server's settings (an administrator switch). " +
        "For multi-brand accounts, pass brand_url when the article belongs to a non-default brand. " + HelpCenterJson.UntrustedContentWarning)]
    public static async Task<string> UpdateArticleTranslation(
        ZendeskService zendesk,
        [Description("The id of the article to update.")] long article_id,
        [Description("Locale code of the translation to update (e.g. 'en-us', 'fr').")] string locale,
        [Description("New title for the translation. Max 500 characters.")] string? title = null,
        [Description("New HTML body for the translation. Max 100,000 characters.")] string? body = null,
        [Description("If true (default), the translation is saved as a draft. If false, it is published immediately, which must be allowed in this server's settings.")] bool draft = true,
        [Description("If true (default), create the translation when it does not exist for the locale. If false, return an error when it is missing.")] bool upsert = true,
        [Description(HelpCenterJson.BrandUrlDescription)] string? brand_url = null,
        CancellationToken cancellationToken = default)
    {
        ToolJson.PositiveId(article_id, "article_id");
        var validLocale = HelpCenterJson.RequiredLocale(locale);
        HelpCenterJson.Text(title, "title", HelpCenterJson.MaxTitleLength);
        HelpCenterJson.Text(body, "body", HelpCenterJson.MaxBodyLength);
        if (string.IsNullOrWhiteSpace(title) && body is null)
        {
            throw new ArgumentException("Provide at least one of title or body.");
        }

        HelpCenterJson.RequirePublishingAllowed(zendesk, draft);

        var translation = new JsonObject();
        if (!string.IsNullOrWhiteSpace(title))
        {
            translation["title"] = title;
        }

        if (body is not null)
        {
            translation["body"] = body;
        }

        translation["draft"] = draft;

        var id = HelpCenterJson.Id(article_id);
        JsonNode response;
        var created = false;
        try
        {
            response = await zendesk.PutAsync(
                $"/api/v2/help_center/articles/{id}/translations/{ZendeskService.Segment(validLocale)}",
                new JsonObject { ["translation"] = translation },
                brandUrl: brand_url,
                cancellationToken: cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            if (!upsert)
            {
                throw new ArgumentException($"Article {article_id} has no '{validLocale}' translation (or the article does not exist). Pass upsert=true to create the translation.");
            }

            // A new translation needs a title; Zendesk refuses one without it.
            if (string.IsNullOrWhiteSpace(title))
            {
                throw new ArgumentException($"Article {article_id} has no '{validLocale}' translation yet, so title is required to create it.");
            }

            var create = (JsonObject)translation.DeepClone();
            create["locale"] = validLocale;
            response = await zendesk.PostAsync(
                $"/api/v2/help_center/articles/{id}/translations",
                new JsonObject { ["translation"] = create },
                brandUrl: brand_url,
                cancellationToken: cancellationToken);
            created = true;
        }

        var result = ToolJson.Pick(response["translation"], "id", "source_id", "locale", "title", "draft", "outdated", "html_url", "created_at", "updated_at");
        result["created"] = created;
        return result.ToJsonString();
    }

    [Authorize(Policy = "zendesk:read")]
    [McpServerTool(Name = "Export_help_center_articles", Title = "Export Help Center articles", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description(
        "Export Help Center articles with their full HTML body. Use this only when you need complete article content (content analysis, migration, full-text processing); to find articles without their bodies, use Search_help_center_articles. " +
        "Each article includes user_segment_ids, the ids of every user segment that can view it (an empty list means everyone can view it); user_segment_id is kept for compatibility and shows at most one segment. Resolve the ids with List_user_segments. " +
        "Filter by section_id (takes priority) or category_id; with neither, every article is exported. " +
        "Results are paginated: when pagination_info.has_more is true, call again with after_cursor set to pagination_info.next_cursor.")]
    public static async Task<string> ExportHelpCenterArticles(
        ZendeskService zendesk,
        [Description("Locale code (e.g. 'en-us', 'fr', 'de-de'). If omitted, the Help Center's default locale is used.")] string? locale = null,
        [Description("Only export articles in this section. Takes priority over category_id.")] long? section_id = null,
        [Description("Only export articles in this category.")] long? category_id = null,
        [Description("Results per page (1-100, default 100).")] int? page_size = null,
        [Description("Cursor for the next page: pagination_info.next_cursor from a previous Export_help_center_articles response.")] string? after_cursor = null,
        [Description(HelpCenterJson.BrandUrlDescription)] string? brand_url = null,
        CancellationToken cancellationToken = default)
    {
        var validLocale = HelpCenterJson.Locale(locale);
        var pageSize = ToolJson.PageSize(page_size);
        var root = HelpCenterJson.Root(validLocale);
        string path;
        if (section_id is not null)
        {
            path = $"{root}/sections/{HelpCenterJson.Id(ToolJson.PositiveId(section_id.Value, "section_id"))}/articles";
        }
        else if (category_id is not null)
        {
            path = $"{root}/categories/{HelpCenterJson.Id(ToolJson.PositiveId(category_id.Value, "category_id"))}/articles";
        }
        else
        {
            path = $"{root}/articles";
        }

        var page = await zendesk.GetAsync(path, HelpCenterJson.PageQuery(pageSize, after_cursor), brand_url, cancellationToken);
        var articles = new JsonArray();
        foreach (var article in page["articles"] as JsonArray ?? [])
        {
            articles.Add(HelpCenterJson.Article(article, includeBody: true));
        }

        return new JsonObject
        {
            ["articles"] = articles,
            ["pagination_info"] = ToolJson.PaginationInfo(page, pageSize),
        }.ToJsonString();
    }

    // A search result's brand base URL, so it can be passed straight back as brand_url. Unverified
    // against the live API: whether results carry brand_url themselves; otherwise the origin of the
    // article's html_url is the brand's Help Center host (its subdomain or host mapping).
    private static string? BrandUrlOf(JsonNode? item, string? htmlUrl)
    {
        var explicitUrl = ToolJson.String(item?["brand_url"]);
        if (!string.IsNullOrWhiteSpace(explicitUrl))
        {
            return explicitUrl;
        }

        return Uri.TryCreate(htmlUrl, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? $"https://{uri.Host}"
            : null;
    }

    /// <summary>The article id and locale in a Help Center article URL (<c>…/hc/{locale}/articles/{id}-{slug}</c>).</summary>
    private static (long? Id, string? Locale) ArticleUrlParts(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return (null, null);
        }

        var match = ArticlePath().Match(uri.AbsolutePath);
        return match.Success && long.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? (id, match.Groups["locale"].Value)
            : (null, null);
    }

    [GeneratedRegex(@"/hc/(?<locale>[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)/articles/(?<id>[0-9]+)(?:-|/|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ArticlePath();

    private static JsonArray? Labels(string[]? labels)
    {
        if (labels is null)
        {
            return null;
        }

        if (labels.Length > 20)
        {
            throw new ArgumentException($"label_names may hold at most 20 labels (got {labels.Length}).");
        }

        var array = new JsonArray();
        foreach (var label in labels)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                throw new ArgumentException("label_names must not contain empty labels.");
            }

            if (label.Length > 100)
            {
                throw new ArgumentException($"Each label in label_names must be at most 100 characters ('{label[..20]}…' is {label.Length}).");
            }

            array.Add(label.Trim());
        }

        return array;
    }

    // Zendesk requires a permission group on every new article. When the caller names none, the only
    // unambiguous choice is the Help Center's single group; with several, guessing would decide which
    // agents may edit the article, so the caller is asked to choose instead.
    private static async Task<long> SinglePermissionGroupAsync(ZendeskService zendesk, string? brandUrl, CancellationToken cancellationToken)
    {
        var groups = await zendesk.GetAllPagesAsync("/api/v2/guide/permission_groups", null, "permission_groups", maxItems: 200, brandUrl: brandUrl, cancellationToken: cancellationToken);
        if (groups.Items.Count == 1 && ToolJson.Long(groups.Items[0]["id"]) is { } only)
        {
            return only;
        }

        if (groups.Items.Count == 0)
        {
            throw new ArgumentException("permission_group_id is required, and no permission groups were found. Use List_permission_groups.");
        }

        var listed = string.Join("; ", groups.Items.Select(g => $"{ToolJson.Long(g["id"])} ({ToolJson.String(g["name"])})"));
        throw new ArgumentException($"permission_group_id is required because this Help Center has {groups.Items.Count} permission groups: {listed}. Pass one of these ids.");
    }

    // A lookup that only decorates the result (section, category or author name): a record that is
    // gone or hidden from this agent leaves the name null instead of failing the whole call.
    private static async Task<JsonNode?> TryGetAsync(ZendeskService zendesk, string path, string property, string? brandUrl, CancellationToken cancellationToken)
    {
        try
        {
            var response = await zendesk.GetAsync(path, brandUrl: brandUrl, cancellationToken: cancellationToken);
            return response[property];
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return null;
        }
    }
}
