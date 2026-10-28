using System.Globalization;
using System.Text.Json.Nodes;

namespace ZendeskMcp.Tools;

/// <summary>Shaping and search-term building shared by the user and organisation tools.</summary>
public static class PeopleSupport
{
    // Characters Zendesk drops when it stores a tag, so a caller's '+6281234567890' finds the tag
    // 6281234567890 rather than nothing.
    private static readonly char[] DroppedTagCharacters = ['+', '#', '@', '?', '&', '\'', '"', '!', '$', '%', '^', '(', ')', '[', ']', '{', '}', '<', '>', '|', '\\', '/', '=', ';', ':', ',', '~', '`'];

    public static readonly string[] UserFields =
    [
        "id", "name", "email", "role", "external_id", "organization_id", "tags", "active", "suspended", "verified",
        "phone", "time_zone", "iana_time_zone", "locale", "user_fields", "created_at", "updated_at",
    ];

    public static readonly string[] OrganizationFields =
    [
        "id", "name", "external_id", "domain_names", "tags", "group_id", "shared_tickets", "shared_comments",
        "details", "notes", "organization_fields", "created_at", "updated_at",
    ];

    /// <summary>The comma-separated tags as Zendesk search terms (<c>tags:x</c>), normalised and checked.</summary>
    public static List<string> TagTerms(string? tags, string parameter = "tags")
    {
        var terms = new List<string>();
        if (string.IsNullOrWhiteSpace(tags))
        {
            return terms;
        }

        foreach (var raw in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException($"{parameter}: a tag cannot contain spaces (got '{raw}'). Write a phone number as digits only.");
            }

            var cleaned = new string(raw.Where(c => Array.IndexOf(DroppedTagCharacters, c) < 0).ToArray()).ToLowerInvariant();
            var stars = cleaned.Count(c => c == '*');
            if (stars > 1 || (stars == 1 && !cleaned.StartsWith('*') && !cleaned.EndsWith('*')))
            {
                throw new ArgumentException($"{parameter}: one '*' is allowed, at the start or end of a tag (got '{raw}').");
            }

            var core = cleaned.Replace("*", string.Empty, StringComparison.Ordinal);
            if (core.Length == 0)
            {
                throw new ArgumentException($"{parameter}: '{raw}' has nothing left once the characters Zendesk drops from tags are removed.");
            }

            if (stars == 1 && core.Length < 2)
            {
                throw new ArgumentException($"{parameter}: a wildcard tag needs at least 2 other characters (got '{raw}').");
            }

            terms.Add("tags:" + cleaned);
        }

        if (terms.Count > 50)
        {
            throw new ArgumentException($"{parameter} is limited to 50 tags.");
        }

        return terms.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>A quoted search value; quote characters are removed because Zendesk search cannot escape them.</summary>
    public static string Quoted(string value) => "\"" + value.Replace("\"", string.Empty, StringComparison.Ordinal).Trim() + "\"";

    /// <summary>A user as the user tools return it.</summary>
    public static JsonObject ShapeUser(JsonNode user, string baseUrl)
    {
        var shaped = ToolJson.Pick(user, UserFields);
        if (ToolJson.Long(user["id"]) is { } id)
        {
            shaped["url"] = $"{baseUrl}/agent/users/{id.ToString(CultureInfo.InvariantCulture)}";
        }

        return shaped;
    }

    /// <summary>An organisation as the organisation tools return it.</summary>
    public static JsonObject ShapeOrganization(JsonNode organization, string baseUrl)
    {
        var shaped = ToolJson.Pick(organization, OrganizationFields);
        if (ToolJson.Long(organization["id"]) is { } id)
        {
            shaped["url"] = $"{baseUrl}/agent/organizations/{id.ToString(CultureInfo.InvariantCulture)}";
        }

        return shaped;
    }

    /// <summary>Validates an id list against the 1–<paramref name="max"/> range and returns the distinct ids.</summary>
    public static List<long> IdList(long[] ids, string parameter, int max = 100)
    {
        if (ids.Length > max)
        {
            throw new ArgumentException($"{parameter} is limited to {max} ids (got {ids.Length}).");
        }

        foreach (var id in ids)
        {
            ToolJson.PositiveId(id, parameter);
        }

        return ids.Distinct().ToList();
    }

    /// <summary>
    /// Validates external ids for <c>show_many?external_ids=</c>, which is comma-separated: a value
    /// containing a comma would be read as two ids, so it is refused.
    /// </summary>
    public static List<string> ExternalIdList(string[] ids, string parameter)
    {
        if (ids.Length > 100)
        {
            throw new ArgumentException($"{parameter} is limited to 100 values (got {ids.Length}).");
        }

        var cleaned = new List<string>();
        foreach (var id in ids)
        {
            var trimmed = id?.Trim() ?? string.Empty;
            if (trimmed.Length == 0 || trimmed.Length > 255 || trimmed.Contains(','))
            {
                throw new ArgumentException($"{parameter}: each external id must be 1-255 characters without commas (got '{id}').");
            }

            cleaned.Add(trimmed);
        }

        return cleaned.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The pagination block for a lookup that returns everything at once.</summary>
    public static JsonObject NoMorePages(int count) => new()
    {
        ["has_more"] = false,
        ["next_cursor"] = null,
        ["page_size"] = count,
    };
}
