using System.Globalization;
using System.Text.Json.Nodes;

namespace ZendeskMcp.Tools;

/// <summary>
/// Small helpers the tool classes share for shaping Zendesk responses and building requests. Kept
/// deliberately thin: anything specific to one resource lives with that resource's tools.
/// </summary>
public static class ToolJson
{
    /// <summary>Query pairs from tuples, dropping null values, so optional filters can be listed inline.</summary>
    public static List<KeyValuePair<string, string?>> Query(params (string Key, object? Value)[] pairs) =>
        pairs.Where(p => p.Value is not null)
            .Select(p => new KeyValuePair<string, string?>(p.Key, Format(p.Value!)))
            .ToList();

    /// <summary>A copy of <paramref name="source"/> holding only <paramref name="fields"/> that are present.</summary>
    public static JsonObject Pick(JsonNode? source, params string[] fields)
    {
        var picked = new JsonObject();
        if (source is not JsonObject obj)
        {
            return picked;
        }

        foreach (var field in fields)
        {
            if (obj.TryGetPropertyValue(field, out var value))
            {
                picked[field] = value?.DeepClone();
            }
        }

        return picked;
    }

    /// <summary>The standard pagination block every paged tool returns, so the model pages the same way everywhere.</summary>
    public static JsonObject PaginationInfo(JsonNode? page, int pageSize) => new()
    {
        ["has_more"] = page?["meta"]?["has_more"]?.GetValue<bool>() ?? false,
        ["next_cursor"] = page?["meta"]?["after_cursor"]?.DeepClone(),
        ["page_size"] = pageSize,
    };

    /// <summary>Validates a page size against Zendesk's 1–100 range, defaulting when unset.</summary>
    public static int PageSize(int? requested, int defaultSize = 100)
    {
        var size = requested ?? defaultSize;
        return size is >= 1 and <= 100
            ? size
            : throw new ArgumentException($"page_size must be between 1 and 100 (got {size}).");
    }

    /// <summary>Comma-joins ids for Zendesk's <c>show_many</c> and <c>ids=</c> parameters.</summary>
    public static string Ids(IEnumerable<long> ids) =>
        string.Join(",", ids.Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Rejects a non-positive id with a message naming the parameter.</summary>
    public static long PositiveId(long id, string parameter) =>
        id > 0 ? id : throw new ArgumentException($"{parameter} must be a positive integer (got {id}).");

    /// <summary>
    /// Refuses an action an administrator has switched off for the whole account. The message says it
    /// is a setting, so the model reports it as one rather than retrying.
    /// </summary>
    public static void RequireFeature(bool enabled, string setting, string action)
    {
        if (!enabled)
        {
            throw new UnauthorizedAccessException(
                $"{action} is switched off for this server (Zendesk:Features:{setting}). Ask the Infrastructure team if it should be enabled.");
        }
    }

    /// <summary>
    /// Reads a whole number from a node that may be a number or absent — whether parsed from Zendesk's
    /// JSON or built in code (a <see cref="JsonValue"/> holding an <c>int</c> does not answer
    /// <c>TryGetValue&lt;long&gt;</c>, so checking only that would read a tool's own values as null).
    /// </summary>
    public static long? Long(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var l))
        {
            return l;
        }

        if (value.TryGetValue<int>(out var i))
        {
            return i;
        }

        return value.TryGetValue<double>(out var d) && d == Math.Floor(d) && Math.Abs(d) < 9e15 ? (long)d : null;
    }

    /// <summary>Reads a string from a node that may be a string or absent.</summary>
    public static string? String(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static string Format(object value) => value switch
    {
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
