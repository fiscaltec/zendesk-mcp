using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZendeskMcp.Tools;

/// <summary>
/// Fills the common Zendesk placeholders in an agent-edited macro comment (Apply_macro's
/// <c>comment_html</c>).
/// </summary>
/// <remarks>
/// <para>
/// Zendesk renders a macro's own comment through <c>…/macros/{id}/apply</c>, but it has no endpoint
/// that renders arbitrary text, and a comment sent through the Tickets API goes out exactly as
/// written. So the replacement text is filled in here, from the ticket, its requester, assignee and
/// organisation, the connected user and the account's dynamic content — the placeholders agents use
/// in replies. Anything else (Liquid filters, conditionals, other objects) is left untouched and
/// named in a warning rather than guessed at.
/// </para>
/// <para>Values are HTML-encoded: a requester's name is customer-controlled text going into HTML.</para>
/// </remarks>
public static partial class MacroPlaceholders
{
    /// <summary>The rendered HTML and the placeholders that could not be filled.</summary>
    public sealed record Rendered(string Html, IReadOnlyList<string> Unresolved);

    public static async Task<Rendered> RenderAsync(ZendeskService zendesk, string html, JsonNode ticket, CancellationToken cancellationToken)
    {
        var matches = Placeholder().Matches(html);
        if (matches.Count == 0)
        {
            return new Rendered(html, AnyPlaceholder().Matches(html).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList());
        }

        var used = matches.Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lower = used.Select(u => u.ToLowerInvariant()).ToList();

        // Fetch only what the text refers to.
        var userIds = new List<long>();
        if (lower.Any(u => u.StartsWith("ticket.requester.", StringComparison.Ordinal)) && ToolJson.Long(ticket["requester_id"]) is { } requesterId)
        {
            userIds.Add(requesterId);
        }

        if (lower.Any(u => u.StartsWith("ticket.assignee.", StringComparison.Ordinal)) && ToolJson.Long(ticket["assignee_id"]) is { } assigneeId)
        {
            userIds.Add(assigneeId);
        }

        var users = new Dictionary<long, JsonNode>();
        if (userIds.Count > 0)
        {
            var found = await zendesk.GetAsync("/api/v2/users/show_many", ToolJson.Query(("ids", ToolJson.Ids(userIds))), cancellationToken: cancellationToken);
            foreach (var user in found["users"] as JsonArray ?? [])
            {
                if (user is not null && ToolJson.Long(user["id"]) is { } id)
                {
                    users[id] = user;
                }
            }
        }

        JsonNode? me = null;
        if (lower.Any(u => u.StartsWith("current_user.", StringComparison.Ordinal)))
        {
            me = await MacroSupport.CurrentUserAsync(zendesk, cancellationToken);
        }

        JsonNode? organization = null;
        if (lower.Any(u => u.StartsWith("ticket.organization.", StringComparison.Ordinal)) && ToolJson.Long(ticket["organization_id"]) is { } orgId)
        {
            organization = (await zendesk.GetAsync($"/api/v2/organizations/{orgId.ToString(CultureInfo.InvariantCulture)}", cancellationToken: cancellationToken))["organization"];
        }

        Dictionary<string, JsonNode>? dynamicContent = null;
        if (lower.Any(u => u.StartsWith("dc.", StringComparison.Ordinal)))
        {
            dynamicContent = await MacroSupport.LoadDynamicContentAsync(zendesk, cancellationToken);
        }

        var requester = ToolJson.Long(ticket["requester_id"]) is { } r && users.TryGetValue(r, out var ru) ? ru : null;
        var assignee = ToolJson.Long(ticket["assignee_id"]) is { } a && users.TryGetValue(a, out var au) ? au : null;
        var requesterLocale = ToolJson.Long(requester?["locale_id"]);

        var unresolved = new List<string>();
        var rendered = Placeholder().Replace(html, match =>
        {
            var name = match.Groups[1].Value.Trim();
            var value = Resolve(name.ToLowerInvariant());
            if (value is null)
            {
                unresolved.Add(match.Value);
                return match.Value;
            }

            return value;
        });

        // Whatever still looks like a placeholder (unknown names, Liquid filters and tags) is reported.
        var left = AnyPlaceholder().Matches(rendered).Select(m => m.Value).Concat(unresolved).Distinct(StringComparer.Ordinal).ToList();
        return new Rendered(rendered, left);

        string? Resolve(string name)
        {
            if (name.StartsWith("dc.", StringComparison.Ordinal))
            {
                // Dynamic content is already HTML written by an admin; it goes in as is, in the
                // requester's language when there is a variant for it.
                if (dynamicContent is null || !dynamicContent.TryGetValue(name[3..], out var item))
                {
                    return null;
                }

                var variants = (item["variants"] as JsonArray ?? []).Where(v => v is not null).ToList();
                var chosen = variants.FirstOrDefault(v => requesterLocale is not null && ToolJson.Long(v!["locale_id"]) == requesterLocale)
                    ?? variants.FirstOrDefault(v => MacroSupport.ParseBool(v!["default"]) == true)
                    ?? variants.FirstOrDefault();
                return ToolJson.String(chosen?["content"]);
            }

            var raw = name switch
            {
                "ticket.id" => ToolJson.Long(ticket["id"])?.ToString(CultureInfo.InvariantCulture),
                "ticket.title" or "ticket.subject" => ToolJson.String(ticket["subject"]),
                "ticket.status" => ToolJson.String(ticket["status"]),
                "ticket.priority" => ToolJson.String(ticket["priority"]),
                "ticket.ticket_type" => ToolJson.String(ticket["type"]),
                "ticket.url" or "ticket.link" => ToolJson.Long(ticket["id"]) is { } tid ? $"{zendesk.BaseUrl}/agent/tickets/{tid.ToString(CultureInfo.InvariantCulture)}" : null,
                "ticket.organization.name" => ToolJson.String(organization?["name"]),
                _ when name.StartsWith("ticket.requester.", StringComparison.Ordinal) => Person(requester, name["ticket.requester.".Length..]),
                _ when name.StartsWith("ticket.assignee.", StringComparison.Ordinal) => Person(assignee, name["ticket.assignee.".Length..]),
                _ when name.StartsWith("current_user.", StringComparison.Ordinal) => Person(me, name["current_user.".Length..]),
                _ => null,
            };
            return raw is null ? null : WebUtility.HtmlEncode(raw);
        }
    }

    private static string? Person(JsonNode? user, string property)
    {
        var name = ToolJson.String(user?["name"]);
        return property switch
        {
            "name" => name,
            "first_name" => name?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
            "last_name" => name is null ? null : name.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } parts ? parts[^1] : null,
            "email" => ToolJson.String(user?["email"]),
            "phone" => ToolJson.String(user?["phone"]),
            _ => null,
        };
    }

    // {{ name }} with no Liquid filter or tag: anything with '|' or '%' is left to the warning.
    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_\.\-]+)\s*\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\{\{.*?\}\}|\{%.*?%\}", RegexOptions.Singleline)]
    private static partial Regex AnyPlaceholder();
}
