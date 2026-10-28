using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZendeskMcp.Tools;

/// <summary>
/// Turns a ticket's comments (or a messaging ticket's conversation log) into the chronological
/// entries Get_ticket_conversation returns.
/// </summary>
public static partial class TicketConversationHelpers
{
    // Unverified against the live API: the full list of via.channel values used by messaging
    // (Sunshine Conversations) tickets. These are the ones whose comments are a poor record of the
    // conversation, so the conversation log is tried first.
    public static readonly HashSet<string> MessagingChannels = new(StringComparer.Ordinal)
    {
        "native_messaging", "whatsapp", "messenger", "instagram_dm", "line", "wechat", "telegram",
        "viber", "google_rcs", "kakaotalk", "apple_business_chat", "sunshine_conversations_api",
        "sunshine_conversations_facebook_messenger", "sunshine_conversations_twitter_dm",
    };

    /// <summary>One comment as a conversation entry.</summary>
    public static JsonObject CommentEntry(JsonNode comment, IReadOnlyDictionary<long, JsonNode> users)
    {
        var authorId = ToolJson.Long(comment["author_id"]);
        JsonNode? author = authorId is { } id && users.TryGetValue(id, out var user) ? user : null;
        var role = ToolJson.String(author?["role"]);

        var html = ToolJson.String(comment["html_body"]);
        var message = !string.IsNullOrEmpty(html)
            ? HtmlToText(html)
            : ToolJson.String(comment["plain_body"]) ?? ToolJson.String(comment["body"]) ?? string.Empty;

        var isPublic = comment["public"] is not JsonValue p || !p.TryGetValue<bool>(out var flag) || flag;
        return new JsonObject
        {
            ["timestamp"] = comment["created_at"]?.DeepClone(),
            ["side"] = Side(role, authorId),
            ["author"] = ToolJson.String(author?["name"]) ?? (authorId is null or <= 0 ? "(system)" : $"user {authorId}"),
            ["message"] = message,
            ["visibility"] = isPublic ? "public" : "private",
            ["attachments"] = Attachments(comment["attachments"]),
        };
    }

    // An end user is the customer. Zendesk itself (author -1) and agents and admins are the agent side;
    // an author missing from the sideload is most likely a deleted end user.
    private static string Side(string? role, long? authorId) => role switch
    {
        "end-user" => "customer",
        "agent" or "admin" => "agent",
        _ => authorId is null or <= 0 ? "agent" : "customer",
    };

    /// <summary>The attachment list each entry carries.</summary>
    public static JsonArray Attachments(JsonNode? attachments)
    {
        var shaped = new JsonArray();
        foreach (var attachment in attachments?.AsArray() ?? [])
        {
            shaped.Add(new JsonObject
            {
                ["id"] = attachment?["id"]?.DeepClone(),
                ["file_name"] = (attachment?["file_name"] ?? attachment?["name"])?.DeepClone(),
                ["content_type"] = (attachment?["content_type"] ?? attachment?["media_type"])?.DeepClone(),
                ["size"] = (attachment?["size"] ?? attachment?["media_size"])?.DeepClone(),
                ["content_url"] = (attachment?["content_url"] ?? attachment?["media_url"])?.DeepClone(),
                ["inline"] = attachment?["inline"]?.DeepClone() ?? false,
            });
        }

        return shaped;
    }

    /// <summary>
    /// Entries from a messaging ticket's conversation log, or an empty list when the response holds
    /// nothing recognisable (the caller then falls back to comments).
    /// </summary>
    /// <remarks>
    /// Unverified against the live API: the conversation_log endpoint and its shape. This reads
    /// <c>events[]</c> with <c>created_at</c>, <c>author {type, display_name|name}</c> and
    /// <c>content {body|text, attachments}</c>, tolerantly, and keeps only entries with a message
    /// or attachments.
    /// </remarks>
    public static List<JsonObject> ConversationLogEntries(JsonNode log)
    {
        var entries = new List<JsonObject>();
        var events = (log["events"] ?? log["conversation_log"] ?? log["messages"]) as JsonArray;
        foreach (var item in events ?? [])
        {
            if (item is not JsonObject evt)
            {
                continue;
            }

            var content = evt["content"] as JsonObject;
            var message = ToolJson.String(content?["body"]) ?? ToolJson.String(content?["text"])
                ?? ToolJson.String(evt["body"]) ?? ToolJson.String(evt["text"]);
            var htmlBody = ToolJson.String(content?["html_body"]) ?? ToolJson.String(evt["html_body"]);
            if (message is null && htmlBody is not null)
            {
                message = HtmlToText(htmlBody);
            }

            var attachments = Attachments(content?["attachments"] ?? evt["attachments"]);
            if (string.IsNullOrWhiteSpace(message) && attachments.Count == 0)
            {
                continue;
            }

            var authorType = (ToolJson.String(evt["author"]?["type"]) ?? string.Empty).ToLowerInvariant();
            var isPublic = evt["public"] is not JsonValue p || !p.TryGetValue<bool>(out var flag) || flag;
            entries.Add(new JsonObject
            {
                ["timestamp"] = (evt["created_at"] ?? evt["timestamp"])?.DeepClone(),
                ["side"] = authorType is "user" or "end-user" or "end_user" or "customer" ? "customer" : "agent",
                ["author"] = ToolJson.String(evt["author"]?["display_name"]) ?? ToolJson.String(evt["author"]?["name"]) ?? authorType,
                ["message"] = message ?? string.Empty,
                ["visibility"] = isPublic ? "public" : "private",
                ["attachments"] = attachments,
            });
        }

        return entries;
    }

    /// <summary>
    /// Plain text from a comment's HTML: block elements become line breaks, list items get a dash,
    /// and inline images become <c>[Image: alt](url)</c> placeholders so the reader knows where they
    /// sat and can fetch them with Get_ticket_attachment.
    /// </summary>
    public static string HtmlToText(string html)
    {
        var text = ScriptOrStyle().Replace(html, string.Empty);
        text = ImageTag().Replace(text, match =>
        {
            var tag = match.Value;
            var alt = Attribute(tag, "alt");
            var src = Attribute(tag, "src") ?? string.Empty;
            return $"[Image: {(string.IsNullOrWhiteSpace(alt) ? "image" : alt)}]({src})";
        });
        text = LineBreak().Replace(text, "\n");
        text = ListItem().Replace(text, "\n- ");
        text = BlockEnd().Replace(text, "\n");
        text = AnyTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        text = HorizontalSpace().Replace(text, " ");
        text = string.Join("\n", text.Split('\n').Select(line => line.Trim()));
        text = BlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $"""\b{name}\s*=\s*(?:"(?<v>[^"]*)"|'(?<v>[^']*)'|(?<v>[^\s>]+))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["v"].Value) : null;
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageTag();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"</(p|div|h[1-6]|blockquote|pre|tr|ul|ol|table)\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t\f\v]+", RegexOptions.CultureInvariant)]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex BlankLines();
}
