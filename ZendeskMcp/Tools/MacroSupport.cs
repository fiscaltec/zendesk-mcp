using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZendeskMcp.Tools;

/// <summary>One {field, value} macro action as a caller supplies it to Create_macro and Update_macro.</summary>
public sealed record MacroActionInput
{
    [JsonPropertyName("field")]
    public string Field { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public JsonElement Value { get; init; }
}

/// <summary>One action Zendesk accepts in a macro, from <c>GET /api/v2/macros/actions</c>.</summary>
public sealed record MacroActionDefinition(string Field, string Title, string? Type, IReadOnlyList<(string Value, string Title)> Values);

/// <summary>
/// Shaping and validation shared by the macro tools: readable actions, access, comments, dynamic
/// content and the comparisons that let the write tools report what they changed.
/// </summary>
public static partial class MacroSupport
{
    /// <summary>The comment actions, reported separately from the other actions (and only on request).</summary>
    public const string CommentHtmlField = "comment_value_html";
    public const string CommentTextField = "comment_value";
    public const string CommentPublicField = "comment_mode_is_public";

    public static bool IsCommentField(string? field) =>
        field is CommentHtmlField or CommentTextField or CommentPublicField;

    /// <summary>Admin Centre URL of a macro, for the person to open.</summary>
    public static string AdminUrl(string baseUrl, long id) =>
        $"{baseUrl}/admin/workspaces/agent-workspace/macros/{id.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The category: everything before the last '::' in the title, or null.</summary>
    public static string? Category(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var at = title.LastIndexOf("::", StringComparison.Ordinal);
        return at > 0 ? title[..at].Trim() : null;
    }

    /// <summary>True when the title sits in <paramref name="category"/> or one of its subcategories.</summary>
    public static bool InCategory(string? title, string category)
    {
        var own = Category(title);
        if (own is null)
        {
            return false;
        }

        var wanted = category.Trim();
        return own.Equals(wanted, StringComparison.OrdinalIgnoreCase)
            || own.StartsWith(wanted + "::", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>'shared', 'group' or 'personal', from the macro's restriction.</summary>
    public static string Access(JsonNode? macro)
    {
        var type = ToolJson.String(macro?["restriction"]?["type"]);
        return type switch
        {
            "Group" => "group",
            "User" => "personal",
            _ => "shared",
        };
    }

    /// <summary>The group ids a group-restricted macro is limited to (Zendesk sends <c>ids</c>, older payloads only <c>id</c>).</summary>
    public static List<long> RestrictionGroupIds(JsonNode? macro)
    {
        var restriction = macro?["restriction"];
        var ids = new List<long>();
        if (restriction?["ids"] is JsonArray array)
        {
            ids.AddRange(array.Select(ToolJson.Long).OfType<long>());
        }

        if (ids.Count == 0 && ToolJson.Long(restriction?["id"]) is { } single)
        {
            ids.Add(single);
        }

        return ids.Distinct().ToList();
    }

    /// <summary>The actions array of a macro, never null.</summary>
    public static JsonArray Actions(JsonNode? macro) => macro?["actions"] as JsonArray ?? [];

    /// <summary>
    /// The macro's comment as HTML: <c>comment_value_html</c> when present, otherwise the plain
    /// <c>comment_value</c> escaped. Null when the macro adds no comment.
    /// </summary>
    public static string? CommentHtml(JsonArray actions)
    {
        var html = actions.FirstOrDefault(a => ToolJson.String(a?["field"]) == CommentHtmlField);
        if (html is not null)
        {
            return ValueText(html["value"]);
        }

        var text = actions.FirstOrDefault(a => ToolJson.String(a?["field"]) == CommentTextField);
        if (text is null)
        {
            return null;
        }

        var plain = ValueText(text["value"]);
        return plain is null ? null : WebUtility.HtmlEncode(plain).Replace("\n", "<br>", StringComparison.Ordinal);
    }

    /// <summary>The comment's visibility as the macro sets it, or null when it leaves it to the agent.</summary>
    public static bool? CommentPublic(JsonArray actions)
    {
        var action = actions.FirstOrDefault(a => ToolJson.String(a?["field"]) == CommentPublicField);
        return action is null ? null : ParseBool(action["value"]);
    }

    /// <summary>Reads Zendesk's "true"/"false" (string or boolean) action values.</summary>
    public static bool? ParseBool(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) && bool.TryParse(s, out var parsed) => parsed,
        _ => null,
    };

    /// <summary>
    /// The text of an action value. <c>comment_value</c> may be a string or <c>[channel, text]</c>;
    /// the text is the last string either way.
    /// </summary>
    public static string? ValueText(JsonNode? value) => value switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray array => array.Select(ToolJson.String).LastOrDefault(s => s is not null),
        _ => null,
    };

    /// <summary>A value as the string Zendesk compares it by: strings as is, numbers and booleans formatted.</summary>
    public static string? ScalarText(JsonNode? value) => value switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<long>(out var n) => n.ToString(CultureInfo.InvariantCulture),
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(CultureInfo.InvariantCulture),
        _ => value.ToJsonString(),
    };

    /// <summary>Plain text of a comment's HTML: block ends become line breaks, tags go, entities decode.</summary>
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var withBreaks = BlockEnd().Replace(html, "\n");
        var stripped = Tag().Replace(withBreaks, string.Empty);
        var decoded = WebUtility.HtmlDecode(stripped);
        var lines = decoded.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n').Select(l => l.Trim());
        return ManyBlankLines().Replace(string.Join("\n", lines), "\n\n").Trim();
    }

    /// <summary>The <c>{{dc.name}}</c> placeholders a text uses, lower-cased and de-duplicated.</summary>
    public static List<string> DynamicContentNames(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : DcPlaceholder().Matches(text).Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToList();

    /// <summary>The text with its <c>{{dc.name}}</c> placeholders removed.</summary>
    public static string WithoutDynamicContent(string text) => DcPlaceholder().Replace(text, " ");

    /// <summary>The dynamic content name an item answers to, from its placeholder (<c>{{dc.name}}</c>) or name.</summary>
    public static string? DynamicContentName(JsonNode? item)
    {
        var placeholder = ToolJson.String(item?["placeholder"]);
        if (placeholder is not null && DcPlaceholder().Match(placeholder) is { Success: true } match)
        {
            return match.Groups[1].Value.ToLowerInvariant();
        }

        return ToolJson.String(item?["name"])?.ToLowerInvariant();
    }

    /// <summary>A dynamic content item with every translation, as the macro tools return it.</summary>
    public static JsonObject ShapeDynamicContent(JsonNode item)
    {
        var shaped = ToolJson.Pick(item, "id", "name", "placeholder", "default_locale_id");
        var variants = new JsonArray();
        foreach (var variant in item["variants"] as JsonArray ?? [])
        {
            variants.Add(ToolJson.Pick(variant, "id", "locale_id", "content", "active", "default"));
        }

        shaped["variants"] = variants;
        return shaped;
    }

    /// <summary>All the account's dynamic content items, keyed by placeholder name.</summary>
    /// <remarks>
    /// Unverified against the live API: that <c>/api/v2/dynamic_content/items</c> answers cursor
    /// pagination. If it only pages by offset, the pager stops after the first page of 100 items.
    /// </remarks>
    public static async Task<Dictionary<string, JsonNode>> LoadDynamicContentAsync(ZendeskService zendesk, CancellationToken cancellationToken)
    {
        var items = await zendesk.GetAllPagesAsync("/api/v2/dynamic_content/items", null, "items", maxItems: 2000, cancellationToken: cancellationToken);
        var byName = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.Items)
        {
            if (DynamicContentName(item) is { } name)
            {
                byName.TryAdd(name, item);
            }
        }

        return byName;
    }

    /// <summary>
    /// The macro action definitions, keyed by field. Zendesk's list includes the account's groups,
    /// agents, statuses and custom field options as values, which is what makes the actions readable.
    /// </summary>
    /// <remarks>
    /// Unverified against the live API: the exact envelope. <c>/api/v2/macros/actions</c> is read as
    /// <c>{"actions":[{"output_key"|"subject", "title", "type", "values":[{"value","title"}]}]}</c>,
    /// and the <c>{"definitions":{"actions":[…]}}</c> shape of <c>/api/v2/macros/definitions</c> is
    /// accepted too.
    /// </remarks>
    public static async Task<Dictionary<string, MacroActionDefinition>> LoadDefinitionsAsync(ZendeskService zendesk, CancellationToken cancellationToken)
    {
        var root = await zendesk.GetAsync("/api/v2/macros/actions", cancellationToken: cancellationToken);
        var list = root["actions"] as JsonArray ?? root["definitions"]?["actions"] as JsonArray ?? [];
        var definitions = new Dictionary<string, MacroActionDefinition>(StringComparer.Ordinal);
        foreach (var node in list)
        {
            var field = ToolJson.String(node?["output_key"]) ?? ToolJson.String(node?["subject"]) ?? ToolJson.String(node?["field"]);
            if (string.IsNullOrEmpty(field))
            {
                continue;
            }

            var values = new List<(string, string)>();
            foreach (var value in node?["values"] as JsonArray ?? [])
            {
                if (ScalarText(value?["value"]) is { } v)
                {
                    values.Add((v, ToolJson.String(value?["title"]) ?? v));
                }
            }

            definitions.TryAdd(field, new MacroActionDefinition(field, ToolJson.String(node?["title"]) ?? field, ToolJson.String(node?["type"]), values));
        }

        return definitions;
    }

    /// <summary>The definitions, or null when Zendesk will not give them (they only make output nicer here).</summary>
    public static async Task<Dictionary<string, MacroActionDefinition>?> TryLoadDefinitionsAsync(ZendeskService zendesk, CancellationToken cancellationToken)
    {
        try
        {
            return await LoadDefinitionsAsync(zendesk, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>One action as {field, field_label, value, value_label}, keeping the raw field and value.</summary>
    public static JsonObject ReadableAction(JsonNode? action, IReadOnlyDictionary<string, MacroActionDefinition>? definitions)
    {
        var field = ToolJson.String(action?["field"]) ?? string.Empty;
        var value = action?["value"];
        MacroActionDefinition? definition = null;
        definitions?.TryGetValue(field, out definition);

        string? valueLabel = null;
        if (definition is not null && definition.Values.Count > 0)
        {
            var raw = ScalarText(value);
            valueLabel = definition.Values.FirstOrDefault(v => v.Value == raw).Title;
        }

        return new JsonObject
        {
            ["field"] = field,
            ["field_label"] = definition?.Title ?? field,
            ["value"] = value?.DeepClone(),
            ["value_label"] = valueLabel,
        };
    }

    /// <summary>
    /// Checks caller-supplied actions against Zendesk's definitions and returns them as Zendesk action
    /// nodes. An unknown field or a value outside a list field's options is refused with the valid ones.
    /// </summary>
    public static JsonArray ValidateActions(IReadOnlyList<MacroActionInput> actions, IReadOnlyDictionary<string, MacroActionDefinition> definitions, bool allowComment)
    {
        var result = new JsonArray();
        foreach (var action in actions)
        {
            var field = action.Field?.Trim() ?? string.Empty;
            if (field.Length == 0)
            {
                throw new ArgumentException("actions: every action needs a field.");
            }

            if (!allowComment && IsCommentField(field))
            {
                throw new ArgumentException($"actions: set the comment with comment_html and comment_public, not with the '{field}' action.");
            }

            var value = action.Value.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(action.Value.GetRawText());

            // Zendesk's definitions do not list the comment actions in every shape; they are well known.
            if (IsCommentField(field))
            {
                result.Add(new JsonObject { ["field"] = field, ["value"] = value });
                continue;
            }

            if (!definitions.TryGetValue(field, out var definition))
            {
                var known = string.Join(", ", definitions.Keys.Order(StringComparer.Ordinal).Take(80));
                throw new ArgumentException($"actions: '{field}' is not a macro action this account supports. Valid fields: {known}.");
            }

            if (definition.Values.Count > 0 && value is not JsonArray)
            {
                var raw = ScalarText(value);
                if (!definition.Values.Any(v => v.Value == raw))
                {
                    var valid = string.Join(", ", definition.Values.Take(80).Select(v => v.Value == v.Title ? v.Value : $"{v.Value} ({v.Title})"));
                    throw new ArgumentException($"actions: '{raw}' is not a valid value for '{field}'. Valid values: {valid}.");
                }
            }

            result.Add(new JsonObject { ["field"] = field, ["value"] = value });
        }

        return result;
    }

    /// <summary>An order-insensitive fingerprint of an action list, for "is this the same macro" checks.</summary>
    public static string ActionsFingerprint(JsonArray actions) =>
        string.Join("\u001f", actions
            .Select(a => $"{ToolJson.String(a?["field"])}={NormaliseValue(a?["value"])}")
            .Order(StringComparer.Ordinal));

    // "true" and true, 5 and "5" are the same value to Zendesk; compare them as such.
    private static string NormaliseValue(JsonNode? value) => value switch
    {
        JsonArray array => "[" + string.Join(",", array.Select(NormaliseValue)) + "]",
        _ => ScalarText(value) ?? "null",
    };

    /// <summary>Usage counts, or null when Zendesk sent none (a plan without usage data).</summary>
    public static JsonObject? Usage(JsonNode? macro)
    {
        if (macro is not JsonObject obj
            || !(obj.ContainsKey("usage_1h") || obj.ContainsKey("usage_24h") || obj.ContainsKey("usage_7d") || obj.ContainsKey("usage_30d")))
        {
            return null;
        }

        return new JsonObject
        {
            ["1h"] = obj["usage_1h"]?.DeepClone(),
            ["24h"] = obj["usage_24h"]?.DeepClone(),
            ["7d"] = obj["usage_7d"]?.DeepClone(),
            ["30d"] = obj["usage_30d"]?.DeepClone(),
        };
    }

    /// <summary>The usage sideloads requested with every macro read.</summary>
    public const string UsageInclude = "usage_1h,usage_24h,usage_7d,usage_30d";

    /// <summary>Names of the groups and agents macros are restricted to, fetched only when needed.</summary>
    public sealed class Names
    {
        public Dictionary<long, string> Groups { get; } = [];
        public Dictionary<long, string> Users { get; } = [];
    }

    /// <summary>Resolves the restriction names for a set of macros: one groups read and one users read at most.</summary>
    public static async Task<Names> LoadRestrictionNamesAsync(ZendeskService zendesk, IEnumerable<JsonNode> macros, CancellationToken cancellationToken)
    {
        var names = new Names();
        var list = macros.ToList();
        if (list.Any(m => Access(m) == "group"))
        {
            try
            {
                var groups = await zendesk.GetAllPagesAsync("/api/v2/groups", null, "groups", maxItems: 5000, cancellationToken: cancellationToken);
                foreach (var group in groups.Items)
                {
                    if (ToolJson.Long(group["id"]) is { } id)
                    {
                        names.Groups[id] = ToolJson.String(group["name"]) ?? string.Empty;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // Names are a nicety; the ids still go back.
            }
        }

        var userIds = list.Where(m => Access(m) == "personal")
            .Select(m => ToolJson.Long(m["restriction"]?["id"]))
            .OfType<long>()
            .Distinct()
            .Take(100)
            .ToList();
        if (userIds.Count > 0)
        {
            try
            {
                var users = await zendesk.GetAsync("/api/v2/users/show_many", ToolJson.Query(("ids", ToolJson.Ids(userIds))), cancellationToken: cancellationToken);
                foreach (var user in users["users"] as JsonArray ?? [])
                {
                    if (ToolJson.Long(user?["id"]) is { } id)
                    {
                        names.Users[id] = ToolJson.String(user?["name"]) ?? string.Empty;
                    }
                }
            }
            catch (HttpRequestException)
            {
                // As above.
            }
        }

        return names;
    }

    /// <summary>
    /// A macro as the macro tools return it: identity, category, access with who it is restricted to,
    /// usage, and its non-comment actions in readable form. The comment is added by the caller on request.
    /// </summary>
    public static JsonObject Shape(JsonNode macro, string baseUrl, IReadOnlyDictionary<string, MacroActionDefinition>? definitions, Names? names)
    {
        var id = ToolJson.Long(macro["id"]) ?? 0;
        var title = ToolJson.String(macro["title"]);
        var access = Access(macro);
        var actions = Actions(macro);

        JsonNode? restrictedTo = null;
        if (access == "group")
        {
            var groups = new JsonArray();
            foreach (var groupId in RestrictionGroupIds(macro))
            {
                groups.Add(new JsonObject
                {
                    ["id"] = groupId,
                    ["name"] = names is not null && names.Groups.TryGetValue(groupId, out var name) ? name : null,
                });
            }

            restrictedTo = groups;
        }
        else if (access == "personal" && ToolJson.Long(macro["restriction"]?["id"]) is { } userId)
        {
            restrictedTo = new JsonObject
            {
                ["id"] = userId,
                ["name"] = names is not null && names.Users.TryGetValue(userId, out var name) ? name : null,
            };
        }

        var readable = new JsonArray();
        foreach (var action in actions)
        {
            if (!IsCommentField(ToolJson.String(action?["field"])))
            {
                readable.Add(ReadableAction(action, definitions));
            }
        }

        return new JsonObject
        {
            ["id"] = id,
            ["title"] = title,
            ["category"] = Category(title),
            ["description"] = macro["description"]?.DeepClone(),
            ["active"] = macro["active"]?.DeepClone(),
            ["position"] = macro["position"]?.DeepClone(),
            ["created_at"] = macro["created_at"]?.DeepClone(),
            ["updated_at"] = macro["updated_at"]?.DeepClone(),
            ["url"] = AdminUrl(baseUrl, id),
            ["access"] = access,
            ["restricted_to"] = restrictedTo,
            ["usage"] = Usage(macro),
            ["has_comment"] = CommentHtml(actions) is not null,
            ["comment_public"] = CommentPublic(actions),
            ["actions"] = readable,
        };
    }

    /// <summary>
    /// The Zendesk user this connection acts as, as a stable key for in-process records such as
    /// Apply_macro's recent-application guard.
    /// </summary>
    public static async Task<string> CallerKeyAsync(ZendeskService zendesk, CancellationToken cancellationToken)
    {
        if (zendesk.Credentials.UsesDevelopmentToken)
        {
            return "dev:" + zendesk.Options.DevelopmentEmail;
        }

        var token = await zendesk.Credentials.GetUserTokenAsync(cancellationToken);
        return "user:" + token.ZendeskUserId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The Zendesk user behind this connection (<c>GET /api/v2/users/me</c>).</summary>
    public static async Task<JsonNode> CurrentUserAsync(ZendeskService zendesk, CancellationToken cancellationToken)
    {
        var me = await zendesk.GetAsync("/api/v2/users/me", cancellationToken: cancellationToken);
        return me["user"] ?? throw new HttpRequestException("Zendesk did not say who the connected user is.");
    }

    /// <summary>A short, single-line preview of a comment for results.</summary>
    public static string Preview(string text, int max = 500)
    {
        var flat = WhiteSpace().Replace(text, " ").Trim();
        return flat.Length <= max ? flat : flat[..max] + "…";
    }

    /// <summary>Plain text joined for searching.</summary>
    public static string SearchText(params string?[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (!string.IsNullOrEmpty(part))
            {
                builder.Append(' ').Append(part);
            }
        }

        return builder.ToString().ToLowerInvariant();
    }

    [GeneratedRegex(@"<\s*(br\s*/?|/p|/div|/li|/h[1-6]|/tr|/blockquote)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ManyBlankLines();

    [GeneratedRegex(@"\{\{\s*dc\.([A-Za-z0-9_\-\.]+)\s*\}\}", RegexOptions.IgnoreCase)]
    private static partial Regex DcPlaceholder();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
