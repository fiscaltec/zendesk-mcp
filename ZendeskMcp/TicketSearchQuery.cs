using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using ZendeskMcp.Tools;

namespace ZendeskMcp;

/// <summary>
/// The ticket filters shared by Search_tickets, Count_tickets, Get_ticket_metrics_summary,
/// Get_sla_summary and Get_sla_queue. Each tool declares the parameters itself (MCP parameters cannot be
/// shared) and builds one of these, so the five turn the same filter into the same Zendesk query.
/// </summary>
public sealed record TicketSearchFilters
{
    public string? Text { get; init; }
    public string? Status { get; init; }
    public string? Priority { get; init; }
    public string? TicketType { get; init; }
    public string? Assignee { get; init; }
    public string? Requester { get; init; }
    public string? Organization { get; init; }
    public string? Group { get; init; }
    public string? Brand { get; init; }
    public string? Form { get; init; }
    public string? IncludeTags { get; init; }
    public string? ExcludeTags { get; init; }
    public long? CustomFieldId { get; init; }
    public string? CustomFieldValue { get; init; }
    public long? CustomStatusId { get; init; }
    public bool? HasAttachments { get; init; }
    public string? StartDate { get; init; }
    public string? EndDate { get; init; }
    public string? SolvedStartDate { get; init; }
    public string? SolvedEndDate { get; init; }

    /// <summary>Channels (see <see cref="TicketSearchQuery.ChannelValues"/>); a ticket on any of them matches.</summary>
    public IReadOnlyList<string>? Channels { get; init; }

    /// <summary>Custom field conditions, all of which must hold.</summary>
    public IReadOnlyList<TicketCustomFieldCondition>? CustomFields { get; init; }
}

/// <summary>
/// One custom field condition in a ticket search: the field has exactly <c>value</c>, or, with
/// <c>has_value</c>, the field is set (true) or empty (false). Exactly one of the two is given.
/// </summary>
public sealed record TicketCustomFieldCondition(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("value")] string? Value = null,
    [property: JsonPropertyName("has_value")] bool? HasValue = null);

/// <summary>
/// Turns <see cref="TicketSearchFilters"/> into a Zendesk Search query (<c>type:ticket status:open …</c>).
/// </summary>
/// <remarks>
/// <para>
/// The caller never writes Zendesk search syntax: each filter is one validated term, and the free
/// text is neutralised so that it can only ever match words. That keeps a model from widening a search
/// by accident — <c>text</c> containing <c>status:solved</c> searches for those words, it does not
/// change the status filter — and keeps the query the tool reports honest about what was searched.
/// </para>
/// <para>
/// Named people, groups, organisations, brands and forms go to Zendesk by name, which Zendesk's search
/// resolves; a numeric value is sent as an id (<c>assignee_id:123</c>) so that a person called "123"
/// cannot be confused with user 123. Multi-word names are quoted.
/// </para>
/// </remarks>
public static class TicketSearchQuery
{
    public const int MaxTextLength = 500;

    /// <summary>Most conditions one custom_fields filter takes.</summary>
    public const int MaxCustomFieldConditions = 10;

    /// <summary>The value that selects every messaging channel together.</summary>
    public const string MessagingChannel = "messaging";

    /// <summary>
    /// The values the channel filter takes: the channel keys Count_tickets groups by, so a channel here
    /// selects exactly the tickets of its group there, plus <see cref="MessagingChannel"/>.
    /// </summary>
    public static readonly string[] ChannelValues =
        [.. TicketCountHelpers.Channels.Select(c => c.Channel), MessagingChannel];

    private static readonly string[] Statuses = ["new", "open", "pending", "hold", "solved", "closed"];
    private static readonly string[] Priorities = ["low", "normal", "high", "urgent"];
    private static readonly string[] TicketTypes = ["question", "incident", "problem", "task"];

    /// <summary>Builds the query. Throws <see cref="ArgumentException"/> naming the bad filter.</summary>
    public static string Build(TicketSearchFilters filters)
    {
        var terms = new List<string> { "type:ticket" };

        if (!string.IsNullOrWhiteSpace(filters.Status))
        {
            terms.Add($"status:{OneOf(filters.Status, Statuses, "status")}");
        }

        foreach (var priority in CommaList(filters.Priority))
        {
            terms.Add($"priority:{OneOf(priority, Priorities, "priority")}");
        }

        foreach (var type in CommaList(filters.TicketType))
        {
            terms.Add($"ticket_type:{OneOf(type, TicketTypes, "ticket_type")}");
        }

        AddPerson(terms, "assignee", filters.Assignee);
        AddPerson(terms, "requester", filters.Requester);
        AddNamed(terms, "organization", filters.Organization, allowNone: true);
        AddNamed(terms, "group", filters.Group, allowNone: true);
        AddNamed(terms, "brand", filters.Brand, allowNone: false);

        if (!string.IsNullOrWhiteSpace(filters.Form))
        {
            terms.Add($"form:{Quote(filters.Form)}");
        }

        foreach (var tag in CommaList(filters.IncludeTags))
        {
            terms.Add($"tags:{Tag(tag)}");
        }

        foreach (var tag in CommaList(filters.ExcludeTags))
        {
            terms.Add($"-tags:{Tag(tag)}");
        }

        if (filters.CustomFieldId is not null || !string.IsNullOrWhiteSpace(filters.CustomFieldValue))
        {
            if (filters.CustomFieldId is not { } fieldId || fieldId <= 0 || string.IsNullOrWhiteSpace(filters.CustomFieldValue))
            {
                throw new ArgumentException("custom_field_id and custom_field_value must be given together, with a positive field id.");
            }

            terms.Add($"custom_field_{fieldId.ToString(CultureInfo.InvariantCulture)}:{Quote(filters.CustomFieldValue)}");
        }

        AddCustomFields(terms, filters);
        AddChannels(terms, filters.Channels);

        if (filters.CustomStatusId is { } customStatus)
        {
            if (customStatus <= 0)
            {
                throw new ArgumentException("custom_status_id must be a positive integer.");
            }

            terms.Add($"custom_status_id:{customStatus.ToString(CultureInfo.InvariantCulture)}");
        }

        if (filters.HasAttachments is { } hasAttachments)
        {
            terms.Add($"has_attachment:{(hasAttachments ? "true" : "false")}");
        }

        AddDate(terms, "created>=", filters.StartDate, "start_date");
        AddDate(terms, "created<=", filters.EndDate, "end_date");
        AddDate(terms, "solved>=", filters.SolvedStartDate, "solved_start_date");
        AddDate(terms, "solved<=", filters.SolvedEndDate, "solved_end_date");

        if (!string.IsNullOrWhiteSpace(filters.Text))
        {
            if (filters.Text.Length > MaxTextLength)
            {
                throw new ArgumentException($"text is limited to {MaxTextLength} characters.");
            }

            terms.Add(NeutraliseText(filters.Text));
        }

        return string.Join(' ', terms);
    }

    /// <summary>The same search in the Zendesk agent interface, for the person to open.</summary>
    public static string AgentSearchUrl(string baseUrl, string query) =>
        $"{baseUrl}/agent/search/1?type=ticket&q={Uri.EscapeDataString(query)}";

    /// <summary>
    /// Parses a date filter as Zendesk will read it. Date-only values are days; values with a time
    /// keep it. Throws <see cref="ArgumentException"/> for anything else.
    /// </summary>
    public static string NormaliseDate(string value, string parameter)
    {
        var trimmed = value.Trim();
        if (DateOnly.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            return day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var moment)
            && trimmed.Contains('T'))
        {
            return moment.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        throw new ArgumentException($"{parameter} must be YYYY-MM-DD or YYYY-MM-DDThh:mm:ss[Z] (got '{value}').");
    }

    /// <summary>
    /// The via: search values a channel filter expands to, in the channel table's order. Each value is
    /// one the live API accepts (TicketCountHelpers.Channels); repeating a keyword ORs its values, as
    /// priority:low priority:normal counts the two priorities together (confirmed against the live API).
    /// </summary>
    public static IReadOnlyList<string> ChannelSearchValues(IReadOnlyList<string>? channels)
    {
        if (channels is null || channels.Count == 0)
        {
            return [];
        }

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var channel in channels)
        {
            var normalised = (channel ?? string.Empty).Trim().ToLowerInvariant();
            if (!ChannelValues.Contains(normalised))
            {
                throw new ArgumentException($"channel values must be from: {string.Join(", ", ChannelValues)} (got '{channel}'). Use this filter for the channel, never 'via:' in text.");
            }

            wanted.Add(normalised);
        }

        // messaging is every messaging channel the table has a search value for. The other messaging
        // channels Zendesk knows (TicketConversationHelpers.MessagingChannels: Instagram DM, LINE, WeChat
        // and so on) are left out: their via: values have not been tried against the live API, and an
        // unknown one makes Zendesk refuse the whole search.
        var messaging = wanted.Contains(MessagingChannel);
        return TicketCountHelpers.Channels
            .Where(c => wanted.Contains(c.Channel) || (messaging && TicketConversationHelpers.MessagingChannels.Contains(c.Channel)))
            .Select(c => c.SearchValue)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static void AddChannels(List<string> terms, IReadOnlyList<string>? channels)
    {
        foreach (var value in ChannelSearchValues(channels))
        {
            terms.Add($"via:{value}");
        }
    }

    // custom_fields: each condition one term, all ANDed. Zendesk ORs two terms on the same field, so a
    // field may appear once, and not alongside custom_field_id. What a field's type allows (a checkbox is
    // never empty, a built-in field has its own filter) needs the field definitions, which
    // TicketSearchHelpers.CheckCustomFieldsAsync reads; this checks only the shape.
    private static void AddCustomFields(List<string> terms, TicketSearchFilters filters)
    {
        if (filters.CustomFields is not { Count: > 0 } conditions)
        {
            return;
        }

        if (conditions.Count > MaxCustomFieldConditions)
        {
            throw new ArgumentException($"custom_fields takes at most {MaxCustomFieldConditions} conditions (got {conditions.Count}).");
        }

        var seen = new HashSet<long>();
        foreach (var condition in conditions)
        {
            if (condition is null || condition.Id <= 0)
            {
                throw new ArgumentException("Each custom_fields condition needs id, a positive ticket field id (see List_ticket_fields).");
            }

            var id = condition.Id.ToString(CultureInfo.InvariantCulture);
            if (!seen.Add(condition.Id))
            {
                throw new ArgumentException($"custom_fields names field {id} more than once; Zendesk would match either condition rather than both. Give each field once.");
            }

            if (filters.CustomFieldId == condition.Id)
            {
                throw new ArgumentException($"Field {id} is already filtered by custom_field_id; give it in one place only.");
            }

            var hasValue = !string.IsNullOrWhiteSpace(condition.Value);
            if (hasValue == condition.HasValue.HasValue || (condition.Value is not null && !hasValue))
            {
                throw new ArgumentException($"custom_fields condition for field {id} needs exactly one of value (an exact value; for a drop-down, the option's tag) or has_value (true: any value, false: empty).");
            }

            // custom_field_<id>:* matches a field holding any value: on a drop-down it counted exactly the
            // tickets outside group_by custom_field's '(none)' group (confirmed against the live API).
            // Unverified against the live API: the negation, -custom_field_<id>:*, for empty fields, and
            // the wildcard on text, number, date and other non-drop-down fields.
            terms.Add(condition.HasValue switch
            {
                true => $"custom_field_{id}:*",
                false => $"-custom_field_{id}:*",
                null => $"custom_field_{id}:{Quote(condition.Value!)}",
            });
        }
    }

    private static void AddDate(List<string> terms, string op, string? value, string parameter)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            terms.Add(op + NormaliseDate(value, parameter));
        }
    }

    // assignee / requester: 'me', 'none', an email, a numeric id, or a name.
    private static void AddPerson(List<string> terms, string keyword, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (trimmed.Equals("me", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            terms.Add($"{keyword}:{trimmed.ToLowerInvariant()}");
        }
        else if (IsId(trimmed))
        {
            terms.Add($"{keyword}_id:{trimmed}");
        }
        else
        {
            terms.Add($"{keyword}:{Quote(trimmed)}");
        }
    }

    private static void AddNamed(List<string> terms, string keyword, string? value, bool allowNone)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var trimmed = value.Trim();
        if (allowNone && trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            terms.Add($"{keyword}:none");
        }
        else if (IsId(trimmed))
        {
            terms.Add($"{keyword}_id:{trimmed}");
        }
        else
        {
            terms.Add($"{keyword}:{Quote(trimmed)}");
        }
    }

    private static bool IsId(string value) => value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit);

    private static string OneOf(string value, string[] allowed, string parameter)
    {
        var normalised = value.Trim().ToLowerInvariant();
        return allowed.Contains(normalised)
            ? normalised
            : throw new ArgumentException($"{parameter} must be one of {string.Join(", ", allowed)} (got '{value}').");
    }

    private static IEnumerable<string> CommaList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Zendesk tags cannot contain spaces or quotes; refusing them is clearer than a search that
    // silently matches nothing.
    private static string Tag(string tag)
    {
        if (tag.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            throw new ArgumentException($"Tags cannot contain spaces or quotes (got '{tag}').");
        }

        return tag.ToLowerInvariant();
    }

    /// <summary>Quotes a value with whitespace; drops quote characters, which Zendesk search cannot escape.</summary>
    public static string Quote(string value)
    {
        var cleaned = value.Replace("\"", string.Empty, StringComparison.Ordinal).Trim();
        return cleaned.Any(char.IsWhiteSpace) ? $"\"{cleaned}\"" : cleaned;
    }

    // Free text matches words, never operators: a word carrying search syntax (a colon, a comparison,
    // a leading minus or a wildcard) is quoted so Zendesk reads it as a phrase.
    private static string NeutraliseText(string text)
    {
        var words = text.Replace("\"", " ", StringComparison.Ordinal)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        foreach (var word in words)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            var hasSyntax = word.IndexOfAny([':', '<', '>', '=', '*']) >= 0 || word.StartsWith('-');
            builder.Append(hasSyntax ? $"\"{word}\"" : word);
        }

        return builder.ToString();
    }
}
