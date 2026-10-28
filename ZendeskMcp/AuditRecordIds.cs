using System.Text.Json;

namespace ZendeskMcp;

/// <summary>
/// The record ids one upstream response returned, and how completely they were captured.
/// </summary>
/// <param name="Ids">Ids captured, up to <see cref="AuditRecordIds.MaxIds"/>.</param>
/// <param name="RecordsFetched">How many records the response actually carried.</param>
/// <param name="IdsAvailable">
/// <c>false</c> when the response shape yielded no ids — an explicit gap, so a record reads as "ids
/// unavailable for this path" rather than looking complete while naming nobody.
/// </param>
public readonly record struct AuditedRecords(
    IReadOnlyList<string> Ids,
    int RecordsFetched,
    bool IdsAvailable)
{
    public int IdsRecorded => Ids.Count;
}

/// <summary>
/// Extracts record ids from a <b>raw</b> Zendesk response.
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ <b>Raw</b> is the load-bearing word. <c>ZendeskService.GetResourcesAsync</c> applies
/// <c>FilterJsonFields</c> <i>after</i> the upstream call, so a caller passing <c>fields=name</c>
/// receives results with no <c>id</c> at all. Extracting from the tool's result rather than the
/// upstream response would therefore name nobody on exactly the calls where a narrow projection was
/// used — a silent gap, and a plausible-looking one.
/// </para>
/// <para>
/// This is what closes the bulk-read gap: <c>List_organizations(limit=100)</c> otherwise records that
/// a hundred customers were read and names none of them, because an unscoped list carries the
/// identities only in the response body.
/// </para>
/// </remarks>
public static class AuditRecordIds
{
    /// <summary>
    /// Most ids one record will carry. The bounded auto-pager can gather a thousand records, so an
    /// uncapped list would put them all in one audit record; <see cref="AuditedRecords.RecordsFetched"/>
    /// keeps the true magnitude visible alongside the capped list.
    /// </summary>
    internal const int MaxIds = 100;

    private static readonly AuditedRecords Unavailable = new([], 0, IdsAvailable: false);

    /// <summary>
    /// Properties that are never the records themselves: pagination and paging metadata, and the
    /// sideloads Zendesk puts beside the primary collection.
    /// </summary>
    private static readonly HashSet<string> NonRecordProperties = new(StringComparer.Ordinal)
    {
        "meta", "links", "next_page", "previous_page", "count", "facets",
    };

    /// <summary>
    /// The record id a mutation's own URL names, for when the response body carries none.
    /// </summary>
    /// <remarks>
    /// Zendesk answers some updates and every delete with an empty body, so <see cref="Extract"/> finds
    /// nothing and the record would name nobody — on exactly the events an access trail is kept for.
    /// <para>
    /// ⚠️ Only a <b>numeric</b> last segment counts, and only for methods whose URL ends in a record id.
    /// Zendesk paths also end in locales (<c>…/translations/en-gb</c>) and collection names
    /// (<c>…/update_many</c>); recording those as though they were records would be a fabricated
    /// identifier, which is far worse than an honest gap — it resolves to nothing and cannot be told
    /// apart from a real one.
    /// </para>
    /// </remarks>
    public static AuditedRecords FromMutationUrl(HttpMethod method, string url)
    {
        if (method != HttpMethod.Delete && method != HttpMethod.Put && method != HttpMethod.Patch)
        {
            return Unavailable;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Unavailable;
        }

        var last = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (last is not null && last.EndsWith(".json", StringComparison.Ordinal))
        {
            last = last[..^5];
        }

        return !string.IsNullOrEmpty(last) && last.All(char.IsAsciiDigit)
            ? new AuditedRecords([last], 1, IdsAvailable: true)
            : Unavailable;
    }

    public static AuditedRecords Extract(string rawJson)
    {
        // An audit component must never be the reason a tool call fails. Zendesk can answer with a
        // body that is not JSON at all — a gateway error page, an empty 204 — and parsing throws on
        // those, so an unguarded parse would turn an upstream hiccup into a failed call.
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawJson);
        }
        catch (JsonException)
        {
            return Unavailable;
        }

        using (document)
        {
            return ExtractFrom(document.RootElement);
        }
    }

    private static AuditedRecords ExtractFrom(JsonElement root)
    {
        if (!TryFindRecordArray(root, out var records))
        {
            return TryReadSingleRecord(root);
        }

        var ids = records.EnumerateArray()
            .Select(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("id", out var id) ? IdText(id) : null)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Take(MaxIds)
            .ToList();

        // A non-empty array none of whose elements carries an `id` is a genuine gap: records were
        // read and not one can be named. An EMPTY array is the opposite — a complete answer with
        // nothing in it — and flagging that would make every "no matches" search look like a broken
        // audit record.
        var fetched = records.GetArrayLength();
        return new AuditedRecords(ids, fetched, IdsAvailable: fetched == 0 || ids.Count > 0);
    }

    /// <summary>
    /// Zendesk names its collection after the resource — <c>{"tickets": [...]}</c>,
    /// <c>{"results": [...]}</c> for search, <c>{"articles": [...]}</c> — and puts sideloads beside it
    /// (<c>users</c>, <c>organizations</c>). The <b>first</b> array of objects is the primary
    /// collection: Zendesk serialises it before its sideloads, and a sideload is context the caller did
    /// not ask for by id. A bare array is taken as-is.
    /// </summary>
    private static bool TryFindRecordArray(JsonElement root, out JsonElement records)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            records = root;
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (NonRecordProperties.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var array = property.Value;
                if (array.GetArrayLength() == 0 || array[0].ValueKind == JsonValueKind.Object)
                {
                    records = array;
                    return true;
                }
            }
        }

        records = default;
        return false;
    }

    /// <summary>
    /// A get-by-id returns one record wrapped in its resource name — <c>{"ticket": {...}}</c> — or,
    /// for a few endpoints, the bare record. The path names that record too, so this is corroboration
    /// rather than the only evidence; but reporting "ids unavailable" here would be a false gap.
    /// </summary>
    private static AuditedRecords TryReadSingleRecord(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Unavailable;
        }

        if (root.TryGetProperty("id", out var bare) && IdText(bare) is { } bareId)
        {
            return new AuditedRecords([bareId], 1, IdsAvailable: true);
        }

        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("id", out var wrapped)
                && IdText(wrapped) is { } wrappedId)
            {
                return new AuditedRecords([wrappedId], 1, IdsAvailable: true);
            }
        }

        return Unavailable;
    }

    // Zendesk ids are integers; a few resources (custom objects, Guide media) use strings. Anything else
    // is not an id. `GetString()` would throw on a number, and this runs inside ZendeskService.SendAsync,
    // so the kind is checked first.
    private static string? IdText(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.Number => id.GetRawText(),
        JsonValueKind.String when !string.IsNullOrEmpty(id.GetString()) => id.GetString(),
        _ => null,
    };
}
